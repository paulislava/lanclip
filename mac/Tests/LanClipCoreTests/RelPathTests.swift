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

    // Длина компонента и итогового пути считается в байтах UTF-8, а не в
    // графемных кластерах (Swift .count) и не в кодовых единицах UTF-16
    // (C# .Length) — иначе платформы расходятся на одном и том же входе:
    // "👋" — 1 графема, 2 code unit-а UTF-16, но 4 байта UTF-8.

    func testAcceptsEmojiComponentAtExactByteLimit() {
        // 37 * 4 = 148 байт + "ab" (2 байта) = 150 байт — ровно на границе.
        let component = String(repeating: "👋", count: 37) + "ab"
        XCTAssertEqual(RelPath.normalize(component), component)
    }

    func testRejectsEmojiComponentOneByteOverLimit() {
        // 38 эмодзи = 152 байта UTF-8, но лишь 76 code unit-ов UTF-16 —
        // старая Swift-реализация (count в графемах, 38 <= 150) это принимала.
        let component = String(repeating: "👋", count: 38)
        XCTAssertNil(RelPath.normalize(component))
    }

    func testRejectsEmojiComponentReportedByReview() {
        // Ровно тот вход, на котором платформы расходились: 76 эмодзи —
        // 76 графем (Swift .count <= 150 — принимал), но 152 code unit-а
        // UTF-16 (C# .Length > 150 — отвергал). В байтах UTF-8 это 304 байта:
        // обе стороны теперь одинаково отвергают.
        let component = String(repeating: "👋", count: 76)
        XCTAssertNil(RelPath.normalize(component))
    }

    func testRejectsCyrillicComponentExceedingByteLimit() {
        // 80 кириллических букв — 80 графем и 80 code unit-ов UTF-16 (обе
        // старые реализации это принимали), но 160 байт UTF-8 — отказ.
        // Осознанное ужесточение, не регрессия.
        let component = String(repeating: "а", count: 80)
        XCTAssertNil(RelPath.normalize(component))
    }
}
