import Foundation

/// Абстракция над уведомлением пользователя об ошибках и статусе — экран/лог
/// появятся в задачах 13/14. Здесь только протокол и пустая заглушка, чтобы
/// `MacPasteboard` и остальное ядро могли принимать `Notifying` уже сейчас.
public protocol Notifying: Sendable {
    func error(_ message: String)
    func info(_ message: String)
}

public struct NullNotifier: Notifying, Sendable {
    public init() {}
    public func error(_ message: String) {}
    public func info(_ message: String) {}
}
