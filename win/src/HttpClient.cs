using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace LanClip
{
    // Проверка живости соседа: GET /health с ожиданием на timeoutMs, без деталей
    // ошибки. Зеркало mac/Sources/LanClipCore/HttpClient.swift: HealthProbing.
    interface IHealthProber
    {
        bool Probe(string host, int port, string token, int timeoutMs);
    }

    // Загрузка манифеста и блобов буфера соседа. Зеркало mac-стороннего BlobFetching.
    // toFile == null -> тело собирается в памяти и возвращается; toFile != null ->
    // тело пишется потоком на диск и возвращается null (зеркало Data? на Mac).
    interface IBlobFetcher
    {
        Manifest Manifest(string host, int port, string token);
        byte[] Blob(string host, int port, string token, int index, int seq, string toFile);
    }

    // Ошибка транспорта при обращении к соседу. Зеркало mac-стороннего
    // HttpClientError: timeout | status(Int) | transport(String). Код хранится
    // строкой (не enum-ом) — PullClient сверяет конкретно статус 409 через Status.
    class HttpClientException : Exception
    {
        public const string CodeTimeout = "timeout";
        public const string CodeStatus = "status";
        public const string CodeTransport = "transport";

        public readonly string Code;
        public readonly int Status; // осмысленно только при Code == CodeStatus

        HttpClientException(string code, int status, string message)
            : base(message)
        {
            Code = code;
            Status = status;
        }

        public static HttpClientException Timeout()
        {
            return new HttpClientException(CodeTimeout, 0, "тайм-аут запроса к соседу");
        }

        public static HttpClientException OfStatus(int status)
        {
            return new HttpClientException(CodeStatus, status, "сосед ответил статусом " + status);
        }

        public static HttpClientException Transport(string detail)
        {
            return new HttpClientException(CodeTransport, 0, detail);
        }

        public override string ToString()
        {
            if (Code == CodeStatus)
            {
                return "status(" + Status + "): " + Message;
            }
            return Code + ": " + Message;
        }
    }

    // HTTP-клиент поверх HttpWebRequest. Зеркало mac/Sources/LanClipCore/HttpClient.swift:
    // NwHttpClient — тот же протокол (GET /health, GET /clip, GET /clip/blob/{i}?seq=),
    // тот же заголовок X-Clip-Token. На Mac ATS вынуждает работать поверх NWConnection
    // напрямую (URLSession отказывается слать plain-HTTP из процесса без Info.plist) —
    // на Windows такого ограничения нет, поэтому HttpWebRequest используется как есть,
    // без необходимости разбирать HTTP руками.
    class WebBlobFetcher : IHealthProber, IBlobFetcher
    {
        const int ReadBufferSize = 65536;

        readonly int timeoutMs;

        public WebBlobFetcher(int timeoutMs)
        {
            this.timeoutMs = timeoutMs;
        }

        // MARK: - IHealthProber

        public bool Probe(string host, int port, string token, int probeTimeoutMs)
        {
            try
            {
                HttpWebResponse response = Perform(host, port, token, "/health", probeTimeoutMs);
                try
                {
                    return (int)response.StatusCode == 200;
                }
                finally
                {
                    response.Close();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // MARK: - IBlobFetcher

        public Manifest Manifest(string host, int port, string token)
        {
            HttpWebResponse response = Perform(host, port, token, "/clip", timeoutMs);
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    byte[] body = ReadAll(stream);
                    return LanClip.Manifest.FromJson(Encoding.UTF8.GetString(body));
                }
            }
            finally
            {
                response.Close();
            }
        }

        public byte[] Blob(string host, int port, string token, int index, int seq, string toFile)
        {
            string path = "/clip/blob/" + index.ToString(CultureInfo.InvariantCulture)
                + "?seq=" + seq.ToString(CultureInfo.InvariantCulture);
            HttpWebResponse response = Perform(host, port, token, path, timeoutMs);
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    // PullClient.DownloadImage/DownloadFiles ловят только
                    // HttpClientException вокруг вызова Blob(...) (зеркало Mac-стороннего
                    // PullClient, который ловит только HttpClientError вокруг
                    // fetcher.blob(...)) — этот метод обязан гарантировать, что ЛЮБой
                    // его собственный отказ, включая локальную запись на диск (место
                    // на диске, права доступа) или обрыв чтения тела ответа, выражается
                    // именно этим типом, а не сырым IOException/UnauthorizedAccessException.
                    try
                    {
                        if (toFile != null)
                        {
                            StreamToFile(stream, toFile);
                            return null;
                        }
                        return ReadAll(stream);
                    }
                    catch (HttpClientException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        throw HttpClientException.Transport("не удалось получить тело блоба: " + e.Message);
                    }
                }
            }
            finally
            {
                response.Close();
            }
        }

        // MARK: - Core transport

        // Выполняет один GET-запрос и возвращает ответ ТОЛЬКО когда сервер вернул
        // 2xx (в этом протоколе — ровно 200). Любой другой исход (не-2xx статус,
        // тайм-аут, отказ соединения, что угодно ещё) превращается в
        // HttpClientException — вызывающий код всегда либо получает готовый
        // HttpWebResponse на 200, либо ловит именно этот тип исключения.
        static HttpWebResponse Perform(string host, int port, string token, string path, int requestTimeoutMs)
        {
            string url = "http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + path;

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
            }
            catch (Exception e)
            {
                throw HttpClientException.Transport(e.Message);
            }

            request.Method = "GET";
            request.Headers["X-Clip-Token"] = token;
            request.Timeout = requestTimeoutMs;
            request.KeepAlive = false;

            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException e)
            {
                if (e.Status == WebExceptionStatus.Timeout)
                {
                    throw HttpClientException.Timeout();
                }

                HttpWebResponse errorResponse = e.Response as HttpWebResponse;
                if (errorResponse != null)
                {
                    int status = (int)errorResponse.StatusCode;
                    errorResponse.Close();
                    throw HttpClientException.OfStatus(status);
                }

                throw HttpClientException.Transport(e.Message);
            }
        }

        static void StreamToFile(Stream input, string path)
        {
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[ReadBufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }
        }

        static byte[] ReadAll(Stream input)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[ReadBufferSize];
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }
    }
}
