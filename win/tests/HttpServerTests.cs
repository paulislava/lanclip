using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/HttpServerTests.swift (задача 7/19). Два слоя:
    // маршрутизация через чистый статический HttpServer.Route (без сокета, по одному
    // случаю на каждый код ответа) и сквозной обмен через настоящий HttpListener.
    static class HttpServerTests
    {
        const string TestToken = "s3cr3t-token";

        // Читатель буфера, детерминированно бросающий из Read() — используется, чтобы
        // проверить обработку ошибок построения снимка (Ruling 2 задачи 6/19), не завися
        // от файловой системы. Мимикрирует то, что Manifest.FromJson может неожиданно
        // бросить InvalidCastException/OverflowException вместо FormatException — сервер
        // обязан пережить ЛЮБОЕ исключение, а не только ожидаемые типы.
        class ThrowingClipboard : IClipboardReader
        {
            public int ChangeCount() { return 1; }

            public ClipContent Read()
            {
                throw new InvalidOperationException("нарочно сломанное чтение буфера");
            }
        }

        static Config MakeConfig()
        {
            Config config = new Config();
            config.Port = 0;
            config.Token = TestToken;
            config.Peers = new List<string>();
            config.MaxBytes = Config.DefaultMaxBytes;
            config.AutoPaste = true;
            return config;
        }

        static SnapshotStore MakeStore(FakeClipboard clipboard)
        {
            return new SnapshotStore(clipboard);
        }

        static HttpRequestSpec Req(string method, string path, string token)
        {
            string barePath = path;
            Dictionary<string, string> query = new Dictionary<string, string>();
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                barePath = path.Substring(0, q);
                string queryString = path.Substring(q + 1);
                foreach (string pair in queryString.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq >= 0)
                    {
                        query[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                    }
                }
            }

            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (token != null)
            {
                headers["x-clip-token"] = token;
            }
            return new HttpRequestSpec(method, barePath, query, headers);
        }

        static PullResult SucceedingPull()
        {
            PullResult result = new PullResult();
            result.Kind = "text";
            result.FileCount = 0;
            result.Bytes = 5;
            return result;
        }

        static PullResult FailingPull()
        {
            throw new InvalidOperationException("сосед недоступен");
        }

        public static void Register()
        {
            RegisterRoutingTests();
            RegisterEndToEndTests();
        }

        // MARK: - Route() unit tests (no networking)

        static void RegisterRoutingTests()
        {
            T.Run("public remote returns 403", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/health", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "8.8.8.8", new Func<PullResult>(SucceedingPull));
                T.Eq(403, response.Status, "status");
            });

            // Регрессия: Swift-сторонний split(separator: ".") по умолчанию опускает
            // пустые подпоследовательности, поэтому хвостовая точка "127.0.0.1." там
            // по-прежнему даёт 4 октета и признаётся приватным адресом. До починки
            // C#-разбор без RemoveEmptyEntries давал 5 частей и отвергал такой адрес —
            // платформы расходились на границе безопасности.
            T.Run("trailing dot in ipv4 remote is still private like on Swift side", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/health", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1.", new Func<PullResult>(SucceedingPull));
                T.Eq(200, response.Status, "status");
            });

            T.Run("bad token returns 401", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/health", "wrong"), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(401, response.Status, "status");
            });

            T.Run("missing token returns 401", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/health", null), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(401, response.Status, "status");
            });

            T.Run("unknown path returns 404", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/nope", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(404, response.Status, "status");
            });

            T.Run("wrong method on known path returns 405", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("POST", "/health", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(405, response.Status, "status");
            });

            T.Run("wrong method on blob path returns 405", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("x");
                HttpResponseSpec response = HttpServer.Route(
                    Req("POST", "/clip/blob/0?seq=1", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(405, response.Status, "status");
            });

            T.Run("stale seq returns 409", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("первый");
                SnapshotStore store = MakeStore(clipboard);
                int staleSeq = store.Current().Manifest.Seq;
                clipboard.Content = ClipContent.OfImage(new byte[] { 1, 2, 3 });

                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/clip/blob/0?seq=" + staleSeq, TestToken), MakeConfig(), store,
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(409, response.Status, "status");
            });

            T.Run("out of range blob index returns 404", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfImage(new byte[] { 1 });
                SnapshotStore store = MakeStore(clipboard);
                int seq = store.Current().Manifest.Seq;

                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/clip/blob/5?seq=" + seq, TestToken), MakeConfig(), store,
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(404, response.Status, "status");
            });

            T.Run("snapshot build failure returns 500", delegate
            {
                ThrowingClipboard throwingReader = new ThrowingClipboard();
                SnapshotStore store = new SnapshotStore(throwingReader);

                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/clip", TestToken), MakeConfig(), store,
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(500, response.Status, "status");
            });

            T.Run("pull failure returns 503 with error body", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("POST", "/pull", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(FailingPull));
                T.Eq(503, response.Status, "status");

                Dictionary<string, object> json = Json.Parse(Encoding.UTF8.GetString(response.Body));
                T.True(json.ContainsKey("error"), "body has error key");
            });

            T.Run("health returns ok host and version", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/health", TestToken), MakeConfig(), MakeStore(clipboard),
                    "mywin", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(200, response.Status, "status");

                Dictionary<string, object> json = Json.Parse(Encoding.UTF8.GetString(response.Body));
                T.Eq(true, (bool)json["ok"], "ok");
                T.Eq("mywin", (string)json["host"], "host");
                T.Eq(1, Convert.ToInt32(json["version"]), "version");
            });

            T.Run("clip returns manifest", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("привет");
                SnapshotStore store = MakeStore(clipboard);

                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/clip", TestToken), MakeConfig(), store,
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(200, response.Status, "status");

                Manifest manifest = Manifest.FromJson(Encoding.UTF8.GetString(response.Body));
                T.Eq("text", manifest.Kind, "kind");
                T.Eq("привет", manifest.Text, "text");
            });

            T.Run("clip blob returns bytes with octet-stream", delegate
            {
                byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfImage(png);
                SnapshotStore store = MakeStore(clipboard);
                int seq = store.Current().Manifest.Seq;

                HttpResponseSpec response = HttpServer.Route(
                    Req("GET", "/clip/blob/0?seq=" + seq, TestToken), MakeConfig(), store,
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(200, response.Status, "status");
                T.Eq("application/octet-stream", response.ContentType, "content type");
                T.True(BytesEqual(png, response.Body), "body bytes");
            });

            T.Run("pull returns result body", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                HttpResponseSpec response = HttpServer.Route(
                    Req("POST", "/pull", TestToken), MakeConfig(), MakeStore(clipboard),
                    "win", "127.0.0.1", new Func<PullResult>(SucceedingPull));
                T.Eq(200, response.Status, "status");

                Dictionary<string, object> json = Json.Parse(Encoding.UTF8.GetString(response.Body));
                T.Eq("text", (string)json["kind"], "kind");
                T.Eq(0, Convert.ToInt32(json["fileCount"]), "fileCount");
                T.Eq(5L, Convert.ToInt64(json["bytes"]), "bytes");
            });
        }

        // MARK: - End-to-end tests over a real HttpListener socket

        static int FindFreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static void RegisterEndToEndTests()
        {
            T.Run("start with out of range port throws instead of crashing", delegate
            {
                // Config не валидируется автоматически перед конструированием сервера —
                // защита должна бросать типизированную ошибку, а не отдавать необработанное
                // исключение из HttpListener где-то глубже.
                Config config = MakeConfig();
                config.Port = 100000;
                FakeClipboard clipboard = new FakeClipboard();
                HttpServer server = new HttpServer(config, MakeStore(clipboard), "win",
                    new Func<PullResult>(SucceedingPull), "127.0.0.1");

                try
                {
                    server.Start();
                    T.True(false, "expected HttpServerException");
                }
                catch (HttpServerException e)
                {
                    T.Eq(100000, e.Port, "reported port");
                }
            });

            T.Run("restart cycle reacquires a port", delegate
            {
                // Доказывает, что последовательный Start() -> Stop() -> Start() -> Stop()
                // остаётся рабочим после того, как исполнитель STA-потока стал создаваться
                // заново в Start() и завершаться в Stop() (иначе повторный Start() унаследовал
                // бы уже остановленный исполнитель, и любой запрос падал бы ObjectDisposedException).
                FakeClipboard clipboard = new FakeClipboard();
                SnapshotStore store = MakeStore(clipboard);
                Config config = MakeConfig();
                config.Port = FindFreePort();
                HttpServer server = new HttpServer(config, store, "win", new Func<PullResult>(SucceedingPull),
                    "127.0.0.1");

                server.Start();
                int firstPort = server.BoundPort;
                T.True(firstPort != 0, "first port bound");
                server.Stop();

                config.Port = FindFreePort();
                server.Start();
                int secondPort = server.BoundPort;
                T.True(secondPort != 0, "second port bound");
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + secondPort + "/health");
                    request.Method = "GET";
                    request.Headers["X-Clip-Token"] = TestToken;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        T.Eq(200, (int)response.StatusCode, "status after restart");
                    }
                }
                finally
                {
                    server.Stop();
                }
            });

            T.Run("end to end clip round trip with and without token", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("сквозной обмен");
                SnapshotStore store = MakeStore(clipboard);

                Config config = MakeConfig();
                config.Port = FindFreePort();
                HttpServer server = new HttpServer(config, store, "win", new Func<PullResult>(SucceedingPull), "127.0.0.1");
                server.Start();
                try
                {
                    HttpWebRequest withToken = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + server.BoundPort + "/clip");
                    withToken.Method = "GET";
                    withToken.Headers["X-Clip-Token"] = TestToken;
                    using (HttpWebResponse response = (HttpWebResponse)withToken.GetResponse())
                    {
                        T.Eq(200, (int)response.StatusCode, "status with token");
                        byte[] body = ReadAll(response.GetResponseStream());
                        Manifest manifest = Manifest.FromJson(Encoding.UTF8.GetString(body));
                        T.Eq("сквозной обмен", manifest.Text, "manifest text");
                    }

                    HttpWebRequest withoutToken = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + server.BoundPort + "/clip");
                    withoutToken.Method = "GET";
                    try
                    {
                        withoutToken.GetResponse();
                        T.True(false, "expected 401 without token");
                    }
                    catch (WebException e)
                    {
                        HttpWebResponse response = (HttpWebResponse)e.Response;
                        T.Eq(401, (int)response.StatusCode, "status without token");
                    }
                }
                finally
                {
                    server.Stop();
                }
            });

            // Находка I7 финального ревью: раньше ЛЮБОЙ Route() (значит, и любой /pull)
            // сериализовался через собственный STA-исполнитель HttpServer — пока шёл
            // /pull, сервер не отвечал даже на /health для других соединений. Этот
            // исполнитель убран (WinClipboard уже маршалит на СВОЙ отдельный STA сам),
            // запросы теперь едут параллельно на ThreadPool.
            T.Run("slow pull does not block a concurrent health request", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                SnapshotStore store = MakeStore(clipboard);
                Config config = MakeConfig();
                config.Port = FindFreePort();
                Func<PullResult> slowPull = new Func<PullResult>(delegate
                {
                    Thread.Sleep(1000);
                    PullResult result = new PullResult();
                    result.Kind = "text";
                    result.FileCount = 0;
                    result.Bytes = 1;
                    return result;
                });
                HttpServer server = new HttpServer(config, store, "win", slowPull, "127.0.0.1");
                server.Start();
                try
                {
                    Thread pullThread = new Thread(new ThreadStart(delegate
                    {
                        try
                        {
                            HttpWebRequest pullRequest = (HttpWebRequest)WebRequest.Create(
                                "http://127.0.0.1:" + server.BoundPort + "/pull");
                            pullRequest.Method = "POST";
                            pullRequest.Headers["X-Clip-Token"] = TestToken;
                            pullRequest.ContentLength = 0;
                            using (HttpWebResponse response = (HttpWebResponse)pullRequest.GetResponse())
                            {
                                response.Close();
                            }
                        }
                        catch (Exception)
                        {
                            // Побочный поток — сам исход /pull здесь не проверяется,
                            // только то, что он не блокирует параллельный /health.
                        }
                    }));
                    pullThread.IsBackground = true;
                    pullThread.Start();

                    // Даём /pull шанс реально начаться на сервере до того, как мерим /health.
                    Thread.Sleep(200);

                    DateTime started = DateTime.UtcNow;
                    HttpWebRequest healthRequest = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + server.BoundPort + "/health");
                    healthRequest.Method = "GET";
                    healthRequest.Headers["X-Clip-Token"] = TestToken;
                    using (HttpWebResponse response = (HttpWebResponse)healthRequest.GetResponse())
                    {
                        T.Eq(200, (int)response.StatusCode, "health status");
                    }
                    double elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;
                    T.True(elapsedMs < 500, "/health не должен ждать окончания параллельного /pull (~1000мс), заняло "
                        + elapsedMs + "мс");

                    pullThread.Join(3000);
                }
                finally
                {
                    server.Stop();
                }
            });

            T.Run("oversized request body returns 400 and keeps server alive", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                SnapshotStore store = MakeStore(clipboard);
                Config config = MakeConfig();
                config.Port = FindFreePort();
                HttpServer server = new HttpServer(config, store, "win", new Func<PullResult>(SucceedingPull), "127.0.0.1");
                server.Start();
                try
                {
                    byte[] oversized = new byte[1200000];
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + server.BoundPort + "/health");
                    request.Method = "POST";
                    request.Headers["X-Clip-Token"] = TestToken;
                    request.ContentLength = oversized.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(oversized, 0, oversized.Length);
                    }

                    try
                    {
                        request.GetResponse();
                        T.True(false, "expected 400 for oversized body");
                    }
                    catch (WebException e)
                    {
                        HttpWebResponse response = (HttpWebResponse)e.Response;
                        T.Eq(400, (int)response.StatusCode, "status for oversized body");
                    }

                    // Сервер обязан продолжать обслуживать дальнейшие соединения.
                    HttpWebRequest healthy = (HttpWebRequest)WebRequest.Create(
                        "http://127.0.0.1:" + server.BoundPort + "/health");
                    healthy.Method = "GET";
                    healthy.Headers["X-Clip-Token"] = TestToken;
                    using (HttpWebResponse response = (HttpWebResponse)healthy.GetResponse())
                    {
                        T.Eq(200, (int)response.StatusCode, "server still healthy");
                    }
                }
                finally
                {
                    server.Stop();
                }
            });

            T.Run("large blob is delivered in full over chunked send", delegate
            {
                string dir = Path.Combine(Path.GetTempPath(), "lanclip-httpserver-" + Guid.NewGuid());
                Directory.CreateDirectory(dir);
                try
                {
                    string file = Path.Combine(dir, "large.bin");
                    byte[] payload = new byte[700000];
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(i % 256);
                    }
                    File.WriteAllBytes(file, payload);

                    FakeClipboard clipboard = new FakeClipboard();
                    List<string> files = new List<string>();
                    files.Add(file);
                    clipboard.Content = ClipContent.OfFiles(files);
                    SnapshotStore store = MakeStore(clipboard);
                    int seq = store.Current().Manifest.Seq;

                    Config config = MakeConfig();
                    config.Port = FindFreePort();
                    HttpServer server = new HttpServer(config, store, "win", new Func<PullResult>(SucceedingPull), "127.0.0.1");
                    server.Start();
                    try
                    {
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                            "http://127.0.0.1:" + server.BoundPort + "/clip/blob/0?seq=" + seq);
                        request.Method = "GET";
                        request.Headers["X-Clip-Token"] = TestToken;
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            T.Eq(200, (int)response.StatusCode, "status");
                            byte[] body = ReadAll(response.GetResponseStream());
                            T.Eq(payload.Length, body.Length, "body length");
                            T.True(BytesEqual(payload, body), "body bytes");
                        }
                    }
                    finally
                    {
                        server.Stop();
                    }
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) { return a == b; }
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        static byte[] ReadAll(Stream stream)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[65536];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }
    }
}
