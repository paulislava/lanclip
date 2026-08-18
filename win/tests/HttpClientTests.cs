using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/HttpClientTests.swift, но говорит с сырым
    // TcpListener вместо настоящего HttpServer/HttpListener — так тест может отвечать
    // произвольным (в том числе враждебным: редирект, тело больше заявленного
    // лимита) сырым HTTP, чего настоящий сервер продукта никогда не пришлёт сам.
    static class HttpClientTests
    {
        const string TestToken = "s3cr3t-token";

        public static void Register()
        {
            // Находка I5 финального ревью: HttpWebRequest по умолчанию следует
            // редиректам и в .NET Framework переносит кастомные заголовки (включая
            // X-Clip-Token) на хост из Location — сосед на той же подсети, знающий
            // токен, мог ответить 302 на произвольный адрес и получить токен там.
            T.Run("redirect response is not auto-followed and reported by status, target never contacted", delegate
            {
                RawResponder evilTarget = new RawResponder(new Func<string, string>(delegate(string request)
                {
                    return "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi";
                }));
                RawResponder redirecting = new RawResponder(new Func<string, string>(delegate(string request)
                {
                    return "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + evilTarget.Port
                        + "/clip\r\nContent-Length: 0\r\n\r\n";
                }));
                try
                {
                    WebBlobFetcher fetcher = new WebBlobFetcher(2000);
                    try
                    {
                        fetcher.Manifest("127.0.0.1", redirecting.Port, TestToken);
                        T.True(false, "expected HttpClientException for a 302 response");
                    }
                    catch (HttpClientException e)
                    {
                        T.Eq(HttpClientException.CodeStatus, e.Code, "code");
                        T.Eq(302, e.Status, "status");
                    }
                    T.Eq(0, evilTarget.RequestCount, "the Location target must never be contacted with the token");
                }
                finally
                {
                    redirecting.Stop();
                    evilTarget.Stop();
                }
            });

            // Находка I5: ReadAll/StreamToFile копили тело без потолка, игнорируя
            // Content-Length — недоверенный сосед мог заставить агента съесть память
            // или залить диск произвольным объёмом. Явный (тестовый) потолок ниже
            // того, что сервер честно присылает — отказ обязан прийти РАНЬШЕ, чем
            // тело было бы прочитано целиком.
            T.Run("response body exceeding the configured cap is rejected as transport error", delegate
            {
                const int bodyLength = 5000;
                RawResponder oversized = new RawResponder(new Func<string, string>(delegate(string request)
                {
                    string body = new string('x', bodyLength);
                    return "HTTP/1.1 200 OK\r\nContent-Length: " + bodyLength + "\r\n\r\n" + body;
                }));
                try
                {
                    WebBlobFetcher fetcher = new WebBlobFetcher(2000, 1000); // потолок меньше тела
                    try
                    {
                        fetcher.Manifest("127.0.0.1", oversized.Port, TestToken);
                        T.True(false, "expected HttpClientException for an oversized body");
                    }
                    catch (HttpClientException e)
                    {
                        T.Eq(HttpClientException.CodeTransport, e.Code, "code");
                    }
                }
                finally
                {
                    oversized.Stop();
                }
            });

            // Мелкая находка финального ревью: сервер продукта всегда шлёт
            // Content-Length на 200 (см. HttpResponse.head() на Mac и
            // HttpServer.WriteResponse() здесь) — его отсутствие означает
            // испорченного/нештатного соседа. .NET сам читает поток до EOF и
            // получил бы реальные байты без явной проверки — то есть, в отличие
            // от Mac (который раньше молча создавал пустой файл), здесь асимметрия
            // была в другую сторону: слишком терпимо. Обе стороны обязаны
            // отвергать такой ответ одинаково явно.
            T.Run("response without Content-Length header is rejected as transport error", delegate
            {
                const string text = "{\"kind\":\"empty\",\"seq\":1}";
                RawResponder noContentLength = new RawResponder(new Func<string, string>(delegate(string request)
                {
                    // Соединение закрывается сразу после тела — единственный способ
                    // обозначить конец сообщения без Content-Length и без chunked.
                    return "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n" + text;
                }));
                try
                {
                    WebBlobFetcher fetcher = new WebBlobFetcher(2000);
                    try
                    {
                        fetcher.Manifest("127.0.0.1", noContentLength.Port, TestToken);
                        T.True(false, "expected HttpClientException for a missing Content-Length");
                    }
                    catch (HttpClientException e)
                    {
                        T.Eq(HttpClientException.CodeTransport, e.Code, "code");
                    }
                }
                finally
                {
                    noContentLength.Stop();
                }
            });

            T.Run("response body within the configured cap is accepted normally", delegate
            {
                const string text = "{\"kind\":\"text\",\"seq\":1,\"text\":\"ok\"}";
                RawResponder small = new RawResponder(new Func<string, string>(delegate(string request)
                {
                    return "HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.UTF8.GetByteCount(text) + "\r\n\r\n" + text;
                }));
                try
                {
                    WebBlobFetcher fetcher = new WebBlobFetcher(2000, 1000);
                    Manifest manifest = fetcher.Manifest("127.0.0.1", small.Port, TestToken);
                    T.Eq("text", manifest.Kind, "kind survives a body within the cap");
                }
                finally
                {
                    small.Stop();
                }
            });
        }

        // Сырой TCP-слушатель, отвечающий тем, что вернёт переданная функция —
        // единственный способ смоделировать враждебный ответ (редирект на чужой
        // хост, тело больше заявленного/сверх разумного предела), который настоящий
        // HttpServer продукта никогда сам не пришлёт.
        class RawResponder
        {
            readonly TcpListener listener;
            readonly Func<string, string> responder;
            volatile bool running = true;
            int requestCount;
            readonly Thread thread;

            public RawResponder(Func<string, string> responder)
            {
                this.responder = responder;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                thread = new Thread(new ThreadStart(AcceptLoop));
                thread.IsBackground = true;
                thread.Start();
            }

            public int Port
            {
                get { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            }

            public int RequestCount
            {
                get { return requestCount; }
            }

            void AcceptLoop()
            {
                while (running)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Interlocked.Increment(ref requestCount);
                    try
                    {
                        HandleClient(client);
                    }
                    catch (Exception)
                    {
                        // Клиент оборвал соединение раньше, чем мы дописали ответ (ожидаемо
                        // для теста с превышением лимита) — не критично для теста.
                    }
                }
            }

            void HandleClient(TcpClient client)
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string request = ReadUntilHeadersEnd(stream);
                    string response = responder(request);
                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            static string ReadUntilHeadersEnd(NetworkStream stream)
            {
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[4096];
                    while (true)
                    {
                        int read = stream.Read(chunk, 0, chunk.Length);
                        if (read <= 0)
                        {
                            break;
                        }
                        buffer.Write(chunk, 0, read);
                        string text = Encoding.UTF8.GetString(buffer.ToArray());
                        if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                        {
                            return text;
                        }
                    }
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }
            }

            public void Stop()
            {
                running = false;
                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
