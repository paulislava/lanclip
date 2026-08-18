import XCTest
@testable import LanClipCore

final class SnapshotTests: XCTestCase {
    private var directory: URL!
    private let clipboard = FakeClipboard()

    override func setUpWithError() throws {
        directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-snapshot-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    /// I6 финального ревью изменил `SnapshotStore.blob(index:seq:)` так, чтобы блобы
    /// файлов отдавались как `.file(url:size:)` (читать с диска потоком должен
    /// вызывающий, а не `SnapshotStore`), а не готовым `Data` — это единственная
    /// точка в тестах, где обе формы приводятся к байтам для сравнения.
    private func loadedBytes(_ payload: BlobPayload?) throws -> Data? {
        guard let payload else { return nil }
        switch payload {
        case .data(let data):
            return data
        case .file(let url, let size):
            let data = try Data(contentsOf: url)
            XCTAssertEqual(data.count, size, "заявленный размер совпадает с реальными байтами на диске")
            return data
        }
    }

    private func makeFile(_ relative: String, _ body: String) throws -> URL {
        let url = directory.appendingPathComponent(relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                               withIntermediateDirectories: true)
        try Data(body.utf8).write(to: url)
        return url
    }

    func testEmptyClipboardYieldsEmptyManifest() throws {
        let store = SnapshotStore(reader: clipboard)
        XCTAssertEqual(try store.current().manifest.kind, .empty)
    }

    func testTextSnapshotCarriesSeqFromChangeCount() throws {
        clipboard.content = .text("привет")
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()
        XCTAssertEqual(snapshot.manifest.kind, .text)
        XCTAssertEqual(snapshot.manifest.text, "привет")
        XCTAssertEqual(snapshot.manifest.seq, clipboard.changeCount())
    }

    func testImageBlobReturnsPngBytes() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])
        clipboard.content = .image(png)
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()
        XCTAssertEqual(snapshot.manifest.totalSize, png.count)
        XCTAssertEqual(try loadedBytes(try store.blob(index: 0, seq: snapshot.manifest.seq)), png)
    }

    func testSingleFileUsesBareName() throws {
        let file = try makeFile("отчёт.pdf", "данные")
        clipboard.content = .files([file])
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()
        XCTAssertEqual(snapshot.manifest.blobs?.map { $0.rel }, ["отчёт.pdf"])
        XCTAssertEqual(try loadedBytes(try store.blob(index: 0, seq: snapshot.manifest.seq)), Data("данные".utf8))
    }

    /// I6: файловые блобы обязаны отдаваться как `.file(url:size:)` — читаемые
    /// потоком с диска вызывающей стороной, а не как готовый `Data` в памяти.
    /// Восьмигигабайтное видео в буфере не должно попадать в память процесса
    /// целиком просто потому, что кто-то запросил его блоб.
    func testFileBlobIsReturnedAsFilePayloadNotLoadedIntoMemory() throws {
        let file = try makeFile("видео.mov", "содержимое файла")
        clipboard.content = .files([file])
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()

        guard let payload = try store.blob(index: 0, seq: snapshot.manifest.seq) else {
            return XCTFail("ожидался payload")
        }
        guard case .file(let url, let size) = payload else {
            return XCTFail("файловый блоб обязан приходить как .file, а не .data")
        }
        XCTAssertEqual(url.lastPathComponent, "видео.mov")
        XCTAssertEqual(size, Data("содержимое файла".utf8).count)
    }

    /// Симметричный контроль: картинка уже лежит в памяти после чтения буфера
    /// (`imagePNG`), поэтому её блоб обязан оставаться `.data`, а не начинать
    /// перечитываться с несуществующего пути на диске.
    func testImageBlobIsReturnedAsDataPayloadNotFile() throws {
        let png = Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])
        clipboard.content = .image(png)
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()

        guard let payload = try store.blob(index: 0, seq: snapshot.manifest.seq) else {
            return XCTFail("ожидался payload")
        }
        guard case .data(let data) = payload else {
            return XCTFail("блоб картинки обязан оставаться .data, а не превращаться в .file")
        }
        XCTAssertEqual(data, png)
    }

    func testFolderIsWalkedRecursivelyWithRelativePaths() throws {
        _ = try makeFile("папка/a.txt", "a")
        _ = try makeFile("папка/вложенная/b.txt", "bb")
        clipboard.content = .files([directory.appendingPathComponent("папка")])
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()
        XCTAssertEqual(snapshot.manifest.blobs?.map { $0.rel }.sorted(),
                       ["папка/a.txt", "папка/вложенная/b.txt"])
        XCTAssertEqual(snapshot.manifest.totalSize, 3)
    }

    func testSymlinkToFileOutsideFolderReportsResolvedSize() throws {
        let target = try makeFile("вне/большой.bin", String(repeating: "x", count: 42))
        _ = try makeFile("папка/обычный.txt", "a")
        let link = directory.appendingPathComponent("папка/ссылка.bin")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)

        clipboard.content = .files([directory.appendingPathComponent("папка")])
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()

        guard let blob = snapshot.manifest.blobs?.first(where: { $0.rel == "папка/ссылка.bin" }) else {
            return XCTFail("missing symlink blob")
        }
        let bytes = try loadedBytes(try store.blob(index: blob.i, seq: snapshot.manifest.seq))
        XCTAssertEqual(blob.size, bytes?.count)
        XCTAssertEqual(bytes?.count, 42)
    }

    func testStaleSeqIsRejected() throws {
        clipboard.content = .text("первый")
        let store = SnapshotStore(reader: clipboard)
        let stale = try store.current().manifest.seq
        clipboard.content = .image(Data([1, 2, 3]))
        _ = try store.current()
        XCTAssertThrowsError(try store.blob(index: 0, seq: stale)) { error in
            XCTAssertEqual(error as? SnapshotError, .staleSeq)
        }
    }

    func testOutOfRangeIndexReturnsNil() throws {
        clipboard.content = .image(Data([1]))
        let store = SnapshotStore(reader: clipboard)
        let seq = try store.current().manifest.seq
        XCTAssertNil(try store.blob(index: 5, seq: seq))
    }
}
