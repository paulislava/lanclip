import Foundation

/// Ошибки разбора аргументов `lanclipd`. `helpRequested` и `unknownCommand` ведут к
/// разным кодам выхода в `main.swift` (0 против 2), поэтому это разные случаи, а не
/// один общий "usage error".
public enum CliArgsError: Error, Equatable {
    case helpRequested
    case missingConfigValue
    case unknownCommand(String)
}

/// Разбор аргументов командной строки `lanclipd` без сторонних зависимостей (никакого
/// `swift-argument-parser`). Живёт в `LanClipCore`, а не в цели `lanclipd`, только
/// чтобы быть тестируемым без запуска процесса — сама программа собирается в
/// `main.swift` исполняемой цели.
public struct CliArgs: Equatable {
    public static let knownCommands = ["serve", "status", "get", "pull"]
    public static let defaultCommand = "serve"

    public let command: String
    public let configURL: URL

    public init(command: String, configURL: URL) {
        self.command = command
        self.configURL = configURL
    }

    /// `argv` — уже без имени процесса (`CommandLine.arguments.dropFirst()`).
    ///
    /// `--config` принимается и до, и после позиционной команды — обе формы
    /// (`lanclipd status --config x` и `lanclipd --config x status`) равноправны.
    /// Вторая позиционная лексема (когда команда уже распознана) трактуется как
    /// неизвестная команда, а не игнорируется — иначе `lanclipd status get` молча
    /// выполнил бы только `status`.
    public static func parse(_ argv: [String]) throws -> CliArgs {
        var configURL: URL?
        var command: String?

        var index = argv.startIndex
        while index < argv.endIndex {
            let arg = argv[index]
            switch arg {
            case "-h", "--help":
                throw CliArgsError.helpRequested

            case "--config":
                let valueIndex = argv.index(after: index)
                guard valueIndex < argv.endIndex else { throw CliArgsError.missingConfigValue }
                configURL = URL(fileURLWithPath: argv[valueIndex])
                index = valueIndex

            default:
                guard command == nil else { throw CliArgsError.unknownCommand(arg) }
                command = arg
            }
            index = argv.index(after: index)
        }

        let resolvedCommand = command ?? defaultCommand
        guard knownCommands.contains(resolvedCommand) else {
            throw CliArgsError.unknownCommand(resolvedCommand)
        }

        return CliArgs(command: resolvedCommand, configURL: configURL ?? Config.defaultURL)
    }
}
