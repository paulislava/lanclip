import Foundation

public enum ClipContent: Equatable, Sendable {
    case empty
    case text(String)
    case image(Data)
    case files([URL])
}

public protocol ClipboardReading: AnyObject {
    func changeCount() -> Int
    func read() throws -> ClipContent
}

public protocol ClipboardWriting: AnyObject {
    func write(_ content: ClipContent) throws
}
