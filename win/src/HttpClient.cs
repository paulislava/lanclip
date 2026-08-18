using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace LanClip
{
    // Итог одной health-пробы — находка I10 финального ревью. Зеркало
    // mac-стороннего ProbeOutcome: раньше Probe() возвращал голый bool, поэтому
    // 401 (сосед жив, но отверг токен) и отказ соединения (сосед выключен/
    // недостижим) сворачивались в одно и то же "сосед не найден". Опечатка при
    // переносе токена — самая вероятная ошибка первой настройки, и старое
    // сообщение отправляло пользователя чинить сеть/файрвол вместо конфига.
    enum ProbeOutcome
    {
        Alive,
        RejectedToken,
        Unreachable
    }

    // Проверка живости соседа: GET /health с ожиданием на timeoutMs, без деталей
    // ошибки. Зеркало mac/Sources/LanClipCore/HttpClient.swift: HealthProbing.
    interface IHealthProber
    {
        ProbeOutcome Probe(string host, int port, string token, int timeoutMs);
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

        // Находка I5 финального ревью: ReadAll/StreamToFile копили тело ответа без
        // всякого потолка, игнорируя Content-Length — сосед, знающий токен (или
        // MITM на той же подсети), мог заставить Windows-агента съесть память
        // (ReadAll -> манифест/картинка в памяти) или залить диск (StreamToFile ->
        // файлы) произвольным объёмом данных. Mac-сторона ограничивает приём в
        // память тем же числом (`NwHttpClient.maxInMemoryResponseBytes` в
        // mac/Sources/LanClipCore/HttpClient.swift, привязано к
        // Config.defaultMaxBytes) — здесь используется тот же источник константы,
        // а не отдельно придуманное число, чтобы оба клиента были согласованы.
        const long DefaultMaxResponseBytes = Config.DefaultMaxBytes;

        readonly int timeoutMs;
        readonly long maxResponseBytes;

        public WebBlobFetcher(int timeoutMs)
            : this(timeoutMs, DefaultMaxResponseBytes)
        {
        }

        // Перегрузка с явным потолком — используется тестами, которым не нужно
        // (и физически не под силу за разумное время) гонять реальные полгигабайта
        // по loopback, чтобы проверить сам механизм отказа при превышении.
        public WebBlobFetcher(int timeoutMs, long maxResponseBytes)
        {
            this.timeoutMs = timeoutMs;
            this.maxResponseBytes = maxResponseBytes;
        }

        // MARK: - IHealthProber

        // Perform() (см. ниже) контрактно либо возвращает ответ со статусом РОВНО
        // 200, либо бросает HttpClientException — для ЛЮБОГО другого статуса,
        // включая 401 (см. HttpServer.Route: неверный/отсутствующий токен) и 3xx
        // (недоверенный сосед мог ответить редиректом — см. находку I5). Поэтому
        // единственное, что здесь нужно различить, — это конкретно
        // HttpClientException.CodeStatus со Status == 401 (сосед жив, токен
        // неверный) от всего остального (сосед недостижим, тайм-аут, любой другой
        // статус — не lanclip на этом порту).
        public ProbeOutcome Probe(string host, int port, string token, int probeTimeoutMs)
        {
            try
            {
                HttpWebResponse response = Perform(host, port, token, "/health", probeTimeoutMs);
                response.Close();
                return ProbeOutcome.Alive;
            }
            catch (HttpClientException e)
            {
                if (e.Code == HttpClientException.CodeStatus && e.Status == 401)
                {
                    return ProbeOutcome.RejectedToken;
                }
                return ProbeOutcome.Unreachable;
            }
            catch (Exception)
            {
                return ProbeOutcome.Unreachable;
            }
        }

        // MARK: - IBlobFetcher

        public Manifest Manifest(string host, int port, string token)
        {
            HttpWebResponse response = Perform(host, port, token, "/clip", timeoutMs);
            try
            {
                RequireContentLength(response);
                using (Stream stream = response.GetResponseStream())
                {
                    byte[] body = ReadAll(stream, maxResponseBytes);
                    return LanClip.Manifest.FromJson(Encoding.UTF8.GetString(body));
                }
            }
            finally
            {
                response.Close();
            }
        }

        // Мелкая находка финального ревью: сервер продукта всегда шлёт
        // Content-Length на 200 (см. HttpResponse.head() на Mac и WriteResponse()
        // здесь) — его отсутствие означает испорченного/нештатного соседа, а не
        // "читай до конца потока и доверяй". HttpWebResponse.ContentLength == -1,
        // когда заголовок не пришёл; в отличие от Mac (который раньше молча
        // трактовал отсутствие как 0 и создавал пустой файл), .NET сам читает
        // поток до EOF и получил бы реальные байты без этой проверки — то есть
        // асимметрия была в другую сторону, но обе стороны обязаны отвергать
        // такой ответ одинаково явно, а не расходиться в терпимости к нему.
        static void RequireContentLength(HttpWebResponse response)
        {
            if (response.ContentLength < 0)
            {
                throw HttpClientException.Transport("ответ 200 без корректного Content-Length");
            }
        }

        public byte[] Blob(string host, int port, string token, int index, int seq, string toFile)
        {
            string path = "/clip/blob/" + index.ToString(CultureInfo.InvariantCulture)
                + "?seq=" + seq.ToString(CultureInfo.InvariantCulture);
            HttpWebResponse response = Perform(host, port, token, path, timeoutMs);
            try
            {
                RequireContentLength(response);
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
                            StreamToFile(stream, toFile, maxResponseBytes);
                            return null;
                        }
                        return ReadAll(stream, maxResponseBytes);
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
            // Находка I5 финального ревью: HttpWebRequest по умолчанию следует
            // редиректам, и в .NET Framework при этом переносит кастомные
            // заголовки (включая X-Clip-Token) на хост, указанный в Location —
            // сосед на той же подсети (или MITM), знающий токен, мог ответить 302
            // на произвольный адрес и получить токен на чужой хост. Swift-сторона
            // редиректов не разбирает вовсе (сама читает HTTP руками через
            // NWConnection), поэтому единственный способ уравнять поведение —
            // выключить автоследование здесь явно, а не полагаться на дефолт.
            request.AllowAutoRedirect = false;

            try
            {
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                // С AllowAutoRedirect=false ответы 300-399 ("Location" на произвольный
                // хост от недоверенного соседа) возвращаются сюда НОРМАЛЬНО, без
                // WebException — контракт этого метода ("либо 200, либо
                // HttpClientException") обязан остаться в силе и для них, иначе
                // вызывающая сторона получила бы 3xx-ответ, которого не ожидает.
                if ((int)response.StatusCode != 200)
                {
                    int status = (int)response.StatusCode;
                    response.Close();
                    throw HttpClientException.OfStatus(status);
                }
                return response;
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

        // Content-Length сознательно не используется как единственная защита: заголовку
        // недоверенного соседа нельзя доверять (он может занизить его и прислать больше
        // по факту, либо не прислать вовсе при chunked-передаче), поэтому предел считается
        // по фактически прочитанным байтам, а не по заявленному значению.
        static void StreamToFile(Stream input, string path, long maxBytes)
        {
            bool overflowed = false;
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[ReadBufferSize];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        overflowed = true;
                        break;
                    }
                    output.Write(buffer, 0, read);
                }
            }

            if (overflowed)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // Не удалось убрать частично записанный файл — не критично, отказ
                    // транспорта важнее и летит наружу в любом случае.
                }
                throw HttpClientException.Transport("тело ответа превысило предел " + maxBytes + " байт");
            }
        }

        static byte[] ReadAll(Stream input, long maxBytes)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[ReadBufferSize];
                long total = 0;
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw HttpClientException.Transport("тело ответа превысило предел " + maxBytes + " байт");
                    }
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }
    }
}
