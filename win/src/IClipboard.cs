using System.Collections.Generic;

namespace LanClip
{
    // Зеркало mac/Sources/LanClipCore/Clipboard.swift: ClipContent — закрытая сумма
    // четырёх случаев буфера (empty/text/image/files), IClipboardReader/IClipboardWriter —
    // граница между ядром и системным буфером. Реальную реализацию поверх
    // System.Windows.Forms.Clipboard даёт задача 22; здесь — только абстракция и
    // подставная реализация для тестов (FakeClipboard).
    enum ClipKindValue
    {
        Empty,
        Text,
        Image,
        Files
    }

    class ClipContent
    {
        public ClipKindValue Kind;
        public string Text;
        public byte[] Png;
        public List<string> Files;

        public static ClipContent Empty()
        {
            ClipContent content = new ClipContent();
            content.Kind = ClipKindValue.Empty;
            return content;
        }

        public static ClipContent OfText(string s)
        {
            ClipContent content = new ClipContent();
            content.Kind = ClipKindValue.Text;
            content.Text = s;
            return content;
        }

        public static ClipContent OfImage(byte[] png)
        {
            ClipContent content = new ClipContent();
            content.Kind = ClipKindValue.Image;
            content.Png = png;
            return content;
        }

        public static ClipContent OfFiles(List<string> paths)
        {
            ClipContent content = new ClipContent();
            content.Kind = ClipKindValue.Files;
            content.Files = paths;
            return content;
        }
    }

    interface IClipboardReader
    {
        int ChangeCount();
        ClipContent Read();
    }

    interface IClipboardWriter
    {
        void Write(ClipContent content);
    }
}
