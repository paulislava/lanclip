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
        // Находка I10 финального ревью: запоминает, ответил ли хоть один адрес из
        // Peers в ПОСЛЕДНЕМ переборе, но отверг токен (ProbeOutcome.RejectedToken).
        // Позволяет вызывающей стороне (PullClient, Program.cs) отличить "сосед
        // выключен/недостижим" от "сосед жив, но токен не совпадает" — раньше оба
        // случая давали одно и то же null из Resolve().
        bool sawTokenRejection;

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

            bool tokenRejectedByAnyPeer = false;
            if (config.Peers != null)
            {
                foreach (string host in config.Peers)
                {
                    ProbeOutcome outcome = prober.Probe(host, config.Port, config.Token, probeTimeoutMs);
                    if (outcome == ProbeOutcome.Alive)
                    {
                        lock (gate)
                        {
                            cachedAddress = host;
                            sawTokenRejection = false;
                        }
                        return host;
                    }
                    if (outcome == ProbeOutcome.RejectedToken)
                    {
                        tokenRejectedByAnyPeer = true;
                    }
                }
            }

            lock (gate)
            {
                sawTokenRejection = tokenRejectedByAnyPeer;
            }
            return null;
        }

        // true, если последний (неудачный) Resolve() увидел хотя бы один ответ с
        // отвергнутым токеном — см. PullException.NoPeer(tokenRejected:). Отражает
        // только САМЫЙ ПОСЛЕДНИЙ перебор: успешный Resolve() (нашёл живого) сам
        // сбрасывает флаг в false, а Invalidate() намеренно его не трогает —
        // значение имеет смысл только сразу после Resolve(), вернувшего null.
        public bool LastResolveSawTokenRejection
        {
            get
            {
                lock (gate)
                {
                    return sawTokenRejection;
                }
            }
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
