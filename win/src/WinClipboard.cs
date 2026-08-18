using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanClip
{
    // Зеркало mac/Sources/LanClipCore/MacPasteboard.swift поверх System.Windows.Forms.Clipboard.
    //
    // Порядок определения типа при чтении — тот же контракт протокола, что и на Mac
    // (proto/PROTOCOL.md): файлы → картинка → текст, первое совпадение побеждает.
    // Картинка всегда отдаётся как PNG.
    //
    // Альфа-канал: Clipboard.SetImage()/GetImage() работают через DIB (DataFormats.Bitmap),
    // который альфа-канал не хранит — прозрачный PNG, записанный только через SetImage,
    // вставился бы непрозрачным. Поэтому при записи картинка кладётся в буфер ДВАЖДЫ:
    // сырые байты PNG под де-факто стандартным именем формата "PNG" (так делают
    // Chrome/GIMP/Photoshop — современные приложения читают именно его без потери
    // прозрачности) и DataFormats.Bitmap через DataObject.SetImage(...) для старых
    // приложений, которые "PNG" не знают (ценой альфа-канала — это единственное,
    // чем можно им угодить). При чтении формат "PNG" проверяется первым: если он
    // есть, байты возвращаются как есть, без перекодирования через GDI+ — именно это
    // и держит альфа-канал точным на собственном цикле запись→чтение. Только если
    // "PNG" на буфере нет (картинку положило постороннее приложение, не знающее об
    // этом соглашении), берём Clipboard.GetImage() и перекодируем в PNG через
    // MemoryStream + ImageFormat.Png — с потерей альфы, но это уже входные данные не
    // от нас.
    //
    // Ссылки на веб-страницы, скопированные из браузера, никогда не попадают в
    // Clipboard как CF_HDROP (файловый drop) — браузеры кладут только текстовые и
    // HTML-представления. ContainsFileDropList()/GetFileDropList() поэтому для них
    // естественно пуст, и чтение проваливается на следующий уровень (картинка, затем
    // текст) без какой-либо специальной фильтрации, в отличие от Mac-стороны, где
    // urlReadingFileURLsOnly отсекает http(s)-ссылки явно.
    //
    // Каждая операция идёт строго через StaExecutor: System.Windows.Forms.Clipboard
    // требует STA-апартамент вызывающего потока, а коллбэки HttpListener приходят в
    // MTA-потоках пула — без маршалинга на выделенный STA-поток доступ падал бы
    // через раз.
    class WinClipboard : IClipboardReader, IClipboardWriter
    {
        // Не зарегистрированное системой имя, а общепринятое соглашение между
        // приложениями (браузеры, графические редакторы) для сырых байт PNG на
        // буфере — аналога DataFormats.Png в BCL нет.
        const string RawPngFormat = "PNG";

        [DllImport("user32.dll")]
        static extern int GetClipboardSequenceNumber();

        readonly StaExecutor sta;

        public WinClipboard(StaExecutor sta)
        {
            this.sta = sta;
        }

        public int ChangeCount()
        {
            return sta.Invoke(new Func<int>(delegate
            {
                return GetClipboardSequenceNumber();
            }));
        }

        public ClipContent Read()
        {
            return sta.Invoke(new Func<ClipContent>(delegate
            {
                if (Clipboard.ContainsFileDropList())
                {
                    StringCollection dropped = Clipboard.GetFileDropList();
                    if (dropped != null && dropped.Count > 0)
                    {
                        List<string> paths = new List<string>();
                        foreach (string path in dropped)
                        {
                            paths.Add(path);
                        }
                        return ClipContent.OfFiles(paths);
                    }
                }

                byte[] rawPng = ReadRawPng();
                if (rawPng != null)
                {
                    return ClipContent.OfImage(rawPng);
                }

                if (Clipboard.ContainsImage())
                {
                    using (Image image = Clipboard.GetImage())
                    {
                        if (image != null)
                        {
                            using (MemoryStream stream = new MemoryStream())
                            {
                                image.Save(stream, ImageFormat.Png);
                                return ClipContent.OfImage(stream.ToArray());
                            }
                        }
                    }
                }

                if (Clipboard.ContainsText())
                {
                    return ClipContent.OfText(Clipboard.GetText());
                }

                return ClipContent.Empty();
            }));
        }

        // Вызывается только изнутри тела, уже выполняющегося на STA-потоке (см. Read()) —
        // сам по себе через StaExecutor не маршалит.
        static byte[] ReadRawPng()
        {
            if (!Clipboard.ContainsData(RawPngFormat))
            {
                return null;
            }

            object raw = Clipboard.GetData(RawPngFormat);

            MemoryStream stream = raw as MemoryStream;
            if (stream != null)
            {
                return stream.ToArray();
            }

            return raw as byte[];
        }

        public void Write(ClipContent content)
        {
            sta.Invoke(new Action(delegate
            {
                switch (content.Kind)
                {
                    case ClipKindValue.Empty:
                        Clipboard.Clear();
                        return;

                    case ClipKindValue.Text:
                        // Находка I4 финального ревью: Clipboard.SetText() бросает
                        // ArgumentException на пустой строке (документированное
                        // поведение WinForms) — воспроизводится тривиально:
                        // `printf '' | pbcopy` на Mac даёт манифест
                        // {"kind":"text","text":""}, и Ctrl+Shift+V на ПК получал
                        // необёрнутое исключение вместо записи пустого буфера.
                        // Пустой (или отсутствующий на уровне ClipContent — сюда не
                        // должно долетать, но на всякий случай тоже трактуется как
                        // пусто) текст — валидное содержимое буфера, а не ошибка.
                        if (string.IsNullOrEmpty(content.Text))
                        {
                            Clipboard.Clear();
                        }
                        else
                        {
                            Clipboard.SetText(content.Text, TextDataFormat.UnicodeText);
                        }
                        return;

                    case ClipKindValue.Image:
                        using (MemoryStream stream = new MemoryStream(content.Png))
                        using (Image image = Image.FromStream(stream))
                        {
                            DataObject data = new DataObject();
                            // Сырые байты — отдельный MemoryStream, а не "stream" выше:
                            // тот отдан под декодирование Image и не должен делить
                            // владение буфером с данными, уходящими в буфер обмена.
                            data.SetData(RawPngFormat, false, new MemoryStream(content.Png));
                            data.SetImage(image);
                            Clipboard.SetDataObject(data, true);
                        }
                        return;

                    case ClipKindValue.Files:
                        StringCollection collection = new StringCollection();
                        foreach (string path in content.Files)
                        {
                            collection.Add(path);
                        }
                        Clipboard.SetFileDropList(collection);
                        return;

                    default:
                        throw new InvalidOperationException("неизвестный ClipKindValue: " + content.Kind);
                }
            }));
        }
    }
}
