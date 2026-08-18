import Foundation
import LanClipCore

// MARK: - Точка входа

let arguments = Array(CommandLine.arguments.dropFirst())

do {
    let args = try CliArgs.parse(arguments)
    switch args.command {
    case "serve":
        runServe(configURL: args.configURL)
    case "status":
        runStatus(configURL: args.configURL)
    case "get":
        runGet(configURL: args.configURL)
    case "pull":
        runPull(configURL: args.configURL)
    default:
        // Недостижимо: CliArgs.parse уже отфильтровал всё, кроме known commands.
        fatalError("непредвиденная команда после разбора аргументов: \(args.command)")
    }
} catch CliArgsError.helpRequested {
    printUsage()
    exit(0)
} catch CliArgsError.missingConfigValue {
    printErr("Флагу --config нужно значение: путь к файлу конфига.\n")
    printUsage()
    exit(2)
} catch CliArgsError.unknownCommand(let name) {
    printErr("Неизвестная команда: \(name)\n")
    printUsage()
    exit(2)
} catch {
    printErr("Непредвиденная ошибка разбора аргументов: \(describe(error))")
    exit(2)
}

// MARK: - Подкоманды

func runServe(configURL: URL) {
    let config = loadValidatedConfig(at: configURL)
    let snapshots = SnapshotStore(reader: MacPasteboard())
    let hostName = ProcessInfo.processInfo.hostName
    let notifier = MacNotifier()

    let resolver = PeerResolver(config: config, prober: NwHttpClient())
    let fetcher = NwHttpClient()
    let staging = Staging(root: Staging.defaultRoot)
    let writer = MacPasteboard()
    let pullClient = PullClient(config: config, resolver: resolver, fetcher: fetcher,
                                 staging: staging, writer: writer)

    // `pullClient.pull()` теперь дёргается из двух независимых мест: обработчик
    // `POST /pull` на очереди HTTP-сервера и обработчик хоткея. Ни `Staging`, ни
    // `MacPasteboard` внутри `pull()` не синхронизированы — сосед может дёрнуть
    // `/pull` ровно в момент, когда пользователь жмёт хоткей, и два одновременных
    // pull() создали бы две перемешанные партии стейджинга и гонку записи в буфер.
    // Единая серийная очередь ниже — общий турникет для ЛЮБОГО вызова pull(),
    // откуда бы он ни пришёл: второй вызов не отклоняется и не падает с "занято",
    // а просто ждёт своей очереди и выполняется по готовности первого. Это
    // осознанный выбор "ждать", а не "отказать": обе стороны (сосед по /pull и
    // локальный хоткей) и так уже терпимы к небольшой задержке ответа (сеть, пауза
    // синтеза), а отказ потребовал бы отдельного состояния "pull уже идёт" и
    // прокидывания нового вида ошибки в оба независимых вызывающих места — ради
    // редкого случая, где оба пути и так получат корректный, а не мусорный,
    // результат, просто по очереди.
    let pullQueue = DispatchQueue(label: "lanclip.pull", qos: .userInitiated)

    // Хоткей регистрируется в задаче 14 — здесь только транспорт: `POST /pull`
    // на этот сервер выполняет настоящий цикл pull и озвучивает результат через
    // системное уведомление, потому что сервер обычно крутится без терминала
    // под рукой (в фоне, позже — как LaunchAgent).
    let server = HttpServer(config: config, snapshots: snapshots, hostName: hostName, pull: {
        do {
            let result = try pullQueue.sync { try pullClient.pull() }
            notifier.info("Забрано с соседа: \(result.kind.rawValue), файлов \(result.fileCount), байт \(result.bytes)")
            return result
        } catch {
            notifier.error(describePullFailure(error))
            throw error
        }
    })

    do {
        try server.start()
    } catch {
        printErr("Не удалось поднять сервер на порту \(config.port): \(describe(error))")
        exit(1)
    }

    // Ctrl+Shift+V: `pull()` уходит в сеть и может занять секунды, поэтому выполняется
    // на фоновой `pullQueue` — обработчик хоткея приходит на главную очередь, и её
    // блокировка сетевым вызовом система сочтёт зависшим процессом. `pullQueue` та
    // же самая серийная очередь, что и у обработчика `POST /pull` выше — это и есть
    // сериализация: если сеть в этот момент уже тянет pull для соседа, хоткей просто
    // встаёт следом в очередь, а не гонится с ним за один и тот же `Staging`/буфер.
    // Синтез нажатия, наоборот, обязан идти на главной очереди и только после того,
    // как pull() уже вернулся и буфер наполнен.
    let hotkey = MacHotkey {
        pullQueue.async {
            do {
                let result = try pullClient.pull()

                if config.autoPaste {
                    DispatchQueue.main.async {
                        synthesizePaste()
                    }
                }

                // Успех тихий — кроме файлов, где полезно знать, что именно приехало
                // и сколько весит, до того как открывать Finder.
                if result.kind == .files {
                    notifier.info("\(pluralizedFiles(result.fileCount)), \(megabytesString(result.bytes))")
                }
            } catch {
                notifier.error(describePullFailure(error))
            }
        }
    }

    do {
        try hotkey.register()
    } catch {
        printErr("Не удалось зарегистрировать глобальный хоткей Ctrl+Shift+V: \(describe(error))")
        exit(1)
    }

    print("lanclip слушает порт \(server.boundPort) (хост \(hostName)), хоткей Ctrl+Shift+V активен")
    RunLoop.current.run()
}

func runStatus(configURL: URL) {
    let config = loadValidatedConfig(at: configURL)
    let snapshots = SnapshotStore(reader: MacPasteboard())

    print("Порт: \(config.port)")

    do {
        let snapshot = try snapshots.current()
        print("Буфер: kind=\(snapshot.manifest.kind.rawValue), seq=\(snapshot.manifest.seq)")
    } catch {
        print("Буфер: не удалось прочитать (\(describe(error)))")
    }

    let resolver = PeerResolver(config: config, prober: NwHttpClient())
    if let peer = resolver.resolve() {
        print("Сосед: \(peer)")
    } else {
        print("Сосед не найден")
    }
}

func runGet(configURL: URL) {
    let config = loadValidatedConfig(at: configURL)
    let resolver = PeerResolver(config: config, prober: NwHttpClient())

    guard let peer = resolver.resolve() else {
        print("Сосед не найден")
        exit(1)
    }

    let client = NwHttpClient()
    do {
        let manifest = try client.manifest(host: peer, port: config.port, token: config.token)
        print(try prettyPrintedManifest(manifest))
    } catch {
        // Carried-over note (задача 11): `Manifest.decode` внутри `client.manifest`
        // ловит только `HttpClientError` сам по себе не бросает ничего специфичного —
        // если сосед пришлёт битый JSON, наружу вылетит сырой `DecodingError`. Это
        // catch-all обязан существовать и печатать внятную строку в любом случае,
        // а не только для ожидаемых `HttpClientError`.
        printErr("Не удалось получить манифест соседа \(peer): \(describe(error))")
        exit(1)
    }
}

func runPull(configURL: URL) {
    let config = loadValidatedConfig(at: configURL)
    let resolver = PeerResolver(config: config, prober: NwHttpClient())
    let fetcher = NwHttpClient()
    let staging = Staging(root: Staging.defaultRoot)
    let writer = MacPasteboard()
    let client = PullClient(config: config, resolver: resolver, fetcher: fetcher,
                             staging: staging, writer: writer)

    do {
        let result = try client.pull()
        print("Готово: kind=\(result.kind.rawValue), файлов=\(result.fileCount), байт=\(result.bytes)")
    } catch {
        printErr(describePullFailure(error))
        exit(1)
    }
}

// MARK: - Конфиг и его ошибки

func loadValidatedConfig(at url: URL) -> Config {
    do {
        let config = try Config.load(at: url)
        try config.validate()
        return config
    } catch {
        printErr(configErrorMessage(error, configURL: url))
        exit(1)
    }
}

func configErrorMessage(_ error: Error, configURL: URL) -> String {
    guard let configError = error as? ConfigError else {
        return "Не удалось прочитать конфиг \(configURL.path): \(describe(error))"
    }

    switch configError {
    case .invalidPort(let port):
        return "Конфиг \(configURL.path): некорректный порт \(port). " +
               "Впишите значение от 1 до 65535 в поле \"port\"."
    case .emptyToken:
        return "Конфиг \(configURL.path): токен пуст. " +
               "Впишите непустую строку в поле \"token\" (или удалите файл — он пересоздастся с новым токеном)."
    case .noPeers:
        return "Конфиг \(configURL.path): список соседей пуст. " +
               "Впишите адрес соседней машины в поле \"peers\", например [\"192.168.1.23\"]."
    case .invalidMaxBytes(let maxBytes):
        return "Конфиг \(configURL.path): некорректный maxBytes=\(maxBytes). " +
               "Впишите положительное число байт в поле \"maxBytes\"."
    case .malformed(let detail):
        return "Конфиг \(configURL.path) повреждён и не читается: \(detail). " +
               "Проверьте синтаксис JSON или удалите файл — он пересоздастся заново."
    }
}

// MARK: - PullError → человекочитаемая строка

/// Carried-over note (задача 11): `PullClient.pull()` ловит только `HttpClientError`
/// на пути `fetchManifest` — совсем битый JSON от соседа долетит сюда сырым
/// `DecodingError`, а не `PullError`. Разбор `PullError` по случаям — правильно, но
/// ветка "всё остальное" обязана существовать и не превращаться в голый
/// `Optional(...)` дамп.
func describePullFailure(_ error: Error) -> String {
    guard let pullError = error as? PullError else {
        return "Непредвиденная ошибка при pull(): \(describe(error))"
    }

    switch pullError {
    case .noPeer:
        return "Сосед не найден: никто из адресов в \"peers\" не отвечает."
    case .peerEmpty:
        return "Буфер соседа пуст — нечего забирать."
    case .tooLarge(let totalSize, let maxBytes):
        return "Содержимое соседа весит \(totalSize) байт — больше лимита maxBytes=\(maxBytes)."
    case .changedMidTransfer:
        return "Буфер соседа изменился прямо во время передачи. Попробуйте ещё раз."
    case .transport(let detail):
        return "Ошибка обмена с соседом: \(detail)"
    }
}

// MARK: - Тост про файлы после хоткея

/// Русское склонение «файл/файла/файлов» по числу — используется только в тосте
/// после хоткея (`Config.autoPaste` и pull файлов), больше нигде в проекте текст
/// не согласуется с числом.
func pluralizedFiles(_ count: Int) -> String {
    let mod100 = count % 100
    let mod10 = count % 10

    let word: String
    if (11...14).contains(mod100) {
        word = "файлов"
    } else if mod10 == 1 {
        word = "файл"
    } else if (2...4).contains(mod10) {
        word = "файла"
    } else {
        word = "файлов"
    }

    return "\(count) \(word)"
}

/// Округлённый размер в мегабайтах (десятичных, ×1_000_000 — как подписи размера
/// файлов в Finder). Ненулевой размер, округлившийся к 0 МБ, показывается как 1 МБ —
/// иначе тост про реально скачанные файлы выглядел бы как «0 МБ», что похоже на баг.
func megabytesString(_ bytes: Int) -> String {
    guard bytes > 0 else { return "0 МБ" }
    let megabytes = max(1, Int((Double(bytes) / 1_000_000).rounded()))
    return "\(megabytes) МБ"
}

// MARK: - Мелкие утилиты вывода

func prettyPrintedManifest(_ manifest: Manifest) throws -> String {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    let data = try encoder.encode(manifest)
    return String(decoding: data, as: UTF8.self)
}

/// `String(describing:)`, а не строковая интерполяция ошибки напрямую — единая точка,
/// чтобы нигде в файле нельзя было случайно напечатать `Optional(...)`, обернув
/// ошибку в необязательный тип перед выводом.
func describe(_ error: Error) -> String {
    String(describing: error)
}

func printErr(_ message: String) {
    FileHandle.standardError.write(Data((message + "\n").utf8))
}

func printUsage() {
    let usage = """
    Использование: lanclipd [команда] [--config <путь>]

    Команды:
      serve   поднять HTTP-сервер и обслуживать буфер обмена (по умолчанию)
      status  показать свой порт, seq/kind буфера и адрес живого соседа
      get     показать манифест соседа в JSON, ничего не меняя локально
      pull    забрать содержимое буфера соседа и вставить в свой буфер

    Флаги:
      --config <путь>  путь к файлу конфига (по умолчанию \(Config.defaultURL.path))
      -h, --help        показать эту справку
    """
    print(usage)
}
