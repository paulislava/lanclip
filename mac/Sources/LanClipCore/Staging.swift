import Foundation

/// Одна партия принятых по сети файлов: подпапка стейджинга с меткой времени.
/// Локальные пути внутри неё кладутся в буфер, чтобы обычный Cmd+V вставлял
/// настоящие файлы, а не какую-то временную труху.
public struct StagingBatch {
    public let root: URL

    /// Строит путь назначения для относительного пути `rel`, приехавшего с чужой
    /// машины, и создаёт промежуточные папки. `rel` — недоверенный ввод: сначала
    /// прогоняется через `RelPath.normalize`, а затем — ещё раз, уже как последний
    /// рубеж перед записью на диск — проверяется, что итоговый нормализованный путь
    /// лежит внутри `root`. Одной нормализации мало: она гарантирует форму строки,
    /// а не то, что финальный URL не выпрыгнул за пределы партии.
    public func destination(for rel: String) throws -> URL {
        guard let normalized = RelPath.normalize(rel) else {
            throw StagingError.unsafeRelativePath(rel)
        }

        let candidate = root.appendingPathComponent(normalized)
        let rootPath = root.standardizedFileURL.path
        let candidatePath = candidate.standardizedFileURL.path
        guard candidatePath == rootPath || candidatePath.hasPrefix(rootPath + "/") else {
            throw StagingError.unsafeRelativePath(rel)
        }

        try FileManager.default.createDirectory(at: candidate.deletingLastPathComponent(),
                                                  withIntermediateDirectories: true)
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
