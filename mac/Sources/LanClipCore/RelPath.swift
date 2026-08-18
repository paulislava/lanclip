import Foundation

public enum RelPath {
    static let maxComponent = 150
    /// Находка I8 финального ревью: было 400, что не влезает в легаси
    /// `MAX_PATH` (260 символов, включая диск и завершающий `NUL`) вместе со
    /// стейджинг-корнем на Windows (`%LOCALAPPDATA%\lanclip\incoming\<метка>\`,
    /// на реальной машине ~66 символов, плюс запас на более длинное имя
    /// пользователя). `rel` длиной 195–400 нормализацию проходил, но на приёме
    /// падал необёрнутым `PathTooLongException`. Выбор 150 (не отдельно
    /// придуманное число, а тот же предел, что и `maxComponent`) с запасом в
    /// ~100 символов на корень партии укладывается в 260 с большим запасом на
    /// обеих платформах. Предел обязан совпадать на Mac и Windows побитово —
    /// это часть протокола, а не деталь реализации одной стороны.
    static let maxTotal = 150

    private static let illegal: Set<Character> = ["<", ">", ":", "\"", "|", "?", "*", "\0"]

    private static let reserved: Set<String> = {
        var names: Set<String> = ["con", "prn", "aux", "nul"]
        for n in 1...9 {
            names.insert("com\(n)")
            names.insert("lpt\(n)")
        }
        return names
    }()

    public static func normalize(_ raw: String) -> String? {
        let unified = raw.replacingOccurrences(of: "\\", with: "/")
        var components: [String] = []

        for piece in unified.split(separator: "/", omittingEmptySubsequences: true) {
            let component = String(piece)
            if component == "." { continue }
            if component == ".." { return nil }
            guard let safe = sanitize(component) else { return nil }
            components.append(safe)
        }

        guard !components.isEmpty else { return nil }
        let joined = components.joined(separator: "/")
        // Байты UTF-8, не графемы: та же единица, что и на Windows-стороне
        // (см. RelPath.cs) — иначе платформы расходятся на одном и том же
        // входе (эмодзи, иероглифы из дополнительных плоскостей Unicode).
        guard joined.utf8.count <= maxTotal else { return nil }
        return joined
    }

    private static func sanitize(_ component: String) -> String? {
        var cleaned = String(component.map { char in
            if illegal.contains(char) { return "_" }
            if let scalar = char.unicodeScalars.first,
               char.unicodeScalars.count == 1,
               scalar.properties.generalCategory == .control {
                return "_"
            }
            return char
        })

        while let last = cleaned.last, last == "." || last == " " {
            cleaned.removeLast()
        }

        guard !cleaned.isEmpty else { return nil }

        // Мелкая находка финального ревью: `split(separator: ".")` по умолчанию
        // ОТБРАСЫВАЕТ пустые подпоследовательности, поэтому для `".con"` (ведущая
        // точка) `base` получался равным "con" — реальный Windows так реестр
        // имён не проверяет: там "имя до первой точки" для `.con` — ПУСТАЯ строка
        // (точка стоит на нулевой позиции), а не "con". Из-за этого `.con`
        // ошибочно распознавался как зарезервированное имя и получал суффикс
        // (`.con_`), а Windows-сторонняя реализация (индекс первой точки, без
        // отбрасывания пустых кусков) корня не трогала вовсе — один и тот же файл
        // приезжал на две машины под разными именами. Взят явный индекс первой
        // точки — так же, как на Windows-стороне (`RelPath.cs`), — вместо `split`.
        let base: String
        if let dotIndex = cleaned.firstIndex(of: ".") {
            base = String(cleaned[cleaned.startIndex..<dotIndex])
        } else {
            base = cleaned
        }
        if reserved.contains(base.lowercased()) {
            cleaned += "_"
        }

        // Байты UTF-8, не графемы (см. комментарий в normalize(_:)).
        guard cleaned.utf8.count <= maxComponent else { return nil }
        return cleaned
    }
}
