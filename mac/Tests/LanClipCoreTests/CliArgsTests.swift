import XCTest
@testable import LanClipCore

final class CliArgsTests: XCTestCase {
    func testDefaultCommandIsServe() throws {
        let args = try CliArgs.parse([])
        XCTAssertEqual(args.command, "serve")
        XCTAssertEqual(args.configURL, Config.defaultURL)
    }

    func testConfigFlagAfterCommandIsRespected() throws {
        let args = try CliArgs.parse(["status", "--config", "/tmp/lanclip-test-config.json"])
        XCTAssertEqual(args.command, "status")
        XCTAssertEqual(args.configURL, URL(fileURLWithPath: "/tmp/lanclip-test-config.json"))
    }

    func testConfigFlagBeforeCommandIsRespected() throws {
        let args = try CliArgs.parse(["--config", "/tmp/lanclip-test-config.json", "pull"])
        XCTAssertEqual(args.command, "pull")
        XCTAssertEqual(args.configURL, URL(fileURLWithPath: "/tmp/lanclip-test-config.json"))
    }

    func testUnknownCommandThrows() {
        XCTAssertThrowsError(try CliArgs.parse(["bogus"])) { error in
            XCTAssertEqual(error as? CliArgsError, .unknownCommand("bogus"))
        }
    }

    func testHelpShortFlagIsRecognized() {
        XCTAssertThrowsError(try CliArgs.parse(["-h"])) { error in
            XCTAssertEqual(error as? CliArgsError, .helpRequested)
        }
    }

    func testHelpLongFlagIsRecognized() {
        XCTAssertThrowsError(try CliArgs.parse(["--help"])) { error in
            XCTAssertEqual(error as? CliArgsError, .helpRequested)
        }
    }

    func testMissingConfigValueThrows() {
        XCTAssertThrowsError(try CliArgs.parse(["--config"])) { error in
            XCTAssertEqual(error as? CliArgsError, .missingConfigValue)
        }
    }

    func testTwoPositionalArgumentsThrowUnknownCommand() {
        // Вторая позиционная лексема после уже распознанной команды — не другая
        // команда, а мусор: тоже должна вести к сообщению об использовании.
        XCTAssertThrowsError(try CliArgs.parse(["status", "get"])) { error in
            XCTAssertEqual(error as? CliArgsError, .unknownCommand("get"))
        }
    }

    func testAllFourCommandsAreAccepted() throws {
        for name in ["serve", "status", "get", "pull"] {
            let args = try CliArgs.parse([name])
            XCTAssertEqual(args.command, name)
        }
    }
}
