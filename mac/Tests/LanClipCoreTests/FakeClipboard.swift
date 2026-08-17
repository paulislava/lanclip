import Foundation
@testable import LanClipCore

final class FakeClipboard: ClipboardReading, ClipboardWriting, @unchecked Sendable {
    private(set) var changes = 1
    private(set) var written: [ClipContent] = []
    var content: ClipContent = .empty {
        didSet { changes += 1 }
    }

    func changeCount() -> Int { changes }
    func read() throws -> ClipContent { content }

    func write(_ content: ClipContent) throws {
        written.append(content)
        self.content = content
    }
}
