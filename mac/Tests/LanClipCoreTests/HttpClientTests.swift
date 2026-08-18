import XCTest
import Network
@testable import LanClipCore

private let testToken = "s3cr3t-token"

/// Тесты гоняют настоящий `HttpServer` из задачи 7 на `127.0.0.1:0` с `FakeClipboard` —
/// клиент проверяется сквозным образом через реальный сокет, а не через route().
final class HttpClientTests: XCTestCase {
    private var directory: URL!
    private let clipboard = FakeClipboard()
    private var server: HttpServer!
    private var client: NwHttpClient!

    override func setUpWithError() throws {
        directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-httpclient-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        let config = Config(port: 0, token: testToken, peers: ["10.0.0.2"])
        let store = SnapshotStore(reader: clipboard)
        server = HttpServer(config: config, snapshots: store, hostName: "mac",
                             pull: { PullResult(kind: .text, fileCount: 0, bytes: 0) })
        try server.start()

        client = NwHttpClient(timeout: 3)
    }

    override func tearDownWithError() throws {
        server.stop()
        try? FileManager.default.removeItem(at: directory)
    }

    private var port: Int { server.boundPort }

    // MARK: - probe()

    func testProbeIsTrueWithCorrectToken() {
        XCTAssertTrue(client.probe(host: "127.0.0.1", port: port, token: testToken, timeout: 3))
    }

    func testProbeIsFalseWithWrongToken() {
        XCTAssertFalse(client.probe(host: "127.0.0.1", port: port, token: "wrong", timeout: 3))
    }

    /// Каждый вызов `perform()` открывает своё `NWConnection` — резидентный агент
    /// делает такие вызовы на каждый хоткей/резолв соседа неделями. Раньше
    /// `stateUpdateHandler` захватывал `connection` сильно, а `connection` хранил этот
    /// же handler в своём свойстве — самозамкнутый ARC-цикл, не освобождаемый никогда.
    /// Этот тест не измеряет память (ненадёжно), а лишь доказывает, что длинная серия
    /// вызовов остаётся рабочей — при цикле утечки объекты просто накапливались бы
    /// молча, не давая явного сбоя, поэтому падение тут не единственный критерий: сама
    /// починка (слабый захват + обнуление handler'а) проверяется чтением диффа.
    func testHundredSequentialProbesAllSucceedWithoutLeakingConnections() {
        for _ in 0..<100 {
            XCTAssertTrue(client.probe(host: "127.0.0.1", port: port, token: testToken, timeout: 3))
        }
    }

    // MARK: - manifest()

    func testManifestReturnsTextManifest() throws {
        clipboard.content = .text("привет из теста")
        let manifest = try client.manifest(host: "127.0.0.1", port: port, token: testToken)
        XCTAssertEqual(manifest.kind, .text)
        XCTAssertEqual(manifest.text, "привет из теста")
    }

    // MARK: - blob(to: nil) — в память

    func testBlobToMemoryReturnsPngBytes() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5])
        clipboard.content = .image(png)
        let seq = try client.manifest(host: "127.0.0.1", port: port, token: testToken).seq

        let data = try client.blob(host: "127.0.0.1", port: port, token: testToken,
                                    index: 0, seq: seq, to: nil)
        XCTAssertEqual(data, png)
    }

    // MARK: - blob(to: file) — потоком на диск

    func testBlobToFileWritesBytesToDisk() throws {
        let payload = Data((0..<500_000).map { UInt8($0 % 256) })
        clipboard.content = .image(payload)
        let seq = try client.manifest(host: "127.0.0.1", port: port, token: testToken).seq

        let destination = directory.appendingPathComponent("out.bin")
        let result = try client.blob(host: "127.0.0.1", port: port, token: testToken,
                                      index: 0, seq: seq, to: destination)
        XCTAssertNil(result)

        let written = try Data(contentsOf: destination)
        XCTAssertEqual(written, payload)
    }

    // MARK: - устаревший seq

    func testBlobWithStaleSeqReturnsStatus409() throws {
        clipboard.content = .image(Data([1, 2, 3]))
        let staleSeq = try client.manifest(host: "127.0.0.1", port: port, token: testToken).seq
        clipboard.content = .image(Data([4, 5, 6, 7]))

        XCTAssertThrowsError(try client.blob(host: "127.0.0.1", port: port, token: testToken,
                                              index: 0, seq: staleSeq, to: nil)) { error in
            XCTAssertEqual(error as? HttpClientError, .status(409))
        }
    }

    func testBlobWithStaleSeqToFileReturnsStatus409AndLeavesNoFile() throws {
        clipboard.content = .image(Data([1, 2, 3]))
        let staleSeq = try client.manifest(host: "127.0.0.1", port: port, token: testToken).seq
        clipboard.content = .image(Data([4, 5, 6, 7]))

        let destination = directory.appendingPathComponent("stale.bin")
        XCTAssertThrowsError(try client.blob(host: "127.0.0.1", port: port, token: testToken,
                                              index: 0, seq: staleSeq, to: destination)) { error in
            XCTAssertEqual(error as? HttpClientError, .status(409))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: destination.path))
    }

    // MARK: - недостижимый порт

    func testUnreachablePortReturnsTransportError() {
        // Порт, гарантированно закрытый локально: боевой порт сервера + 1, либо
        // фиксированный высокий порт, если тот случайно совпал.
        let deadPort = server.boundPort == 65_535 ? 65_534 : server.boundPort + 1
        XCTAssertThrowsError(try client.manifest(host: "127.0.0.1", port: deadPort, token: testToken)) { error in
            guard case .transport = error as? HttpClientError else {
                XCTFail("ожидали .transport, получили \(error)")
                return
            }
        }
    }

    // MARK: - таймаут (соединение принято, но сервер не отвечает)

    /// Слушатель, который принимает TCP-соединение и молчит — не шлёт ни байта.
    /// В отличие от недостижимого порта (быстрый `.failed`), это единственный способ
    /// реально прогнать ветку `DispatchSemaphore.wait(timeout:)` в `perform()`.
    private final class SilentListener: @unchecked Sendable {
        private let listener: NWListener
        private var connections: [NWConnection] = []
        private let queue = DispatchQueue(label: "silent-listener-test")

        init() throws {
            listener = try NWListener(using: .tcp, on: .any)
            let ready = DispatchSemaphore(value: 0)
            listener.newConnectionHandler = { [weak self] connection in
                connection.start(queue: self?.queue ?? .main)
                self?.connections.append(connection)
            }
            listener.stateUpdateHandler = { state in
                if case .ready = state { ready.signal() }
            }
            listener.start(queue: queue)
            guard ready.wait(timeout: .now() + 5) == .success else {
                throw HttpServerError.listenerTimedOut
            }
        }

        var port: Int { Int(listener.port?.rawValue ?? 0) }

        func stop() { listener.cancel() }
    }

    /// I2 (находка финального ревью): `timeout` раньше был бюджетом на ВЕСЬ блоб
    /// целиком — семафор заводился до старта соединения и снимался только когда
    /// тело было записано полностью. Слушатель здесь шлёт байты чанками с паузами
    /// МЕНЬШЕ `timeout`, но суммарное время передачи БОЛЬШЕ `timeout` — старая
    /// реализация оборвала бы такую передачу как `.timeout`, хотя прогресс шёл
    /// постоянно. Дедлайн обязан сбрасываться при каждом полученном чанке.
    private final class SlowTricklingListener: @unchecked Sendable {
        private let listener: NWListener
        private var connections: [NWConnection] = []
        private let queue = DispatchQueue(label: "trickling-listener-test")

        init(chunks: [Data], interChunkDelay: TimeInterval) throws {
            listener = try NWListener(using: .tcp, on: .any)
            let ready = DispatchSemaphore(value: 0)
            listener.newConnectionHandler = { [weak self] connection in
                guard let self else { return }
                connection.start(queue: self.queue)
                self.connections.append(connection)
                self.drainRequestThenSend(connection, chunks: chunks, interChunkDelay: interChunkDelay)
            }
            listener.stateUpdateHandler = { state in
                if case .ready = state { ready.signal() }
            }
            listener.start(queue: queue)
            guard ready.wait(timeout: .now() + 5) == .success else {
                throw HttpServerError.listenerTimedOut
            }
        }

        var port: Int { Int(listener.port?.rawValue ?? 0) }

        func stop() { listener.cancel() }

        /// Не разбирает запрос по-настоящему — просто ждёт, пока клиент замолчит
        /// (одно `receive`, достаточно для одного маленького GET без тела), затем
        /// шлёт чанки тела с задержкой между ними.
        private func drainRequestThenSend(_ connection: NWConnection, chunks: [Data], interChunkDelay: TimeInterval) {
            connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self] _, _, _, _ in
                guard let self else { return }
                let totalBody = chunks.reduce(0) { $0 + $1.count }
                let head = Data("HTTP/1.1 200 OK\r\nContent-Length: \(totalBody)\r\n\r\n".utf8)
                connection.send(content: head, completion: .contentProcessed { _ in
                    self.sendChunks(connection, chunks: chunks, interChunkDelay: interChunkDelay)
                })
            }
        }

        private func sendChunks(_ connection: NWConnection, chunks: [Data], interChunkDelay: TimeInterval) {
            guard let first = chunks.first else { return }
            let rest = Array(chunks.dropFirst())
            connection.send(content: first, completion: .contentProcessed { [weak self] _ in
                guard let self, !rest.isEmpty else { return }
                self.queue.asyncAfter(deadline: .now() + interChunkDelay) {
                    self.sendChunks(connection, chunks: rest, interChunkDelay: interChunkDelay)
                }
            })
        }
    }

    func testProgressResetsDeadlineForLongTricklingTransfer() throws {
        // 6 чанков, пауза 0.35с между ними — суммарно ~1.75с, заметно больше
        // `timeout: 0.5`, но каждая пауза меньше него: с "прогресс сбрасывает
        // дедлайн" передача обязана дойти до конца, а не оборваться по старому,
        // фиксированному на весь вызов бюджету.
        let chunk = Data(repeating: 0xAB, count: 1_000)
        let chunks = Array(repeating: chunk, count: 6)
        let slow = try SlowTricklingListener(chunks: chunks, interChunkDelay: 0.35)
        defer { slow.stop() }

        let patientClient = NwHttpClient(timeout: 0.5, progressTimeout: 5)
        let destination = directory.appendingPathComponent("trickled.bin")
        let result = try patientClient.blob(host: "127.0.0.1", port: slow.port, token: testToken,
                                             index: 0, seq: 0, to: destination)
        XCTAssertNil(result)
        let written = try Data(contentsOf: destination)
        XCTAssertEqual(written.count, chunks.count * chunk.count)
    }

    func testTimeoutFiresAndCancelsConnectionWithoutCrashing() throws {
        let silent = try SilentListener()
        defer { silent.stop() }

        let fastClient = NwHttpClient(timeout: 0.3)
        let started = Date()
        XCTAssertThrowsError(try fastClient.manifest(host: "127.0.0.1", port: silent.port, token: testToken)) { error in
            XCTAssertEqual(error as? HttpClientError, .timeout)
        }
        // Таймаут обязан сработать примерно за заданное время, а не зависнуть —
        // разумный потолок с запасом на шедулинг очереди.
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)

        // Late-callback safety: молчащий слушатель ещё жив некоторое время после того,
        // как клиент бросил `.timeout` — если бы отменённый колбэк ронял процесс или
        // висел, следующий вызов на том же клиенте тоже был бы задет.
        XCTAssertThrowsError(try fastClient.manifest(host: "127.0.0.1", port: silent.port, token: testToken)) { error in
            XCTAssertEqual(error as? HttpClientError, .timeout)
        }
    }
}
