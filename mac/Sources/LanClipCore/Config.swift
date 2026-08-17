import Foundation

public enum ConfigError: Error, Equatable {
    case invalidPort(Int)
    case emptyToken
    case noPeers
    case invalidMaxBytes(Int)
    case malformed(String)
}

public struct Config: Codable, Equatable, Sendable {
    public var port: Int
    public var token: String
    public var peers: [String]
    public var maxBytes: Int
    public var autoPaste: Bool

    public static let defaultPort = 8899
    public static let defaultMaxBytes = 536_870_912

    public static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/lanclip/config.json")
    }

    public init(port: Int = Config.defaultPort, token: String, peers: [String],
                maxBytes: Int = Config.defaultMaxBytes, autoPaste: Bool = true) {
        self.port = port
        self.token = token
        self.peers = peers
        self.maxBytes = maxBytes
        self.autoPaste = autoPaste
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        port = try container.decodeIfPresent(Int.self, forKey: .port) ?? Config.defaultPort
        token = try container.decode(String.self, forKey: .token)
        peers = try container.decodeIfPresent([String].self, forKey: .peers) ?? []
        maxBytes = try container.decodeIfPresent(Int.self, forKey: .maxBytes) ?? Config.defaultMaxBytes
        autoPaste = try container.decodeIfPresent(Bool.self, forKey: .autoPaste) ?? true
    }

    public static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        for index in bytes.indices { bytes[index] = UInt8.random(in: 0...255) }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    public static func load(at url: URL) throws -> Config {
        if !FileManager.default.fileExists(atPath: url.path) {
            let fresh = Config(token: generateToken(), peers: [])
            try fresh.write(to: url)
            return fresh
        }

        let data = try Data(contentsOf: url)
        do {
            return try JSONDecoder().decode(Config.self, from: data)
        } catch {
            throw ConfigError.malformed(String(describing: error))
        }
    }

    public func write(to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                               withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        try encoder.encode(self).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    public func validate() throws {
        guard port > 0, port < 65_536 else { throw ConfigError.invalidPort(port) }
        guard !token.isEmpty else { throw ConfigError.emptyToken }
        guard !peers.isEmpty else { throw ConfigError.noPeers }
        guard maxBytes > 0 else { throw ConfigError.invalidMaxBytes(maxBytes) }
    }
}
