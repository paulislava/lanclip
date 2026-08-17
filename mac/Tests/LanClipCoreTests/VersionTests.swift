import XCTest
@testable import LanClipCore

final class VersionTests: XCTestCase {
    func testProtocolVersionIsOne() {
        XCTAssertEqual(protocolVersion, 1)
    }
}
