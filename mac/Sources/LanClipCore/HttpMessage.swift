import Foundation

public enum HttpParseError: Error, Equatable {
    case incomplete
    case malformed
}

public struct HttpRequest: Equatable, Sendable {
    public let method: String
    public let path: String
    public let query: [String: String]
    public let headers: [String: String]
    public let body: Data
}

public struct HttpResponse: Sendable {
    public let status: Int
    public let headers: [String: String]
    public let body: Data?

    public init(status: Int, headers: [String: String] = [:], body: Data? = nil) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public static func json(_ status: Int, _ payload: Data) -> HttpResponse {
        HttpResponse(status: status,
                     headers: ["Content-Type": "application/json; charset=utf-8"],
                     body: payload)
    }

    public static func bytes(_ payload: Data) -> HttpResponse {
        HttpResponse(status: 200,
                     headers: ["Content-Type": "application/octet-stream"],
                     body: payload)
    }

    public static func empty(_ status: Int) -> HttpResponse {
        HttpResponse(status: status)
    }

    public func head() -> Data {
        var text = "HTTP/1.1 \(status) \(HttpResponse.reason(status))\r\n"
        for (key, value) in headers.sorted(by: { $0.key < $1.key }) {
            text += "\(key): \(value)\r\n"
        }
        text += "Content-Length: \(body?.count ?? 0)\r\n"
        text += "Connection: close\r\n\r\n"
        return Data(text.utf8)
    }

    static func reason(_ status: Int) -> String {
        switch status {
        case 200: return "OK"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 405: return "Method Not Allowed"
        case 409: return "Conflict"
        case 500: return "Internal Server Error"
        case 503: return "Service Unavailable"
        default: return "Status"
        }
    }
}

private let crlfcrlf = Data("\r\n\r\n".utf8)

public func parseHttpRequest(_ data: Data) throws -> HttpRequest {
    guard let separator = data.range(of: crlfcrlf) else { throw HttpParseError.incomplete }

    let headText = String(decoding: data[data.startIndex..<separator.lowerBound], as: UTF8.self)
    var lines = headText.components(separatedBy: "\r\n")
    guard let requestLine = lines.first else { throw HttpParseError.malformed }
    lines.removeFirst()

    let parts = requestLine.split(separator: " ")
    guard parts.count == 3, parts[2].hasPrefix("HTTP/") else { throw HttpParseError.malformed }

    let target = String(parts[1])
    let split = target.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
    let path = String(split[0])

    var query: [String: String] = [:]
    if split.count == 2 {
        for pair in split[1].split(separator: "&") {
            let kv = pair.split(separator: "=", maxSplits: 1)
            guard let key = kv.first else { continue }
            let value = kv.count == 2 ? String(kv[1]) : ""
            query[String(key)] = value.removingPercentEncoding ?? value
        }
    }

    var headers: [String: String] = [:]
    for line in lines where !line.isEmpty {
        let kv = line.split(separator: ":", maxSplits: 1)
        guard kv.count == 2 else { throw HttpParseError.malformed }
        headers[kv[0].lowercased()] = kv[1].trimmingCharacters(in: .whitespaces)
    }

    let body = data[separator.upperBound...]
    let declared = Int(headers["content-length"] ?? "0") ?? 0
    guard body.count >= declared else { throw HttpParseError.incomplete }

    return HttpRequest(method: String(parts[0]).uppercased(), path: path, query: query,
                       headers: headers, body: Data(body.prefix(declared)))
}

public func parseHttpResponse(_ data: Data) throws -> (status: Int, headers: [String: String], body: Data) {
    guard let separator = data.range(of: crlfcrlf) else { throw HttpParseError.incomplete }

    let headText = String(decoding: data[data.startIndex..<separator.lowerBound], as: UTF8.self)
    var lines = headText.components(separatedBy: "\r\n")
    guard let statusLine = lines.first else { throw HttpParseError.malformed }
    lines.removeFirst()

    let parts = statusLine.split(separator: " ")
    guard parts.count >= 2, let status = Int(parts[1]) else { throw HttpParseError.malformed }

    var headers: [String: String] = [:]
    for line in lines where !line.isEmpty {
        let kv = line.split(separator: ":", maxSplits: 1)
        guard kv.count == 2 else { continue }
        headers[kv[0].lowercased()] = kv[1].trimmingCharacters(in: .whitespaces)
    }

    let body = Data(data[separator.upperBound...])
    if let declared = Int(headers["content-length"] ?? ""), body.count < declared {
        throw HttpParseError.incomplete
    }
    return (status, headers, body)
}
