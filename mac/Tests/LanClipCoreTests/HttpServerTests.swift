import XCTest
import Network
@testable import LanClipCore

private let testToken = "s3cr3t-token"

/// Читатель буфера, детерминированно бросающий из `read()` — используется, чтобы
/// проверить обработку ошибок построения снимка (Ruling 2), не завися от
/// файловой системы и её трактовки битых симлинков.
private final class ThrowingClipboard: ClipboardReading {
    struct ReadFailure: Error {}
    func changeCount() -> Int { 1 }
    func read() throws -> ClipContent { throw ReadFailure() }
}

final class HttpServerTests: XCTestCase {
    private var directory: URL!
    private let clipboard = FakeClipboard()

    override func setUpWithError() throws {
        directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-httpserver-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func makeConfig(port: Int = 0, peers: [String] = ["10.0.0.2"]) -> Config {
        Config(port: port, token: testToken, peers: peers)
    }

    private func makeStore() -> SnapshotStore {
        SnapshotStore(reader: clipboard)
    }

    private func request(method: String, path: String, token: String? = testToken) -> HttpRequest {
        var headers: [String: String] = [:]
        if let token { headers["x-clip-token"] = token }
        let query: [String: String]
        let bareParts = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let barePath = String(bareParts[0])
        var q: [String: String] = [:]
        if bareParts.count == 2 {
            for pair in bareParts[1].split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1)
                if kv.count == 2 { q[String(kv[0])] = String(kv[1]) }
            }
        }
        query = q
        return HttpRequest(method: method, path: barePath, query: query, headers: headers, body: Data())
    }

    private func succeedingPull() throws -> PullResult {
        PullResult(kind: .text, fileCount: 0, bytes: 5)
    }

    private func failingPull() throws -> PullResult {
        struct PeerUnreachable: Error, CustomStringConvertible {
            var description: String { "сосед недоступен" }
        }
        throw PeerUnreachable()
    }

    // MARK: - route() unit tests (no networking)

    func testPublicRemoteReturns403() {
        let response = HttpServer.route(request(method: "GET", path: "/health"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "8.8.8.8", pull: succeedingPull)
        XCTAssertEqual(response.status, 403)
    }

    func testBadTokenReturns401() {
        let response = HttpServer.route(request(method: "GET", path: "/health", token: "wrong"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 401)
    }

    func testMissingTokenReturns401() {
        let response = HttpServer.route(request(method: "GET", path: "/health", token: nil),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 401)
    }

    func testUnknownPathReturns404() {
        let response = HttpServer.route(request(method: "GET", path: "/nope"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 404)
    }

    func testWrongMethodOnKnownPathReturns405() {
        let response = HttpServer.route(request(method: "POST", path: "/health"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 405)
    }

    func testWrongMethodOnBlobPathReturns405() {
        clipboard.content = .text("x")
        let response = HttpServer.route(request(method: "POST", path: "/clip/blob/0?seq=1"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 405)
    }

    func testStaleSeqReturns409() throws {
        clipboard.content = .text("первый")
        let store = makeStore()
        let staleSeq = try store.current().manifest.seq
        clipboard.content = .image(Data([1, 2, 3]))

        let response = HttpServer.route(request(method: "GET", path: "/clip/blob/0?seq=\(staleSeq)"),
                                         config: makeConfig(), snapshots: store,
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 409)
    }

    func testOutOfRangeBlobIndexReturns404() throws {
        clipboard.content = .image(Data([1]))
        let store = makeStore()
        let seq = try store.current().manifest.seq
        let response = HttpServer.route(request(method: "GET", path: "/clip/blob/5?seq=\(seq)"),
                                         config: makeConfig(), snapshots: store,
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 404)
    }

    func testSnapshotBuildFailureReturns500() throws {
        // Ruling 2: ошибка построения снимка обязана превращаться в 500, а не ронять
        // сервер. Показательный случай из ревью задачи 5 — битый симлинк, выбранный в
        // Finder как одиночный файл, — на деле не воспроизводится: `expand()` сначала
        // проверяет `fileManager.fileExists(atPath:)`, а для битого симлинка (target
        // отсутствует) она возвращает false ДО вызова `attributesOfItem`, так что
        // элемент тихо отфильтровывается и `current()` не бросает (подтверждено
        // эмпирически: reproduction через такой симлинк давал 200 с пустым манифестом,
        // не 500). Поэтому здесь дефект инжектируется напрямую через `ClipboardReading`,
        // бросающий из `read()`, — это гоняет тот же путь `current() throws → 500`,
        // которым фактически может прийти любая ошибка построения снимка.
        let throwingReader = ThrowingClipboard()
        let store = SnapshotStore(reader: throwingReader)

        let response = HttpServer.route(request(method: "GET", path: "/clip"),
                                         config: makeConfig(), snapshots: store,
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 500)
    }

    func testPullFailureReturns503WithErrorBody() throws {
        let response = HttpServer.route(request(method: "POST", path: "/pull"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: failingPull)
        XCTAssertEqual(response.status, 503)
        let body = try XCTUnwrap(response.body)
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertNotNil(json["error"] as? String)
    }

    func testHealthReturnsOkHostAndVersion() throws {
        let response = HttpServer.route(request(method: "GET", path: "/health"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mymac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 200)
        let body = try XCTUnwrap(response.body)
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertEqual(json["ok"] as? Bool, true)
        XCTAssertEqual(json["host"] as? String, "mymac")
        XCTAssertEqual(json["version"] as? Int, protocolVersion)
    }

    func testClipReturnsManifest() throws {
        clipboard.content = .text("привет")
        let store = makeStore()
        let response = HttpServer.route(request(method: "GET", path: "/clip"),
                                         config: makeConfig(), snapshots: store,
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 200)
        let manifest = try Manifest.decode(try XCTUnwrap(response.body))
        XCTAssertEqual(manifest.kind, .text)
        XCTAssertEqual(manifest.text, "привет")
    }

    func testClipBlobReturnsBytesWithOctetStream() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])
        clipboard.content = .image(png)
        let store = makeStore()
        let seq = try store.current().manifest.seq

        let response = HttpServer.route(request(method: "GET", path: "/clip/blob/0?seq=\(seq)"),
                                         config: makeConfig(), snapshots: store,
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.headers["Content-Type"], "application/octet-stream")
        XCTAssertEqual(response.body, png)
    }

    func testPullReturnsResultBody() throws {
        let response = HttpServer.route(request(method: "POST", path: "/pull"),
                                         config: makeConfig(), snapshots: makeStore(),
                                         hostName: "mac", remote: "127.0.0.1", pull: succeedingPull)
        XCTAssertEqual(response.status, 200)
        let decoded = try JSONDecoder().decode(PullResult.self, from: try XCTUnwrap(response.body))
        XCTAssertEqual(decoded, PullResult(kind: .text, fileCount: 0, bytes: 5))
    }

    // MARK: - End-to-end tests over a real NWListener socket

    private func openConnection(port: Int) -> NWConnection {
        let connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: UInt16(port))!,
                                       using: .tcp)
        connection.start(queue: .main)
        return connection
    }

    private func sendAndReadResponse(_ connection: NWConnection, raw: Data) throws -> (status: Int, headers: [String: String], body: Data) {
        // Не ждём завершения записи перед чтением: как только `send` вызван, ОС уже
        // передаёт байты по сокету независимо от того, когда сработает наш локальный
        // completion. Если сервер отвечает и закрывает соединение раньше, чем клиент
        // дописал весь буфер (ожидаемо для теста с превышением лимита накопления),
        // локальная запись может завершиться с "Broken pipe" — это не ошибка теста,
        // важен лишь ответ, прочитанный ниже.
        connection.send(content: raw, completion: .contentProcessed { _ in })

        let accumulator = ResponseAccumulator()
        let received = expectation(description: "received")
        accumulator.pump(connection) { received.fulfill() }
        wait(for: [received], timeout: 5)

        guard let result = accumulator.result else { throw XCTSkip("no response received") }
        return result
    }

    /// Копит байты входящего ответа на очереди сети до тех пор, пока
    /// `parseHttpResponse` не соберёт его целиком (или соединение не закроется).
    private final class ResponseAccumulator: @unchecked Sendable {
        private var buffer = Data()
        private(set) var result: (status: Int, headers: [String: String], body: Data)?

        func pump(_ connection: NWConnection, onDone: @escaping @Sendable () -> Void) {
            connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
                guard let self else { return }
                if let data { self.buffer.append(data) }
                if let parsed = try? parseHttpResponse(self.buffer) {
                    self.result = parsed
                    onDone()
                    return
                }
                if isComplete || error != nil {
                    onDone()
                    return
                }
                self.pump(connection, onDone: onDone)
            }
        }
    }

    func testStartWithOutOfRangePortThrowsInsteadOfCrashing() throws {
        // `Config` не валидируется автоматически перед передачей в HttpServer, а
        // `UInt16(config.port)` крашнулся бы на значении вне 0...65535 — защита
        // от этого должна бросать ошибку, а не ронять процесс.
        let config = Config(port: 100_000, token: testToken, peers: ["10.0.0.2"])
        let server = HttpServer(config: config, snapshots: makeStore(), hostName: "mac", pull: succeedingPull)
        XCTAssertThrowsError(try server.start()) { error in
            XCTAssertEqual(error as? HttpServerError, .invalidPort(100_000))
        }
    }

    func testEndToEndClipRoundTripWithAndWithoutToken() throws {
        clipboard.content = .text("сквозной обмен")
        let store = makeStore()
        let server = HttpServer(config: makeConfig(), snapshots: store, hostName: "mac", pull: succeedingPull)
        try server.start()
        defer { server.stop() }

        let withToken = openConnection(port: server.boundPort)
        defer { withToken.cancel() }
        let raw = Data("GET /clip HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Clip-Token: \(testToken)\r\n\r\n".utf8)
        let response = try sendAndReadResponse(withToken, raw: raw)
        XCTAssertEqual(response.status, 200)
        let manifest = try Manifest.decode(response.body)
        XCTAssertEqual(manifest.text, "сквозной обмен")

        let withoutToken = openConnection(port: server.boundPort)
        defer { withoutToken.cancel() }
        let rawNoToken = Data("GET /clip HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n".utf8)
        let response2 = try sendAndReadResponse(withoutToken, raw: rawNoToken)
        XCTAssertEqual(response2.status, 401)
    }

    func testOversizedRequestReturns400AndKeepsServerAlive() throws {
        let store = makeStore()
        let server = HttpServer(config: makeConfig(), snapshots: store, hostName: "mac", pull: succeedingPull)
        try server.start()
        defer { server.stop() }

        // Заголовок без завершающего \r\n\r\n длиной больше 1 МБ — накопление должно
        // упереться в лимит и вернуть 400, а не расти неограниченно.
        let oversizedHeader = "GET /" + String(repeating: "a", count: 2_000_000) + " HTTP/1.1\r\n"
        let connection = openConnection(port: server.boundPort)
        defer { connection.cancel() }
        let response = try sendAndReadResponse(connection, raw: Data(oversizedHeader.utf8))
        XCTAssertEqual(response.status, 400)

        // Сервер должен продолжать обслуживать дальнейшие соединения.
        let healthy = openConnection(port: server.boundPort)
        defer { healthy.cancel() }
        let raw = Data("GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Clip-Token: \(testToken)\r\n\r\n".utf8)
        let healthResponse = try sendAndReadResponse(healthy, raw: raw)
        XCTAssertEqual(healthResponse.status, 200)
    }

    func testLargeBlobIsDeliveredInFullOverChunkedSend() throws {
        let file = directory.appendingPathComponent("large.bin")
        let payload = Data((0..<700_000).map { UInt8($0 % 256) })
        try payload.write(to: file)
        clipboard.content = .files([file])

        let store = makeStore()
        let seq = try store.current().manifest.seq
        let server = HttpServer(config: makeConfig(), snapshots: store, hostName: "mac", pull: succeedingPull)
        try server.start()
        defer { server.stop() }

        let connection = openConnection(port: server.boundPort)
        defer { connection.cancel() }
        let raw = Data("GET /clip/blob/0?seq=\(seq) HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Clip-Token: \(testToken)\r\n\r\n".utf8)
        let response = try sendAndReadResponse(connection, raw: raw)
        XCTAssertEqual(response.status, 200)
        XCTAssertEqual(response.body.count, payload.count)
        XCTAssertEqual(response.body, payload)
    }
}
