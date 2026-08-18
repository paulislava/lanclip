import XCTest
import Network
@testable import LanClipCore

private let testToken = "s3cr3t-token"

// MARK: - Test doubles (подставные BlobFetching/HealthProbing для слоя 1)

/// Подставной `HealthProbing`: всегда отвечает `alive`, считает обращения — этого
/// достаточно, чтобы через настоящий `PeerResolver` проверить инвалидацию кеша
/// (см. `PeerResolverTests` — тот же приём).
private final class FakeProber: HealthProbing, @unchecked Sendable {
    private let lock = NSLock()
    private var alive: Bool

    init(alive: Bool = true) {
        self.alive = alive
    }

    func setAlive(_ value: Bool) {
        lock.lock(); alive = value; lock.unlock()
    }

    func probe(host: String, port: Int, token: String, timeout: TimeInterval) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return alive
    }
}

/// Подставной `BlobFetching`: манифест и блобы задаются заранее, вызовы можно
/// сконфигурировать на успех или на бросок конкретной ошибки — так проверяются
/// все ветки `PullClient` без поднятия сокета.
private final class FakeFetcher: BlobFetching, @unchecked Sendable {
    private let lock = NSLock()
    var manifestResult: Result<Manifest, Error> = .failure(HttpClientError.transport("не настроено"))
    // Если непусто — используется вместо `manifestResult`, по одному элементу на
    // вызов (последний элемент переиспользуется, если вызовов больше, чем элементов).
    // Нужно тестам повтора при 409: первый вызов манифеста обязан отличаться от
    // второго (другой seq/содержимое), а не быть одним и тем же значением на все вызовы.
    var manifestResults: [Result<Manifest, Error>] = []
    var blobResults: [Int: Result<Data?, Error>] = [:]
    // Если для индекса задана непустая очередь — используется вместо `blobResults[index]`
    // для ЭТОГО индекса, по одному элементу на вызов данного индекса (аналогично
    // `manifestResults`) — тот же приём для блобов: первый вызов индекса 0 бросает 409,
    // второй (после повтора) отдаёт настоящие байты.
    var blobResultsSequence: [Int: [Result<Data?, Error>]] = [:]
    private(set) var manifestCallCount = 0
    private(set) var blobCallIndexes: [Int] = []
    private var blobCallCountByIndex: [Int: Int] = [:]

    func manifest(host: String, port: Int, token: String) throws -> Manifest {
        lock.lock()
        manifestCallCount += 1
        let result: Result<Manifest, Error>
        if !manifestResults.isEmpty {
            let idx = min(manifestCallCount - 1, manifestResults.count - 1)
            result = manifestResults[idx]
        } else {
            result = manifestResult
        }
        lock.unlock()
        return try result.get()
    }

    func blob(host: String, port: Int, token: String, index: Int, seq: Int, to file: URL?) throws -> Data? {
        lock.lock()
        blobCallIndexes.append(index)
        let result: Result<Data?, Error>
        if let sequence = blobResultsSequence[index], !sequence.isEmpty {
            let callNumber = blobCallCountByIndex[index] ?? 0
            blobCallCountByIndex[index] = callNumber + 1
            let idx = min(callNumber, sequence.count - 1)
            result = sequence[idx]
        } else {
            result = blobResults[index] ?? .failure(HttpClientError.status(404))
        }
        lock.unlock()

        let data = try result.get()
        guard let file else { return data }
        guard let data else { return nil }
        FileManager.default.createFile(atPath: file.path, contents: data)
        return nil
    }
}

final class PullClientTests: XCTestCase {
    private var stagingRoot: URL!

    override func setUpWithError() throws {
        stagingRoot = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-pullclient-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: stagingRoot, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: stagingRoot)
    }

    private func makeConfig(maxBytes: Int = Config.defaultMaxBytes) -> Config {
        Config(port: 8899, token: testToken, peers: ["10.0.0.2"], maxBytes: maxBytes)
    }

    private func makeStaging() -> Staging {
        Staging(root: stagingRoot)
    }

    private func makeClient(config: Config, prober: FakeProber, fetcher: FakeFetcher,
                             writer: FakeClipboard) -> (PullClient, PeerResolver) {
        let resolver = PeerResolver(config: config, prober: prober)
        let client = PullClient(config: config, resolver: resolver, fetcher: fetcher,
                                 staging: makeStaging(), writer: writer)
        return (client, resolver)
    }

    // MARK: - Текст

    func testTextIsWrittenToClipboardAndReportsTextResult() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.text("привет с соседа", seq: 1))
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .text)
        XCTAssertEqual(result.fileCount, 0)
        XCTAssertEqual(result.bytes, "привет с соседа".utf8.count)
        XCTAssertEqual(writer.written, [.text("привет с соседа")])
        XCTAssertEqual(writer.content, .text("привет с соседа"))
    }

    // MARK: - Картинка

    func testImageArrivesAsPngBytesInClipboard() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.image(pngSize: png.count, seq: 7))
        fetcher.blobResults[0] = .success(png)
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .image)
        XCTAssertEqual(result.fileCount, 0)
        XCTAssertEqual(result.bytes, png.count)
        XCTAssertEqual(writer.content, .image(png))
        XCTAssertEqual(fetcher.blobCallIndexes, [0])
    }

    // MARK: - Два файла

    func testTwoFilesLandInBatchWithPreservedRelAndClipboardGetsLocalPaths() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        let blobs = [
            BlobRef(i: 0, rel: "docs/a.txt", size: 5),
            BlobRef(i: 1, rel: "b.txt", size: 3),
        ]
        fetcher.manifestResult = .success(.files(blobs, seq: 3))
        fetcher.blobResults[0] = .success(Data("hello".utf8))
        fetcher.blobResults[1] = .success(Data("hi!".utf8))
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .files)
        XCTAssertEqual(result.fileCount, 2)
        XCTAssertEqual(result.bytes, 8)

        guard case .files(let urls)? = writer.written.first else {
            return XCTFail("ожидали запись .files в буфер")
        }
        XCTAssertEqual(urls.count, 2)
        XCTAssertTrue(urls[0].path.hasSuffix("docs/a.txt"))
        XCTAssertTrue(urls[1].path.hasSuffix("b.txt"))
        XCTAssertEqual(try Data(contentsOf: urls[0]), Data("hello".utf8))
        XCTAssertEqual(try Data(contentsOf: urls[1]), Data("hi!".utf8))
    }

    // MARK: - Превышение maxBytes

    func testTooLargeManifestThrowsAndLeavesClipboardUntouched() throws {
        let config = makeConfig(maxBytes: 10)
        let fetcher = FakeFetcher()
        let blobs = [BlobRef(i: 0, rel: "big.bin", size: 100)]
        fetcher.manifestResult = .success(.files(blobs, seq: 1))
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .tooLarge(totalSize: 100, maxBytes: 10))
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
    }

    // MARK: - 409 при скачивании блоба

    func testStatus409DuringBlobFetchThrowsChangedMidTransferAndLeavesClipboardUntouched() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.image(pngSize: 3, seq: 9))
        fetcher.blobResults[0] = .failure(HttpClientError.status(409))
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, resolver) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .changedMidTransfer)
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))

        // 409 — это не сбой транспорта, сосед жив: кеш резолвера не должен сбрасываться.
        XCTAssertEqual(resolver.resolve(), "10.0.0.2")
    }

    func testStatus409DuringSecondFileFetchLeavesClipboardUntouched() throws {
        // Обрыв на втором файле партии — уже записанный на диск первый файл не
        // должен просочиться в буфер: либо оба файла, либо ничего.
        let config = makeConfig()
        let fetcher = FakeFetcher()
        let blobs = [
            BlobRef(i: 0, rel: "a.txt", size: 5),
            BlobRef(i: 1, rel: "b.txt", size: 3),
        ]
        fetcher.manifestResult = .success(.files(blobs, seq: 4))
        fetcher.blobResults[0] = .success(Data("hello".utf8))
        fetcher.blobResults[1] = .failure(HttpClientError.status(409))
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .changedMidTransfer)
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .empty)
    }

    // MARK: - Рулинг ревью задачи 26: ровно один автоматический повтор при 409

    func testChangedMidTransferRetriesOnceAndSucceedsWithSecondAttemptContent() throws {
        // Замер на реальном ПК (задача 26): ~20% pull-ов файлов ловили 409 из-за
        // фонового шума Windows (seq буфера уезжал между манифестом и блобом) — каждый
        // пятый Ctrl+Shift+V показывал ошибку вместо результата. Первая попытка здесь
        // нарочно рвётся на 409, вторая — отдаёт другой манифест (другой seq, другие
        // байты), чтобы отличить "повторили тот же манифест" от "дёрнули заново,
        // честно приняли текущее состояние соседа".
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResults = [
            .success(.image(pngSize: 3, seq: 5)),
            .success(.image(pngSize: 5, seq: 6)),
        ]
        fetcher.blobResultsSequence[0] = [
            .failure(HttpClientError.status(409)),
            .success(Data([9, 9, 9, 9, 9])),
        ]
        let writer = FakeClipboard()
        let (client, resolver) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .image)
        XCTAssertEqual(result.bytes, 5, "байты должны быть от второй, успешной попытки, а не от первой сорвавшейся")
        XCTAssertEqual(writer.written.count, 1, "буфер записан ровно один раз")
        XCTAssertEqual(writer.content, .image(Data([9, 9, 9, 9, 9])), "содержимое — от успешного повтора")
        XCTAssertEqual(fetcher.manifestCallCount, 2, "манифест обязан быть перезапрошен заново на повторе")

        // 409 не считается сбоем транспорта ни на первой, ни на второй попытке — кеш
        // резолвера не должен трогаться.
        XCTAssertEqual(resolver.resolve(), "10.0.0.2", "кеш резолвера должен пережить повтор")
    }

    func testChangedMidTransferOnBothAttemptsPropagatesErrorAndLeavesClipboardUntouched() throws {
        // Ровно ОДИН повтор, не цикл до успеха: если сосед продолжает меняться, второй
        // 409 подряд обязан долететь до пользователя как обычная ошибка, а не зависнуть
        // в бесконечном повторе.
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.image(pngSize: 3, seq: 5))
        fetcher.blobResults[0] = .failure(HttpClientError.status(409)) // одинаково на каждый вызов
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, resolver) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .changedMidTransfer)
        }
        XCTAssertTrue(writer.written.isEmpty, "буфер не тронут ни на первой, ни на второй попытке")
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
        XCTAssertEqual(fetcher.manifestCallCount, 2, "манифест запрошен дважды — по разу на попытку, не более")

        XCTAssertEqual(resolver.resolve(), "10.0.0.2",
                       "409 на обеих попытках всё равно не должен сбрасывать кеш резолвера")
    }

    func testChangedMidTransferRetryCreatesFreshStagingBatchNotReusingFirst() throws {
        // Каждая попытка обязана создавать СВОЮ партию стейджинга — `download(manifest:
        // host:)` вызывает `staging.newBatch()` заново при каждом вызове, поэтому вторая
        // попытка не должна досыпать файлы в партию первой, сорвавшейся попытки.
        let config = makeConfig()
        let fetcher = FakeFetcher()
        let blobs = [BlobRef(i: 0, rel: "a.txt", size: 5)]
        fetcher.manifestResults = [
            .success(.files(blobs, seq: 10)),
            .success(.files(blobs, seq: 11)),
        ]
        fetcher.blobResultsSequence[0] = [
            .failure(HttpClientError.status(409)),
            .success(Data("hello".utf8)),
        ]
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .files)
        guard case .files(let urls)? = writer.written.first else {
            return XCTFail("ожидали запись .files в буфер")
        }
        XCTAssertEqual(urls.count, 1)
        XCTAssertEqual(try Data(contentsOf: urls[0]), Data("hello".utf8), "содержимое — от успешного повтора")

        // Под stagingRoot должно быть ровно 2 подпапки: одна (почти пустая, без файла)
        // от первой сорвавшейся попытки, вторая — с настоящим файлом от второй. Если бы
        // повтор переиспользовал партию первой попытки, подпапка была бы одна.
        let batches = try FileManager.default.contentsOfDirectory(atPath: stagingRoot.path)
        XCTAssertEqual(batches.count, 2, "каждая попытка обязана создать свою партию: \(batches)")
    }

    // MARK: - Пустой буфер соседа

    func testEmptyPeerClipboardThrowsPeerEmptyAndLeavesClipboardUntouched() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.empty(seq: 1))
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .peerEmpty)
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
    }

    // MARK: - Нет живого соседа

    func testNoLivePeerThrowsNoPeerAndLeavesClipboardUntouched() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, _) = makeClient(config: config, prober: FakeProber(alive: false), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .noPeer)
        }
        XCTAssertEqual(fetcher.manifestCallCount, 0)
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
    }

    // MARK: - Ошибка транспорта инвалидирует кеш резолвера

    func testTransportErrorDuringManifestInvalidatesResolverCache() throws {
        let config = makeConfig()
        let prober = FakeProber(alive: true)
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .failure(HttpClientError.transport("соединение оборвалось"))
        let writer = FakeClipboard()
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        // Прогреваем кеш резолвера самостоятельно, чтобы убедиться именно в сбросе,
        // а не в том, что кеш просто не был заполнен.
        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)

        // Доказательство инвалидации — через наблюдаемое поведение `PeerResolver`
        // (см. `PeerResolverTests`): после сброса кеша `resolve()` обязан заново
        // опросить `prober`, а не тихо отдать прежний адрес. Гасим адрес и
        // проверяем, что резолвер это заметит — значит, кеш действительно пуст.
        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "после ошибки транспорта кеш должен быть сброшен, иначе резолвер отдал бы старый адрес не проверяя")
    }

    /// I9 (находка финального ревью): `fetchManifest` раньше ловил только
    /// `HttpClientError` — совсем битый или самопротиворечивый JSON от соседа
    /// (`{"kind":"weird"}`, `"totalSize":"5"` вместо числа) даёт `DecodingError`,
    /// который не оборачивается в `PullError` и, главное, не сбрасывает кеш
    /// резолвера. `FakeFetcher.manifestResult` — `Result<Manifest, Error>`, поэтому
    /// сюда можно подставить ЛЮБОЙ тип ошибки, не только `HttpClientError`.
    func testNonHttpClientErrorDuringManifestParsingIsTreatedAsTransportAndInvalidatesCache() throws {
        struct FakeDecodingFailure: Error, CustomStringConvertible {
            var description: String { "притворная поломка JSONDecoder" }
        }

        let config = makeConfig()
        let prober = FakeProber(alive: true)
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .failure(FakeDecodingFailure())
        let writer = FakeClipboard()
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2", "warm resolve")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)

        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(),
                      "неожиданный тип ошибки разбора манифеста тоже обязан сбрасывать кеш резолвера")
    }

    func testTransportErrorDuringBlobFetchInvalidatesResolverCache() throws {
        let config = makeConfig()
        let prober = FakeProber(alive: true)
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.image(pngSize: 3, seq: 2))
        fetcher.blobResults[0] = .failure(HttpClientError.transport("соединение оборвалось"))
        let writer = FakeClipboard()
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)

        // Тот же приём, что и для ошибки на манифесте: гасим адрес и убеждаемся,
        // что резолвер её действительно перепроверяет, а не отдаёт кеш.
        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "ошибка транспорта при скачивании блоба тоже обязана сбрасывать кеш резолвера")
    }

    // MARK: - Ruling 1: манифест соседа не проверяется на межполевые инварианты
    //
    // Самопротиворечивый манифест — не гонка с соседским буфером (та ловится через
    // 409 и обрабатывается как .changedMidTransfer без сброса кеша). Это дефект
    // кодировщика на той стороне либо подделка: повтор тому же соседу воспроизведёт
    // то же самое, поэтому здесь всегда .transport(...) и всегда resolver.invalidate().

    func testManifestWithImageKindButNoBlobsIsTreatedAsCorruptedTransport() throws {
        // `Manifest.decode` разбирает `{"kind":"image","seq":1}` без `blobs` совершенно
        // успешно — PullClient обязан отловить это сам, а не писать пустоту в буфер и не
        // выглядеть успехом.
        let raw = Data(#"{"kind":"image","seq":1}"#.utf8)
        let manifest = try Manifest.decode(raw)
        XCTAssertNil(manifest.blobs) // подтверждаем предпосылку ревью — decode не падает

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let prober = FakeProber(alive: true)
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
        XCTAssertEqual(fetcher.blobCallIndexes, [], "блоб не должен запрашиваться для сорванного манифеста")

        // Самопротиворечивый манифест сбрасывает кеш резолвера наравне с прочими
        // ошибками транспорта — доказываем через наблюдаемое поведение PeerResolver.
        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "манифест без обязательных blobs обязан сбрасывать кеш резолвера")
    }

    func testManifestWithTextKindButNoTextIsTreatedAsCorruptedTransport() throws {
        let raw = Data(#"{"kind":"text","seq":1}"#.utf8)
        let manifest = try Manifest.decode(raw)
        XCTAssertNil(manifest.text)

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
    }

    func testManifestWithFilesKindButEmptyBlobsIsTreatedAsCorruptedTransport() throws {
        let raw = Data(#"{"kind":"files","seq":1,"blobs":[]}"#.utf8)
        let manifest = try Manifest.decode(raw)

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
    }

    // MARK: - Ревью раунда 2: totalSize не может быть числом, которое сосед сам себе назначает

    func testManifestWithTotalSizeMismatchingBlobsSumIsTreatedAsCorruptedTransport() throws {
        // Сосед мог бы прислать заведомо маленький totalSize при огромных blobs —
        // прежде проверка размера (`manifest.totalSize ?? 0 > maxBytes`) доверяла этому
        // числу напрямую и тривиально проходила. Расхождение totalSize с суммой по
        // blobs — это тот же класс дефекта, что и отсутствующие blobs (Ruling 1):
        // самопротиворечивый манифест, а не гонка с соседским буфером.
        let raw = Data(#"{"kind":"files","seq":1,"totalSize":1,"blobs":[{"i":0,"rel":"big.bin","size":999999999,"mime":null}]}"#.utf8)
        let manifest = try Manifest.decode(raw)
        XCTAssertEqual(manifest.totalSize, 1) // decode берёт присланное число как есть, без проверки

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let prober = FakeProber(alive: true)
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(fetcher.blobCallIndexes, [],
                       "загрузка не должна начинаться для манифеста с расходящимся totalSize")

        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "расхождение totalSize с суммой blobs обязано сбрасывать кеш резолвера")
    }

    func testComputedTotalSizeFromBlobsGovernsTooLargeEvenWhenManifestOmitsTotalSize() throws {
        // Симметричная проверка: даже когда манифест вовсе не содержит totalSize (а не
        // содержит неверный), лимит maxBytes обязан сработать по сумме, посчитанной
        // нами по blobs, а не тривиально пройти из-за `manifest.totalSize ?? 0 == 0`.
        let raw = Data(#"{"kind":"files","seq":1,"blobs":[{"i":0,"rel":"big.bin","size":999999999,"mime":null}]}"#.utf8)
        let manifest = try Manifest.decode(raw)
        XCTAssertNil(manifest.totalSize) // подтверждаем предпосылку — поле в JSON отсутствует

        let config = makeConfig(maxBytes: 10)
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .tooLarge(totalSize: 999_999_999, maxBytes: 10))
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(fetcher.blobCallIndexes, [], "загрузка не должна начинаться при превышении лимита")
    }

    // MARK: - Ревью раунда 2: фактический размер блоба сверяется с обещанным в манифесте

    func testFileArrivingWithWrongSizeIsTreatedAsCorruptedTransport() throws {
        // Сосед может честно объявить totalSize, но прислать по конкретному блобу не
        // то количество байт, что обещано в blob.size — Content-Length одного ответа
        // никак не связан с манифестом. Ловим это после записи файла на диск.
        let config = makeConfig()
        let fetcher = FakeFetcher()
        let blobs = [BlobRef(i: 0, rel: "a.txt", size: 5)]
        fetcher.manifestResult = .success(.files(blobs, seq: 1))
        fetcher.blobResults[0] = .success(Data("hello world".utf8)) // 11 байт вместо заявленных 5
        let writer = FakeClipboard()
        let prober = FakeProber(alive: true)
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)

        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "файл не того размера обязан сбрасывать кеш резолвера")
    }

    func testImageArrivingWithWrongSizeIsTreatedAsCorruptedTransport() throws {
        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.image(pngSize: 3, seq: 1))
        fetcher.blobResults[0] = .success(Data([1, 2, 3, 4, 5])) // 5 байт вместо заявленных 3
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
    }

    // MARK: - Ревью раунда 3: переполнение суммы по blobs не должно ронять процесс

    func testTwoBlobsAtIntMaxDoNotCrashAndAreTreatedAsCorruptedTransport() throws {
        // До починки задачи 11 сумма `blob.size` считалась наивным `+`, который в Swift
        // при переполнении не заворачивается, а падает фатальной ошибкой — и это
        // падение достижимо ОДНИМ манифестом, ещё до единого HTTP-запроса за блобом
        // (тот же класс дефекта, что `Content-Length: -1` в парсере задачи 6). Тест
        // фиксирует, что теперь переполнение ловится как испорченный манифест, а не
        // роняет процесс.
        let raw = Data("""
        {"kind":"files","seq":1,"blobs":[
            {"i":0,"rel":"a.bin","size":9223372036854775807,"mime":null},
            {"i":1,"rel":"b.bin","size":9223372036854775807,"mime":null}
        ]}
        """.utf8)
        let manifest = try Manifest.decode(raw)

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let prober = FakeProber(alive: true)
        let (client, resolver) = makeClient(config: config, prober: prober, fetcher: fetcher, writer: writer)

        XCTAssertEqual(resolver.resolve(), "10.0.0.2")

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(fetcher.blobCallIndexes, [], "переполнение обязано отсекаться до единого запроса за блобом")

        prober.setAlive(false)
        XCTAssertNil(resolver.resolve(), "переполнение суммы обязано сбрасывать кеш резолвера")
    }

    func testBlobWithNegativeSizeIsTreatedAsCorruptedTransport() throws {
        // Отрицательный size не роняет процесс, но незаметно занижает сумму и обходит
        // лимит maxBytes — отклоняем его так же, как переполнение.
        let raw = Data(#"{"kind":"files","seq":1,"blobs":[{"i":0,"rel":"a.bin","size":-1,"mime":null}]}"#.utf8)
        let manifest = try Manifest.decode(raw)

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(manifest)
        let writer = FakeClipboard()
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            guard case .transport = error as? PullError else {
                return XCTFail("ожидали .transport, получили \(error)")
            }
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(fetcher.blobCallIndexes, [], "отрицательный size обязан отсекаться до запроса за блобом")
    }

    // MARK: - Ревью раунда 3: лимит maxBytes действует и на текст

    func testTextLongerThanMaxBytesThrowsTooLargeAndLeavesClipboardUntouched() throws {
        // Текст, в отличие от файлов, целиком приезжает в теле манифеста и уже лежит в
        // памяти к моменту проверки — тот же лимит maxBytes обязан его гейтить, а не
        // пропускать безусловно.
        let text = "это заведомо длинный текст для проверки лимита maxBytes"
        XCTAssertGreaterThan(text.utf8.count, 20)

        let config = makeConfig(maxBytes: 20)
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.text(text, seq: 1))
        let writer = FakeClipboard()
        writer.content = .text("прежнее содержимое")
        let (client, _) = makeClient(config: config, prober: FakeProber(), fetcher: fetcher, writer: writer)

        XCTAssertThrowsError(try client.pull()) { error in
            XCTAssertEqual(error as? PullError, .tooLarge(totalSize: text.utf8.count, maxBytes: 20))
        }
        XCTAssertTrue(writer.written.isEmpty)
        XCTAssertEqual(writer.content, .text("прежнее содержимое"))
    }

    // MARK: - Ревью раунда 2: отказ уборки не должен превращать успех в ошибку

    func testSuccessfulPullSucceedsEvenWhenStagingCleanupFails() throws {
        // Уборка — housekeeping вокруг уже состоявшегося успеха. Ломаем cleanup()
        // по-настоящему, без фейков: подсовываем staging.root, который существует, но
        // является обычным файлом, а не папкой — `contentsOfDirectory(at:)` бросит
        // реальную ошибку файловой системы.
        let brokenRoot = stagingRoot.appendingPathComponent("not-a-directory")
        try Data("я файл, а не папка".utf8).write(to: brokenRoot)
        let staging = Staging(root: brokenRoot)

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.text("успех несмотря на грязную уборку", seq: 1))
        let writer = FakeClipboard()
        let resolver = PeerResolver(config: config, prober: FakeProber())
        let client = PullClient(config: config, resolver: resolver, fetcher: fetcher, staging: staging, writer: writer)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .text)
        XCTAssertEqual(writer.content, .text("успех несмотря на грязную уборку"))
    }

    // MARK: - staging.cleanup() вызывается только на успехе

    func testSuccessfulPullTriggersStagingCleanup() throws {
        // Создаём избыточную партию заранее (все — в пределах одного короткого окна,
        // чтобы правило "старше 7 дней" не сработало само по себе) и проверяем, что
        // успешный pull() вызывает staging.cleanup() — уборка держит только
        // keepBatches последних партий.
        var current = Date(timeIntervalSince1970: 0)
        let staging = Staging(root: stagingRoot, now: { current })
        var oldBatches: [StagingBatch] = []
        for i in 0..<(Staging.keepBatches + 1) {
            current = Date(timeIntervalSince1970: TimeInterval(i * 60))
            oldBatches.append(try staging.newBatch())
        }

        let config = makeConfig()
        let fetcher = FakeFetcher()
        fetcher.manifestResult = .success(.text("x", seq: 1))
        let writer = FakeClipboard()
        let resolver = PeerResolver(config: config, prober: FakeProber())
        let client = PullClient(config: config, resolver: resolver, fetcher: fetcher, staging: staging, writer: writer)

        _ = try client.pull()

        let remaining = try FileManager.default.contentsOfDirectory(atPath: stagingRoot.path)
        XCTAssertEqual(remaining.count, Staging.keepBatches)
        XCTAssertFalse(FileManager.default.fileExists(atPath: oldBatches[0].root.path),
                        "самая старая избыточная партия должна быть убрана после успешного pull()")
    }
}

// MARK: - Слой 2: сквозной тест «ядро против ядра»

/// Единственное место во всём проекте, где протокол проверяется целиком: настоящий
/// `HttpServer` (задача 7) на `port: 0` с одним `FakeClipboard` в роли соседа, и
/// `PullClient` с настоящим `NwHttpClient` (задача 8), пишущий во второй, независимый
/// `FakeClipboard`. Каждый тест поднимает свой сервер — та же изоляция, что и в
/// `HttpServerTests`/`HttpClientTests`.
final class PullClientEndToEndTests: XCTestCase {
    private var stagingRoot: URL!
    private var filesRoot: URL!
    private let peerClipboard = FakeClipboard()
    private var server: HttpServer!

    override func setUpWithError() throws {
        stagingRoot = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-pullclient-e2e-staging-\(UUID().uuidString)")
        filesRoot = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-pullclient-e2e-files-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: stagingRoot, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: filesRoot, withIntermediateDirectories: true)

        let config = Config(port: 0, token: testToken, peers: ["127.0.0.1"])
        let store = SnapshotStore(reader: peerClipboard)
        // Наш `PullClient` никогда не вызывает `POST /pull` — он забирает манифест и
        // блобы через `GET /clip`/`GET /clip/blob/*`. Замыкание тут — просто заглушка,
        // требуемая сигнатурой `HttpServer.init`.
        server = HttpServer(config: config, snapshots: store, hostName: "peer-mac",
                             pull: { PullResult(kind: .empty, fileCount: 0, bytes: 0) })
        try server.start()
    }

    override func tearDownWithError() throws {
        server.stop()
        try? FileManager.default.removeItem(at: stagingRoot)
        try? FileManager.default.removeItem(at: filesRoot)
    }

    private func makePullClient(writer: ClipboardWriting) -> PullClient {
        let config = Config(port: server.boundPort, token: testToken, peers: ["127.0.0.1"])
        let httpClient = NwHttpClient(timeout: 5)
        let resolver = PeerResolver(config: config, prober: httpClient)
        let staging = Staging(root: stagingRoot)
        return PullClient(config: config, resolver: resolver, fetcher: httpClient,
                           staging: staging, writer: writer)
    }

    func testEndToEndTextTransfer() throws {
        peerClipboard.content = .text("сквозной текст через настоящий сервер и клиент")
        let localClipboard = FakeClipboard()
        let client = makePullClient(writer: localClipboard)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .text)
        XCTAssertEqual(result.fileCount, 0)
        XCTAssertEqual(localClipboard.content, .text("сквозной текст через настоящий сервер и клиент"))
    }

    func testEndToEndImageTransfer() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] + (0..<5000).map { UInt8($0 % 256) })
        peerClipboard.content = .image(png)
        let localClipboard = FakeClipboard()
        let client = makePullClient(writer: localClipboard)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .image)
        XCTAssertEqual(result.bytes, png.count)
        XCTAssertEqual(localClipboard.content, .image(png))
    }

    func testEndToEndTwoFilesTransfer() throws {
        let fileA = filesRoot.appendingPathComponent("report.txt")
        let fileB = filesRoot.appendingPathComponent("notes.md")
        try Data("годовой отчёт".utf8).write(to: fileA)
        try Data("# заметки".utf8).write(to: fileB)
        peerClipboard.content = .files([fileA, fileB])

        let localClipboard = FakeClipboard()
        let client = makePullClient(writer: localClipboard)

        let result = try client.pull()

        XCTAssertEqual(result.kind, .files)
        XCTAssertEqual(result.fileCount, 2)

        guard case .files(let urls) = localClipboard.content else {
            return XCTFail("ожидали .files в локальном буфере")
        }
        XCTAssertEqual(urls.count, 2)
        let contents = try urls.map { try Data(contentsOf: $0) }
        XCTAssertTrue(contents.contains(Data("годовой отчёт".utf8)))
        XCTAssertTrue(contents.contains(Data("# заметки".utf8)))
        // Партия стейджинга — не исходная папка: локальные пути должны указывать
        // внутрь `stagingRoot`, а не на `filesRoot` соседа.
        for url in urls {
            XCTAssertTrue(url.path.hasPrefix(stagingRoot.resolvingSymlinksInPath().path))
        }
    }
}
