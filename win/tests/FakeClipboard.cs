using System.Collections.Generic;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/FakeClipboard.swift: счётчик изменений
    // растёт при каждой записи в Content, журнал Written хранит всё, что когда-либо
    // было записано через Write.
    class FakeClipboard : IClipboardReader, IClipboardWriter
    {
        int changes = 1;
        ClipContent content = ClipContent.Empty();

        public readonly List<ClipContent> Written = new List<ClipContent>();

        public ClipContent Content
        {
            get { return content; }
            set
            {
                content = value;
                changes++;
            }
        }

        public int ChangeCount()
        {
            return changes;
        }

        public ClipContent Read()
        {
            return content;
        }

        public void Write(ClipContent content)
        {
            Written.Add(content);
            Content = content;
        }
    }
}
