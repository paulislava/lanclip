using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanClip
{
    // Единственный публичный исход операции PullClient.Pull() сверх успешного
    // PullResult. Зеркало mac-стороннего PullError: noPeer | peerEmpty | tooLarge |
    // changed | transport.
    class PullException : Exception
    {
        public const string CodeNoPeer = "noPeer";
        public const string CodePeerEmpty = "peerEmpty";
        public const string CodeTooLarge = "tooLarge";
        public const string CodeChanged = "changed";
        public const string CodeTransport = "transport";

        public readonly string Code;
        public readonly long TotalSize; // осмысленно только при Code == CodeTooLarge
        public readonly long MaxBytes;  // осмысленно только при Code == CodeTooLarge
        // Находка I10 финального ревью: осмысленно только при Code == CodeNoPeer.
        // Раньше NoPeer покрывал и "никто не отвечает" (сеть/файрвол), и "сосед
        // ответил 401" (опечатка в токене) одним и тем же случаем — true, если
        // хотя бы один адрес из peers ответил, но отверг токен (см.
        // PeerResolver.LastResolveSawTokenRejection).
        public readonly bool TokenRejected;

        PullException(string code, long totalSize, long maxBytes, bool tokenRejected, string message)
            : base(message)
        {
            Code = code;
            TotalSize = totalSize;
            MaxBytes = maxBytes;
            TokenRejected = tokenRejected;
        }

        public static PullException NoPeer(bool tokenRejected)
        {
            string message = tokenRejected
                ? "сосед ответил, но отверг токен (401): проверьте поле token в конфиге"
                : "нет ни одного живого соседа";
            return new PullException(CodeNoPeer, 0, 0, tokenRejected, message);
        }

        public static PullException PeerEmpty()
        {
            return new PullException(CodePeerEmpty, 0, 0, false, "буфер соседа пуст");
        }

        public static PullException TooLarge(long totalSize, long maxBytes)
        {
            return new PullException(CodeTooLarge, totalSize, maxBytes, false,
                "содержимое соседа " + totalSize + " байт превышает предел " + maxBytes + " байт");
        }

        public static PullException Changed()
        {
            return new PullException(CodeChanged, 0, 0, false,
                "буфер соседа изменился во время передачи, повторите позже");
        }

        public static PullException Transport(string detail)
        {
            return new PullException(CodeTransport, 0, 0, false, "ошибка транспорта: " + detail);
        }
    }

    // Оркестрирует полный цикл pull: находит живого соседа через PeerResolver,
    // забирает у него манифест и содержимое через IBlobFetcher, раскладывает файлы в
    // партию Staging и записывает итог в локальный буфер через IClipboardWriter.
    // Зеркало mac/Sources/LanClipCore/PullClient.swift.
    //
    // Инвариант на всех путях ошибок: буфер либо не тронут вовсе, либо получает полный
    // результат — частичной записи быть не может, потому что writer.Write вызывается
    // один раз, только после того как все данные (текст/картинка в память, файлы — на
    // диск партии) собраны целиком без ошибок.
    class PullClient
    {
        readonly Config config;
        readonly PeerResolver resolver;
        readonly IBlobFetcher fetcher;
        readonly Staging staging;
        readonly IClipboardWriter writer;

        public PullClient(Config config, PeerResolver resolver, IBlobFetcher fetcher,
            Staging staging, IClipboardWriter writer)
        {
            this.config = config;
            this.resolver = resolver;
            this.fetcher = fetcher;
            this.staging = staging;
            this.writer = writer;
        }

        // Ровно один автоматический повтор при CodeChanged (рулинг ревью задачи 26).
        // Мелкая находка финального ревью починила этот комментарий: он раньше
        // приписывал измеренную частоту "этой стороне" (Windows) — на самом деле
        // замер (2 из 10 попыток, направление ПК -> Mac, файлы с пробелами и
        // кириллицей в именах) снят с Mac-стороннего lanclipd pull, тянущего
        // буфер С ЭТОЙ, Windows, машины; про частоту повторов НА САМОЙ Windows
        // ничего не измерялось. Первая гипотеза о причине (синхронность
        // Clipboard.SetDataObject/насос сообщений WinForms) не выдержала разбора
        // ревью и отклонена; настоящая первопричина гонки НЕ установлена (подробности
        // и отклонённые гипотезы — ai/ERRORS.md). Повтор оправдан устойчивостью
        // протокола к гонке ЛЮБОЙ природы, а не тем, что причина известна и это
        // частый баг именно здесь — раньше несовпадение seq долетало до
        // пользователя как явная ошибка при каждом случае; теперь протокол сам
        // пробует ещё раз, прежде чем сдаться.
        //
        // Повтор — это весь цикл заново (манифест + скачивание), а не докачка
        // недостающего блоба: раз seq уехал, прежний манифест целиком недействителен,
        // докачивать по его Blobs уже нечего. AttemptPull(host) возвращает совершенно
        // новый результат второй попытки — от первой, сорвавшейся попытки, в локальном
        // буфере ничего не остаётся (она обрывается до writer.Write, как и раньше), а
        // на диске остаётся не более чем мусорная партия стейджинга, которую уберёт
        // staging.Cleanup() на следующем успешном Pull().
        //
        // Ровно один повтор, не цикл до успеха: если буфер соседа меняется непрерывно,
        // бесконечный повтор превратил бы нажатие хоткея в зависание. Второй
        // CodeChanged подряд не перехватывается этим catch и летит наружу как обычная
        // ошибка — ровно так же, как раньше вела себя первая попытка.
        //
        // host резолвится один раз до обеих попыток: 409 не считается сбоем
        // транспорта (сосед в порядке, только его буфер уехал), поэтому кеш
        // PeerResolver не трогается ни при первой, ни при второй попытке.
        public PullResult Pull()
        {
            string host = resolver.Resolve();
            if (host == null)
            {
                throw PullException.NoPeer(resolver.LastResolveSawTokenRejection);
            }

            try
            {
                return AttemptPull(host);
            }
            catch (PullException e)
            {
                if (e.Code != PullException.CodeChanged)
                {
                    throw;
                }
                return AttemptPull(host);
            }
        }

        // Один полный цикл "манифест -> проверка -> скачивание -> запись" — то, чем
        // раньше целиком был Pull(). Вынесено отдельно, чтобы Pull() мог прогнать его
        // дважды подряд при CodeChanged, не дублируя логику.
        PullResult AttemptPull(string host)
        {
            Manifest manifest = FetchManifest(host);

            if (manifest.Kind == "empty")
            {
                throw PullException.PeerEmpty();
            }

            // Ruling задачи 11 (Mac) / 21 (Windows): Manifest.FromJson не проверяет
            // межполевые инварианты манифеста — {"kind":"image","seq":1} без blobs, либо
            // с totalSize, расходящимся с суммой по blobs, разбирается без единой
            // ошибки. Здесь же считается настоящий totalSize — сумма по blobs, а не
            // присланное соседом число: maxBytes обязан ловить и того, кто занизил
            // размер в манифесте, а прислал больше данных по факту (см. сверку размера
            // каждого скачанного блоба ниже).
            long totalSize = ValidateManifestIntegrity(manifest);

            if (totalSize > config.MaxBytes)
            {
                throw PullException.TooLarge(totalSize, config.MaxBytes);
            }

            // Новая партия стейджинга на каждый вызов AttemptPull — DownloadFiles
            // вызывает staging.NewBatch() заново, поэтому вторая попытка сама по себе
            // никогда не досыпает файлы в партию первой, сорвавшейся попытки.
            DownloadOutcome outcome = Download(manifest, host);

            writer.Write(outcome.Content);
            try
            {
                staging.Cleanup();
            }
            catch (Exception)
            {
                // Уборка — housekeeping вокруг уже состоявшегося успеха: её отказ не
                // должен превращать успешную вставку в ошибку Pull() (тем же принципом
                // сам Staging.Cleanup() уже терпим к отказу удаления отдельной партии).
            }

            PullResult result = new PullResult();
            result.Kind = manifest.Kind;
            result.FileCount = outcome.FileCount;
            result.Bytes = outcome.Bytes;
            return result;
        }

        // MARK: - Манифест

        Manifest FetchManifest(string host)
        {
            try
            {
                return fetcher.Manifest(host, config.Port, config.Token);
            }
            catch (Exception e)
            {
                // Манифест разбирается на чужой машине: Manifest.FromJson использует
                // ToLong, который на вложенном объекте или переполнении в size/totalSize
                // бросает InvalidCastException/OverflowException вместо контрактного
                // FormatException — сюда может прилететь любой из этих типов вперемешку
                // с HttpClientException (статус/тайм-аут/обрыв соединения). Всё это —
                // сорванный обмен с соседом: кеш резолвера сбрасывается наравне с
                // прочими ошибками транспорта, повтор тому же соседу почти наверняка
                // воспроизведёт то же самое.
                resolver.Invalidate();
                throw PullException.Transport(Describe(e));
            }
        }

        // Проверяет межполевые инварианты, которые Manifest.FromJson не проверяет сам, и
        // возвращает настоящий размер содержимого, который должен гейтиться maxBytes —
        // для text это длина текста в UTF-8, для image/files — сумма по blobs,
        // посчитанная нами, а не присланное соседом число (иначе сосед мог бы объявить
        // totalSize: 1 и приложить блобы суммарно на гигабайты).
        long ValidateManifestIntegrity(Manifest manifest)
        {
            if (manifest.Kind == "text")
            {
                if (manifest.Text == null)
                {
                    throw CorruptedManifest("kind=text без text");
                }
                // Текст, в отличие от файлов, целиком приезжает в теле манифеста и уже
                // лежит в памяти к этому моменту — тот же лимит maxBytes обязан его
                // гейтить на общих основаниях, а не считать текст бесплатным.
                return Encoding.UTF8.GetByteCount(manifest.Text);
            }

            if (manifest.Kind == "image" || manifest.Kind == "files")
            {
                if (manifest.Blobs == null || manifest.Blobs.Count == 0)
                {
                    throw CorruptedManifest("kind=" + manifest.Kind + " без blobs");
                }

                // BlobRef.Size — обычное число с провода, ничем не ограниченное:
                // складывать его наивным + нельзя — в C# арифметика по умолчанию
                // заворачивается молча (в отличие от Swift, где это фатальный краш), и
                // сумма превратилась бы в маленькое число, тривиально проходящее лимит.
                // checked{} заставляет переполнение бросить OverflowException явно.
                // Отрицательный size отдельного блоба тоже отклоняется — он не роняет
                // арифметику, но незаметно занижает сумму и обходит лимит maxBytes.
                long computedTotal = 0;
                foreach (BlobRef blob in manifest.Blobs)
                {
                    if (blob.Size < 0)
                    {
                        throw CorruptedManifest("blob " + blob.Rel + " с отрицательным size=" + blob.Size);
                    }
                    try
                    {
                        checked
                        {
                            computedTotal = computedTotal + blob.Size;
                        }
                    }
                    catch (OverflowException)
                    {
                        throw CorruptedManifest("сумма размеров blobs переполняет Int64");
                    }
                }

                // Manifest.TotalSize — long? (зеркало Mac-стороннего Int?, после
                // починки Manifest.cs по итогам ревью задачи 21): null означает "поле
                // отсутствовало в JSON", а не "прислали 0" — поэтому сосед, честно
                // приславший totalSize:0 при ненулевой сумме blobs, ловится этой
                // проверкой наравне с любым другим расхождением. Граница maxBytes при
                // этом ни в одном случае не ослабляется: она двумя строками выше
                // считается по computedTotal, а не по TotalSize, независимо от исхода
                // этой проверки.
                if (manifest.TotalSize.HasValue && manifest.TotalSize.Value != computedTotal)
                {
                    throw CorruptedManifest("totalSize=" + manifest.TotalSize.Value
                        + " в манифесте расходится с суммой по blobs=" + computedTotal);
                }
                return computedTotal;
            }

            return 0; // kind=empty — отсечено раньше в Pull().
        }

        // Самопротиворечивый манифест или блоб, пришедший не того размера, что был
        // обещан, — не гонка с соседским буфером (та ловится через 409), а дефект/подделка
        // на стороне отправителя. Кеш резолвера сбрасывается наравне с прочими ошибками
        // транспорта.
        PullException CorruptedManifest(string detail)
        {
            resolver.Invalidate();
            return PullException.Transport("манифест соседа испорчен: " + detail);
        }

        static string Describe(Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }

        // MARK: - Загрузка содержимого

        class DownloadOutcome
        {
            public ClipContent Content;
            public int FileCount;
            public long Bytes;
        }

        DownloadOutcome Download(Manifest manifest, string host)
        {
            if (manifest.Kind == "text")
            {
                // Проверено в ValidateManifestIntegrity(_:).
                string text = manifest.Text != null ? manifest.Text : "";
                DownloadOutcome outcome = new DownloadOutcome();
                outcome.Content = ClipContent.OfText(text);
                outcome.FileCount = 0;
                outcome.Bytes = Encoding.UTF8.GetByteCount(text);
                return outcome;
            }

            if (manifest.Kind == "image")
            {
                return DownloadImage(manifest, host);
            }

            if (manifest.Kind == "files")
            {
                return DownloadFiles(manifest, host);
            }

            // Отсечено раньше в Pull() — сюда попасть невозможно.
            throw PullException.PeerEmpty();
        }

        DownloadOutcome DownloadImage(Manifest manifest, string host)
        {
            // Проверено в ValidateManifestIntegrity(_:): Blobs не null и не пуст.
            BlobRef blob = manifest.Blobs[0];

            byte[] data;
            try
            {
                data = fetcher.Blob(host, config.Port, config.Token, blob.I, manifest.Seq, null);
            }
            catch (HttpClientException e)
            {
                throw MapBlobFetchError(e);
            }

            if (data == null)
            {
                throw CorruptedManifest("сервер не вернул тело блоба изображения");
            }

            // Соседу мало объявить малый totalSize — реальный размер каждого блоба
            // сверяется отдельно, иначе лимит maxBytes ловил бы только то, что сосед сам
            // про себя сказал, а не то, что реально пришло.
            if (data.Length != blob.Size)
            {
                throw CorruptedManifest("блоб изображения пришёл размером " + data.Length
                    + " байт, манифест обещал " + blob.Size);
            }

            DownloadOutcome outcome = new DownloadOutcome();
            outcome.Content = ClipContent.OfImage(data);
            outcome.FileCount = 0;
            outcome.Bytes = data.Length;
            return outcome;
        }

        DownloadOutcome DownloadFiles(Manifest manifest, string host)
        {
            // Проверено в ValidateManifestIntegrity(_:): Blobs не null и не пуст.
            StagingBatch batch = staging.NewBatch();

            List<string> paths = new List<string>();
            long totalBytes = 0;
            foreach (BlobRef blob in manifest.Blobs)
            {
                // batch.Destination может бросить StagingException для небезопасного
                // rel — это отдельный, ортогональный рубеж защиты (задача 9/20), а не
                // ошибка транспорта: пропускаем её наружу необёрнутой, как и на Mac
                // (там destination(for:) точно так же не перехватывается специально).
                // Буфер к этому моменту ещё не тронут — writer.Write ниже не вызван.
                string destination = batch.Destination(blob.Rel);

                try
                {
                    fetcher.Blob(host, config.Port, config.Token, blob.I, manifest.Seq, destination);
                }
                catch (HttpClientException e)
                {
                    throw MapBlobFetchError(e);
                }

                long actualSize = new FileInfo(destination).Length;
                if (actualSize != blob.Size)
                {
                    throw CorruptedManifest("файл " + blob.Rel + " пришёл размером " + actualSize
                        + " байт, манифест обещал " + blob.Size);
                }

                paths.Add(destination);
                totalBytes += actualSize;
            }

            DownloadOutcome outcome = new DownloadOutcome();
            outcome.Content = ClipContent.OfFiles(paths);
            outcome.FileCount = paths.Count;
            outcome.Bytes = totalBytes;
            return outcome;
        }

        // 409 сигналит, что содержимое соседа сменилось между манифестом и скачиванием
        // блоба — это не сбой сети (сосед ответил, просто данные устарели), поэтому кеш
        // резолвера не трогаем. Любой другой статус или транспортная ошибка — то самое
        // "любая ошибка транспорта", после которой кеш обязан быть сброшен.
        PullException MapBlobFetchError(HttpClientException error)
        {
            if (error.Code == HttpClientException.CodeStatus && error.Status == 409)
            {
                return PullException.Changed();
            }
            resolver.Invalidate();
            return PullException.Transport(error.ToString());
        }
    }
}
