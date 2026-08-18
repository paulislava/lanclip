import Foundation
import LanClipCore

/// `Notifying` поверх системных уведомлений macOS.
///
/// Показывает баннер через `osascript -e 'display notification ...'`, а не
/// `UNUserNotificationCenter`: центру уведомлений нужен настоящий bundle с
/// bundle identifier и явное разрешение пользователя, а `lanclipd` — голый
/// SPM-бинарник (и будущий LaunchAgent из задачи 14) без bundle вовсе.
/// `osascript` работает из любого процесса без этих требований — а видимость
/// нужна именно потому, что `POST /pull` чаще всего сработает без терминала
/// под рукой (сервер поднят в фоне, вызов пришёл по сети или от хоткея).
public final class MacNotifier: Notifying {
    public init() {}

    public func error(_ message: String) {
        post(title: "lanclip — ошибка", message: message)
    }

    public func info(_ message: String) {
        post(title: "lanclip", message: message)
    }

    private func post(title: String, message: String) {
        let script = "display notification \(MacNotifier.quoted(message)) with title \(MacNotifier.quoted(title))"
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", script]
        // Уведомление — best-effort побочный эффект: сбой запуска osascript
        // (например, его нет на диске в минимальной установке) не должен
        // прерывать вызывающую операцию (сам pull уже состоялся или провалился
        // независимо от того, увидит ли пользователь баннер).
        try? process.run()
    }

    /// AppleScript-строка в двойных кавычках: экранирует обратный слеш и кавычку,
    /// которые иначе оборвали бы строковый литерал в сгенерированном скрипте.
    private static func quoted(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }
}
