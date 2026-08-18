import Foundation

/// Ошибки, возникающие при выполнении `PullClient.pull()`. Это единственный публичный
/// исход операции сверх успешного `PullResult` — все ветки протокола (нет соседа,
/// пустой буфер, превышение лимита, гонка с соседской записью, сбой транспорта)
/// сведены сюда, чтобы вызывающая сторона (хоткей, `HttpServer.pull`) могла решить,
/// что показать пользователю, одним `switch`.
public enum PullError: Error, Equatable {
    /// `tokenRejected` — находка I10 финального ревью: раньше `noPeer` покрывал и
    /// "никто не отвечает" (сеть/файрвол), и "сосед ответил 401" (опечатка в
    /// токене) одним и тем же случаем — самая вероятная ошибка первой настройки
    /// отправляла пользователя чинить сеть вместо того, чтобы перепроверить
    /// конфиг. `true`, если хотя бы один адрес из `peers` ответил, но отверг наш
    /// токен (см. `PeerResolver.lastResolveSawTokenRejection`).
    case noPeer(tokenRejected: Bool)
    case peerEmpty
    case tooLarge(totalSize: Int, maxBytes: Int)
    case changedMidTransfer
    case transport(String)
}

/// Оркестрирует полный цикл `pull`: находит живого соседа через `PeerResolver`,
/// забирает у него манифест и содержимое через `BlobFetching`, раскладывает файлы в
/// партию `Staging` и записывает итог в локальный буфер через `ClipboardWriting`.
///
/// Инвариант на всех путях ошибок: буфер либо не тронут вовсе, либо получает полный
/// результат — частичной записи быть не может, потому что `writer.write` вызывается
/// один раз, только после того как все данные (текст/картинка в память, файлы —
/// на диск партии) собраны целиком без ошибок.
///
/// `@unchecked Sendable` (мелкая находка финального ревью, закрывает предупреждение
/// компилятора на захвате `pullClient` в `@Sendable`-замыкании хоткея в `main.swift`):
/// аудит потокобезопасности проведён финальным ревью и чист для фактического
/// использования в этом проекте — `PullClient` иммутабелен (все поля `let`,
/// присваиваются только в `init`), `PeerResolver` синхронизирует своё состояние
/// собственным `NSLock`, `Staging`/`MacPasteboard` состояния между вызовами не
/// держат вовсе (каждый вызов самодостаточен), `SnapshotStore` трогается только с
/// серийной очереди HTTP-сервера, а оба места, откуда вызывается `pull()` (обработчик
/// `POST /pull` и обработчик хоткея), идут через одну и ту же серийную `pullQueue` —
/// то есть сами вызовы `pull()` тоже не гонятся друг с другом. Компилятор не может
/// вывести это сам: `BlobFetching`/`ClipboardWriting` — протоколы, а не конкретные
/// `Sendable`-типы, и Swift обязан консервативно считать классы, хранящие такие
/// экзистенциалы, потенциально не изолированными.
public final class PullClient: @unchecked Sendable {
    private let config: Config
    private let resolver: PeerResolver
    private let fetcher: BlobFetching
    private let staging: Staging
    private let writer: ClipboardWriting

    public init(config: Config, resolver: PeerResolver, fetcher: BlobFetching,
                staging: Staging, writer: ClipboardWriting) {
        self.config = config
        self.resolver = resolver
        self.fetcher = fetcher
        self.staging = staging
        self.writer = writer
    }

    /// Ровно один автоматический повтор при `changedMidTransfer` (рулинг ревью
    /// задачи 26). Мелкая находка финального ревью починила этот комментарий: он
    /// раньше приписывал измеренную частоту "Windows-стороне" — на самом деле замер
    /// (2 из 10 попыток, направление ПК → Mac, файлы с пробелами и кириллицей в
    /// именах) снят как раз с ЭТОГО, Mac-стороннего, `lanclipd pull`, тянущего буфер
    /// с Windows-машины; про частоту НА САМОЙ Windows ничего не измерялось. Первая
    /// гипотеза о причине (синхронность `Clipboard.SetDataObject`/насос сообщений
    /// WinForms) не выдержала разбора ревью и отклонена; настоящая первопричина
    /// гонки НЕ установлена (подробности и отклонённые гипотезы — `ai/ERRORS.md`).
    /// Повтор оправдан устойчивостью протокола к гонке ЛЮБОЙ природы, а не тем, что
    /// причина известна и это частый баг именно здесь — раньше несовпадение `seq`
    /// долетало до пользователя как явная ошибка при каждом случае; теперь протокол
    /// сам пробует ещё раз, прежде чем сдаться.
    ///
    /// Повтор — это весь цикл заново (манифест + скачивание), а не докачка
    /// недостающего блоба: раз `seq` уехал, прежний манифест целиком недействителен,
    /// и докачивать по его `blobs` уже нечего. `attemptPull(host:)` возвращает
    /// совершенно новый результат второй попытки — от первой, сорвавшейся попытки, в
    /// локальном буфере ничего не остаётся (она обрывается до `writer.write`, как и
    /// раньше), а на диске остаётся не более чем мусорная партия стейджинга, которую
    /// уберёт `staging.cleanup()` на следующем успешном `pull()`.
    ///
    /// Ровно один повтор, не цикл до успеха: если буфер соседа меняется непрерывно,
    /// бесконечный повтор превратил бы нажатие хоткея в зависание. Второй
    /// `changedMidTransfer` подряд не перехватывается этим `catch` и летит наружу как
    /// обычная ошибка — ровно так же, как раньше вела себя первая попытка.
    ///
    /// `host` резолвится один раз до обеих попыток: 409 не считается сбоем транспорта
    /// (сосед в порядке, только его буфер уехал), поэтому кеш `PeerResolver` не
    /// трогается ни при первой, ни при второй попытке — искать другого соседа незачем.
    public func pull() throws -> PullResult {
        guard let host = resolver.resolve() else {
            throw PullError.noPeer(tokenRejected: resolver.lastResolveSawTokenRejection)
        }

        do {
            return try attemptPull(host: host)
        } catch PullError.changedMidTransfer {
            return try attemptPull(host: host)
        }
    }

    /// Один полный цикл «манифест → проверка → скачивание → запись» — то, чем раньше
    /// целиком был `pull()`. Вынесено отдельно, чтобы `pull()` мог прогнать его дважды
    /// подряд при `changedMidTransfer`, не дублируя логику.
    private func attemptPull(host: String) throws -> PullResult {
        let manifest = try fetchManifest(host: host)

        guard manifest.kind != .empty else {
            throw PullError.peerEmpty
        }

        // Ruling задачи 3/11: `Manifest.decode` не проверяет межполевые инварианты —
        // манифест `{"kind":"image","seq":1}` без `blobs`, либо с `totalSize`,
        // расходящимся с суммой по `blobs`, декодируется успешно. Мы — первая точка в
        // проекте, где манифест приходит из сети, поэтому именно здесь отлавливается
        // самопротиворечивый манифест — это НЕ то же самое, что 409 при скачивании
        // блоба: там сосед в порядке, просто его буфер уехал между двумя запросами
        // (повтор почти наверняка сработает, кеш резолвера не трогаем). Самопротиворечивый
        // манифест — это дефект кодировщика на той стороне либо подделка: повтор тому же
        // соседу воспроизведёт то же самое, поэтому кеш сбрасывается наравне с прочими
        // ошибками транспорта, и `peers` получает шанс выбрать другого соседа.
        //
        // Заодно здесь же считается настоящий `totalSize` — сумма по `blobs`, а не
        // присланное соседом число: `maxBytes` обязан ловить и того, кто занизил размер
        // в манифесте, а прислал больше данных по факту (см. проверку размера каждого
        // скачанного блоба ниже).
        let totalSize = try validateManifestIntegrity(manifest)

        guard totalSize <= config.maxBytes else {
            throw PullError.tooLarge(totalSize: totalSize, maxBytes: config.maxBytes)
        }

        // Новая партия стейджинга на каждый вызов `attemptPull` — `download(manifest:
        // host:)` вызывает `staging.newBatch()` заново (см. `downloadFiles`), поэтому
        // вторая попытка сама по себе никогда не досыпает файлы в партию первой,
        // сорвавшейся попытки.
        let outcome = try download(manifest: manifest, host: host)

        try writer.write(outcome.content)
        // Уборка — housekeeping вокруг уже состоявшегося успеха: её отказ не должен
        // превращать успешную вставку в ошибку `pull()` (тем же принципом сам
        // `Staging.cleanup()` уже терпим к отказу удаления отдельной партии — см. его
        // `try?` внутри цикла удаления).
        try? staging.cleanup()

        return PullResult(kind: manifest.kind, fileCount: outcome.fileCount, bytes: outcome.bytes)
    }

    // MARK: - Манифест

    private func fetchManifest(host: String) throws -> Manifest {
        do {
            return try fetcher.manifest(host: host, port: config.port, token: config.token)
        } catch {
            // Находка I9 финального ревью: ловился только `HttpClientError`. Если сосед
            // пришлёт что-то, что проходит HTTP-транспорт (200 OK), но не разбирается как
            // валидный `Manifest` (`{"kind":"weird"}`, `"totalSize":"5"` вместо числа —
            // `JSONDecoder` в отличие от C#-стороннего `Convert` строгий и такое не
            // примет), `fetcher.manifest(...)` бросает сырой `DecodingError`, который не
            // ловился этим `catch` — ошибка улетала из `pull()` необёрнутой в `PullError`,
            // и, что хуже, `resolver.invalidate()` не вызывался: Mac продолжил бы
            // долбиться в того же испорченного соседа с закешированным адресом. Любая
            // ошибка на этом пути — сорванный обмен с соседом того же класса, что и
            // `HttpClientError`, поэтому ловится и оборачивается одинаково.
            resolver.invalidate()
            throw PullError.transport(String(describing: error))
        }
    }

    /// Проверяет межполевые инварианты, которые `Manifest.decode` не проверяет сам, и
    /// возвращает настоящий размер содержимого, который должен гейтиться `maxBytes` —
    /// для `.text` это длина текста в UTF-8, для `.image`/`.files` — сумма по `blobs`,
    /// посчитанная нами, а не присланное соседом число (иначе сосед мог бы объявить
    /// `totalSize: 1` и приложить блобы суммарно на гигабайты — лимит `maxBytes` тогда
    /// не работал бы вовсе).
    private func validateManifestIntegrity(_ manifest: Manifest) throws -> Int {
        switch manifest.kind {
        case .text:
            guard let text = manifest.text else {
                throw corruptedManifestError("kind=text без text")
            }
            // Текст, в отличие от файлов, целиком приезжает в теле манифеста и уже
            // лежит в памяти к этому моменту — тот же лимит maxBytes обязан его
            // гейтить на общих основаниях, а не считать текст бесплатным.
            return text.utf8.count

        case .image, .files:
            guard let blobs = manifest.blobs, !blobs.isEmpty else {
                throw corruptedManifestError("kind=\(manifest.kind.rawValue) без blobs")
            }

            // `BlobRef.size` — обычное число с провода, ничем не ограниченное:
            // складывать его наивным `+` нельзя — Swift не заворачивает переполнение,
            // а падает фатальной ошибкой, и это падение достижимо ОДНИМ манифестом,
            // ещё до единого HTTP-запроса за блобом (см. ревью — тот же класс дефекта,
            // что `Content-Length: -1` в парсере задачи 6). Копим сумму вручную с
            // контролем переполнения; отрицательный размер отдельного блоба тоже
            // отклоняем — он не роняет процесс, но незаметно занижает сумму и обходит
            // лимит `maxBytes`.
            var computedTotal = 0
            for blob in blobs {
                guard blob.size >= 0 else {
                    throw corruptedManifestError("blob \(blob.rel) с отрицательным size=\(blob.size)")
                }
                let (sum, overflow) = computedTotal.addingReportingOverflow(blob.size)
                guard !overflow else {
                    throw corruptedManifestError("сумма размеров blobs переполняет Int")
                }
                computedTotal = sum
            }

            if let declaredTotal = manifest.totalSize, declaredTotal != computedTotal {
                throw corruptedManifestError(
                    "totalSize=\(declaredTotal) в манифесте расходится с суммой по blobs=\(computedTotal)")
            }
            return computedTotal

        case .empty:
            return 0 // отсечено раньше в pull()
        }
    }

    /// Самопротиворечивый манифест или блоб, пришедший не того размера, что был
    /// обещан, — не гонка с соседским буфером (та ловится через 409), а дефект/подделка
    /// на стороне отправителя. Кеш резолвера сбрасывается наравне с прочими ошибками
    /// транспорта — см. комментарий в `pull()`.
    private func corruptedManifestError(_ detail: String) -> PullError {
        resolver.invalidate()
        return .transport("манифест соседа испорчен: \(detail)")
    }

    // MARK: - Загрузка содержимого

    private struct DownloadOutcome {
        let content: ClipContent
        let fileCount: Int
        let bytes: Int
    }

    private func download(manifest: Manifest, host: String) throws -> DownloadOutcome {
        switch manifest.kind {
        case .text:
            // Проверено в validateManifestIntegrity(_:).
            let text = manifest.text ?? ""
            return DownloadOutcome(content: .text(text), fileCount: 0, bytes: text.utf8.count)

        case .image:
            return try downloadImage(manifest: manifest, host: host)

        case .files:
            return try downloadFiles(manifest: manifest, host: host)

        case .empty:
            // Отсечено раньше в pull() — сюда попасть невозможно.
            throw PullError.peerEmpty
        }
    }

    private func downloadImage(manifest: Manifest, host: String) throws -> DownloadOutcome {
        // Проверено в validateManifestIntegrity(_:): blobs не nil и не пуст.
        let blob = (manifest.blobs ?? [])[0]

        do {
            guard let data = try fetcher.blob(host: host, port: config.port, token: config.token,
                                               index: blob.i, seq: manifest.seq, to: nil) else {
                throw corruptedManifestError("сервер не вернул тело блоба изображения")
            }
            // Соседу мало объявить малый totalSize — реальный размер каждого блоба
            // сверяется отдельно, иначе лимит maxBytes ловил бы только то, что сосед
            // сам про себя сказал, а не то, что реально пришло.
            guard data.count == blob.size else {
                throw corruptedManifestError(
                    "блоб изображения пришёл размером \(data.count) байт, манифест обещал \(blob.size)")
            }
            return DownloadOutcome(content: .image(data), fileCount: 0, bytes: data.count)
        } catch let error as HttpClientError {
            throw mapBlobFetchError(error)
        }
    }

    private func downloadFiles(manifest: Manifest, host: String) throws -> DownloadOutcome {
        // Проверено в validateManifestIntegrity(_:): blobs не nil и не пуст.
        let blobs = manifest.blobs ?? []
        let batch = try staging.newBatch()

        var urls: [URL] = []
        var totalBytes = 0
        for blob in blobs {
            let destination = try batch.destination(for: blob.rel)
            do {
                _ = try fetcher.blob(host: host, port: config.port, token: config.token,
                                      index: blob.i, seq: manifest.seq, to: destination)
            } catch let error as HttpClientError {
                throw mapBlobFetchError(error)
            }

            let actualSize = try fileSize(at: destination)
            guard actualSize == blob.size else {
                throw corruptedManifestError(
                    "файл \(blob.rel) пришёл размером \(actualSize) байт, манифест обещал \(blob.size)")
            }

            urls.append(destination)
            totalBytes += actualSize
        }

        return DownloadOutcome(content: .files(urls), fileCount: urls.count, bytes: totalBytes)
    }

    private func fileSize(at url: URL) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes[.size] as? NSNumber)?.intValue ?? 0
    }

    /// `409` сигналит, что содержимое соседа сменилось между манифестом и скачиванием
    /// блоба — это не сбой сети (сосед ответил, просто данные устарели), поэтому кеш
    /// резолвера не трогаем. Любой другой статус или транспортная ошибка — то самое
    /// «любая ошибка транспорта», после которой кеш обязан быть сброшен.
    private func mapBlobFetchError(_ error: HttpClientError) -> PullError {
        if case .status(409) = error {
            return .changedMidTransfer
        }
        resolver.invalidate()
        return .transport(String(describing: error))
    }
}
