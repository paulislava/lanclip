import Foundation
import Network

/// Итог одной health-пробы — находка I10 финального ревью: раньше `probe`
/// возвращал голый `Bool`, поэтому `401` (сосед жив, но отверг токен) и отказ
/// соединения (сосед выключен/недостижим) сворачивались в одно и то же
/// «сосед не найден». Опечатка при переносе токена — самая вероятная ошибка
/// первой настройки, и старое сообщение отправляло пользователя чинить сеть и
/// файрвол вместо конфига.
public enum ProbeOutcome: Equatable, Sendable {
    /// Ответил `200` — сосед жив и токен принят.
    case alive
    /// Ответил, но не `200` (в первую очередь `401`) — сосед жив, токен неверный.
    case rejectedToken
    /// Не ответил вовсе (таймаут, отказ соединения, недостижимый хост).
    case unreachable
}

/// Проверка живости соседа: `GET /health` с ожиданием на `timeout`, без деталей ошибки.
public protocol HealthProbing: Sendable {
    func probe(host: String, port: Int, token: String, timeout: TimeInterval) -> ProbeOutcome
}

/// Загрузка манифеста и блобов буфера соседа.
public protocol BlobFetching: Sendable {
    func manifest(host: String, port: Int, token: String) throws -> Manifest
    func blob(host: String, port: Int, token: String, index: Int, seq: Int, to file: URL?) throws -> Data?
}

public enum HttpClientError: Error, Equatable {
    case timeout
    case status(Int)
    case transport(String)
}

/// HTTP-клиент поверх `NWConnection`. `URLSession` использовать нельзя — ATS блокирует
/// plain-HTTP из процессов без Info.plist (см. `HttpServer.swift` — тот же довод для сервера).
///
/// Каждый вызов открывает собственное соединение, шлёт один запрос с `Connection: close`
/// и синхронно ждёт ответ через `DispatchSemaphore`, поэтому наружу класс выглядит как
/// обычный блокирующий клиент, хотя транспорт целиком асинхронный.
public final class NwHttpClient: HealthProbing, BlobFetching, @unchecked Sendable {
    /// Потолок накопления ответа в памяти (манифест, `blob(to: nil)`). Совпадает с
    /// дефолтным `Config.maxBytes` — это тот же содержательный предел одного блоба
    /// буфера обмена, только на клиентской стороне.
    private static let maxInMemoryResponseBytes = Config.defaultMaxBytes
    /// Потолок накопления, пока не найдены заголовки ответа (для файлового варианта).
    /// Заголовки всегда крошечные — это защита от зависшего соединения, слающего мусор
    /// без `\r\n\r\n`, а не реальный лимит для нормальной работы.
    private static let maxHeaderBytes = 65_536

    /// Дефолт зеркалит `HttpWebRequest.ReadWriteTimeout` на Windows-стороне
    /// (`win/src/HttpClient.cs`, `WebBlobFetcher.ReadWriteTimeoutMs`) — обе платформы
    /// теперь явно используют одно и то же число для одной и той же фазы передачи,
    /// а не совпадают по умолчанию случайно.
    public static let defaultProgressTimeout: TimeInterval = 300

    /// Сколько ждать БЕЗ единого байта ответа — от старта соединения до первого
    /// полученного чанка. Зеркало `HttpWebRequest.Timeout` на Windows (время до
    /// заголовков ответа).
    private let timeout: TimeInterval
    /// Сколько ждать МЕЖДУ последовательными чанками уже начавшегося ответа — сбрасывается
    /// при каждом полученном чанке (находка I2 финального ревью). Раньше `timeout`
    /// был бюджетом на ВЕСЬ блоб целиком: семафор заводился до старта соединения и
    /// снимался только когда тело было записано полностью, так что передача блоба
    /// больше ~50–200 МБ по Wi-Fi гарантированно упиралась в дефолтные 10 секунд,
    /// даже если данные исправно текли. Windows этой болезни не знал: `request.Timeout`
    /// у него относится только к получению ответа (до заголовков), а чтение тела идёт
    /// под `ReadWriteTimeout`, применяемым к каждому отдельному чтению потока.
    private let progressTimeout: TimeInterval
    private let queue = DispatchQueue(label: "lanclip.httpclient")

    public init(timeout: TimeInterval = 10, progressTimeout: TimeInterval = NwHttpClient.defaultProgressTimeout) {
        self.timeout = timeout
        self.progressTimeout = progressTimeout
    }

    // MARK: - HealthProbing

    public func probe(host: String, port: Int, token: String, timeout: TimeInterval) -> ProbeOutcome {
        guard let outcome = try? perform(host: host, port: port, token: token, path: "/health",
                                          timeout: timeout, file: nil) else {
            return .unreachable
        }
        if outcome.status == 200 { return .alive }
        // `HttpServer.route` отвечает 401 без тела ровно на неверный/отсутствующий
        // токен — это и есть искомый "сосед жив, но токен неверный". Любой другой
        // код (403 — не должен случаться для приватного LAN-адреса, 404/405 —
        // сосед вообще не lanclip) трактуется как unreachable: мы не притворяемся,
        // что знаем причину точнее, чем "не удалось получить внятный ответ".
        if outcome.status == 401 { return .rejectedToken }
        return .unreachable
    }

    // MARK: - BlobFetching

    public func manifest(host: String, port: Int, token: String) throws -> Manifest {
        let outcome = try perform(host: host, port: port, token: token, path: "/clip",
                                   timeout: timeout, file: nil)
        guard outcome.status == 200 else { throw HttpClientError.status(outcome.status) }
        return try Manifest.decode(outcome.body ?? Data())
    }

    public func blob(host: String, port: Int, token: String, index: Int, seq: Int, to file: URL?) throws -> Data? {
        let path = "/clip/blob/\(index)?seq=\(seq)"
        let outcome = try perform(host: host, port: port, token: token, path: path,
                                   timeout: timeout, file: file)
        guard outcome.status == 200 else { throw HttpClientError.status(outcome.status) }
        return outcome.body
    }

    // MARK: - Core transport

    private struct PerformOutcome {
        let status: Int
        let headers: [String: String]
        /// `nil`, когда тело писалось потоком в файл, а не собиралось в память.
        let body: Data?
    }

    private func perform(host: String, port: Int, token: String, path: String,
                          timeout: TimeInterval, file: URL?) throws -> PerformOutcome {
        guard (1...Int(UInt16.max)).contains(port), let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else {
            throw HttpClientError.transport("некорректный порт \(port)")
        }

        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        let box = OutcomeBox()
        let semaphore = DispatchSemaphore(value: 0)
        let requestHead = NwHttpClient.requestHead(path: path, host: host, token: token)

        // `[weak connection]` разрывает ARC-цикл: `connection.stateUpdateHandler`
        // хранит это самое замыкание, и если бы оно захватывало `connection` сильно,
        // получился бы самозамкнутый граф (connection -> handler -> connection),
        // который ARC не в силах разорвать сам — объект жил бы до конца процесса
        // (резидентный агент, вызовы на каждый хоткей/резолв соседа — то есть по
        // одному такому графу на вызов, и без выхода). `connection.stateUpdateHandler
        // = nil` ниже (и в перформе после `cancel()`) — вторая, независимая линия
        // защиты: она освобождает замыкание сразу, не дожидаясь, пока `connection`
        // потеряет последнюю сильную ссылку сама по себе.
        connection.stateUpdateHandler = { [weak connection] state in
            guard let connection else { return }
            switch state {
            case .ready:
                // `NWConnection` может повторно войти в `.ready` после смены сетевого
                // пути (например, роуминг Wi-Fi) уже отправив запрос — без этой защиты
                // второй `.ready` заново вызвал бы `send`, дописав второй запрос в тот
                // же сокет и испортив байтовый поток ответа.
                guard box.markRequestSentOnce() else { return }
                connection.send(content: requestHead, completion: .contentProcessed { error in
                    if let error {
                        if box.trySettle(.failure(.transport(String(describing: error)))) {
                            semaphore.signal()
                        }
                        return
                    }
                    if let file {
                        NwHttpClient.receiveHeadThenStream(connection: connection, buffer: Data(),
                                                            fileURL: file, box: box, semaphore: semaphore)
                    } else {
                        NwHttpClient.receiveInMemory(connection: connection, buffer: Data(),
                                                      box: box, semaphore: semaphore)
                    }
                })
            case .waiting(let error):
                // Эмпирически (macOS 14, loopback): отказ в соединении (ECONNREFUSED)
                // даёт `.waiting`, а НЕ `.failed`, и в `.waiting` виснет бессрочно — без
                // особого разбора клиент прождал бы весь `timeout` и вернул `.timeout`
                // вместо требуемого `.transport`. Но `.waiting` — штатное состояние
                // ожидания сети (смена маршрута, DNS, роуминг Wi-Fi, сосед ещё
                // загружается), и в большинстве этих случаев соединение установилось
                // бы само в пределах `timeout`. Поэтому завершаем немедленно только на
                // классе «в соединении определённо отказано», а по всем прочим
                // причинам `.waiting` даём таймауту отработать естественно.
                guard NwHttpClient.isDefinitiveConnectionFailure(error) else { return }
                if box.trySettle(.failure(.transport(String(describing: error)))) {
                    semaphore.signal()
                }
            case .failed(let error):
                if box.trySettle(.failure(.transport(String(describing: error)))) {
                    semaphore.signal()
                }
            case .cancelled:
                // Штатная отмена (после успеха, ошибки или по таймауту) — settle уже
                // зафиксировала исход, trySettle тут не пройдёт и просто ничего не
                // сделает. Обнуляем handler и здесь тоже (вторая линия защиты от
                // цикла — см. комментарий выше), на случай гонки, когда `perform()`
                // не успел сделать это сам до того, как отмена долетела до колбэка.
                connection.stateUpdateHandler = nil
                if box.trySettle(.failure(.transport("соединение отменено до ответа"))) {
                    semaphore.signal()
                }
            default:
                break
            }
        }

        connection.start(queue: queue)

        // Дедлайн больше не фиксированная точка на весь вызов: пока не пришло ни
        // байта ответа, окно — `timeout` (время на соединение и первый чанк); как
        // только `receiveInMemory`/`receiveHeadThenStream`/`pumpBodyToFile` фиксируют
        // хоть один полученный чанк через `box.recordProgress()`, окно переключается
        // на `progressTimeout` и отсчитывается заново от последнего чанка — большой
        // блоб, который продолжает течь, никогда не упрётся в `timeout`, но
        // застрявшая на середине передача всё равно оборвётся не позже
        // `progressTimeout` после последнего байта.
        let pollInterval: TimeInterval = 0.5
        var timedOut = false
        while true {
            let (lastActivity, hasProgress) = box.activitySnapshot()
            let window = hasProgress ? progressTimeout : timeout
            let remaining = lastActivity.addingTimeInterval(window).timeIntervalSinceNow
            if remaining <= 0 {
                timedOut = true
                break
            }
            if semaphore.wait(timeout: .now() + min(remaining, pollInterval)) == .success {
                break
            }
        }

        if timedOut {
            // Помечаем исход зафиксированным ДО cancel(): колбэк `.cancelled`/поздний
            // колбэк чтения, который сработает уже после этого момента, увидит
            // `trySettle` вернувшим false и не станет сигналить семафор, по которому
            // уже никто не ждёт, и не тронет состояние, которое мы сейчас читаем.
            box.markSettledExternally()
            connection.cancel()
            connection.stateUpdateHandler = nil
            throw HttpClientError.timeout
        }

        connection.cancel()
        connection.stateUpdateHandler = nil

        switch box.consume() {
        case .success(let outcome): return outcome
        case .failure(let error): throw error
        case nil: throw HttpClientError.transport("соединение завершилось без ответа")
        }
    }

    /// `.waiting` с такой причиной не разрешится само по себе — это отказ на уровне
    /// сети/хоста, а не временная задержка. Сюда попадает эмпирически проверенный
    /// ECONNREFUSED (недостижимый порт на 127.0.0.1 виснет в `.waiting` бессрочно, см.
    /// комментарий у вызова) и явно родственные коды того же класса «пункт назначения
    /// определённо недостижим прямо сейчас». Любая другая причина `.waiting` (DNS,
    /// смена маршрута, Wi-Fi роуминг, сосед ещё не поднял сокет) может разрешиться в
    /// пределах `timeout` — по ней клиент ждёт естественного таймаута, а не рвёт сразу.
    private static func isDefinitiveConnectionFailure(_ error: NWError) -> Bool {
        guard case .posix(let code) = error else { return false }
        switch code {
        case .ECONNREFUSED, .EHOSTUNREACH, .ENETUNREACH, .EHOSTDOWN, .ENETDOWN:
            return true
        default:
            return false
        }
    }

    private static func requestHead(path: String, host: String, token: String) -> Data {
        var text = "GET \(path) HTTP/1.1\r\n"
        text += "Host: \(host)\r\n"
        text += "X-Clip-Token: \(token)\r\n"
        text += "Connection: close\r\n"
        text += "\r\n"
        return Data(text.utf8)
    }

    // MARK: - In-memory receive (health / manifest / blob(to: nil))

    private static func receiveInMemory(connection: NWConnection, buffer: Data,
                                         box: OutcomeBox, semaphore: DispatchSemaphore) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { data, _, isComplete, error in
            var buffer = buffer
            if let data, !data.isEmpty {
                buffer.append(data)
                box.recordProgress()
            }

            if buffer.count > maxInMemoryResponseBytes {
                if box.trySettle(.failure(.transport("ответ превысил предел \(maxInMemoryResponseBytes) байт в памяти"))) {
                    semaphore.signal()
                }
                return
            }

            do {
                let parsed = try parseHttpResponse(buffer)
                if box.trySettle(.success(PerformOutcome(status: parsed.status, headers: parsed.headers, body: parsed.body))) {
                    semaphore.signal()
                }
            } catch HttpParseError.incomplete {
                if isComplete || error != nil {
                    if box.trySettle(.failure(.transport("соединение закрылось до полного ответа"))) {
                        semaphore.signal()
                    }
                    return
                }
                receiveInMemory(connection: connection, buffer: buffer, box: box, semaphore: semaphore)
            } catch {
                if box.trySettle(.failure(.transport("не удалось разобрать ответ: \(error)"))) {
                    semaphore.signal()
                }
            }
        }
    }

    // MARK: - Streaming receive (blob(to: file))

    /// Разбирает только заголовки ответа (без ожидания полного тела) — отдельная от
    /// `parseHttpResponse` функция, потому что та осознанно требует уже накопленное
    /// целиком тело (`bodySlice.count >= declared`), а именно этого при потоковой
    /// записи в файл мы избегаем: тело может весить сотни мегабайт.
    private static func parseResponseHead(_ data: Data) throws -> (status: Int, headers: [String: String], bodyStart: Data.Index)? {
        let crlfcrlf = Data("\r\n\r\n".utf8)
        guard let separator = data.range(of: crlfcrlf) else { return nil }

        let headText = String(decoding: data[data.startIndex..<separator.lowerBound], as: UTF8.self)
        var lines = headText.components(separatedBy: "\r\n")
        guard let statusLine = lines.first else { throw HttpParseError.malformed }
        lines.removeFirst()

        let parts = statusLine.split(separator: " ")
        guard parts.count >= 2, let status = Int(parts[1]) else { throw HttpParseError.malformed }

        var headers: [String: String] = [:]
        for line in lines where !line.isEmpty {
            let kv = line.split(separator: ":", maxSplits: 1)
            guard kv.count == 2 else { continue }
            headers[kv[0].lowercased()] = kv[1].trimmingCharacters(in: .whitespaces)
        }
        return (status, headers, separator.upperBound)
    }

    private static func receiveHeadThenStream(connection: NWConnection, buffer: Data, fileURL: URL,
                                               box: OutcomeBox, semaphore: DispatchSemaphore) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { data, _, isComplete, error in
            var buffer = buffer
            if let data, !data.isEmpty {
                buffer.append(data)
                box.recordProgress()
            }

            if buffer.count > maxHeaderBytes {
                if box.trySettle(.failure(.transport("заголовки ответа превысили предел \(maxHeaderBytes) байт"))) {
                    semaphore.signal()
                }
                return
            }

            do {
                guard let head = try parseResponseHead(buffer) else {
                    if isComplete || error != nil {
                        if box.trySettle(.failure(.transport("соединение закрылось до заголовков ответа"))) {
                            semaphore.signal()
                        }
                        return
                    }
                    receiveHeadThenStream(connection: connection, buffer: buffer, fileURL: fileURL,
                                          box: box, semaphore: semaphore)
                    return
                }

                guard head.status == 200 else {
                    // Ошибочные статусы (401/403/404/409/500) сервер шлёт без тела —
                    // файл создавать незачем, вызывающий сам решит по `.status(...)`,
                    // не трогая место назначения.
                    if box.trySettle(.success(PerformOutcome(status: head.status, headers: head.headers, body: nil))) {
                        semaphore.signal()
                    }
                    return
                }

                let declaredLength = head.headers["content-length"].flatMap(Int.init) ?? 0
                let bodyPrefix = Data(buffer[head.bodyStart...].prefix(declaredLength))

                guard FileManager.default.createFile(atPath: fileURL.path, contents: nil) else {
                    if box.trySettle(.failure(.transport("не удалось создать файл \(fileURL.path)"))) {
                        semaphore.signal()
                    }
                    return
                }
                let handle: FileHandle
                do {
                    handle = try FileHandle(forWritingTo: fileURL)
                } catch {
                    if box.trySettle(.failure(.transport("не удалось открыть файл для записи: \(error)"))) {
                        semaphore.signal()
                    }
                    return
                }

                do {
                    try handle.write(contentsOf: bodyPrefix)
                } catch {
                    try? handle.close()
                    if box.trySettle(.failure(.transport("не удалось записать блоб на диск: \(error)"))) {
                        semaphore.signal()
                    }
                    return
                }

                let written = bodyPrefix.count
                if written >= declaredLength {
                    try? handle.close()
                    if box.trySettle(.success(PerformOutcome(status: head.status, headers: head.headers, body: nil))) {
                        semaphore.signal()
                    }
                    return
                }

                pumpBodyToFile(connection: connection, handle: handle, written: written,
                               declaredLength: declaredLength, status: head.status, headers: head.headers,
                               box: box, semaphore: semaphore)
            } catch {
                if box.trySettle(.failure(.transport("не удалось разобрать заголовки ответа: \(error)"))) {
                    semaphore.signal()
                }
            }
        }
    }

    /// Пишет байты тела в `FileHandle` по мере поступления, не накапливая их в памяти —
    /// заголовки уже разобраны один раз в `receiveHeadThenStream`.
    private static func pumpBodyToFile(connection: NWConnection, handle: FileHandle, written: Int,
                                        declaredLength: Int, status: Int, headers: [String: String],
                                        box: OutcomeBox, semaphore: DispatchSemaphore) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { data, _, isComplete, error in
            var written = written
            if let data, !data.isEmpty {
                box.recordProgress()
                let remaining = declaredLength - written
                if remaining > 0 {
                    let chunk = Data(data.prefix(remaining))
                    do {
                        try handle.write(contentsOf: chunk)
                        written += chunk.count
                    } catch {
                        try? handle.close()
                        if box.trySettle(.failure(.transport("не удалось записать блоб на диск: \(error)"))) {
                            semaphore.signal()
                        }
                        return
                    }
                }
            }

            if written >= declaredLength {
                try? handle.close()
                if box.trySettle(.success(PerformOutcome(status: status, headers: headers, body: nil))) {
                    semaphore.signal()
                }
                return
            }

            if isComplete || error != nil {
                try? handle.close()
                if box.trySettle(.failure(.transport("соединение закрылось до конца тела (\(written)/\(declaredLength) байт)"))) {
                    semaphore.signal()
                }
                return
            }

            pumpBodyToFile(connection: connection, handle: handle, written: written, declaredLength: declaredLength,
                           status: status, headers: headers, box: box, semaphore: semaphore)
        }
    }

    /// Однопоточная почтовая ячейка для передачи исхода запроса из колбэков сети
    /// (очередь `queue`) обратно на поток, заблокированный в `semaphore.wait`.
    /// `trySettle` фиксирует исход не более одного раза: как только кто-то — колбэк
    /// сети или таймаут в `perform()` — зафиксировал состояние, все последующие
    /// вызовы становятся no-op. Это единственное место, где решается, сигналить ли
    /// семафор: поздний колбэк, пришедший уже после таймаута, увидит `false` и не
    /// станет сигналить по семафору, который никто больше не ждёт.
    private final class OutcomeBox: @unchecked Sendable {
        private let lock = NSLock()
        private var settled = false
        private var requestSent = false
        private var result: Result<PerformOutcome, HttpClientError>?
        /// Момент последнего полученного чанка ответа; инициализируется временем
        /// создания ячейки (то есть примерно временем старта соединения), чтобы до
        /// первого чанка окно ожидания в `perform()` отсчитывалось от начала вызова.
        private var lastActivity = Date()
        /// Хоть один непустой чанк ответа уже получен — переключает окно ожидания в
        /// `perform()` с `timeout` (время на соединение и первый байт) на
        /// `progressTimeout` (время между последовательными чанками).
        private var hasProgress = false

        /// Фиксирует получение непустого чанка данных — сбрасывает "часы бездействия"
        /// для дедлайна `perform()`. Вызывается из колбэков `receive` на очереди сети.
        func recordProgress() {
            lock.lock()
            lastActivity = Date()
            hasProgress = true
            lock.unlock()
        }

        /// Снимок для цикла ожидания в `perform()`: момент последней активности и то,
        /// была ли уже хоть какая-то активность (то есть какое окно ожидания сейчас в силе).
        func activitySnapshot() -> (lastActivity: Date, hasProgress: Bool) {
            lock.lock()
            defer { lock.unlock() }
            return (lastActivity, hasProgress)
        }

        /// Возвращает true только при первом вызове — защита от повторной отправки
        /// запроса, если `.ready` наступит второй раз (см. комментарий у вызова).
        func markRequestSentOnce() -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !requestSent else { return false }
            requestSent = true
            return true
        }

        func trySettle(_ value: Result<PerformOutcome, HttpClientError>) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !settled else { return false }
            settled = true
            result = value
            return true
        }

        /// Вызывается из `perform()` при таймауте — блокирует последующие `trySettle`
        /// без записи собственного результата (результат в этом случае — `.timeout`,
        /// брошенный самим `perform()`, а не хранящийся в ячейке).
        func markSettledExternally() {
            lock.lock()
            settled = true
            lock.unlock()
        }

        func consume() -> Result<PerformOutcome, HttpClientError>? {
            lock.lock()
            defer { lock.unlock() }
            return result
        }
    }
}
