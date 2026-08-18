using System;

namespace LanClip
{
    // Находит живого соседа из config.Peers и кеширует его адрес, чтобы не опрашивать
    // сеть на каждый хоткей/pull. Кеш живёт до явного Invalidate(). Зеркало
    // mac/Sources/LanClipCore/PeerResolver.swift.
    class PeerResolver
    {
        // Совпадает с дефолтом Mac-стороннего PeerResolver.init(timeout: TimeInterval = 2).
        const int DefaultProbeTimeoutMs = 2000;

        readonly Config config;
        readonly IHealthProber prober;
        readonly int probeTimeoutMs;

        readonly object gate = new object();
        string cachedAddress;

        public PeerResolver(Config config, IHealthProber prober)
            : this(config, prober, DefaultProbeTimeoutMs)
        {
        }

        public PeerResolver(Config config, IHealthProber prober, int probeTimeoutMs)
        {
            this.config = config;
            this.prober = prober;
            this.probeTimeoutMs = probeTimeoutMs;
        }

        // Возвращает кешированный живой адрес, если он уже известен, иначе перебирает
        // config.Peers по порядку и опрашивает каждого через prober.Probe, пока не
        // найдёт живого — тот и кешируется. Если живых нет, возвращает null и кеш не
        // заполняет (следующий вызов повторит перебор).
        public string Resolve()
        {
            lock (gate)
            {
                if (cachedAddress != null)
                {
                    return cachedAddress;
                }
            }

            if (config.Peers != null)
            {
                foreach (string host in config.Peers)
                {
                    if (prober.Probe(host, config.Port, config.Token, probeTimeoutMs))
                    {
                        lock (gate)
                        {
                            cachedAddress = host;
                        }
                        return host;
                    }
                }
            }

            return null;
        }

        // Сбрасывает кеш живого адреса — следующий Resolve() начнёт перебор
        // config.Peers заново с начала списка, а не продолжит с места остановки.
        public void Invalidate()
        {
            lock (gate)
            {
                cachedAddress = null;
            }
        }
    }
}
