import Foundation

/// Одна партия принятых по сети файлов: подпапка стейджинга с меткой времени.
/// Локальные пути внутри неё кладутся в буфер, чтобы обычный Cmd+V вставлял
/// настоящие файлы, а не какую-то временную труху.
public struct StagingBatch {
    public let root: URL

    /// Строит путь назначения для относительного пути `rel`, приехавшего с чужой
    /// машины, и создаёт промежуточные папки. `rel` — недоверенный ввод: сначала
    /// прогоняется через `RelPath.normalize`, а затем — ещё раз, уже как последний
    /// рубеж перед записью на диск — проверяется, что итоговый путь лежит внутри
    /// `root`. Одной нормализации мало: она гарантирует форму строки, а не то, что
    /// финальный URL не выпрыгнул за пределы партии.
    ///
    /// Эта последняя проверка обязана быть symlink-aware, а не просто лексической:
    /// `standardized`/`standardizedFileURL` схлопывает только `.`/`..` в строке и не
    /// разрешает символические ссылки. Если внутри партии окажется каталог-симлинк
    /// (например, `root/sub` в реальности указывает на `/etc`), кандидат
    /// `root/sub/passwd` текстуально лежит внутри `root` и прошёл бы лексическую
    /// проверку, но `createDirectory`/запись на диск ушли бы по ссылке наружу.
    ///
    /// Проверять нужно самого глубокого УЖЕ СУЩЕСТВУЮЩЕГО предка целевого каталога,
    /// а не сам целевой каталог после его создания: `createDirectory(withIntermediateDirectories:
    /// true)` одним вызовом создал бы недостающие компоненты по пути ещё ДО проверки —
    /// и если под симлинком есть хотя бы один несуществующий сегмент (`sub/deeper/evil.txt`,
    /// где `sub` ведёт наружу, а `deeper` ещё не существует), этот вызов физически создал бы
    /// `deeper` в чужом каталоге, и только потом сработал бы отказ. Пройти сквозь симлинк
    /// можно лишь по уже существующему компоненту — так что проверка такого предка
    /// закрывает дыру полностью: компоненты, которые создаёт код сам (после проверки),
    /// это настоящие каталоги, а не ссылки, новых путей наружу они не открывают.
    ///
    /// `root` сам обычно лежит под симлинком на macOS (`/var` -> `/private/var`), поэтому
    /// сравнение ведётся между двумя РАЗРЕШЁННЫМИ путями — иначе неразрешённый `root`
    /// отвергал бы вообще всё.
    public func destination(for rel: String) throws -> URL {
        guard let normalized = RelPath.normalize(rel) else {
            throw StagingError.unsafeRelativePath(rel)
        }

        let fileManager = FileManager.default
        let candidate = root.appendingPathComponent(normalized)
        let candidateDirectory = candidate.deletingLastPathComponent()

        var existingAncestor = candidateDirectory
        while !fileManager.fileExists(atPath: existingAncestor.path) {
            let parent = existingAncestor.deletingLastPathComponent()
            guard parent.path != existingAncestor.path else { break } // достигли корня файловой системы
            existingAncestor = parent
        }

        let resolvedRoot = root.resolvingSymlinksInPath().standardizedFileURL.path
        let resolvedAncestor = existingAncestor.resolvingSymlinksInPath().standardizedFileURL.path
        guard resolvedAncestor == resolvedRoot || resolvedAncestor.hasPrefix(resolvedRoot + "/") else {
            throw StagingError.unsafeRelativePath(rel)
        }

        // Проверка прошла: все существующие предки лежат внутри партии, поэтому
        // теперь безопасно создать недостающие компоненты одним вызовом.
        try fileManager.createDirectory(at: candidateDirectory, withIntermediateDirectories: true)

        return candidate
    }
}

public enum StagingError: Error, Equatable {
    case unsafeRelativePath(String)
}

/// Управляет партиями стейджинга: создание новой партии с меткой времени и
/// периодическая уборка — партии старше `maxAgeDays` и всё за пределами
/// `keepBatches` последних партий удаляются.
public final class Staging {
    public static let keepBatches = 20
    public static let maxAgeDays = 7

    private let root: URL
    private let now: () -> Date
    private let fileManager: FileManager

    public init(root: URL, now: @escaping () -> Date = Date.init, fileManager: FileManager = .default) {
        self.root = root
        self.now = now
        self.fileManager = fileManager
    }

    public static var defaultRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/lanclip/incoming")
    }

    /// Формат метки партии: `yyyyMMdd-HHmmss`. Локаль зафиксирована на `en_US_POSIX`,
    /// а часовой пояс — на UTC: иначе на машине с другой локалью/поясом метка либо
    /// развалится (12-часовой формат, локальные названия), либо "поедет" во времени
    /// при смене летнего/зимнего времени.
    private static let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter
    }()

    public static func stamp(_ date: Date) -> String {
        formatter.string(from: date)
    }

    /// Создаёт новую партию с меткой текущего времени. Метка имеет секундную
    /// точность — два вызова в течение одной секунды дали бы одинаковую папку,
    /// поэтому при коллизии добавляется числовой суффикс `-2`, `-3`, ...
    public func newBatch() throws -> StagingBatch {
        let base = Staging.stamp(now())
        var candidate = root.appendingPathComponent(base)
        var suffix = 2
        while fileManager.fileExists(atPath: candidate.path) {
            candidate = root.appendingPathComponent("\(base)-\(suffix)")
            suffix += 1
        }

        try fileManager.createDirectory(at: candidate, withIntermediateDirectories: true)
        return StagingBatch(root: candidate)
    }

    /// Удаляет партии старше `maxAgeDays` дней и, независимо от возраста, всё за
    /// пределами `keepBatches` последних (по имени папки — метка сортируется как
    /// строка в хронологическом порядке) партий. Каждое правило применяется само по
    /// себе: партия может быть удалена и как устаревшая, и как избыточная.
    public func cleanup() throws {
        guard fileManager.fileExists(atPath: root.path) else { return }

        let entries = try fileManager.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
        // Метка сортируется как строка в том же порядке, что и хронологически
        // (`yyyyMMdd-HHmmss`), поэтому сравнение возраста и отбор "последних N"
        // ведутся по строке, без парсинга обратно в `Date`.
        let batchNames = entries.map { $0.lastPathComponent }.sorted()

        // Партия ровно на границе 7 дней не должна удаляться — порог считается как
        // "строго раньше `now - 7 дней`", а не "не позже".
        let ageCutoffStamp = Staging.stamp(now().addingTimeInterval(-TimeInterval(Staging.maxAgeDays) * 24 * 3600))
        let namesToKeepByRecency = Set(batchNames.suffix(Staging.keepBatches))

        for name in batchNames {
            let baseStamp = String(name.split(separator: "-").prefix(2).joined(separator: "-"))
            let isTooOld = baseStamp < ageCutoffStamp
            let isBeyondKeepLimit = !namesToKeepByRecency.contains(name)

            guard isTooOld || isBeyondKeepLimit else { continue }
            try? fileManager.removeItem(at: root.appendingPathComponent(name))
        }
    }
}
