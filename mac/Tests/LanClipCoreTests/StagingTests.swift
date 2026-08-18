import XCTest
@testable import LanClipCore

final class StagingTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-staging-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func fixedDate() -> Date {
        // 2026-08-18 12:34:56 UTC — фиксированная дата, не зависящая от текущего времени/локали.
        var components = DateComponents()
        components.year = 2026; components.month = 8; components.day = 18
        components.hour = 12; components.minute = 34; components.second = 56
        components.timeZone = TimeZone(identifier: "UTC")
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar.date(from: components)!
    }

    // MARK: - stamp()

    func testStampFormatsFixedDateAsYyyyMMddHHmmss() {
        XCTAssertEqual(Staging.stamp(fixedDate()), "20260818-123456")
    }

    // MARK: - newBatch()

    func testNewBatchCreatesFolderWithStamp() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let batch = try staging.newBatch()

        XCTAssertEqual(batch.root.lastPathComponent, "20260818-123456")
        var isDirectory: ObjCBool = false
        XCTAssertTrue(FileManager.default.fileExists(atPath: batch.root.path, isDirectory: &isDirectory))
        XCTAssertTrue(isDirectory.boolValue)
    }

    func testTwoBatchesInSameSecondGetDistinctFolders() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let first = try staging.newBatch()
        let second = try staging.newBatch()

        XCTAssertNotEqual(first.root.path, second.root.path)
        XCTAssertEqual(first.root.lastPathComponent, "20260818-123456")
        XCTAssertEqual(second.root.lastPathComponent, "20260818-123456-2")

        var isDirectory: ObjCBool = false
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.root.path, isDirectory: &isDirectory))
        XCTAssertTrue(isDirectory.boolValue)
    }

    func testThirdBatchInSameSecondGetsSuffixThree() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        _ = try staging.newBatch()
        _ = try staging.newBatch()
        let third = try staging.newBatch()

        XCTAssertEqual(third.root.lastPathComponent, "20260818-123456-3")
    }

    // MARK: - destination(for:)

    func testDestinationCreatesIntermediateFolders() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let batch = try staging.newBatch()

        let destination = try batch.destination(for: "sub/folder/file.txt")

        XCTAssertEqual(destination.path, batch.root.appendingPathComponent("sub/folder/file.txt").path)
        var isDirectory: ObjCBool = false
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.deletingLastPathComponent().path,
                                                       isDirectory: &isDirectory))
        XCTAssertTrue(isDirectory.boolValue)
    }

    func testDestinationRejectsParentTraversal() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let batch = try staging.newBatch()

        XCTAssertThrowsError(try batch.destination(for: "../x")) { error in
            XCTAssertEqual(error as? StagingError, .unsafeRelativePath("../x"))
        }
    }

    func testDestinationRejectsAbsoluteEscapeAttempt() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let batch = try staging.newBatch()

        XCTAssertThrowsError(try batch.destination(for: "sub/../../../../etc/passwd"))
    }

    func testDestinationStaysWithinRootForPlainName() throws {
        let staging = Staging(root: root, now: { self.fixedDate() })
        let batch = try staging.newBatch()

        let destination = try batch.destination(for: "a.png")
        XCTAssertTrue(destination.standardized.path.hasPrefix(batch.root.standardized.path))
    }

    // MARK: - cleanup()

    func testCleanupRemovesBatchesOlderThanSevenDays() throws {
        var current = fixedDate()
        let staging = Staging(root: root, now: { current })

        let old = try staging.newBatch() // day 0
        current = fixedDate().addingTimeInterval(8 * 24 * 3600) // +8 days
        let fresh = try staging.newBatch()

        try staging.cleanup()

        XCTAssertFalse(FileManager.default.fileExists(atPath: old.root.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: fresh.root.path))
    }

    func testCleanupKeepsBatchExactlyAtSevenDayBoundary() throws {
        var current = fixedDate()
        let staging = Staging(root: root, now: { current })

        let boundary = try staging.newBatch() // day 0
        current = fixedDate().addingTimeInterval(7 * 24 * 3600) // exactly +7 days
        _ = try staging.newBatch()

        try staging.cleanup()

        XCTAssertTrue(FileManager.default.fileExists(atPath: boundary.root.path),
                      "партия ровно на границе 7 дней не должна удаляться")
    }

    func testCleanupKeepsExactlyTwentyMostRecentOfTwentyFive() throws {
        var current = fixedDate()
        let staging = Staging(root: root, now: { current })

        var batches: [StagingBatch] = []
        for i in 0..<25 {
            // Разносим партии по времени на минуту каждую — иначе все 25 создались бы
            // в одну секунду и порядок "последних 20" был бы не определён.
            current = fixedDate().addingTimeInterval(TimeInterval(i * 60))
            batches.append(try staging.newBatch())
        }

        try staging.cleanup()

        let remaining = try FileManager.default.contentsOfDirectory(atPath: root.path)
        XCTAssertEqual(remaining.count, 20, "должно остаться ровно 20 партий")

        // Первые 5 (самые старые) должны быть удалены, последние 20 — остаться.
        for i in 0..<5 {
            XCTAssertFalse(FileManager.default.fileExists(atPath: batches[i].root.path),
                            "партия \(i) должна быть удалена как избыточная")
        }
        for i in 5..<25 {
            XCTAssertTrue(FileManager.default.fileExists(atPath: batches[i].root.path),
                           "партия \(i) должна остаться среди последних 20")
        }
    }

    func testDefaultRootPointsToApplicationSupportIncoming() {
        let expected = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/lanclip/incoming")
        XCTAssertEqual(Staging.defaultRoot.path, expected.path)
    }
}
