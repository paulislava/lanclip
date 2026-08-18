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
    // Картинка всегда отдаётся как PNG: Clipboard.GetImage() может вернуть Bitmap из
    // любого исходного формата (DIB, EMF...), перекодируем через MemoryStream +
    // ImageFormat.Png, а не отдаём как есть.
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
                        Clipboard.SetText(content.Text ?? string.Empty, TextDataFormat.UnicodeText);
                        return;

                    case ClipKindValue.Image:
                        using (MemoryStream stream = new MemoryStream(content.Png))
                        using (Image image = Image.FromStream(stream))
                        {
                            Clipboard.SetImage(image);
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
