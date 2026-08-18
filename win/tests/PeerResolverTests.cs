using System.Collections.Generic;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/PeerResolverTests.swift (задача 10/21).
    static class PeerResolverTests
    {
        // Подставной IHealthProber, считающий обращения по каждому адресу — тесты
        // резолвера проверяют не только итоговый результат, но и то, что кеш не даёт
        // резолверу ходить в сеть повторно, пока не сброшен.
        class FakeProber : IHealthProber
        {
            readonly Dictionary<string, bool> alive;
            public readonly Dictionary<string, int> CallCounts = new Dictionary<string, int>();

            public FakeProber(Dictionary<string, bool> alive)
            {
                this.alive = alive;
            }

            public int TotalCalls
            {
                get
                {
                    int total = 0;
                    foreach (int c in CallCounts.Values) { total += c; }
                    return total;
                }
            }

            public void SetAlive(bool isAlive, string host)
            {
                alive[host] = isAlive;
            }

            public bool Probe(string host, int port, string token, int timeoutMs)
            {
                int count;
                CallCounts.TryGetValue(host, out count);
                CallCounts[host] = count + 1;

                bool result;
                return alive.TryGetValue(host, out result) && result;
            }
        }

        static Config MakeConfig(List<string> peers)
        {
            Config config = new Config();
            config.Port = Config.DefaultPort;
            config.Token = "test-token";
            config.Peers = peers;
            config.MaxBytes = Config.DefaultMaxBytes;
            config.AutoPaste = true;
            return config;
        }

        static Dictionary<string, bool> Alive(params object[] pairs)
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                map[(string)pairs[i]] = (bool)pairs[i + 1];
            }
            return map;
        }

        public static void Register()
        {
            T.Run("resolve returns first live address", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", true, "10.0.0.2", true));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq("10.0.0.1", resolver.Resolve(), "resolved");
            });

            T.Run("resolve skips dead addresses", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", false, "10.0.0.2", true));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq("10.0.0.2", resolver.Resolve(), "resolved");
            });

            T.Run("resolve returns null when none alive", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", false, "10.0.0.2", false));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq(null, resolver.Resolve(), "resolved");
            });

            T.Run("repeated resolve does not probe network again", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", true, "10.0.0.2", true));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq("10.0.0.1", resolver.Resolve(), "first resolve");
                int callsAfterFirst = prober.TotalCalls;
                T.Eq("10.0.0.1", resolver.Resolve(), "second resolve");
                T.Eq("10.0.0.1", resolver.Resolve(), "third resolve");

                T.Eq(callsAfterFirst, prober.TotalCalls, "кеш должен предотвращать повторные обращения к сети");
            });

            T.Run("invalidate causes reprobe", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", true));
                PeerResolver resolver = new PeerResolver(MakeConfig(new List<string> { "10.0.0.1" }), prober);

                T.Eq("10.0.0.1", resolver.Resolve(), "first resolve");
                int callsBeforeInvalidate = prober.CallCounts["10.0.0.1"];

                resolver.Invalidate();
                T.Eq("10.0.0.1", resolver.Resolve(), "resolve after invalidate");

                T.Eq(callsBeforeInvalidate + 1, prober.CallCounts["10.0.0.1"],
                    "после Invalidate() резолвер обязан снова опросить сеть");
            });

            T.Run("after invalidate former live address that died is skipped for next alive", delegate
            {
                FakeProber prober = new FakeProber(Alive("10.0.0.1", true, "10.0.0.2", true));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq("10.0.0.1", resolver.Resolve(), "first resolve");

                prober.SetAlive(false, "10.0.0.1");
                resolver.Invalidate();

                T.Eq("10.0.0.2", resolver.Resolve(), "resolve after death of first");
            });

            T.Run("invalidate restarts probing from the beginning of the list", delegate
            {
                // После Invalidate() перебор обязан идти с начала списка Peers, а не
                // продолжаться с адреса, на котором резолвер остановился в прошлый раз.
                FakeProber prober = new FakeProber(Alive("10.0.0.1", false, "10.0.0.2", true));
                PeerResolver resolver = new PeerResolver(
                    MakeConfig(new List<string> { "10.0.0.1", "10.0.0.2" }), prober);

                T.Eq("10.0.0.2", resolver.Resolve(), "first resolve");
                T.Eq(1, prober.CallCounts["10.0.0.1"], "first probe count");

                resolver.Invalidate();
                prober.SetAlive(true, "10.0.0.1");

                T.Eq("10.0.0.1", resolver.Resolve(), "resolve after invalidate");
                T.Eq(2, prober.CallCounts["10.0.0.1"], "перебор после Invalidate() должен снова начаться с 10.0.0.1");
            });

            T.Run("resolve with empty peers returns null without probing", delegate
            {
                FakeProber prober = new FakeProber(Alive());
                PeerResolver resolver = new PeerResolver(MakeConfig(new List<string>()), prober);

                T.Eq(null, resolver.Resolve(), "resolved");
                T.Eq(0, prober.TotalCalls, "no probing for empty peers");
            });
        }
    }
}
