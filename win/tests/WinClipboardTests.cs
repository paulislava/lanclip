using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/MacPasteboardTests.swift — но, в отличие от
    // Mac-стороны, здесь нет способа завести отдельный именованный системный буфер
    // (NSPasteboard(name:) не имеет аналога у System.Windows.Forms.Clipboard): буфер
    // один на весь рабочий стол пользователя. Поэтому весь набор тестов оборачивает
    // сама себя в сохранение исходного содержимого буфера при входе и восстановление
    // при выходе — в try/finally, так что восстановление происходит даже если один из
    // тестов внутри упал (T.Run сам не даёт исключению вылететь наружу, но мы всё
    // равно не полагаемся на это и держим finally на верхнем уровне).
    //
    // Снимок и восстановление идут через сырой Clipboard.GetDataObject()/SetDataObject(),
    // а не через WinClipboard.Read()/Write(). ClipContent знает только четыре случая
    // (пусто/текст/PNG/файлы) — если исходное содержимое буфера было богаче этого
    // (несколько форматов сразу: RTF+текст, HTML+текст, картинка с форматами помимо
    // Bitmap, формат конкретного приложения), Read() схлопнул бы его до одного
    // совпавшего вида, а последующий Write(original) переписал бы буфер этой
    // урезанной версией — то есть каждый прогон тестов тихо терял бы часть
    // содержимого буфера Павла. Сырой IDataObject копирует все присутствующие
    // форматы как есть, без прохода через ClipContent.
    //
    // Если исходное содержимое всё же не удалось ни снять, ни потом восстановить —
    // буфер просто очищается, а не оставляется с тестовым мусором. Итог
    // восстановления печатается в консоль явно, чтобы это было видно в выводе
    // прогона, а не тонуло молча.
    static class WinClipboardTests
    {
        public static void Register()
        {
            StaExecutor sta = new StaExecutor();
            WinClipboard sut = new WinClipboard(sta);

            DataObject backup = null;
            bool captured = false;
            try
            {
                sta.Invoke(new Action(delegate { backup = CaptureRawClipboard(); }));
                captured = true;
            }
            catch (Exception e)
            {
                Console.WriteLine("WinClipboardTests: не удалось снять снимок исходного буфера перед тестами: "
                    + e.GetType().Name + ": " + e.Message);
            }

            try
            {
                RegisterCases(sta, sut);
            }
            finally
            {
                RestoreOriginal(sta, captured, backup);
                sta.Shutdown();
            }
        }

        // Вызывается уже на STA-потоке (изнутри sta.Invoke в Register()).
        //
        // GetFormats(false) — только форматы, реально присутствующие на буфере, без
        // автоматически достраиваемых конвертируемых вариантов (иначе восстановление
        // положило бы на буфер форматы, которых там не было). GetData(format, false) —
        // тот же принцип для самих байт: без автоконвертации, то есть без
        // перекодирования на пути "снять/положить обратно".
        //
        // Важно запрашивать содержимое каждого формата немедленно, а не откладывать:
        // IDataObject, который вернул Clipboard.GetDataObject(), может отдавать данные
        // отложенным рендерингом от исходного приложения. Если сам буфер сменится
        // (а тесты его меняют) до того, как мы прочли байты, повторный запрос к тому
        // же IDataObject может вернуть уже пусто. Поэтому копируем данные каждого
        // формата в новый DataObject сразу здесь, а не храним ссылку на исходный
        // IDataObject для использования позже.
        static DataObject CaptureRawClipboard()
        {
            IDataObject current = Clipboard.GetDataObject();
            if (current == null)
            {
                return null;
            }

            string[] formats = current.GetFormats(false);
            DataObject snapshot = new DataObject();
            bool any = false;

            foreach (string format in formats)
            {
                try
                {
                    object data = current.GetData(format, false);
                    if (data != null)
                    {
                        snapshot.SetData(format, false, data);
                        any = true;
                    }
                }
                catch (Exception)
                {
                    // Формат объявлен, но не читается (например, приложение-источник
                    // уже закрылось и не дорендерило отложенный формат) — пропускаем
                    // именно этот формат, а не весь снимок.
                }
            }

            return any ? snapshot : null;
        }

        static void RestoreOriginal(StaExecutor sta, bool captured, DataObject backup)
        {
            if (captured)
            {
                try
                {
                    sta.Invoke(new Action(delegate
                    {
                        if (backup == null)
                        {
                            Clipboard.Clear();
                        }
                        else
                        {
                            // persist:true — рендерит все формы немедленно в системный
                            // буфер обмена, а не откладывает до выхода процесса: процесс
                            // тестов (особенно запущенный разовой задачей планировщика)
                            // вот-вот завершится сам.
                            Clipboard.SetDataObject(backup, true);
                        }
                    }));
                    Console.WriteLine("WinClipboardTests: исходное содержимое буфера восстановлено (" +
                        (backup == null ? "буфер был пуст" : string.Join(", ", backup.GetFormats(false))) + ")");
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine("WinClipboardTests: восстановить исходное содержимое буфера не удалось ("
                        + e.GetType().Name + ": " + e.Message + "), буфер будет очищен");
                }
            }
            else
            {
                Console.WriteLine("WinClipboardTests: исходное содержимое буфера не было снято перед тестами " +
                    "(ошибка при снятии снимка?), буфер будет очищен");
            }

            try
            {
                sta.Invoke(new Action(delegate { Clipboard.Clear(); }));
                Console.WriteLine("WinClipboardTests: буфер очищен");
            }
            catch (Exception e)
            {
                Console.WriteLine("WinClipboardTests: даже очистка буфера не удалась: "
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        static void RegisterCases(StaExecutor sta, WinClipboard sut)
        {
            // MARK: - Текст

            T.Run("write and read cyrillic text roundtrips", delegate
            {
                sut.Write(ClipContent.OfText("привет, мир"));
                ClipContent read = sut.Read();
                T.Eq(ClipKindValue.Text, read.Kind, "kind");
                T.Eq("привет, мир", read.Text, "text");
            });

            // MARK: - Пустой буфер

            T.Run("empty clipboard yields empty", delegate
            {
                sut.Write(ClipContent.Empty());
                T.Eq(ClipKindValue.Empty, sut.Read().Kind, "kind");
            });

            T.Run("writing empty clears previous content", delegate
            {
                sut.Write(ClipContent.OfText("было"));
                sut.Write(ClipContent.Empty());
                T.Eq(ClipKindValue.Empty, sut.Read().Kind, "kind");
            });

            // Находка I4 финального ревью: Clipboard.SetText() бросает ArgumentException
            // на пустой строке — воспроизводится тривиально через printf '' | pbcopy на
            // Mac (манифест {"kind":"text","text":""}), и Ctrl+Shift+V на ПК падал
            // необёрнутым исключением вместо записи пустого буфера.
            T.Run("writing text-kind content with an empty string does not throw and clears clipboard", delegate
            {
                sut.Write(ClipContent.OfText("будет стёрто"));
                sut.Write(ClipContent.OfText(""));
                ClipContent read = sut.Read();
                T.Eq(ClipKindValue.Empty, read.Kind, "empty text clears the clipboard instead of throwing");
            });

            // MARK: - changeCount

            T.Run("changeCount increases after write", delegate
            {
                int before = sut.ChangeCount();
                sut.Write(ClipContent.OfText("change-" + Guid.NewGuid().ToString("N")));
                int after = sut.ChangeCount();
                T.True(after > before, "change count increased (" + before + " -> " + after + ")");
            });

            // MARK: - Картинка (PNG)

            T.Run("write and read png image roundtrips byte for byte", delegate
            {
                byte[] png = MakeOpaqueTestPng();
                sut.Write(ClipContent.OfImage(png));

                ClipContent read = sut.Read();
                T.Eq(ClipKindValue.Image, read.Kind, "kind");
                T.True(HasPngSignature(read.Png), "roundtripped bytes still carry the real PNG signature");
                T.True(BytesEqual(png, read.Png), "roundtripped bytes are byte-for-byte identical to the source");

                using (MemoryStream stream = new MemoryStream(read.Png))
                using (Image decoded = Image.FromStream(stream))
                {
                    T.Eq(2, decoded.Width, "width preserved");
                    T.Eq(2, decoded.Height, "height preserved");
                }
            });

            // Clipboard.SetImage()/GetImage() в одиночку теряют альфа-канал (DIB не
            // хранит прозрачность) — этот тест берёт PNG с реально прозрачными и
            // полупрозрачными пикселями и проверяет, что байты, вернувшиеся из
            // буфера, совпадают с исходными один в один, а не просто "похожая
            // картинка без альфы".
            T.Run("write and read png image preserves transparency exactly", delegate
            {
                byte[] png = MakeTransparentTestPng();
                sut.Write(ClipContent.OfImage(png));

                ClipContent read = sut.Read();
                T.Eq(ClipKindValue.Image, read.Kind, "kind");
                T.True(BytesEqual(png, read.Png),
                    "a SetImage()-only path would silently strip alpha via DIB conversion");

                using (MemoryStream stream = new MemoryStream(read.Png))
                using (Bitmap decoded = new Bitmap(stream))
                {
                    Color transparentCorner = decoded.GetPixel(0, 0);
                    T.Eq(0, (int)transparentCorner.A, "fully transparent pixel preserved");

                    Color semiCorner = decoded.GetPixel(1, 1);
                    T.True(semiCorner.A > 0 && semiCorner.A < 255, "semi-transparent pixel preserved");
                }
            });

            // MARK: - Файлы

            T.Run("write and read file list roundtrips", delegate
            {
                string dir = MakeTempDir();
                try
                {
                    string a = MakeFile(dir, "a.txt", "aaa");
                    string b = MakeFile(dir, Path.Combine("вложенная", "б.txt"), "бб");
                    List<string> files = new List<string>();
                    files.Add(a);
                    files.Add(b);

                    sut.Write(ClipContent.OfFiles(files));
                    ClipContent read = sut.Read();

                    T.Eq(ClipKindValue.Files, read.Kind, "kind");
                    HashSet<string> expected = new HashSet<string>(files);
                    HashSet<string> actual = new HashSet<string>(read.Files ?? new List<string>());
                    T.True(expected.SetEquals(actual), "same paths back (" +
                        string.Join(", ", read.Files ?? new List<string>()) + ")");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            // MARK: - Порядок определения типа: файлы побеждают текстовое представление

            T.Run("files win over text representation when both present", delegate
            {
                string dir = MakeTempDir();
                try
                {
                    string file = MakeFile(dir, "finder.txt", "содержимое");

                    sta.Invoke(new Action(delegate
                    {
                        StringCollection files = new StringCollection();
                        files.Add(file);

                        DataObject data = new DataObject();
                        data.SetFileDropList(files);
                        data.SetText(file, TextDataFormat.UnicodeText);
                        Clipboard.SetDataObject(data, true);
                    }));

                    ClipContent read = sut.Read();
                    T.Eq(ClipKindValue.Files, read.Kind, "files must win over the text representation");
                    T.Eq(1, read.Files != null ? read.Files.Count : -1, "file count");
                    if (read.Files != null && read.Files.Count == 1)
                    {
                        T.Eq(file, read.Files[0], "path");
                    }
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            // MARK: - Ссылка на веб-страницу — не файл
            //
            // Реальные браузеры (Chrome/Edge) при копировании ссылки кладут на буфер
            // только текстовое и HTML-представление — никогда CF_HDROP (файловый
            // drop). Здесь это воспроизведено через DataObject с теми же форматами,
            // без CF_HDROP, и проверяется, что Clipboard.ContainsFileDropList()/
            // GetFileDropList() на такой буфер не путает WinClipboard.Read() в Files.
            T.Run("browser-like url on clipboard is not read as files", delegate
            {
                const string url = "https://example.com/";

                sta.Invoke(new Action(delegate
                {
                    DataObject data = new DataObject();
                    data.SetText(url, TextDataFormat.UnicodeText);
                    data.SetText("<html><body><!--StartFragment--><a href=\"" + url + "\">example</a>" +
                        "<!--EndFragment--></body></html>", TextDataFormat.Html);
                    Clipboard.SetDataObject(data, true);
                }));

                T.True(!sta.Invoke(new Func<bool>(delegate { return Clipboard.ContainsFileDropList(); })),
                    "a plain url must not present a file drop list");

                ClipContent read = sut.Read();
                T.True(read.Kind != ClipKindValue.Files, "url must not be read as files");
                T.Eq(ClipKindValue.Text, read.Kind, "falls through to text");
                T.Eq(url, read.Text, "text value is the url itself");
            });
        }

        static bool HasPngSignature(byte[] data)
        {
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (data == null || data.Length < signature.Length)
            {
                return false;
            }
            for (int i = 0; i < signature.Length; i++)
            {
                if (data[i] != signature[i])
                {
                    return false;
                }
            }
            return true;
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        static byte[] MakeOpaqueTestPng()
        {
            using (Bitmap bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(255, 200, 10, 10));
                bitmap.SetPixel(1, 0, Color.FromArgb(255, 10, 200, 10));
                bitmap.SetPixel(0, 1, Color.FromArgb(255, 10, 10, 200));
                bitmap.SetPixel(1, 1, Color.FromArgb(255, 250, 250, 10));

                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        static byte[] MakeTransparentTestPng()
        {
            using (Bitmap bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(0, 255, 0, 0));     // полностью прозрачный
                bitmap.SetPixel(1, 0, Color.FromArgb(255, 0, 255, 0));   // непрозрачный
                bitmap.SetPixel(0, 1, Color.FromArgb(255, 0, 0, 255));   // непрозрачный
                bitmap.SetPixel(1, 1, Color.FromArgb(128, 250, 250, 10)); // полупрозрачный

                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lanclip-winclipboard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string MakeFile(string root, string relative, string body)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, body, new System.Text.UTF8Encoding(false));
            return path;
        }
    }
}
