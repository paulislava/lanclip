import AppKit
import XCTest
@testable import LanClipCore

/// Тесты работают на отдельном именованном `NSPasteboard`, а не на `.general` —
/// системный буфер пользователя они не трогают ни на чтение, ни на запись.
final class MacPasteboardTests: XCTestCase {
    private var pasteboard: NSPasteboard!
    private var sut: MacPasteboard!
    private var directory: URL!

    override func setUpWithError() throws {
        pasteboard = NSPasteboard(name: NSPasteboard.Name("com.lanclip.tests.\(UUID().uuidString)"))
        pasteboard.clearContents()
        sut = MacPasteboard(pasteboard: pasteboard)

        directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-mac-pasteboard-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        pasteboard.clearContents()
        pasteboard.releaseGlobally()
        pasteboard = nil
        sut = nil
        try? FileManager.default.removeItem(at: directory)
    }

    // MARK: - Текст

    func testWriteAndReadCyrillicText() throws {
        try sut.write(.text("привет, мир"))
        XCTAssertEqual(try sut.read(), .text("привет, мир"))
    }

    // MARK: - Пустой буфер

    func testEmptyPasteboardYieldsEmpty() throws {
        XCTAssertEqual(try sut.read(), .empty)
    }

    func testWritingEmptyClearsPreviousContent() throws {
        try sut.write(.text("было"))
        try sut.write(.empty)
        XCTAssertEqual(try sut.read(), .empty)
    }

    // MARK: - changeCount

    func testChangeCountIncreasesAfterWrite() throws {
        let before = sut.changeCount()
        try sut.write(.text("x"))
        XCTAssertGreaterThan(sut.changeCount(), before)
    }

    // MARK: - Картинка (PNG)

    func testWriteAndReadPNGImage() throws {
        let png = try makeImageData(fileType: .png)
        try sut.write(.image(png))

        guard case .image(let data) = try sut.read() else {
            return XCTFail("ожидался .image")
        }
        XCTAssertEqual(data, png)
    }

    // MARK: - TIFF в буфере должен читаться как PNG

    func testTIFFOnPasteboardIsReadAsPNG() throws {
        let tiff = try makeImageData(fileType: .tiff)
        pasteboard.clearContents()
        pasteboard.setData(tiff, forType: .tiff)

        guard case .image(let data) = try sut.read() else {
            return XCTFail("ожидался .image")
        }
        // Настоящая сигнатура PNG (89 50 4E 47 0D 0A 1A 0A), а не переименованный TIFF.
        XCTAssertEqual(Array(data.prefix(8)), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        XCTAssertNotEqual(data, tiff)
    }

    // MARK: - Файлы

    func testWriteAndReadTwoFilesReturnsSamePaths() throws {
        let a = try makeFile("a.txt", "aaa")
        let b = try makeFile("вложенная/б.txt", "бб")
        try sut.write(.files([a, b]))

        guard case .files(let urls) = try sut.read() else {
            return XCTFail("ожидался .files")
        }
        XCTAssertEqual(Set(urls.map { $0.path }), Set([a.path, b.path]))
    }

    // MARK: - Легаси NSFilenamesPboardType для старых приложений

    func testWriteFilesAlsoDeclaresLegacyFilenamesPropertyList() throws {
        let a = try makeFile("a.txt", "aaa")
        let b = try makeFile("b.txt", "bbb")
        try sut.write(.files([a, b]))

        let legacyType = NSPasteboard.PasteboardType("NSFilenamesPboardType")
        guard let filenames = pasteboard.propertyList(forType: legacyType) as? [String] else {
            return XCTFail("ожидался property list строк по NSFilenamesPboardType")
        }
        XCTAssertEqual(filenames, [a.path, b.path])
    }

    // MARK: - Порядок определения типа: файлы побеждают текстовое представление

    func testFilesWinOverTextRepresentationWhenBothPresent() throws {
        let file = try makeFile("finder.txt", "содержимое")
        pasteboard.clearContents()
        // Так делает Finder: рядом с ссылкой на файл кладёт и её текстовое представление.
        pasteboard.writeObjects([file as NSURL])
        pasteboard.setString(file.path, forType: .string)

        guard case .files(let urls) = try sut.read() else {
            return XCTFail("файлы обязаны победить текстовое представление")
        }
        XCTAssertEqual(urls.map { $0.path }, [file.path])
    }

    // MARK: - http-ссылка — не файл

    func testHttpURLIsNotReadAsFiles() throws {
        pasteboard.clearContents()
        pasteboard.writeObjects([NSURL(string: "https://example.com")!])

        XCTAssertEqual(try sut.read(), .empty)
    }

    // MARK: - Помощники

    private enum TestError: Error { case encodingFailed }

    private func makeImageData(fileType: NSBitmapImageRep.FileType) throws -> Data {
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 2,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
            isPlanar: false, colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0
        ) else {
            throw TestError.encodingFailed
        }
        rep.setColor(.red, atX: 0, y: 0)
        rep.setColor(.blue, atX: 1, y: 1)
        guard let data = rep.representation(using: fileType, properties: [:]) else {
            throw TestError.encodingFailed
        }
        return data
    }

    private func makeFile(_ relative: String, _ body: String) throws -> URL {
        let url = directory.appendingPathComponent(relative)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        try Data(body.utf8).write(to: url)
        return url
    }
}
