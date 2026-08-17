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
        XCTAssertEqual(try store.blob(index: 0, seq: snapshot.manifest.seq), png)
    }

    func testSingleFileUsesBareName() throws {
        let file = try makeFile("отчёт.pdf", "данные")
        clipboard.content = .files([file])
        let store = SnapshotStore(reader: clipboard)
        let snapshot = try store.current()
        XCTAssertEqual(snapshot.manifest.blobs?.map { $0.rel }, ["отчёт.pdf"])
        XCTAssertEqual(try store.blob(index: 0, seq: snapshot.manifest.seq), Data("данные".utf8))
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
