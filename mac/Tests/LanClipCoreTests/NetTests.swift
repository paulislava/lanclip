import XCTest
@testable import LanClipCore

final class NetTests: XCTestCase {
    func testAcceptsPrivateRanges() {
        for address in ["192.168.1.184", "10.0.0.5", "172.16.3.9", "172.31.255.255",
                        "127.0.0.1", "::1", "fe80::1%en0"] {
            XCTAssertTrue(isPrivateAddress(address), address)
        }
    }

    func testRejectsPublicAddresses() {
        for address in ["83.222.27.227", "8.8.8.8", "172.32.0.1", "2606:4700::1111", ""] {
            XCTAssertFalse(isPrivateAddress(address), address)
        }
    }
}
