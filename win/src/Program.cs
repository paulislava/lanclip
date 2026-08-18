using System;
using System.Threading;
using System.Windows.Forms;

namespace LanClip
{
    // Точка входа lanclipd. Зеркало mac/Sources/lanclipd/main.swift: те же четыре
    // подкоманды (serve/status/get/pull), тот же порядок валидации конфига, те же
    // сообщения об ошибках (переведённые на русский текст уже заложен в
    // Config/PullException — здесь только маршрутизация и перевод типизированных
    // ошибок в консольный вывод и код выхода).
    //
    // Логика самой команды (CliArgs, TrayNotifier/NullNotifier) вынесена в отдельные
    // файлы (CliArgs.cs, Notifier.cs) не просто по вкусу: build.ps1 исключает
    // Program.cs из сборки тестового исполняемого файла, поэтому всё, что должно
    // быть протестировано юнит-тестами, обязано жить вне этого файла.
    static class Program
    {
        // Таймаут HTTP-запросов к соседу (манифест/блоб) — зеркало дефолта
        // Mac-стороннего NwHttpClient(timeout: TimeInterval = 10). Таймаут проверки
        // живости (health-проба внутри PeerResolver) — отдельное, более короткое
        // значение по умолчанию (2000мс), которое PeerResolver уже использует сам.
        const int FetchTimeoutMs = 10000;

        [STAThread]
        static int Main(string[] args)
        {
            CliArgs cliArgs;
            try
            {
                cliArgs = CliArgs.Parse(args);
            }
            catch (CliArgsException e)
            {
                return HandleCliArgsError(e);
            }

            switch (cliArgs.Command)
            {
                case "serve":
                    return RunServe(cliArgs.ConfigPath);
                case "status":
                    return RunStatus(cliArgs.ConfigPath);
                case "get":
                    return RunGet(cliArgs.ConfigPath);
                case "pull":
                    return RunPull(cliArgs.ConfigPath);
                default:
                    // Недостижимо: CliArgs.Parse уже отфильтровал всё, кроме известных
                    // команд — Array.IndexOf(KnownCommands, ...) бросил бы
                    // CliArgsException раньше, чем сюда попасть.
                    throw new InvalidOperationException(
                        "непредвиденная команда после разбора аргументов: " + cliArgs.Command);
            }
        }

        static int HandleCliArgsError(CliArgsException e)
        {
            if (e.Code == CliArgsException.CodeHelpRequested)
            {
                PrintUsage();
                return 0;
            }
            if (e.Code == CliArgsException.CodeMissingConfigValue)
            {
                Console.Error.WriteLine("Флагу --config нужно значение: путь к файлу конфига.");
                PrintUsage();
                return 2;
            }
            if (e.Code == CliArgsException.CodeUnknownCommand)
            {
                Console.Error.WriteLine("Неизвестная команда: " + e.CommandName);
                PrintUsage();
                return 2;
            }

            // Ветка "всё остальное" обязана существовать сама по себе — на случай,
            // если CliArgsException когда-нибудь обзаведётся новым кодом, о котором
            // это место ещё не знает: печатаем понятную строку, а не падаем молча.
            Console.Error.WriteLine("Непредвиденная ошибка разбора аргументов: " + Describe(e));
            return 2;
        }

        // MARK: - serve

        static int RunServe(string configPath)
        {
            Config config = LoadValidatedConfig(configPath);
            if (config == null)
            {
                return 1;
            }

            TrayNotifier notifier = new TrayNotifier();

            StaExecutor sta = new StaExecutor();
            WinClipboard clipboard = new WinClipboard(sta);
            SnapshotStore snapshots = new SnapshotStore(clipboard);
            string hostName = Environment.MachineName;

            WebBlobFetcher fetcher = new WebBlobFetcher(FetchTimeoutMs);
            PeerResolver resolver = new PeerResolver(config, fetcher);
            Staging staging = new Staging(Staging.DefaultRoot(), delegate { return DateTime.Now; });
            PullClient pullClient = new PullClient(config, resolver, fetcher, staging, clipboard);

            // pullClient.Pull() дёргается из двух независимых мест: обработчик
            // POST /pull ниже и обработчик хоткея дальше по функции — ни Staging,
            // ни WinClipboard внутри Pull() сами по себе не синхронизированы: сосед
            // может дёрнуть /pull ровно в момент, когда пользователь жмёт хоткей, и
            // два одновременных Pull() создали бы две перемешанные партии стейджинга
            // и гонку записи в буфер. pullLock — общий турникет для ЛЮБОГО вызова
            // Pull(), откуда бы он ни пришёл: второй вызов не отклоняется и не падает
            // с "занято", а просто ждёт своей очереди (оба вызывающих и так терпимы к
            // небольшой задержке — сеть, пауза синтеза) — зеркало серийной
            // DispatchQueue на Mac-стороне.
            object pullLock = new object();

            HttpServer server = new HttpServer(config, snapshots, hostName, delegate
            {
                try
                {
                    PullResult result;
                    lock (pullLock)
                    {
                        result = pullClient.Pull();
                    }
                    notifier.Info("Забрано с соседа: " + result.Kind + ", файлов " + result.FileCount
                        + ", байт " + result.Bytes);
                    return result;
                }
                catch (Exception e)
                {
                    notifier.Error(DescribePullFailure(e));
                    throw;
                }
            });

            try
            {
                server.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Не удалось поднять сервер на порту " + config.Port + ": " + Describe(e));
                return 1;
            }

            // Ctrl+Shift+V: Pull() ходит в сеть и может занять секунды, поэтому
            // выполняется на фоновом потоке пула — обработчик хоткея сам приходит на
            // главный поток (тот, на котором крутится цикл сообщений Windows вместе с
            // TrayNotifier), и блокировка этого потока сетевым вызовом заморозила бы
            // и хоткей, и трей на время всего Pull(). pullLock — тот же самый
            // турникет, что и у обработчика POST /pull выше: если сеть в этот момент
            // уже тянет pull для соседа, хоткей просто встаёт следом в очередь, а не
            // гонится с ним за один и тот же Staging/буфер. Синтез вставки, наоборот,
            // идёт только после того, как Pull() уже вернулся и буфер наполнен.
            Hotkey hotkey = new Hotkey(delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        PullResult result;
                        lock (pullLock)
                        {
                            result = pullClient.Pull();
                        }

                        // Буфер уже наполнен на этой строке — отказ синтеза ниже не должен
                        // маскироваться под отказ Pull() (DescribePullFailure говорит про
                        // соседа/сеть, а не про клавиатурный ввод) и не должен проглатываться
                        // молча (см. ревью задачи 24: недомерная структура INPUT заставляла
                        // SendInput отклонять вообще каждый вызов без единого следа).
                        if (config.AutoPaste)
                        {
                            try
                            {
                                Paste.Send();
                            }
                            catch (Exception pasteError)
                            {
                                notifier.Error("Буфер обновлён, но автоматическая вставка не удалась: "
                                    + Describe(pasteError) + ". Вставьте вручную (Ctrl+V).");
                            }
                        }

                        // Успех тихий — кроме файлов, где полезно знать, что именно
                        // приехало и сколько весит, до того как открывать проводник.
                        if (result.Kind == "files")
                        {
                            notifier.Info(PluralizedFiles(result.FileCount) + ", " + MegabytesString(result.Bytes));
                        }
                    }
                    catch (Exception e)
                    {
                        notifier.Error(DescribePullFailure(e));
                    }
                });
            });

            try
            {
                hotkey.Register();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Не удалось зарегистрировать глобальный хоткей Ctrl+Shift+V: " + Describe(e));
                return 1;
            }

            Console.WriteLine("lanclip слушает порт " + server.BoundPort + " (хост " + hostName
                + "), хоткей Ctrl+Shift+V активен");

            // Application.Run() без аргументов: главный поток крутит цикл сообщений
            // Windows бесконечно (аналог RunLoop.current.run() на Mac) — именно он
            // доставляет WM_HOTKEY скрытому окну Hotkey и события NotifyIcon у
            // TrayNotifier. Сам цикл ничем не блокируется: сетевой Pull() и синтез
            // вставки уходят на ThreadPool выше, а не выполняются здесь.
            Application.Run();
            return 0;
        }

        // MARK: - status

        static int RunStatus(string configPath)
        {
            Config config = LoadValidatedConfig(configPath);
            if (config == null)
            {
                return 1;
            }

            StaExecutor sta = new StaExecutor();
            try
            {
                WinClipboard clipboard = new WinClipboard(sta);
                SnapshotStore snapshots = new SnapshotStore(clipboard);

                Console.WriteLine("Порт: " + config.Port);

                try
                {
                    ClipSnapshot snapshot = snapshots.Current();
                    Console.WriteLine("Буфер: kind=" + snapshot.Manifest.Kind + ", seq=" + snapshot.Manifest.Seq);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Буфер: не удалось прочитать (" + Describe(e) + ")");
                }

                WebBlobFetcher prober = new WebBlobFetcher(FetchTimeoutMs);
                PeerResolver resolver = new PeerResolver(config, prober);
                string peer = resolver.Resolve();
                if (peer != null)
                {
                    Console.WriteLine("Сосед: " + peer);
                }
                else
                {
                    Console.WriteLine(NoPeerMessage(resolver));
                }
            }
            finally
            {
                sta.Shutdown();
            }

            return 0;
        }

        // MARK: - get

        static int RunGet(string configPath)
        {
            Config config = LoadConfigRequiringPeers(configPath);
            if (config == null)
            {
                return 1;
            }

            WebBlobFetcher fetcher = new WebBlobFetcher(FetchTimeoutMs);
            PeerResolver resolver = new PeerResolver(config, fetcher);

            string peer = resolver.Resolve();
            if (peer == null)
            {
                Console.WriteLine(NoPeerMessage(resolver));
                return 1;
            }

            try
            {
                Manifest manifest = fetcher.Manifest(peer, config.Port, config.Token);
                Console.WriteLine(manifest.ToJson());
                return 0;
            }
            catch (Exception e)
            {
                // Carried-over note (задача 11/21): fetcher.Manifest ловит только
                // HttpClientException сам по себе не бросает ничего специфичного —
                // если сосед пришлёт битый JSON, наружу вылетит сырой FormatException
                // (Manifest.FromJson) или InvalidCastException/OverflowException
                // (ToLong внутри него). Эта ветка обязана существовать и печатать
                // внятную строку в любом случае, а не только для HttpClientException.
                Console.Error.WriteLine("Не удалось получить манифест соседа " + peer + ": " + Describe(e));
                return 1;
            }
        }

        // MARK: - pull

        static int RunPull(string configPath)
        {
            Config config = LoadConfigRequiringPeers(configPath);
            if (config == null)
            {
                return 1;
            }

            StaExecutor sta = new StaExecutor();
            try
            {
                WinClipboard clipboard = new WinClipboard(sta);
                WebBlobFetcher fetcher = new WebBlobFetcher(FetchTimeoutMs);
                PeerResolver resolver = new PeerResolver(config, fetcher);
                Staging staging = new Staging(Staging.DefaultRoot(), delegate { return DateTime.Now; });
                PullClient client = new PullClient(config, resolver, fetcher, staging, clipboard);

                try
                {
                    PullResult result = client.Pull();
                    Console.WriteLine("Готово: kind=" + result.Kind + ", файлов=" + result.FileCount
                        + ", байт=" + result.Bytes);
                    return 0;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(DescribePullFailure(e));
                    return 1;
                }
            }
            finally
            {
                sta.Shutdown();
            }
        }

        // MARK: - Конфиг и его ошибки

        static Config LoadValidatedConfig(string configPath)
        {
            try
            {
                Config config = Config.Load(configPath);
                config.Validate();
                return config;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(ConfigErrorMessage(e, configPath));
                return null;
            }
        }

        // Для get/pull — команд, которым без соседа делать нечего. Общие проверки
        // те же, что и у всех (LoadValidatedConfig), плюс отдельно Peers: serve и
        // status этой проверки не делают, им пустой Peers не мешает работать (см.
        // Config.ValidatePeers()).
        static Config LoadConfigRequiringPeers(string configPath)
        {
            Config config = LoadValidatedConfig(configPath);
            if (config == null)
            {
                return null;
            }

            try
            {
                config.ValidatePeers();
                return config;
            }
            catch (Exception)
            {
                // Config.ValidatePeers() бросает ровно по одной причине — пустой Peers —
                // поэтому сообщение здесь не идёт через общий ConfigErrorMessage (который
                // вынужден быть универсальным чеклистом из-за отсутствия типизированных
                // кодов в Config.cs): для этого конкретного, самого частого при первом
                // запуске случая можно и нужно дать точное однострочное сообщение с
                // примером значения — зеркало Mac-стороннего ConfigError.noPeers.
                Console.Error.WriteLine("Конфиг " + configPath + ": список соседей пуст. "
                    + "Впишите адрес соседней машины в поле \"peers\", например [\"192.168.1.23\"].");
                return null;
            }
        }

        // Config.cs не различает причины типизированными кодами (в отличие от
        // PullException/HttpServerException) — вместо того чтобы гадать по тексту
        // сообщения, какое поле поправить, печатаем один универсальный, но полный
        // список того, что стоит проверить, вместе с путём к файлу — этого
        // достаточно, чтобы починить конфиг руками, не заглядывая в исходники.
        static string ConfigErrorMessage(Exception e, string configPath)
        {
            if (!(e is ConfigException))
            {
                return "Не удалось прочитать конфиг " + configPath + ": " + Describe(e);
            }

            return "Конфиг " + configPath + ": " + e.Message + ". Проверьте файл: порт должен быть в диапазоне"
                + " 1-65535 (поле \"port\"), token и maxBytes — непустым и положительным соответственно,"
                + " peers (для get/pull) — содержать хотя бы одного соседа; либо удалите файл целиком — он"
                + " будет пересоздан заново со свежим токеном.";
        }

        // MARK: - PullException -> человекочитаемая строка

        static string DescribePullFailure(Exception e)
        {
            PullException pullError = e as PullException;
            if (pullError == null)
            {
                // Carried-over note (задача 11/21): PullClient.Pull() ловит только
                // HttpClientException на пути FetchManifest — совсем битый JSON от
                // соседа долетит сюда сырым FormatException/InvalidCastException, а
                // не PullException. Ветка "всё остальное" обязана существовать и не
                // превращаться в голый дамп исключения.
                return "Непредвиденная ошибка при Pull(): " + Describe(e);
            }

            switch (pullError.Code)
            {
                case PullException.CodeNoPeer:
                    return NoPeerMessage(pullError.TokenRejected);
                case PullException.CodePeerEmpty:
                    return "Буфер соседа пуст — нечего забирать.";
                case PullException.CodeTooLarge:
                    return "Содержимое соседа весит " + pullError.TotalSize + " байт — больше лимита maxBytes="
                        + pullError.MaxBytes + ".";
                case PullException.CodeChanged:
                    return "Буфер соседа изменился прямо во время передачи. Попробуйте ещё раз.";
                case PullException.CodeTransport:
                    // Message уже полностью самодостаточен ("ошибка транспорта: ...") —
                    // отдельный человекочитаемый префикс здесь был бы дублированием.
                    return pullError.Message;
                default:
                    return "Непредвиденная ошибка Pull(): " + Describe(pullError);
            }
        }

        // MARK: - "Сосед не найден" -> человекочитаемая строка (находка I10)

        // Единая формулировка для всех трёх мест, где CLI сообщает об отсутствии
        // соседа (status/get/DescribePullFailure) — раньше "сосед не найден"
        // печаталось одинаково и при полностью выключенном соседе, и при неверном
        // токене (сосед ответил 401), из-за чего опечатка в конфиге выглядела как
        // сетевая/файрвольная проблема. Зеркало mac-стороннего noPeerMessage(_:).
        static string NoPeerMessage(bool tokenRejected)
        {
            if (tokenRejected)
            {
                return "Сосед ответил, но отверг токен (401). Проверьте, что поле \"token\" в конфиге "
                    + "совпадает на обеих машинах.";
            }
            return "Сосед не найден: никто из адресов в \"peers\" не отвечает.";
        }

        static string NoPeerMessage(PeerResolver resolver)
        {
            return NoPeerMessage(resolver.LastResolveSawTokenRejection);
        }

        // MARK: - Тост про файлы после хоткея

        // Русское склонение «файл/файла/файлов» по числу — используется только в
        // тосте после хоткея, больше нигде в проекте текст не согласуется с числом.
        static string PluralizedFiles(int count)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;

            string word;
            if (mod100 >= 11 && mod100 <= 14)
            {
                word = "файлов";
            }
            else if (mod10 == 1)
            {
                word = "файл";
            }
            else if (mod10 >= 2 && mod10 <= 4)
            {
                word = "файла";
            }
            else
            {
                word = "файлов";
            }

            return count + " " + word;
        }

        // Округлённый размер в мегабайтах (десятичных, x1_000_000 — как подписи
        // размера файлов в Проводнике). Ненулевой размер, округлившийся к 0 МБ,
        // показывается как 1 МБ — иначе тост про реально скачанные файлы выглядел
        // бы как "0 МБ", что похоже на баг.
        static string MegabytesString(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 МБ";
            }
            long megabytes = (long)Math.Max(1, Math.Round(bytes / 1000000.0));
            return megabytes + " МБ";
        }

        // MARK: - Мелкие утилиты вывода

        static string Describe(Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }

        static void PrintUsage()
        {
            string usage =
                "Использование: lanclipd [команда] [--config <путь>]\n" +
                "\n" +
                "Команды:\n" +
                "  serve   поднять HTTP-сервер и обслуживать буфер обмена (по умолчанию)\n" +
                "  status  показать свой порт, seq/kind буфера и адрес живого соседа\n" +
                "  get     показать манифест соседа в JSON, ничего не меняя локально\n" +
                "  pull    забрать содержимое буфера соседа и вставить в свой буфер\n" +
                "\n" +
                "Флаги:\n" +
                "  --config <путь>  путь к файлу конфига (по умолчанию " + Config.DefaultPath() + ")\n" +
                "  -h, --help        показать эту справку";
            Console.WriteLine(usage);
        }
    }
}
