import XCTest
@testable import LanClipCore

final class RelPathTests: XCTestCase {
    func testKeepsSimplePath() {
        XCTAssertEqual(RelPath.normalize("img/a.png"), "img/a.png")
    }

    func testConvertsBackslashesAndCollapsesSeparators() {
        XCTAssertEqual(RelPath.normalize("img\\\\sub//a.png"), "img/sub/a.png")
    }

    func testRejectsTraversal() {
        XCTAssertNil(RelPath.normalize("../secret"))
        XCTAssertNil(RelPath.normalize("img/../../secret"))
    }

    func testStripsAbsolutePrefix() {
        XCTAssertEqual(RelPath.normalize("/etc/passwd"), "etc/passwd")
    }

    func testSanitizesDriveLetterAndIllegalChars() {
        XCTAssertEqual(RelPath.normalize("C:\\\\Users\\\\a?b.txt"), "C_/Users/a_b.txt")
    }

    func testKeepsCyrillicAndSpaces() {
        XCTAssertEqual(RelPath.normalize("папка/мой отчёт.pdf"), "папка/мой отчёт.pdf")
    }

    func testTrimsTrailingDotsAndSpaces() {
        XCTAssertEqual(RelPath.normalize("name. ."), "name")
    }

    func testSuffixesReservedWindowsNames() {
        XCTAssertEqual(RelPath.normalize("NUL"), "NUL_")
        XCTAssertEqual(RelPath.normalize("com1.txt"), "com1.txt_")
    }

    func testRejectsReservedNameExceedingLimitAfterSuffix() {
        let overLimit = "nul." + String(repeating: "a", count: 146)
        XCTAssertNil(RelPath.normalize(overLimit))
    }

    func testAcceptsReservedNameWithinLimitAfterSuffix() {
        let withinLimit = "nul." + String(repeating: "a", count: 145)
        XCTAssertEqual(RelPath.normalize(withinLimit), withinLimit + "_")
    }

    func testRejectsOverlongComponentAndPath() {
        XCTAssertNil(RelPath.normalize(String(repeating: "a", count: 151)))
        let deep = (0..<10).map { _ in String(repeating: "b", count: 45) }.joined(separator: "/")
        XCTAssertNil(RelPath.normalize(deep))
    }

    func testRejectsEmptyResult() {
        XCTAssertNil(RelPath.normalize(""))
        XCTAssertNil(RelPath.normalize("./././"))
    }
}
