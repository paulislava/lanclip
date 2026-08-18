import AppKit
import Foundation

/// Ошибки, специфичные для настоящего системного буфера macOS. Единственный
/// случай, где чтение может не свестись к одному из вариантов `ClipContent`, —
/// буфер содержит TIFF, который не удаётся перекодировать в PNG.
public enum MacPasteboardError: Error, Equatable {
    case tiffConversionFailed
}

/// `ClipboardReading`/`ClipboardWriting` поверх настоящего `NSPasteboard`.
///
/// Порядок определения типа при чтении — контракт протокола
/// (`proto/PROTOCOL.md`): файлы → картинка → текст, первое совпадение
/// побеждает. Картинка всегда отдаётся как PNG: если в буфере лежит TIFF
/// (обычное дело для копий из Preview/Finder), он перекодируется через
/// `NSBitmapImageRep`, а не отдаётся как есть.
public final class MacPasteboard: ClipboardReading, ClipboardWriting {
    /// Легаси-тип, которым старые приложения (до перехода на UTI/NSURL)
    /// ожидают увидеть список путей файлов на буфере.
    private static let filenamesType = NSPasteboard.PasteboardType("NSFilenamesPboardType")

    private let pasteboard: NSPasteboard

    public init(pasteboard: NSPasteboard = .general) {
        self.pasteboard = pasteboard
    }

    public func changeCount() -> Int {
        pasteboard.changeCount
    }

    public func read() throws -> ClipContent {
        // Файлы — первыми: `urlReadingFileURLsOnly` отсекает обычные http(s)
        // ссылки, которые тоже читаются как NSURL, но файлами не являются.
        if let urls = pasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        ) as? [URL], !urls.isEmpty {
            return .files(urls)
        }

        if let png = pasteboard.data(forType: .png) {
            return .image(png)
        }

        if let tiff = pasteboard.data(forType: .tiff) {
            guard let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                throw MacPasteboardError.tiffConversionFailed
            }
            return .image(png)
        }

        if let text = pasteboard.string(forType: .string) {
            return .text(text)
        }

        return .empty
    }

    public func write(_ content: ClipContent) throws {
        pasteboard.clearContents()

        switch content {
        case .empty:
            break

        case .text(let value):
            pasteboard.setString(value, forType: .string)

        case .image(let png):
            pasteboard.setData(png, forType: .png)

        case .files(let urls):
            // Современные приложения читают NSURL-объекты…
            pasteboard.writeObjects(urls as [NSURL])
            // …а старые (без поддержки NSURL на буфере) — легаси-список путей.
            let paths = urls.map { $0.path }
            pasteboard.setPropertyList(paths, forType: Self.filenamesType)
        }
    }
}
