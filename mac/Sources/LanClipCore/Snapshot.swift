import Foundation

public enum SnapshotError: Error, Equatable {
    case staleSeq
}

/// Итог `SnapshotStore.blob(index:seq:)` — находка I6 финального ревью: раньше
/// метод всегда возвращал `Data?`, читая файлы блобов `files` целиком в память
/// (`Data(contentsOf:)`) внутри `blob(...)` независимо от вызывающей стороны.
/// `maxBytes` — проверка на стороне КЛИЕНТА, поэтому `GET /clip/blob/{i}` можно
/// послать напрямую, в обход неё; многогигабайтное видео в буфере пользователя
/// вырубило бы сервер одним таким запросом, ещё до попытки отдать хоть байт.
/// `.file` заставляет вызывающую сторону (`HttpServer`) читать файл потоком, не
/// требуя от `SnapshotStore` знать что-либо о сети.
public enum BlobPayload: Sendable {
    case data(Data)
    case file(url: URL, size: Int)
}

public struct ClipSnapshot: Sendable {
    public let manifest: Manifest
    public let text: String?
    public let imagePNG: Data?
    public let sources: [Int: URL]
}

public final class SnapshotStore {
    private let reader: ClipboardReading
    private let fileManager: FileManager
    private var cached: ClipSnapshot?

    public init(reader: ClipboardReading, fileManager: FileManager = .default) {
        self.reader = reader
        self.fileManager = fileManager
    }

    public func current() throws -> ClipSnapshot {
        let seq = reader.changeCount()
        if let cached, cached.manifest.seq == seq { return cached }

        let snapshot = try build(seq: seq, content: try reader.read())
        cached = snapshot
        return snapshot
    }

    public func blob(index: Int, seq: Int) throws -> BlobPayload? {
        let snapshot = try current()
        guard snapshot.manifest.seq == seq else { throw SnapshotError.staleSeq }

        if let png = snapshot.imagePNG {
            return index == 0 ? .data(png) : nil
        }
        guard let source = snapshot.sources[index] else { return nil }
        // Размер считается заново с диска (не из манифеста) — это тот же файл,
        // который сейчас будет прочитан, поэтому `Content-Length`, ушедший в
        // заголовках, гарантированно совпадает с тем, что реально станет читать
        // `HttpServer` дальше чанками.
        return .file(url: source, size: try size(of: source))
    }

    private func build(seq: Int, content: ClipContent) throws -> ClipSnapshot {
        switch content {
        case .empty:
            return ClipSnapshot(manifest: .empty(seq: seq), text: nil, imagePNG: nil, sources: [:])

        case .text(let value):
            return ClipSnapshot(manifest: .text(value, seq: seq), text: value,
                                imagePNG: nil, sources: [:])

        case .image(let png):
            return ClipSnapshot(manifest: .image(pngSize: png.count, seq: seq), text: nil,
                                imagePNG: png, sources: [:])

        case .files(let urls):
            var blobs: [BlobRef] = []
            var sources: [Int: URL] = [:]

            for url in urls {
                for entry in try expand(url) {
                    guard let rel = RelPath.normalize(entry.rel) else { continue }
                    sources[blobs.count] = entry.url
                    blobs.append(BlobRef(i: blobs.count, rel: rel, size: entry.size))
                }
            }

            if blobs.isEmpty {
                return ClipSnapshot(manifest: .empty(seq: seq), text: nil, imagePNG: nil, sources: [:])
            }
            return ClipSnapshot(manifest: .files(blobs, seq: seq), text: nil,
                                imagePNG: nil, sources: sources)
        }
    }

    private struct Entry {
        let url: URL
        let rel: String
        let size: Int
    }

    private func expand(_ url: URL) throws -> [Entry] {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory) else { return [] }

        if !isDirectory.boolValue {
            let resolved = url.resolvingSymlinksInPath()
            return [Entry(url: resolved, rel: url.lastPathComponent, size: try size(of: resolved))]
        }

        let base = url.lastPathComponent
        guard let walker = fileManager.enumerator(atPath: url.path) else {
            return []
        }

        var entries: [Entry] = []
        for case let subpath as String in walker {
            let child = url.appendingPathComponent(subpath)
            var isChildDirectory: ObjCBool = false
            guard fileManager.fileExists(atPath: child.path, isDirectory: &isChildDirectory) else { continue }
            if isChildDirectory.boolValue { continue }
            let resolved = child.resolvingSymlinksInPath()
            entries.append(Entry(url: resolved, rel: base + "/" + subpath, size: try size(of: resolved)))
        }
        return entries
    }

    private func size(of url: URL) throws -> Int {
        let attributes = try fileManager.attributesOfItem(atPath: url.path)
        return (attributes[.size] as? NSNumber)?.intValue ?? 0
    }
}
