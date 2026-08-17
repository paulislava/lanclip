import Foundation
import Network

/// Итог операции `POST /pull`: что агент забрал у соседа и записал в локальный буфер.
public struct PullResult: Codable, Equatable, Sendable {
    public let kind: ClipKind
    public let fileCount: Int
    public let bytes: Int

    public init(kind: ClipKind, fileCount: Int, bytes: Int) {
        self.kind = kind
        self.fileCount = fileCount
        self.bytes = bytes
    }
}

/// HTTP-сервер буфера обмена поверх `NWListener`/`NWConnection`.
///
/// `URLSession` использовать нельзя — ATS блокирует plain-HTTP из процессов без
/// Info.plist, поэтому транспорт целиком на `Network.framework`.
///
/// Маршрутизация вынесена в статический `route(_:config:snapshots:hostName:remote:pull:)` —
/// он чистый относительно сети, так что все коды ответов проверяются без поднятия сокета.
public final class HttpServer: @unchecked Sendable {
    /// Приходящий запрос накапливается до этого предела; превышение обрывает
    /// соединение ответом 400, чтобы его нельзя было раздуть до исчерпания памяти.
    private static let maxIncomingRequestBytes = 1_048_576
    /// Тело ответа отдаётся чанками этого размера, а не одним куском в память —
    /// блоб может весить сотни мегабайт.
    private static let sendChunkSize = 262_144
    private static let listenerReadyTimeout: TimeInterval = 5

    private let config: Config
    private let snapshots: SnapshotStore
    private let hostName: String
    private let pull: () throws -> PullResult

    private let queue = DispatchQueue(label: "lanclip.httpserver")
    private let stateLock = NSLock()
    private var listener: NWListener?
    private var _boundPort: Int = 0

    public init(config: Config, snapshots: SnapshotStore, hostName: String,
                pull: @escaping () throws -> PullResult) {
        self.config = config
        self.snapshots = snapshots
        self.hostName = hostName
        self.pull = pull
    }

    public var boundPort: Int {
        stateLock.lock()
        defer { stateLock.unlock() }
        return _boundPort
    }

    // MARK: - Transport

    public func start() throws {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        let port: NWEndpoint.Port
        if config.port == 0 {
            port = .any
        } else if (1...Int(UInt16.max)).contains(config.port),
                  let explicit = NWEndpoint.Port(rawValue: UInt16(config.port)) {
            // `UInt16(config.port)` крашнулся бы на значении вне 0...65535 — Config
            // не валидируется автоматически перед передачей сюда, так что диапазон
            // проверяется явно вместо доверия вызывающей стороне.
            port = explicit
        } else {
            throw HttpServerError.invalidPort(config.port)
        }

        let listener = try NWListener(using: params, on: port)
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }

        let semaphore = DispatchSemaphore(value: 0)
        let outcome = StartOutcomeBox()
        listener.stateUpdateHandler = { state in
            switch state {
            case .ready:
                semaphore.signal()
            case .failed(let error):
                outcome.error = error
                semaphore.signal()
            default:
                break
            }
        }

        listener.start(queue: queue)
        guard semaphore.wait(timeout: .now() + Self.listenerReadyTimeout) == .success else {
            listener.cancel()
            throw HttpServerError.listenerTimedOut
        }
        if let startError = outcome.error {
            throw startError
        }

        stateLock.lock()
        _boundPort = Int(listener.port?.rawValue ?? 0)
        self.listener = listener
        stateLock.unlock()
    }

    public func stop() {
        stateLock.lock()
        let current = listener
        listener = nil
        stateLock.unlock()
        current?.cancel()
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: queue)
        receive(on: connection, buffer: Data(), remote: HttpServer.remoteAddress(of: connection))
    }

    private func receive(on connection: NWConnection, buffer: Data, remote: String) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] data, _, isComplete, error in
            guard let self else { return }

            var buffer = buffer
            if let data {
                buffer.append(data)
            }

            if buffer.count > Self.maxIncomingRequestBytes {
                self.respond(.empty(400), on: connection)
                return
            }

            do {
                let request = try parseHttpRequest(buffer)
                let response = HttpServer.route(request, config: self.config, snapshots: self.snapshots,
                                                 hostName: self.hostName, remote: remote, pull: self.pull)
                self.respond(response, on: connection)
            } catch HttpParseError.incomplete {
                if isComplete || error != nil {
                    connection.cancel()
                    return
                }
                self.receive(on: connection, buffer: buffer, remote: remote)
            } catch {
                self.respond(.empty(400), on: connection)
            }
        }
    }

    private func respond(_ response: HttpResponse, on connection: NWConnection) {
        let head = response.head()
        connection.send(content: head, completion: .contentProcessed { error in
            guard error == nil else {
                connection.cancel()
                return
            }
            HttpServer.sendBody(response.body ?? Data(), offset: 0, on: connection)
        })
    }

    private static func sendBody(_ body: Data, offset: Int, on connection: NWConnection) {
        guard offset < body.count else {
            connection.cancel()
            return
        }
        let end = min(offset + sendChunkSize, body.count)
        let chunk = Data(body[offset..<end])
        connection.send(content: chunk, completion: .contentProcessed { error in
            guard error == nil else {
                connection.cancel()
                return
            }
            sendBody(body, offset: end, on: connection)
        })
    }

    private static func remoteAddress(of connection: NWConnection) -> String {
        guard case let .hostPort(host, _) = connection.endpoint else { return "" }
        return "\(host)"
    }

    // MARK: - Routing (pure, no networking)

    public static func route(_ request: HttpRequest, config: Config, snapshots: SnapshotStore,
                              hostName: String, remote: String,
                              pull: () throws -> PullResult) -> HttpResponse {
        guard isPrivateAddress(remote) else { return .empty(403) }
        guard tokensMatch(request.headers["x-clip-token"] ?? "", config.token) else { return .empty(401) }

        switch (request.method, request.path) {
        case ("GET", "/health"):
            return healthResponse(hostName: hostName)
        case ("GET", "/clip"):
            return clipResponse(snapshots: snapshots)
        case ("POST", "/pull"):
            return pullResponse(pull: pull)
        default:
            break
        }

        if request.path.hasPrefix("/clip/blob/") {
            guard request.method == "GET" else { return .empty(405) }
            return blobResponse(request, snapshots: snapshots)
        }

        if ["/health", "/clip", "/pull"].contains(request.path) {
            return .empty(405)
        }

        return .empty(404)
    }

    private static func healthResponse(hostName: String) -> HttpResponse {
        let body = HealthBody(ok: true, host: hostName, version: protocolVersion)
        guard let data = try? JSONEncoder().encode(body) else { return .empty(500) }
        return .json(200, data)
    }

    private static func clipResponse(snapshots: SnapshotStore) -> HttpResponse {
        do {
            let snapshot = try snapshots.current()
            return .json(200, try snapshot.manifest.encoded())
        } catch {
            return .empty(500)
        }
    }

    private static func blobResponse(_ request: HttpRequest, snapshots: SnapshotStore) -> HttpResponse {
        let suffix = request.path.dropFirst("/clip/blob/".count)
        guard let index = Int(suffix) else { return .empty(404) }
        let seq = request.query["seq"].flatMap(Int.init) ?? -1

        do {
            guard let data = try snapshots.blob(index: index, seq: seq) else { return .empty(404) }
            return .bytes(data)
        } catch SnapshotError.staleSeq {
            return .empty(409)
        } catch {
            return .empty(500)
        }
    }

    private static func pullResponse(pull: () throws -> PullResult) -> HttpResponse {
        do {
            let result = try pull()
            let data = try JSONEncoder().encode(result)
            return .json(200, data)
        } catch {
            let body = ErrorBody(error: String(describing: error))
            let data = (try? JSONEncoder().encode(body)) ?? Data("{\"error\":\"unknown\"}".utf8)
            return .json(503, data)
        }
    }

    /// Сравнение токена по всей длине без раннего выхода — защита от подбора по
    /// времени ответа. Наивное `==` на строках такой гарантии не даёт.
    private static func tokensMatch(_ provided: String, _ expected: String) -> Bool {
        let providedBytes = Array(provided.utf8)
        let expectedBytes = Array(expected.utf8)
        var mismatch: UInt8 = providedBytes.count == expectedBytes.count ? 0 : 1

        let length = max(providedBytes.count, expectedBytes.count)
        for index in 0..<length {
            let lhs = index < providedBytes.count ? providedBytes[index] : 0
            let rhs = index < expectedBytes.count ? expectedBytes[index] : 0
            mismatch |= lhs ^ rhs
        }
        return mismatch == 0
    }

    private struct HealthBody: Codable {
        let ok: Bool
        let host: String
        let version: Int
    }

    private struct ErrorBody: Codable {
        let error: String
    }
}

public enum HttpServerError: Error, Equatable {
    case listenerTimedOut
    case invalidPort(Int)
}

/// Однопоточная почтовая ячейка для передачи ошибки старта listener'а из
/// `stateUpdateHandler` (выполняется на очереди сервера) обратно на вызывающий
/// поток. Доступ сериализован семафором в `start()`, поэтому гонок нет.
private final class StartOutcomeBox: @unchecked Sendable {
    var error: Error?
}
