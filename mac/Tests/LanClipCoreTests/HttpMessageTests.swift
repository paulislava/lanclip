import XCTest
@testable import LanClipCore

final class HttpMessageTests: XCTestCase {
    func testParsesRequestLineHeadersAndQuery() throws {
        let raw = Data("GET /clip/blob/2?seq=44 HTTP/1.1\r\nHost: pc:8899\r\nX-Clip-Token: abc\r\n\r\n".utf8)
        let request = try parseHttpRequest(raw)
        XCTAssertEqual(request.method, "GET")
        XCTAssertEqual(request.path, "/clip/blob/2")
        XCTAssertEqual(request.query["seq"], "44")
        XCTAssertEqual(request.headers["x-clip-token"], "abc")
        XCTAssertTrue(request.body.isEmpty)
    }

    func testThrowsIncompleteUntilHeadersEnd() {
        XCTAssertThrowsError(try parseHttpRequest(Data("GET / HTTP/1.1\r\nHost: x".utf8))) { error in
            XCTAssertEqual(error as? HttpParseError, .incomplete)
        }
    }

    func testThrowsIncompleteUntilBodyArrives() {
        let raw = Data("POST /pull HTTP/1.1\r\nContent-Length: 5\r\n\r\nab".utf8)
        XCTAssertThrowsError(try parseHttpRequest(raw)) { error in
            XCTAssertEqual(error as? HttpParseError, .incomplete)
        }
    }

    func testReadsBodyOfDeclaredLength() throws {
        let raw = Data("POST /pull HTTP/1.1\r\nContent-Length: 5\r\n\r\nabcde".utf8)
        XCTAssertEqual(try parseHttpRequest(raw).body, Data("abcde".utf8))
    }

    func testRejectsGarbageRequestLine() {
        XCTAssertThrowsError(try parseHttpRequest(Data("ГАРБИДЖ\r\n\r\n".utf8))) { error in
            XCTAssertEqual(error as? HttpParseError, .malformed)
        }
    }

    func testResponseHeadCarriesContentLength() throws {
        let head = String(decoding: HttpResponse.json(200, Data("{}".utf8)).head(), as: UTF8.self)
        XCTAssertTrue(head.hasPrefix("HTTP/1.1 200 OK\r\n"))
        XCTAssertTrue(head.contains("Content-Length: 2\r\n"))
        XCTAssertTrue(head.contains("Content-Type: application/json; charset=utf-8\r\n"))
        XCTAssertTrue(head.hasSuffix("\r\n\r\n"))
    }

    func testEmptyResponseDeclaresZeroLength() {
        let head = String(decoding: HttpResponse.empty(401).head(), as: UTF8.self)
        XCTAssertTrue(head.hasPrefix("HTTP/1.1 401 Unauthorized\r\n"))
        XCTAssertTrue(head.contains("Content-Length: 0\r\n"))
    }

    func testParsesResponse() throws {
        let raw = Data("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc".utf8)
        let parsed = try parseHttpResponse(raw)
        XCTAssertEqual(parsed.status, 200)
        XCTAssertEqual(parsed.headers["content-length"], "3")
        XCTAssertEqual(parsed.body, Data("abc".utf8))
    }

    func testRejectsNegativeContentLength() {
        XCTAssertThrowsError(try parseHttpRequest(Data("POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n".utf8))) { error in
            XCTAssertEqual(error as? HttpParseError, .malformed)
        }
    }

    func testRejectsNonnumericContentLength() {
        XCTAssertThrowsError(try parseHttpRequest(Data("POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n".utf8))) { error in
            XCTAssertEqual(error as? HttpParseError, .malformed)
        }
    }

    func testRequestWithoutContentLengthHasEmptyBody() throws {
        let raw = Data("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n".utf8)
        let request = try parseHttpRequest(raw)
        XCTAssertTrue(request.body.isEmpty)
    }

    func testResponseBodyTruncatedToContentLength() throws {
        let raw = Data("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabcXYZ".utf8)
        let parsed = try parseHttpResponse(raw)
        XCTAssertEqual(parsed.body, Data("abc".utf8))
        XCTAssertEqual(parsed.body.count, 3)
    }
}
