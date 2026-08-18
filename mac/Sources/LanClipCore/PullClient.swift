import Foundation

/// Ошибки, возникающие при выполнении `PullClient.pull()`. Это единственный публичный
/// исход операции сверх успешного `PullResult` — все ветки протокола (нет соседа,
/// пустой буфер, превышение лимита, гонка с соседской записью, сбой транспорта)
/// сведены сюда, чтобы вызывающая сторона (хоткей, `HttpServer.pull`) могла решить,
/// что показать пользователю, одним `switch`.
public enum PullError: Error, Equatable {
    case noPeer
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
public final class PullClient {
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

    public func pull() throws -> PullResult {
        guard let host = resolver.resolve() else {
            throw PullError.noPeer
        }

        let manifest = try fetchManifest(host: host)

        guard manifest.kind != .empty else {
            throw PullError.peerEmpty
        }

        // Ruling задачи 3/11: `Manifest.decode` не проверяет межполевые инварианты —
        // манифест `{"kind":"image","seq":1}` без `blobs` декодируется успешно. Мы —
        // первая точка в проекте, где манифест приходит из сети, поэтому именно здесь
        // отлавливается сорванная передача, прежде чем её примут за пустой буфер.
        // По духу это тот же случай, что и 409 при скачивании блоба (то, что мы
        // получили, не соответствует тому, что было обещано, потому что состояние
        // соседа изменилось) — сосед жив, поэтому кеш резолвера не сбрасывается.
        try validateManifestIntegrity(manifest)

        let totalSize = manifest.totalSize ?? 0
        guard totalSize <= config.maxBytes else {
            throw PullError.tooLarge(totalSize: totalSize, maxBytes: config.maxBytes)
        }

        let outcome = try download(manifest: manifest, host: host)

        try writer.write(outcome.content)
        try staging.cleanup()

        return PullResult(kind: manifest.kind, fileCount: outcome.fileCount, bytes: outcome.bytes)
    }

    // MARK: - Манифест

    private func fetchManifest(host: String) throws -> Manifest {
        do {
            return try fetcher.manifest(host: host, port: config.port, token: config.token)
        } catch let error as HttpClientError {
            resolver.invalidate()
            throw PullError.transport(String(describing: error))
        }
    }

    private func validateManifestIntegrity(_ manifest: Manifest) throws {
        switch manifest.kind {
        case .text:
            guard manifest.text != nil else { throw PullError.changedMidTransfer }
        case .image, .files:
            guard let blobs = manifest.blobs, !blobs.isEmpty else { throw PullError.changedMidTransfer }
        case .empty:
            break // отсечено раньше в pull()
        }
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
        do {
            guard let data = try fetcher.blob(host: host, port: config.port, token: config.token,
                                               index: 0, seq: manifest.seq, to: nil) else {
                resolver.invalidate()
                throw PullError.transport("сервер не вернул тело блоба изображения")
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
            urls.append(destination)
            totalBytes += blob.size
        }

        return DownloadOutcome(content: .files(urls), fileCount: urls.count, bytes: totalBytes)
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
