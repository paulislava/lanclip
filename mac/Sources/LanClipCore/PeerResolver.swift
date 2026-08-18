import Foundation

/// Находит живого соседа из `config.peers` и кеширует его адрес, чтобы не
/// опрашивать сеть на каждый хоткей/pull. Кеш живёт до явного `invalidate()`.
public final class PeerResolver {
    private let config: Config
    private let prober: HealthProbing
    private let timeout: TimeInterval

    private let lock = NSLock()
    private var cachedAddress: String?

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

        for host in config.peers {
            if prober.probe(host: host, port: config.port, token: config.token, timeout: timeout) {
                lock.lock()
                cachedAddress = host
                lock.unlock()
                return host
            }
        }
        return nil
    }

    /// Сбрасывает кеш живого адреса — следующий `resolve()` начнёт перебор
    /// `config.peers` заново с начала списка, а не продолжит с места остановки.
    public func invalidate() {
        lock.lock()
        cachedAddress = nil
        lock.unlock()
    }
}
