using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/PullClientTests.swift (задача 11/21). Три
    // слоя: подставной IHealthProber (счётчик вызовов — переиспользуется тот же приём,
    // что и в PeerResolverTests), подставной IBlobFetcher на все ветки ошибок, и
    // сквозной тест "ядро против ядра" (настоящий HttpServer + настоящий WebBlobFetcher).
    static class PullClientTests
    {
        const string TestToken = "s3cr3t-token";

        // MARK: - Test doubles (слой 1/2)

        class FakeProber : IHealthProber
        {
            bool alive;

            public FakeProber(bool alive)
            {
                this.alive = alive;
            }

            public void SetAlive(bool value)
            {
                alive = value;
            }

            public bool Probe(string host, int port, string token, int timeoutMs)
            {
                return alive;
            }
        }

        // Подставной IBlobFetcher: манифест и блобы задаются заранее, вызовы можно
        // сконфигурировать на успех или на бросок конкретной ошибки — так проверяются
        // все ветки PullClient без поднятия сокета.
        class FakeFetcher : IBlobFetcher
        {
            public Manifest ManifestResult;
            public Exception ManifestException;
            public int ManifestCallCount;

            public readonly Dictionary<int, byte[]> BlobResults = new Dictionary<int, byte[]>();
            public readonly Dictionary<int, Exception> BlobExceptions = new Dictionary<int, Exception>();
            public readonly List<int> BlobCallIndexes = new List<int>();

            public Manifest Manifest(string host, int port, string token)
            {
                ManifestCallCount++;
                if (ManifestException != null)
                {
                    throw ManifestException;
                }
                return ManifestResult;
            }

            public byte[] Blob(string host, int port, string token, int index, int seq, string toFile)
            {
                BlobCallIndexes.Add(index);

                Exception configuredException;
                if (BlobExceptions.TryGetValue(index, out configuredException))
                {
                    throw configuredException;
                }

                byte[] data;
                if (!BlobResults.TryGetValue(index, out data))
                {
                    throw HttpClientException.OfStatus(404);
                }

                if (toFile != null)
                {
                    File.WriteAllBytes(toFile, data);
                    return null;
                }
                return data;
            }
        }

        static string stagingRoot;

        static Config MakeConfig(long maxBytes)
        {
            Config config = new Config();
            config.Port = 8899;
            config.Token = TestToken;
            config.Peers = new List<string> { "10.0.0.2" };
            config.MaxBytes = maxBytes;
            config.AutoPaste = true;
            return config;
        }

        static Staging MakeStaging()
        {
            return new Staging(stagingRoot, new Func<DateTime>(delegate { return DateTime.UtcNow; }));
        }

        static PullClient MakeClient(Config config, FakeProber prober, FakeFetcher fetcher,
            FakeClipboard writer, out PeerResolver resolver)
        {
            resolver = new PeerResolver(config, prober);
            return new PullClient(config, resolver, fetcher, MakeStaging(), writer);
        }

        static Manifest Files(List<BlobRef> blobs, int seq)
        {
            return Manifest.OfFiles(blobs, seq);
        }

        static BlobRef Blob(int i, string rel, long size)
        {
            BlobRef b = new BlobRef();
            b.I = i;
            b.Rel = rel;
            b.Size = size;
            return b;
        }

        public static void Register()
        {
            stagingRoot = Path.Combine(Path.GetTempPath(), "lanclip-pullclient-" + Guid.NewGuid());
            Directory.CreateDirectory(stagingRoot);

            RegisterHappyPathTests();
            RegisterErrorTests();
            RegisterManifestIntegrityTests();
            RegisterCleanupTests();

            PullClientEndToEndTests.Register();
        }

        // MARK: - Счастливый путь

        static void RegisterHappyPathTests()
        {
            T.Run("text is written to clipboard and reports text result", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfText("привет с соседа", 1);
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullResult result = client.Pull();

                T.Eq("text", result.Kind, "kind");
                T.Eq(0, result.FileCount, "file count");
                T.Eq((long)Encoding.UTF8.GetByteCount("привет с соседа"), result.Bytes, "bytes");
                T.Eq(1, writer.Written.Count, "one write");
                T.Eq(ClipKindValue.Text, writer.Content.Kind, "content kind");
                T.Eq("привет с соседа", writer.Content.Text, "content text");
            });

            T.Run("image arrives as png bytes in clipboard", delegate
            {
                byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfImage(png.Length, 7);
                fetcher.BlobResults[0] = png;
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullResult result = client.Pull();

                T.Eq("image", result.Kind, "kind");
                T.Eq(0, result.FileCount, "file count");
                T.Eq((long)png.Length, result.Bytes, "bytes");
                T.Eq(ClipKindValue.Image, writer.Content.Kind, "content kind");
                T.True(BytesEqual(png, writer.Content.Png), "content png bytes");
                T.Eq(1, fetcher.BlobCallIndexes.Count, "one blob call");
                T.Eq(0, fetcher.BlobCallIndexes[0], "blob index");
            });

            T.Run("two files land in batch with preserved rel and clipboard gets local paths", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                List<BlobRef> blobs = new List<BlobRef>
                {
                    Blob(0, "docs/a.txt", 5),
                    Blob(1, "b.txt", 3),
                };
                fetcher.ManifestResult = Files(blobs, 3);
                fetcher.BlobResults[0] = Encoding.UTF8.GetBytes("hello");
                fetcher.BlobResults[1] = Encoding.UTF8.GetBytes("hi!");
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullResult result = client.Pull();

                T.Eq("files", result.Kind, "kind");
                T.Eq(2, result.FileCount, "file count");
                T.Eq(8L, result.Bytes, "bytes");

                T.Eq(ClipKindValue.Files, writer.Written[0].Kind, "written kind");
                List<string> paths = writer.Written[0].Files;
                T.Eq(2, paths.Count, "path count");
                T.True(paths[0].EndsWith("docs" + Path.DirectorySeparatorChar + "a.txt"), "first path suffix");
                T.True(paths[1].EndsWith("b.txt"), "second path suffix");
                T.Eq("hello", Encoding.UTF8.GetString(File.ReadAllBytes(paths[0])), "first file content");
                T.Eq("hi!", Encoding.UTF8.GetString(File.ReadAllBytes(paths[1])), "second file content");
            });
        }

        // MARK: - Ветки ошибок

        static void RegisterErrorTests()
        {
            T.Run("too large manifest throws and leaves clipboard untouched", delegate
            {
                Config config = MakeConfig(10);
                FakeFetcher fetcher = new FakeFetcher();
                List<BlobRef> blobs = new List<BlobRef> { Blob(0, "big.bin", 100) };
                fetcher.ManifestResult = Files(blobs, 1);
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTooLarge, caught.Code, "code");
                T.Eq(100L, caught.TotalSize, "total size");
                T.Eq(10L, caught.MaxBytes, "max bytes");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");
            });

            T.Run("text longer than maxBytes throws tooLarge and leaves clipboard untouched", delegate
            {
                string text = "это заведомо длинный текст для проверки лимита maxBytes";
                T.True(Encoding.UTF8.GetByteCount(text) > 20, "premise: text longer than 20 bytes");

                Config config = MakeConfig(20);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfText(text, 1);
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTooLarge, caught.Code, "code");
                T.Eq((long)Encoding.UTF8.GetByteCount(text), caught.TotalSize, "total size");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");
            });

            T.Run("status 409 during blob fetch throws changed and leaves clipboard untouched", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfImage(3, 9);
                fetcher.BlobExceptions[0] = HttpClientException.OfStatus(409);
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                FakeProber prober = new FakeProber(true);
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeChanged, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");

                // 409 — это не сбой транспорта, сосед жив: кеш резолвера не должен сбрасываться.
                T.Eq("10.0.0.2", resolver.Resolve(), "resolver cache kept after 409");
            });

            T.Run("status 409 during second file fetch leaves clipboard untouched", delegate
            {
                // Обрыв на втором файле партии — уже записанный на диск первый файл не
                // должен просочиться в буфер: либо оба файла, либо ничего.
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                List<BlobRef> blobs = new List<BlobRef> { Blob(0, "a.txt", 5), Blob(1, "b.txt", 3) };
                fetcher.ManifestResult = Files(blobs, 4);
                fetcher.BlobResults[0] = Encoding.UTF8.GetBytes("hello");
                fetcher.BlobExceptions[1] = HttpClientException.OfStatus(409);
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeChanged, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq(ClipKindValue.Empty, writer.Content.Kind, "clipboard stays empty");
            });

            T.Run("empty peer clipboard throws peerEmpty and leaves clipboard untouched", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.Empty(1);
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodePeerEmpty, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");
            });

            T.Run("no live peer throws noPeer and leaves clipboard untouched", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(false), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeNoPeer, caught.Code, "code");
                T.Eq(0, fetcher.ManifestCallCount, "no manifest call");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");
            });

            T.Run("transport error during manifest invalidates resolver cache", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeProber prober = new FakeProber(true);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestException = HttpClientException.Transport("соединение оборвалось");
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                // Прогреваем кеш резолвера самостоятельно, чтобы убедиться именно в
                // сбросе, а не в том, что кеш просто не был заполнен.
                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(),
                    "после ошибки транспорта кеш должен быть сброшен, иначе резолвер отдал бы старый адрес не проверяя");
            });

            T.Run("transport error during blob fetch invalidates resolver cache", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeProber prober = new FakeProber(true);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfImage(3, 2);
                fetcher.BlobExceptions[0] = HttpClientException.Transport("соединение оборвалось");
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "ошибка транспорта при скачивании блоба тоже обязана сбрасывать кеш резолвера");
            });

            T.Run("unexpected exception type from manifest parsing is treated as transport and invalidates cache", delegate
            {
                // Manifest.FromJson использует ToLong, который на вложенном объекте или
                // переполнении в size/totalSize бросает InvalidCastException/
                // OverflowException вместо контрактного FormatException — PullClient
                // обязан пережить ЛЮБОЙ тип исключения из fetcher.Manifest(...), а не
                // только HttpClientException.
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeProber prober = new FakeProber(true);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestException = new InvalidCastException("притворная поломка ToLong");
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "неожиданный тип исключения тоже обязан сбрасывать кеш резолвера");
            });
        }

        // MARK: - Ruling: манифест соседа не проверяется на межполевые инварианты

        static void RegisterManifestIntegrityTests()
        {
            T.Run("manifest with image kind but no blobs is treated as corrupted transport", delegate
            {
                Manifest manifest = Manifest.FromJson("{\"kind\":\"image\",\"seq\":1}");
                T.Eq(0, manifest.Blobs.Count, "premise: no blobs parsed");

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                writer.Content = ClipContent.OfText("прежнее содержимое");
                FakeProber prober = new FakeProber(true);
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq("прежнее содержимое", writer.Content.Text, "clipboard untouched");
                T.Eq(0, fetcher.BlobCallIndexes.Count, "блоб не должен запрашиваться для сорванного манифеста");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "манифест без обязательных blobs обязан сбрасывать кеш резолвера");
            });

            T.Run("manifest with text kind but no text is treated as corrupted transport", delegate
            {
                // Manifest.FromJson не различает "text отсутствовал" и "text явно
                // пустая строка" (fallback у Json.Str для text — ""), поэтому этот
                // случай воспроизводится прямой конструкцией Manifest, а не через JSON —
                // так проверяется собственная защита PullClient, а не поведение парсера.
                Manifest manifest = new Manifest();
                manifest.Kind = "text";
                manifest.Seq = 1;

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
            });

            T.Run("manifest with files kind but empty blobs is treated as corrupted transport", delegate
            {
                Manifest manifest = Manifest.FromJson("{\"kind\":\"files\",\"seq\":1,\"blobs\":[]}");

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
            });

            T.Run("manifest with totalSize mismatching blobs sum is treated as corrupted transport", delegate
            {
                // Сосед мог бы прислать заведомо маленький totalSize при огромных
                // blobs — проверка размера обязана опираться на сумму по blobs, а не
                // на присланное число. Расхождение totalSize с суммой — тот же класс
                // дефекта, что и отсутствующие blobs: самопротиворечивый манифест, а не
                // гонка с соседским буфером.
                Manifest manifest = Manifest.FromJson(
                    "{\"kind\":\"files\",\"seq\":1,\"totalSize\":1,\"blobs\":[{\"i\":0,\"rel\":\"big.bin\",\"size\":999999999,\"mime\":null}]}");
                T.Eq(1L, manifest.TotalSize, "premise: declared totalSize taken as-is");

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                FakeProber prober = new FakeProber(true);
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq(0, fetcher.BlobCallIndexes.Count, "загрузка не должна начинаться для манифеста с расходящимся totalSize");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "расхождение totalSize с суммой blobs обязано сбрасывать кеш резолвера");
            });

            T.Run("computed total size from blobs governs tooLarge even when manifest omits totalSize", delegate
            {
                Manifest manifest = Manifest.FromJson(
                    "{\"kind\":\"files\",\"seq\":1,\"blobs\":[{\"i\":0,\"rel\":\"big.bin\",\"size\":999999999,\"mime\":null}]}");
                T.Eq(0L, manifest.TotalSize, "premise: totalSize key absent from JSON");

                Config config = MakeConfig(10);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTooLarge, caught.Code, "code");
                T.Eq(999999999L, caught.TotalSize, "total size");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq(0, fetcher.BlobCallIndexes.Count, "загрузка не должна начинаться при превышении лимита");
            });

            T.Run("file arriving with wrong size is treated as corrupted transport", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                List<BlobRef> blobs = new List<BlobRef> { Blob(0, "a.txt", 5) };
                fetcher.ManifestResult = Files(blobs, 1);
                fetcher.BlobResults[0] = Encoding.UTF8.GetBytes("hello world"); // 11 байт вместо заявленных 5
                FakeClipboard writer = new FakeClipboard();
                FakeProber prober = new FakeProber(true);
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "файл не того размера обязан сбрасывать кеш резолвера");
            });

            T.Run("image arriving with wrong size is treated as corrupted transport", delegate
            {
                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = Manifest.OfImage(3, 1);
                fetcher.BlobResults[0] = new byte[] { 1, 2, 3, 4, 5 }; // 5 байт вместо заявленных 3
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
            });

            T.Run("two blobs at Int64 max do not crash and are treated as corrupted transport", delegate
            {
                // Конструируется напрямую, а не через Manifest.FromJson: у литерала
                // long.MaxValue в JSON есть риск потери точности при разборе через
                // JavaScriptSerializer (округление к double). Здесь проверяется
                // собственная защита PullClient от переполнения суммы, а не поведение
                // парсера JSON на грани точности представления чисел.
                Manifest manifest = new Manifest();
                manifest.Kind = "files";
                manifest.Seq = 1;
                manifest.Blobs = new List<BlobRef>
                {
                    Blob(0, "a.bin", long.MaxValue),
                    Blob(1, "b.bin", long.MaxValue),
                };

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                FakeProber prober = new FakeProber(true);
                PeerResolver resolver;
                PullClient client = MakeClient(config, prober, fetcher, writer, out resolver);

                T.Eq("10.0.0.2", resolver.Resolve(), "warm resolve");

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq(0, fetcher.BlobCallIndexes.Count, "переполнение обязано отсекаться до единого запроса за блобом");

                prober.SetAlive(false);
                T.Eq(null, resolver.Resolve(), "переполнение суммы обязано сбрасывать кеш резолвера");
            });

            T.Run("blob with negative size is treated as corrupted transport", delegate
            {
                Manifest manifest = Manifest.FromJson(
                    "{\"kind\":\"files\",\"seq\":1,\"blobs\":[{\"i\":0,\"rel\":\"a.bin\",\"size\":-1,\"mime\":null}]}");

                Config config = MakeConfig(Config.DefaultMaxBytes);
                FakeFetcher fetcher = new FakeFetcher();
                fetcher.ManifestResult = manifest;
                FakeClipboard writer = new FakeClipboard();
                PeerResolver resolver;
                PullClient client = MakeClient(config, new FakeProber(true), fetcher, writer, out resolver);

                PullException caught = ExpectThrows(new Action(delegate { client.Pull(); }));
                T.Eq(PullException.CodeTransport, caught.Code, "code");
                T.True(writer.Written.Count == 0, "no write");
                T.Eq(0, fetcher.BlobCallIndexes.Count, "отрицательный size обязан отсекаться до запроса за блобом");
            });
        }

        // MARK: - Уборка

        static void RegisterCleanupTests()
        {
            T.Run("successful pull succeeds even when staging cleanup fails", delegate
            {
                // Уборка — housekeeping вокруг уже состоявшегося успеха. Ломаем
                // Cleanup() по-настоящему: Staging.now() бросает исключение прямо
                // внутри Cleanup() (используется для ageCutoffStamp), а этот текст —
                // манифест "text", поэтому staging.NewBatch() (который тоже читает
                // now()) вовсе не вызывается до этого момента.
                string root = Path.Combine(Path.GetTempPath(), "lanclip-pullclient-cleanup-" + Guid.NewGuid());
                Directory.CreateDirectory(root);
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(delegate
                    {
                        throw new IOException("сломанные часы");
                    }));

                    Config config = MakeConfig(Config.DefaultMaxBytes);
                    FakeFetcher fetcher = new FakeFetcher();
                    fetcher.ManifestResult = Manifest.OfText("успех несмотря на грязную уборку", 1);
                    FakeClipboard writer = new FakeClipboard();
                    PeerResolver resolver = new PeerResolver(config, new FakeProber(true));
                    PullClient client = new PullClient(config, resolver, fetcher, staging, writer);

                    PullResult result = client.Pull();

                    T.Eq("text", result.Kind, "kind");
                    T.Eq("успех несмотря на грязную уборку", writer.Content.Text, "content written despite cleanup failure");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("successful pull triggers staging cleanup", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "lanclip-pullclient-cleanup2-" + Guid.NewGuid());
                Directory.CreateDirectory(root);
                try
                {
                    DateTime current = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    Staging staging = new Staging(root, new Func<DateTime>(delegate { return current; }));

                    List<StagingBatch> oldBatches = new List<StagingBatch>();
                    for (int i = 0; i < Staging.KeepBatches + 1; i++)
                    {
                        current = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
                        oldBatches.Add(staging.NewBatch());
                    }

                    Config config = MakeConfig(Config.DefaultMaxBytes);
                    FakeFetcher fetcher = new FakeFetcher();
                    fetcher.ManifestResult = Manifest.OfText("x", 1);
                    FakeClipboard writer = new FakeClipboard();
                    PeerResolver resolver = new PeerResolver(config, new FakeProber(true));
                    PullClient client = new PullClient(config, resolver, fetcher, staging, writer);

                    client.Pull();

                    string[] remaining = Directory.GetDirectories(root);
                    T.Eq(Staging.KeepBatches, remaining.Length, "должно остаться ровно KeepBatches партий");
                    T.True(!Directory.Exists(oldBatches[0].Root),
                        "самая старая избыточная партия должна быть убрана после успешного Pull()");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }

        // MARK: - Helpers

        static PullException ExpectThrows(Action body)
        {
            try
            {
                body();
            }
            catch (PullException e)
            {
                return e;
            }
            T.True(false, "expected PullException, nothing thrown");
            return null;
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
    }

    // MARK: - Слой 3: сквозной тест "ядро против ядра"

    // Единственное место, где протокол проверяется целиком: настоящий HttpServer на
    // порту 0 с одним FakeClipboard в роли соседа, и PullClient с настоящим
    // WebBlobFetcher, пишущий во второй, независимый FakeClipboard.
    static class PullClientEndToEndTests
    {
        const string TestToken = "s3cr3t-token";

        static int FindFreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static PullResult DummyPull()
        {
            PullResult r = new PullResult();
            r.Kind = "empty";
            r.FileCount = 0;
            r.Bytes = 0;
            return r;
        }

        static Config MakeServerConfig()
        {
            Config config = new Config();
            config.Port = FindFreePort();
            config.Token = TestToken;
            config.Peers = new List<string>();
            config.MaxBytes = Config.DefaultMaxBytes;
            config.AutoPaste = true;
            return config;
        }

        static PullClient MakeClient(int serverPort, string stagingRoot, IClipboardWriter writer)
        {
            Config clientConfig = new Config();
            clientConfig.Port = serverPort;
            clientConfig.Token = TestToken;
            clientConfig.Peers = new List<string> { "127.0.0.1" };
            clientConfig.MaxBytes = Config.DefaultMaxBytes;
            clientConfig.AutoPaste = true;

            WebBlobFetcher httpClient = new WebBlobFetcher(5000);
            PeerResolver resolver = new PeerResolver(clientConfig, httpClient);
            Staging staging = new Staging(stagingRoot, new Func<DateTime>(delegate { return DateTime.UtcNow; }));
            return new PullClient(clientConfig, resolver, httpClient, staging, writer);
        }

        static string MakeTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
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

        public static void Register()
        {
            T.Run("end to end text transfer", delegate
            {
                string stagingRoot = MakeTempDir("lanclip-pullclient-e2e-text-");
                Config serverConfig = MakeServerConfig();
                FakeClipboard peerClipboard = new FakeClipboard();
                peerClipboard.Content = ClipContent.OfText("сквозной текст через настоящий сервер и клиент");
                SnapshotStore store = new SnapshotStore(peerClipboard);
                HttpServer server = new HttpServer(serverConfig, store, "peer-win",
                    new Func<PullResult>(DummyPull), "127.0.0.1");
                server.Start();
                try
                {
                    FakeClipboard localClipboard = new FakeClipboard();
                    PullClient client = MakeClient(server.BoundPort, stagingRoot, localClipboard);

                    PullResult result = client.Pull();

                    T.Eq("text", result.Kind, "kind");
                    T.Eq(0, result.FileCount, "file count");
                    T.Eq(ClipKindValue.Text, localClipboard.Content.Kind, "local kind");
                    T.Eq("сквозной текст через настоящий сервер и клиент", localClipboard.Content.Text, "local text");
                }
                finally
                {
                    server.Stop();
                    Directory.Delete(stagingRoot, true);
                }
            });

            T.Run("end to end image transfer", delegate
            {
                string stagingRoot = MakeTempDir("lanclip-pullclient-e2e-image-");
                byte[] png = new byte[8 + 5000];
                byte[] header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                Array.Copy(header, png, header.Length);
                for (int i = 0; i < 5000; i++) { png[8 + i] = (byte)(i % 256); }

                Config serverConfig = MakeServerConfig();
                FakeClipboard peerClipboard = new FakeClipboard();
                peerClipboard.Content = ClipContent.OfImage(png);
                SnapshotStore store = new SnapshotStore(peerClipboard);
                HttpServer server = new HttpServer(serverConfig, store, "peer-win",
                    new Func<PullResult>(DummyPull), "127.0.0.1");
                server.Start();
                try
                {
                    FakeClipboard localClipboard = new FakeClipboard();
                    PullClient client = MakeClient(server.BoundPort, stagingRoot, localClipboard);

                    PullResult result = client.Pull();

                    T.Eq("image", result.Kind, "kind");
                    T.Eq((long)png.Length, result.Bytes, "bytes");
                    T.Eq(ClipKindValue.Image, localClipboard.Content.Kind, "local kind");
                    T.True(BytesEqual(png, localClipboard.Content.Png), "local png bytes");
                }
                finally
                {
                    server.Stop();
                    Directory.Delete(stagingRoot, true);
                }
            });

            T.Run("web blob fetcher wraps local file write failure as HttpClientException", delegate
            {
                // Регрессия само-ревью: StreamToFile/ReadAll внутри WebBlobFetcher.Blob
                // могут бросить сырой IOException (нет каталога назначения, нет прав,
                // диск полон) — PullClient.DownloadImage/DownloadFiles ловят вокруг
                // Blob(...) только HttpClientException (зеркало Mac-стороннего
                // PullClient), поэтому WebBlobFetcher обязан сам гарантировать, что
                // любой его отказ, включая локальную запись на диск, выражается именно
                // этим типом, а не сырым исключением файловой системы.
                Config serverConfig = MakeServerConfig();
                FakeClipboard peerClipboard = new FakeClipboard();
                peerClipboard.Content = ClipContent.OfImage(new byte[] { 1, 2, 3 });
                SnapshotStore store = new SnapshotStore(peerClipboard);
                HttpServer server = new HttpServer(serverConfig, store, "peer-win",
                    new Func<PullResult>(DummyPull), "127.0.0.1");
                server.Start();
                try
                {
                    int seq = store.Current().Manifest.Seq;
                    WebBlobFetcher httpClient = new WebBlobFetcher(5000);
                    string missingDir = Path.Combine(Path.GetTempPath(), "lanclip-missing-" + Guid.NewGuid());
                    string badPath = Path.Combine(missingDir, "sub", "blob.bin");

                    try
                    {
                        httpClient.Blob("127.0.0.1", server.BoundPort, TestToken, 0, seq, badPath);
                        T.True(false, "expected HttpClientException");
                    }
                    catch (HttpClientException e)
                    {
                        T.Eq(HttpClientException.CodeTransport, e.Code, "code");
                    }
                }
                finally
                {
                    server.Stop();
                }
            });

            T.Run("end to end two files transfer", delegate
            {
                string stagingRoot = MakeTempDir("lanclip-pullclient-e2e-files-staging-");
                string filesRoot = MakeTempDir("lanclip-pullclient-e2e-files-source-");
                try
                {
                    string fileA = Path.Combine(filesRoot, "report.txt");
                    string fileB = Path.Combine(filesRoot, "notes.md");
                    File.WriteAllBytes(fileA, Encoding.UTF8.GetBytes("годовой отчёт"));
                    File.WriteAllBytes(fileB, Encoding.UTF8.GetBytes("# заметки"));

                    Config serverConfig = MakeServerConfig();
                    FakeClipboard peerClipboard = new FakeClipboard();
                    peerClipboard.Content = ClipContent.OfFiles(new List<string> { fileA, fileB });
                    SnapshotStore store = new SnapshotStore(peerClipboard);
                    HttpServer server = new HttpServer(serverConfig, store, "peer-win",
                        new Func<PullResult>(DummyPull), "127.0.0.1");
                    server.Start();
                    try
                    {
                        FakeClipboard localClipboard = new FakeClipboard();
                        PullClient client = MakeClient(server.BoundPort, stagingRoot, localClipboard);

                        PullResult result = client.Pull();

                        T.Eq("files", result.Kind, "kind");
                        T.Eq(2, result.FileCount, "file count");
                        T.Eq(ClipKindValue.Files, localClipboard.Content.Kind, "local kind");

                        List<string> paths = localClipboard.Content.Files;
                        T.Eq(2, paths.Count, "path count");

                        List<string> contents = new List<string>();
                        foreach (string p in paths) { contents.Add(Encoding.UTF8.GetString(File.ReadAllBytes(p))); }
                        T.True(contents.Contains("годовой отчёт"), "report content present");
                        T.True(contents.Contains("# заметки"), "notes content present");

                        string resolvedStagingRoot = Path.GetFullPath(stagingRoot);
                        foreach (string p in paths)
                        {
                            T.True(Path.GetFullPath(p).StartsWith(resolvedStagingRoot),
                                "path lands inside staging root, not the peer's source folder");
                        }
                    }
                    finally
                    {
                        server.Stop();
                    }
                }
                finally
                {
                    Directory.Delete(stagingRoot, true);
                    Directory.Delete(filesRoot, true);
                }
            });
        }
    }
}
