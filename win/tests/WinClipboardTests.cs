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
    // Если исходное содержимое буфера не удалось ни прочитать, ни потом записать
    // обратно (экзотический формат, которого нет ни в одном из четырёх случаев
    // ClipContent) — буфер просто очищается, а не оставляется с тестовым мусором.
    // Итог восстановления печатается в консоль явно, чтобы это было видно в выводе
    // прогона, а не тонуло молча.
    static class WinClipboardTests
    {
        public static void Register()
        {
            StaExecutor sta = new StaExecutor();
            WinClipboard sut = new WinClipboard(sta);

            ClipContent original = null;
            bool captured = false;
            try
            {
                original = sut.Read();
                captured = true;
            }
            catch (Exception e)
            {
                Console.WriteLine("WinClipboardTests: не удалось прочитать исходный буфер перед тестами: "
                    + e.GetType().Name + ": " + e.Message);
            }

            try
            {
                RegisterCases(sta, sut);
            }
            finally
            {
                RestoreOriginal(sta, sut, captured, original);
                sta.Shutdown();
            }
        }

        static void RestoreOriginal(StaExecutor sta, WinClipboard sut, bool captured, ClipContent original)
        {
            if (captured)
            {
                try
                {
                    sut.Write(original);
                    Console.WriteLine("WinClipboardTests: исходное содержимое буфера восстановлено (" +
                        original.Kind + ")");
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
                Console.WriteLine("WinClipboardTests: исходное содержимое буфера не было прочитано " +
                    "(экзотический формат?), буфер будет очищен");
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

            // MARK: - changeCount

            T.Run("changeCount increases after write", delegate
            {
                int before = sut.ChangeCount();
                sut.Write(ClipContent.OfText("change-" + Guid.NewGuid().ToString("N")));
                int after = sut.ChangeCount();
                T.True(after > before, "change count increased (" + before + " -> " + after + ")");
            });

            // MARK: - Картинка (PNG)

            T.Run("write and read png image roundtrips", delegate
            {
                byte[] png = MakeTestPng();
                sut.Write(ClipContent.OfImage(png));

                ClipContent read = sut.Read();
                T.Eq(ClipKindValue.Image, read.Kind, "kind");
                T.True(read.Png != null && read.Png.Length > 8, "png bytes present");
                T.True(HasPngSignature(read.Png), "roundtripped bytes still carry the real PNG signature");

                using (MemoryStream stream = new MemoryStream(read.Png))
                using (Image decoded = Image.FromStream(stream))
                {
                    T.Eq(2, decoded.Width, "width preserved");
                    T.Eq(2, decoded.Height, "height preserved");
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

        static byte[] MakeTestPng()
        {
            using (Bitmap bitmap = new Bitmap(2, 2))
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
