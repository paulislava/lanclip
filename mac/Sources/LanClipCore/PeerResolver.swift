import Foundation

/// Находит живого соседа из `config.peers` и кеширует его адрес, чтобы не
/// опрашивать сеть на каждый хоткей/pull. Кеш живёт до явного `invalidate()`.
public final class PeerResolver {
    private let config: Config
    private let prober: HealthProbing
    private let timeout: TimeInterval

    private let lock = NSLock()
    private var cachedAddress: String?
    /// Находка I10 финального ревью: запоминает, ответил ли хоть один адрес из
    /// `peers` в ПОСЛЕДНЕМ переборе, но отверг токен (`ProbeOutcome.rejectedToken`).
    /// Позволяет вызывающей стороне (`PullClient`, CLI) отличить «сосед выключен/
    /// недостижим» от «сосед жив, но токен не совпадает» — раньше оба случая
    /// давали одно и то же `nil` из `resolve()`.
    private var sawTokenRejection = false

    public init(config: Config, prober: HealthProbing, timeout: TimeInterval = 2) {
        self.config = config
        self.prober = prober
        self.timeout = timeout
    }

    /// Возвращает кешированный живой адрес, если он уже известен, иначе перебирает
    /// `config.peers` по порядку и опрашивает каждого через `prober.probe`, пока не
    /// найдёт живого — тот и кешируется. Если живых нет, возвращает `nil` и кеш не
    /// заполняет (следующий вызов повторит перебор).
    public func resolve() -> String? {
        lock.lock()
        if let cached = cachedAddress {
            lock.unlock()
            return cached
        }
        lock.unlock()

        var tokenRejectedByAnyPeer = false
        for host in config.peers {
            switch prober.probe(host: host, port: config.port, token: config.token, timeout: timeout) {
            case .alive:
                lock.lock()
                cachedAddress = host
                sawTokenRejection = false
                lock.unlock()
                return host
            case .rejectedToken:
                tokenRejectedByAnyPeer = true
            case .unreachable:
                break
            }
        }

        lock.lock()
        sawTokenRejection = tokenRejectedByAnyPeer
        lock.unlock()
        return nil
    }

    /// `true`, если последний (неудачный) `resolve()` увидел хотя бы один ответ с
    /// отвергнутым токеном — см. `PullError.noPeer(tokenRejected:)`. Отражает
    /// только САМЫЙ ПОСЛЕДНИЙ перебор: успешный `resolve()` (нашёл живого) сам
    /// сбрасывает флаг в `false`, а `invalidate()` намеренно его не трогает —
    /// значение имеет смысл только сразу после `resolve()`, вернувшего `nil`.
    public var lastResolveSawTokenRejection: Bool {
        lock.lock()
        defer { lock.unlock() }
        return sawTokenRejection
    }

    /// Сбрасывает кеш живого адреса — следующий `resolve()` начнёт перебор
    /// `config.peers` заново с начала списка, а не продолжит с места остановки.
    public func invalidate() {
        lock.lock()
        cachedAddress = nil
        lock.unlock()
    }
}
