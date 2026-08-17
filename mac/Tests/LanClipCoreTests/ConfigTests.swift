import XCTest
@testable import LanClipCore

final class ConfigTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lanclip-config-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func write(_ json: String) throws -> URL {
        let url = directory.appendingPathComponent("config.json")
        try Data(json.utf8).write(to: url)
        return url
    }

    func testGeneratesThirtyTwoHexToken() {
        let token = Config.generateToken()
        XCTAssertEqual(token.count, 32)
        XCTAssertTrue(token.allSatisfy { $0.isHexDigit && !$0.isUppercase })
        XCTAssertNotEqual(token, Config.generateToken())
    }

    func testCreatesFileWhenMissing() throws {
        let url = directory.appendingPathComponent("config.json")
        let config = try Config.load(at: url)

        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        XCTAssertEqual(config.port, 8899)
        XCTAssertEqual(config.maxBytes, 536_870_912)
        XCTAssertTrue(config.autoPaste)
        XCTAssertEqual(config.token.count, 32)
        XCTAssertTrue(config.peers.isEmpty)

        let permissions = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.int16Value, 0o600)
    }

    func testAppliesDefaultsForMissingKeys() throws {
        let url = try write(#"{"token":"abc","peers":["pc"]}"#)
        let config = try Config.load(at: url)
        XCTAssertEqual(config.port, 8899)
        XCTAssertEqual(config.maxBytes, 536_870_912)
        XCTAssertTrue(config.autoPaste)
        XCTAssertEqual(config.peers, ["pc"])
    }

    func testReadsExplicitValues() throws {
        let url = try write(#"{"port":9001,"token":"t","peers":["a","b"],"maxBytes":1024,"autoPaste":false}"#)
        let config = try Config.load(at: url)
        XCTAssertEqual(config, Config(port: 9001, token: "t", peers: ["a", "b"],
                                      maxBytes: 1024, autoPaste: false))
    }

    func testRejectsMalformedJson() throws {
        let url = try write("{ не json")
        XCTAssertThrowsError(try Config.load(at: url)) { error in
            guard case ConfigError.malformed = error else {
                return XCTFail("ожидалась malformed, получено \(error)")
            }
        }
    }

    func testValidateRejectsBadValues() {
        XCTAssertThrowsError(try Config(port: 0, token: "t", peers: ["pc"]).validate()) { error in
            XCTAssertEqual(error as? ConfigError, .invalidPort(0))
        }
        XCTAssertThrowsError(try Config(token: "", peers: ["pc"]).validate()) { error in
            XCTAssertEqual(error as? ConfigError, .emptyToken)
        }
        XCTAssertThrowsError(try Config(token: "t", peers: []).validate()) { error in
            XCTAssertEqual(error as? ConfigError, .noPeers)
        }
        XCTAssertThrowsError(try Config(token: "t", peers: ["pc"], maxBytes: 0).validate()) { error in
            XCTAssertEqual(error as? ConfigError, .invalidMaxBytes(0))
        }
    }

    func testValidateAcceptsGoodConfig() throws {
        try Config(token: "t", peers: ["pc"]).validate()
    }

    func testPreservesRestrictivePermissionsOnUpdate() throws {
        let url = directory.appendingPathComponent("config.json")
        // Создаём файл с дефолтными правами (обычно 0644)
        try Data("{}".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)

        // Перезаписываем конфиг
        try Config(token: "secret", peers: ["pc"]).write(to: url)

        // Проверяем, что права остались 0600
        let permissions = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.int16Value, 0o600)
    }
}
