import Foundation

public enum ClipKind: String, Codable, Sendable {
    case empty, text, image, files
}

public struct BlobRef: Codable, Equatable, Sendable {
    public let i: Int
    public let rel: String
    public let size: Int
    public let mime: String?

    public init(i: Int, rel: String, size: Int, mime: String? = nil) {
        self.i = i
        self.rel = rel
        self.size = size
        self.mime = mime
    }
}

public struct Manifest: Codable, Equatable, Sendable {
    public let kind: ClipKind
    public let seq: Int
    public let text: String?
    public let blobs: [BlobRef]?
    public let totalSize: Int?

    private init(kind: ClipKind, seq: Int, text: String?, blobs: [BlobRef]?) {
        self.kind = kind
        self.seq = seq
        self.text = text
        self.blobs = blobs
        self.totalSize = blobs.map { list in list.reduce(0) { $0 + $1.size } }
    }

    public static func empty(seq: Int) -> Manifest {
        Manifest(kind: .empty, seq: seq, text: nil, blobs: nil)
    }

    public static func text(_ value: String, seq: Int) -> Manifest {
        Manifest(kind: .text, seq: seq, text: value, blobs: nil)
    }

    public static func image(pngSize: Int, seq: Int) -> Manifest {
        Manifest(kind: .image, seq: seq, text: nil,
                 blobs: [BlobRef(i: 0, rel: "clip.png", size: pngSize, mime: "image/png")])
    }

    public static func files(_ blobs: [BlobRef], seq: Int) -> Manifest {
        Manifest(kind: .files, seq: seq, text: nil, blobs: blobs)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> Manifest {
        try JSONDecoder().decode(Manifest.self, from: data)
    }
}
