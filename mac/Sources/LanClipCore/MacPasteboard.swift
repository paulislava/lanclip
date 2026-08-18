import AppKit
import Foundation

/// Ошибки, специфичные для настоящего системного буфера macOS.
public enum MacPasteboardError: Error, Equatable {
    /// Буфер содержит TIFF, который не удаётся перекодировать в PNG.
    case tiffConversionFailed
    /// Находка I3 финального ревью: `setString`/`setData`/`writeObjects`/
    /// `setPropertyList` возвращают `Bool` и все четыре результата раньше были
    /// отброшены. `NSPasteboard` документированно возвращает `false`, если вызывающая
    /// сторона потеряла владение буфером между `clearContents()` и записью (другой
    /// процесс успел вклиниться и тоже вызвать `clearContents()`) — до этой правки
    /// такой отказ никак не всплывал: `write()` рапортовал успех, `pull()` показывал
    /// тост об успехе, а буфер оставался пустым, причём прежнее содержимое уже было
    /// уничтожено первым `clearContents()`.
    case writeFailed(String)
}

/// Минимальный протокол-обёртка над теми методами `NSPasteboard`, которые
/// использует `MacPasteboard` — тестовый шов: `NSPasteboard` не подсаживается на
/// шпион напрямую (нет доступного `init`, экземпляры только через фабричные
/// методы вроде `.general`), но тест может подставить сюда `FakeRawPasteboard`,
/// у которого `setString`/`setData`/`writeObjects`/`setPropertyList` детерминированно
/// возвращают `false`, чтобы проверить, что `write()` действительно бросает, а не
/// проглатывает отказ. `NSPasteboard` уже реализует все эти методы с точно такими
/// же сигнатурами, поэтому `extension NSPasteboard: RawPasteboard {}` ниже не
/// требует ни одной новой строчки кода.
public protocol RawPasteboard: AnyObject {
    var changeCount: Int { get }
    func clearContents() -> Int
    func data(forType dataType: NSPasteboard.PasteboardType) -> Data?
    func string(forType dataType: NSPasteboard.PasteboardType) -> String?
    func readObjects(forClasses classArray: [AnyClass], options: [NSPasteboard.ReadingOptionKey: Any]?) -> [Any]?
    func setString(_ string: String, forType dataType: NSPasteboard.PasteboardType) -> Bool
    func setData(_ data: Data?, forType dataType: NSPasteboard.PasteboardType) -> Bool
    func writeObjects(_ objects: [NSPasteboardWriting]) -> Bool
    func setPropertyList(_ propertyList: Any, forType dataType: NSPasteboard.PasteboardType) -> Bool
}

extension NSPasteboard: RawPasteboard {}

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

    private let pasteboard: RawPasteboard

    public init(pasteboard: RawPasteboard = NSPasteboard.general) {
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
        _ = pasteboard.clearContents()

        switch content {
        case .empty:
            break

        case .text(let value):
            guard pasteboard.setString(value, forType: .string) else {
                throw MacPasteboardError.writeFailed("setString(forType: .string)")
            }

        case .image(let png):
            guard pasteboard.setData(png, forType: .png) else {
                throw MacPasteboardError.writeFailed("setData(forType: .png)")
            }

        case .files(let urls):
            // Современные приложения читают NSURL-объекты…
            guard pasteboard.writeObjects(urls as [NSURL]) else {
                throw MacPasteboardError.writeFailed("writeObjects(_:)")
            }
            // …а старые (без поддержки NSURL на буфере) — легаси-список путей.
            let paths = urls.map { $0.path }
            guard pasteboard.setPropertyList(paths, forType: Self.filenamesType) else {
                throw MacPasteboardError.writeFailed("setPropertyList(forType: NSFilenamesPboardType)")
            }
        }
    }
}
