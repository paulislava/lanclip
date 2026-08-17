import XCTest
@testable import LanClipCore

final class ManifestTests: XCTestCase {
    private func json(_ manifest: Manifest) throws -> [String: Any] {
        let data = try manifest.encoded()
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testEmptyOmitsOptionalKeys() throws {
        let object = try json(.empty(seq: 41))
        XCTAssertEqual(object["kind"] as? String, "empty")
        XCTAssertEqual(object["seq"] as? Int, 41)
        XCTAssertNil(object["text"])
        XCTAssertNil(object["blobs"])
        XCTAssertNil(object["totalSize"])
    }

    func testTextCarriesTextOnly() throws {
        let object = try json(.text("привет 👋", seq: 42))
        XCTAssertEqual(object["kind"] as? String, "text")
        XCTAssertEqual(object["text"] as? String, "привет 👋")
        XCTAssertNil(object["blobs"])
        XCTAssertNil(object["totalSize"])
    }

    func testImageDescribesSinglePngBlob() throws {
        let manifest = Manifest.image(pngSize: 48213, seq: 43)
        XCTAssertEqual(manifest.totalSize, 48213)
        let blob = try XCTUnwrap(manifest.blobs?.first)
        XCTAssertEqual(blob.i, 0)
        XCTAssertEqual(blob.rel, "clip.png")
        XCTAssertEqual(blob.size, 48213)
        XCTAssertEqual(blob.mime, "image/png")
    }

    func testFilesSumsTotalSize() {
        let manifest = Manifest.files([
            BlobRef(i: 0, rel: "отчёт.pdf", size: 91234),
            BlobRef(i: 1, rel: "img/a.png", size: 5120),
        ], seq: 44)
        XCTAssertEqual(manifest.totalSize, 96354)
        XCTAssertEqual(manifest.kind, .files)
    }

    func testRoundTripsThroughJson() throws {
        let original = Manifest.files([BlobRef(i: 0, rel: "папка/файл.txt", size: 7)], seq: 9)
        XCTAssertEqual(try Manifest.decode(try original.encoded()), original)
    }

    func testDecodesManifestFromForeignAgent() throws {
        let raw = Data(#"{"kind":"text","seq":5,"text":"hi"}"#.utf8)
        let manifest = try Manifest.decode(raw)
        XCTAssertEqual(manifest.kind, .text)
        XCTAssertEqual(manifest.text, "hi")
        XCTAssertNil(manifest.blobs)
    }

    func testRejectsUnknownKind() {
        let raw = Data(#"{"kind":"video","seq":1}"#.utf8)
        XCTAssertThrowsError(try Manifest.decode(raw))
    }
}
