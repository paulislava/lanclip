using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace LanClip
{
    // HTTP-сервер буфера обмена поверх System.Net.HttpListener. Зеркало
    // mac/Sources/LanClipCore/HttpServer.swift (задачи 6/7). Маршрутизация вынесена
    // в статический Route(...) — он чист относительно сети и StaExecutor, поэтому все
    // коды ответов проверяются без поднятия сокета (первый слой тестов). Транспортный
    // слой (Start/Stop/обработка соединений) проверяется вторым слоем — сквозными
    // тестами поверх настоящего HttpListener.
    class HttpResponseSpec
    {
        public int Status;
        public string ContentType;
        // Тело, уже целиком лежащее в памяти (JSON, ошибки, картинка — она и так уже
        // была в памяти после чтения буфера). Взаимоисключающе с FilePath.
        public byte[] Body;
        // Тело, которое нужно дочитать с диска при отправке (находка I6 финального
        // ревью) — см. BlobPayload в Snapshot.cs. Взаимоисключающе с Body.
        public string FilePath;
        public long FileSize;

        public static HttpResponseSpec Empty(int status)
        {
            HttpResponseSpec response = new HttpResponseSpec();
            response.Status = status;
            response.Body = new byte[0];
            return response;
        }

        public static HttpResponseSpec Json(int status, byte[] body)
        {
            HttpResponseSpec response = new HttpResponseSpec();
            response.Status = status;
            response.ContentType = "application/json";
            response.Body = body;
            return response;
        }

        public static HttpResponseSpec Bytes(byte[] body)
        {
            HttpResponseSpec response = new HttpResponseSpec();
            response.Status = 200;
            response.ContentType = "application/octet-stream";
            response.Body = body;
            return response;
        }

        // Блоб, который сервер обязан отдать потоком с диска — см. FilePath выше.
        public static HttpResponseSpec File(string path, long size)
        {
            HttpResponseSpec response = new HttpResponseSpec();
            response.Status = 200;
            response.ContentType = "application/octet-stream";
            response.FilePath = path;
            response.FileSize = size;
            return response;
        }
    }

    // Разобранный входящий запрос — независим от того, пришёл ли он через настоящий
    // HttpListener или был собран вручную тестом. Заголовки хранятся с ключами в
    // нижнем регистре.
    class HttpRequestSpec
    {
        public readonly string Method;
        public readonly string Path;
        public readonly Dictionary<string, string> Query;
        public readonly Dictionary<string, string> Headers;

        public HttpRequestSpec(string method, string path, Dictionary<string, string> query,
            Dictionary<string, string> headers)
        {
            Method = method;
            Path = path;
            Query = query != null ? query : new Dictionary<string, string>();
            Headers = headers != null ? headers : new Dictionary<string, string>();
        }

        public string Header(string name)
        {
            string value;
            if (Headers.TryGetValue(name.ToLowerInvariant(), out value))
            {
                return value;
            }
            return null;
        }

        public string QueryParam(string name)
        {
            string value;
            if (Query.TryGetValue(name, out value))
            {
                return value;
            }
            return null;
        }
    }

    // Порт вне допустимого диапазона 1..65535 — зеркало Swift-стороннего
    // HttpServerError.invalidPort(Int): Config не валидируется автоматически перед
    // конструированием сервера, поэтому эта проверка должна быть предсказуемым
    // типизированным отказом, а не падать где-то глубже в HttpListener.
    class HttpServerException : Exception
    {
        public readonly int Port;

        public HttpServerException(int port)
            : base("неверный порт: " + port)
        {
            Port = port;
        }
    }

    class HttpServer
    {
        // Приходящее тело запроса накапливается до этого предела; превышение
        // обрывает обработку ответом 400, чтобы его нельзя было раздуть до
        // исчерпания памяти (Ruling: то же самое, что и на Mac, где для этого
        // накопление байт до полного HTTP-сообщения было ограничено явно).
        const int MaxIncomingRequestBytes = 1048576;

        // Тело ответа отдаётся чанками этого размера, а не одним куском в
        // память — блоб может весить сотни мегабайт.
        const int SendChunkSize = 262144;

        readonly Config config;
        readonly SnapshotStore snapshots;
        readonly string hostName;
        readonly Func<PullResult> pull;
        readonly string bindHost;

        HttpListener listener;
        Thread acceptThread;
        int boundPort;
        volatile bool running;

        // Рабочий режим: слушает "http://+:<port>/". Для этого префикса нужны либо
        // права администратора, либо запись в urlacl (её делает установочный скрипт
        // задачи 25) — сам HttpServer на это не полагается и не проверяет.
        public HttpServer(Config config, SnapshotStore snapshots, string hostName, Func<PullResult> pull)
            : this(config, snapshots, hostName, pull, "+")
        {
        }

        // Тестовый режим: явный bindHost (обычно "127.0.0.1") не требует urlacl вовсе,
        // независимо от прав процесса — в отличие от "+", это не сильный (wildcard)
        // префикс. Используется только тестами этого файла.
        public HttpServer(Config config, SnapshotStore snapshots, string hostName, Func<PullResult> pull,
            string bindHost)
        {
            this.config = config;
            this.snapshots = snapshots;
            this.hostName = hostName;
            this.pull = pull;
            this.bindHost = bindHost;
        }

        public int BoundPort
        {
            get { return boundPort; }
        }

        public void Start()
        {
            // Config не валидируется автоматически перед передачей сюда (Program.cs
            // появится только в задаче 23, и полагаться на то, что он не забудет
            // вызвать Config.Validate(), нельзя) — без явной проверки диапазона
            // порт вне 1..65535 дошёл бы до HttpListener необработанным
            // HttpListenerException вместо предсказуемого типизированного отказа.
            if (config.Port < 1 || config.Port > 65535)
            {
                throw new HttpServerException(config.Port);
            }

            HttpListener newListener = new HttpListener();
            string prefix = "http://" + bindHost + ":" + config.Port.ToString(CultureInfo.InvariantCulture) + "/";
            newListener.Prefixes.Add(prefix);
            newListener.Start();

            listener = newListener;
            boundPort = config.Port;
            running = true;

            acceptThread = new Thread(new ThreadStart(AcceptLoop));
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }

        public void Stop()
        {
            running = false;
            HttpListener current = listener;
            listener = null;
            if (current != null)
            {
                try
                {
                    current.Stop();
                }
                catch (Exception)
                {
                    // Останов уже небезопасен для дальнейшего использования — не важно, как
                    // именно он не удался.
                }
                try
                {
                    current.Close();
                }
                catch (Exception)
                {
                }
            }

            Thread thread = acceptThread;
            acceptThread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        void AcceptLoop()
        {
            HttpListener current = listener;
            while (running && current != null)
            {
                HttpListenerContext context;
                try
                {
                    context = current.GetContext();
                }
                catch (Exception)
                {
                    // Listener остановлен/закрыт (Stop()/Close()) — выходим из цикла.
                    return;
                }

                HttpListenerContext capturedContext = context;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Handle(capturedContext);
                });
            }
        }

        void Handle(HttpListenerContext context)
        {
            try
            {
                HandleInner(context);
            }
            catch (Exception)
            {
                // Любая необработанная ошибка на этом уровне не должна ронять сервер —
                // соединение просто обрывается.
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                }
            }
        }

        void HandleInner(HttpListenerContext context)
        {
            HttpListenerRequest httpRequest = context.Request;

            byte[] body;
            if (!TryReadBodyWithLimit(httpRequest.InputStream, MaxIncomingRequestBytes, out body))
            {
                WriteResponse(context, HttpResponseSpec.Empty(400));
                return;
            }

            Dictionary<string, string> query = new Dictionary<string, string>();
            foreach (string key in httpRequest.QueryString.AllKeys)
            {
                if (key != null)
                {
                    query[key] = httpRequest.QueryString[key];
                }
            }

            Dictionary<string, string> headers = new Dictionary<string, string>();
            foreach (string key in httpRequest.Headers.AllKeys)
            {
                if (key != null)
                {
                    headers[key.ToLowerInvariant()] = httpRequest.Headers[key];
                }
            }

            string remote = httpRequest.RemoteEndPoint != null
                ? httpRequest.RemoteEndPoint.Address.ToString()
                : "";

            HttpRequestSpec spec = new HttpRequestSpec(httpRequest.HttpMethod, httpRequest.Url.AbsolutePath,
                query, headers);

            // Находка I7 финального ревью: раньше здесь стоял ЕЩЁ ОДИН, свой собственный
            // STA-исполнитель HttpServer — избыточный, потому что WinClipboard (см.
            // WinClipboard.cs) уже маршалит каждое обращение к System.Windows.Forms.
            // Clipboard на СВОЙ отдельный STA-поток сам. Запрос без нужды прыгал через
            // ДВА STA-потока подряд, и вдобавок весь Route() (включая долгий Pull())
            // сериализовался через этот же исполнитель — пока один запрос ждал pull(),
            // остальные (включая /health) не обслуживались вовсе. Route() теперь
            // выполняется прямо на потоке из ThreadPool — запросы едут параллельно, а
            // сериализация настоящего доступа к буферу остаётся на StaExecutor'е
            // WinClipboard, где она и нужна. SnapshotStore.Current()/Blob() при этом
            // обязаны сами быть потокобезопасны (добавлена блокировка — см. Snapshot.cs),
            // раз теперь их можно вызвать из нескольких потоков одновременно.
            HttpResponseSpec response = Route(spec, config, snapshots, hostName, remote, pull);

            WriteResponse(context, response);
        }

        // Читает тело запроса, обрывая накопление, как только оно превысило предел —
        // независимо от того, что заявлено в Content-Length (заголовку не доверяем).
        static bool TryReadBodyWithLimit(Stream input, int limit, out byte[] body)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[65536];
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > limit)
                    {
                        body = null;
                        return false;
                    }
                }
                body = buffer.ToArray();
                return true;
            }
        }

        static void WriteResponse(HttpListenerContext context, HttpResponseSpec response)
        {
            HttpListenerResponse httpResponse = context.Response;
            try
            {
                // Зеркало Mac-стороны, где каждое соединение обслуживает ровно один запрос
                // и закрывается после ответа (connection.cancel() в конце sendBody): keep-alive
                // здесь не реализован, поэтому принудительно закрываем соединение сами — иначе
                // недочитанный (например, оборванный по лимиту) остаток тела запроса остался бы
                // во входном потоке и был бы ошибочно принят за начало следующего запроса на
                // переиспользуемом соединении.
                httpResponse.KeepAlive = false;

                if (response.FilePath != null)
                {
                    WriteFileResponse(httpResponse, response);
                    return;
                }

                httpResponse.StatusCode = response.Status;
                if (response.ContentType != null)
                {
                    httpResponse.ContentType = response.ContentType;
                }

                byte[] body = response.Body != null ? response.Body : new byte[0];
                httpResponse.ContentLength64 = body.Length;

                int offset = 0;
                while (offset < body.Length)
                {
                    int chunkSize = Math.Min(SendChunkSize, body.Length - offset);
                    httpResponse.OutputStream.Write(body, offset, chunkSize);
                    offset += chunkSize;
                }
            }
            finally
            {
                httpResponse.OutputStream.Close();
            }
        }

        // Отдаёт response.FilePath чанками прямо с диска (находка I6) — файл
        // никогда не оказывается целиком в памяти процесса, независимо от его
        // размера. Файл открывается ДО того, как в ответ уходит статус/заголовки:
        // если открыть не удалось (файл исчез между построением снимка и отдачей —
        // гонка с уборкой стейджинга или пользователь удалил исходник), можно
        // честно ответить 500 вместо того, чтобы объявить Content-Length, а потом
        // оборвать поток на пустом месте.
        static void WriteFileResponse(HttpListenerResponse httpResponse, HttpResponseSpec response)
        {
            FileStream input;
            try
            {
                input = new FileStream(response.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception)
            {
                httpResponse.StatusCode = 500;
                httpResponse.ContentLength64 = 0;
                return;
            }

            using (input)
            {
                httpResponse.StatusCode = response.Status;
                if (response.ContentType != null)
                {
                    httpResponse.ContentType = response.ContentType;
                }
                httpResponse.ContentLength64 = response.FileSize;

                byte[] buffer = new byte[SendChunkSize];
                long remaining = response.FileSize;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = input.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        // Файл на диске оказался короче, чем Content-Length, уже объявленный
                        // выше (изменился между построением снимка и отдачей) — обрываем
                        // передачу здесь; клиент увидит меньше байт, чем было обещано, и
                        // корректно распознает это как оборванный ответ, а не молча примет
                        // усечённые данные за полные.
                        return;
                    }
                    httpResponse.OutputStream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        // MARK: - Routing (pure, no networking, no StaExecutor)

        public static HttpResponseSpec Route(HttpRequestSpec request, Config config, SnapshotStore snapshots,
            string hostName, string remote, Func<PullResult> pull)
        {
            if (!IsPrivateAddress(remote))
            {
                return HttpResponseSpec.Empty(403);
            }

            string provided = request.Header("x-clip-token");
            if (!TokensMatch(provided != null ? provided : "", config.Token))
            {
                return HttpResponseSpec.Empty(401);
            }

            if (request.Method == "GET" && request.Path == "/health")
            {
                return HealthResponse(hostName);
            }
            if (request.Method == "GET" && request.Path == "/clip")
            {
                return ClipResponse(snapshots);
            }
            if (request.Method == "POST" && request.Path == "/pull")
            {
                return PullResponse(pull);
            }

            if (request.Path.StartsWith("/clip/blob/", StringComparison.Ordinal))
            {
                if (request.Method != "GET")
                {
                    return HttpResponseSpec.Empty(405);
                }
                return BlobResponse(request, snapshots);
            }

            if (request.Path == "/health" || request.Path == "/clip" || request.Path == "/pull")
            {
                return HttpResponseSpec.Empty(405);
            }

            return HttpResponseSpec.Empty(404);
        }

        static HttpResponseSpec HealthResponse(string hostName)
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["ok"] = true;
            obj["host"] = hostName;
            obj["version"] = Version.Protocol;
            return HttpResponseSpec.Json(200, Encoding.UTF8.GetBytes(Json.Write(obj)));
        }

        static HttpResponseSpec ClipResponse(SnapshotStore snapshots)
        {
            try
            {
                ClipSnapshot snapshot = snapshots.Current();
                return HttpResponseSpec.Json(200, Encoding.UTF8.GetBytes(snapshot.Manifest.ToJson()));
            }
            catch (Exception)
            {
                // Ruling: ошибка построения снимка обязана превращаться в код ответа, а не
                // ронять сервер — и не только ожидаемые типы исключений, а вообще любые
                // (Manifest.FromJson и файловый ввод-вывод могут бросить что угодно).
                return HttpResponseSpec.Empty(500);
            }
        }

        static HttpResponseSpec BlobResponse(HttpRequestSpec request, SnapshotStore snapshots)
        {
            string suffix = request.Path.Substring("/clip/blob/".Length);
            int index;
            if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                return HttpResponseSpec.Empty(404);
            }

            int seq = -1;
            string seqParam = request.QueryParam("seq");
            if (seqParam != null)
            {
                int parsedSeq;
                if (int.TryParse(seqParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedSeq))
                {
                    seq = parsedSeq;
                }
            }

            try
            {
                BlobPayload payload = snapshots.Blob(index, seq);
                if (payload == null)
                {
                    return HttpResponseSpec.Empty(404);
                }
                if (payload.FilePath != null)
                {
                    return HttpResponseSpec.File(payload.FilePath, payload.FileSize);
                }
                return HttpResponseSpec.Bytes(payload.Data);
            }
            catch (StaleSeqException)
            {
                return HttpResponseSpec.Empty(409);
            }
            catch (Exception)
            {
                return HttpResponseSpec.Empty(500);
            }
        }

        static HttpResponseSpec PullResponse(Func<PullResult> pull)
        {
            try
            {
                PullResult result = pull();
                return HttpResponseSpec.Json(200, Encoding.UTF8.GetBytes(result.ToJson()));
            }
            catch (Exception e)
            {
                Dictionary<string, object> obj = new Dictionary<string, object>();
                obj["error"] = e.Message;
                return HttpResponseSpec.Json(503, Encoding.UTF8.GetBytes(Json.Write(obj)));
            }
        }

        // Сравнение токена по всей длине без раннего выхода — защита от подбора по
        // времени ответа. Наивное string == такой гарантии не даёт (сравнение
        // прерывается на первом несовпавшем символе).
        static bool TokensMatch(string provided, string expected)
        {
            byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            int mismatch = providedBytes.Length == expectedBytes.Length ? 0 : 1;

            int length = Math.Max(providedBytes.Length, expectedBytes.Length);
            for (int i = 0; i < length; i++)
            {
                byte lhs = i < providedBytes.Length ? providedBytes[i] : (byte)0;
                byte rhs = i < expectedBytes.Length ? expectedBytes[i] : (byte)0;
                mismatch |= lhs ^ rhs;
            }
            return mismatch == 0;
        }

        // Зеркало mac/Sources/LanClipCore/Net.swift: isPrivateAddress. Ни один из
        // оставшихся Windows-тасков не создаёт для этого отдельного файла, а сервер без
        // проверки приватности собраться не может — реализация живёт здесь.
        static bool IsPrivateAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return false;
            }

            string bare = address;
            int percentIndex = bare.IndexOf('%');
            if (percentIndex >= 0)
            {
                bare = bare.Substring(0, percentIndex);
            }
            if (bare.Length == 0)
            {
                return false;
            }

            if (bare.IndexOf(':') >= 0)
            {
                string lowered = bare.ToLowerInvariant();
                if (lowered == "::1")
                {
                    return true;
                }

                string[] groups = lowered.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                string firstGroup = groups.Length > 0 ? groups[0] : "";
                if (firstGroup.Length == 0)
                {
                    // "::" покрывает и loopback, и link-local в сокращённой записи.
                    return true;
                }

                // Только AllowHexSpecifier, без AllowLeadingWhite/AllowTrailingWhite:
                // NumberStyles.HexNumber включает оба этих флага и тем самым молча
                // принимает пробелы по краям группы — Swift-сторонний Int(_:radix:)
                // такого не допускает, разбор обязан быть не мягче эталона.
                int first;
                if (int.TryParse(firstGroup, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out first))
                {
                    // Link-local: fe80::/10 (fe80..febf)
                    if (first >= 0xfe80 && first <= 0xfebf)
                    {
                        return true;
                    }
                    // ULA: fc00::/7 (fc00..fdff)
                    if (first >= 0xfc00 && first <= 0xfdff)
                    {
                        return true;
                    }
                }
                return false;
            }

            // RemoveEmptyEntries — зеркало Swift-стороннего split(separator: "."), который
            // по умолчанию опускает пустые подпоследовательности. Без этого "127.0.0.1."
            // (обычный хвостовой разделитель) давал бы здесь 5 частей вместо 4 и
            // расходился бы с Swift, который трактует его как те же 4 октета и признаёт
            // приватным.
            string[] parts = bare.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
            {
                return false;
            }

            int[] octets = new int[4];
            for (int i = 0; i < 4; i++)
            {
                // AllowLeadingSign без AllowLeadingWhite/AllowTrailingWhite: NumberStyles.Integer
                // включает оба этих флага и тем самым молча принимает пробелы по краям
                // октета — Swift-сторонний Int(String) такого не допускает.
                int value;
                if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
                    || value < 0 || value > 255)
                {
                    return false;
                }
                octets[i] = value;
            }

            if (octets[0] == 127 || octets[0] == 10)
            {
                return true;
            }
            if (octets[0] == 192 && octets[1] == 168)
            {
                return true;
            }
            if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            {
                return true;
            }
            if (octets[0] == 169 && octets[1] == 254)
            {
                return true;
            }
            return false;
        }
    }
}
