using System;
using System.Collections.Generic;
using System.IO;

namespace LanClip
{
    // Зеркало mac/Sources/LanClipCore/Snapshot.swift (задача 5). Два дефекта,
    // найденных ревью на Swift-стороне, сюда не переносятся:
    //
    //  1. Относительные пути строятся из позиции обхода (relPrefix + "/" + имя),
    //     а не вычитанием префикса из полного пути — так temp-каталог, зарезолвленный
    //     через junction/подставленный диск, не даёт мусора в rel.
    //  2. Размер и отдаваемые байты берутся из одной и той же строки пути: Expand()
    //     вычисляет её один раз (CanonicalizeForBothReads — чисто лексическая
    //     нормализация, не разыменование symlink/junction) и кладёт эту же строку и
    //     в BlobRef.Size (через FileInfo), и в Sources (для последующего чтения в
    //     Blob()). Расхождение между "что посчитали" и "что прочитали" структурно
    //     невозможно — это буквально один и тот же путь, а не два разных API с
    //     разным поведением на symlink, как было в исходном Swift-баге.

    class ClipSnapshot
    {
        public Manifest Manifest;
        public byte[] ImagePng;
        public Dictionary<int, string> Sources;
    }

    class StaleSeqException : Exception
    {
        public StaleSeqException()
            : base("устаревший seq: буфер уже изменился, нужен свежий снимок")
        {
        }
    }

    class SnapshotStore
    {
        readonly IClipboardReader reader;
        ClipSnapshot cached;

        public SnapshotStore(IClipboardReader reader)
        {
            this.reader = reader;
        }

        public ClipSnapshot Current()
        {
            int seq = reader.ChangeCount();
            if (cached != null && cached.Manifest.Seq == seq)
            {
                return cached;
            }

            ClipSnapshot snapshot = Build(seq, reader.Read());
            cached = snapshot;
            return snapshot;
        }

        // null — индекс вне диапазона (нет такого blob-а в текущем снимке).
        public byte[] Blob(int index, int seq)
        {
            ClipSnapshot snapshot = Current();
            if (snapshot.Manifest.Seq != seq)
            {
                throw new StaleSeqException();
            }

            if (snapshot.ImagePng != null)
            {
                return index == 0 ? snapshot.ImagePng : null;
            }

            string source;
            if (!snapshot.Sources.TryGetValue(index, out source))
            {
                return null;
            }
            return File.ReadAllBytes(source);
        }

        static ClipSnapshot Build(int seq, ClipContent content)
        {
            if (content.Kind == ClipKindValue.Empty)
            {
                return EmptySnapshot(seq);
            }

            if (content.Kind == ClipKindValue.Text)
            {
                ClipSnapshot snapshot = new ClipSnapshot();
                snapshot.Manifest = Manifest.OfText(content.Text, seq);
                snapshot.Sources = new Dictionary<int, string>();
                return snapshot;
            }

            if (content.Kind == ClipKindValue.Image)
            {
                ClipSnapshot snapshot = new ClipSnapshot();
                snapshot.Manifest = Manifest.OfImage(content.Png.Length, seq);
                snapshot.ImagePng = content.Png;
                snapshot.Sources = new Dictionary<int, string>();
                return snapshot;
            }

            return BuildFiles(seq, content.Files);
        }

        static ClipSnapshot EmptySnapshot(int seq)
        {
            ClipSnapshot snapshot = new ClipSnapshot();
            snapshot.Manifest = Manifest.Empty(seq);
            snapshot.Sources = new Dictionary<int, string>();
            return snapshot;
        }

        static ClipSnapshot BuildFiles(int seq, List<string> paths)
        {
            List<BlobRef> blobs = new List<BlobRef>();
            Dictionary<int, string> sources = new Dictionary<int, string>();

            foreach (string path in paths)
            {
                foreach (Entry entry in Expand(path))
                {
                    string rel = RelPath.Normalize(entry.Rel);
                    if (rel == null)
                    {
                        continue;
                    }

                    BlobRef blob = new BlobRef();
                    blob.I = blobs.Count;
                    blob.Rel = rel;
                    blob.Size = entry.Size;

                    sources[blobs.Count] = entry.AbsolutePath;
                    blobs.Add(blob);
                }
            }

            if (blobs.Count == 0)
            {
                return EmptySnapshot(seq);
            }

            ClipSnapshot snapshot = new ClipSnapshot();
            snapshot.Manifest = Manifest.OfFiles(blobs, seq);
            snapshot.Sources = sources;
            return snapshot;
        }

        struct Entry
        {
            public string AbsolutePath;
            public string Rel;
            public long Size;
        }

        static List<Entry> Expand(string path)
        {
            List<Entry> entries = new List<Entry>();

            if (File.Exists(path))
            {
                string absolute = CanonicalizeForBothReads(path);
                Entry entry = new Entry();
                entry.AbsolutePath = absolute;
                entry.Rel = Path.GetFileName(path);
                entry.Size = new FileInfo(absolute).Length;
                entries.Add(entry);
                return entries;
            }

            if (!Directory.Exists(path))
            {
                return entries;
            }

            string baseName = new DirectoryInfo(path).Name;
            Walk(path, baseName, entries);
            return entries;
        }

        // Обходит дерево рекурсивно, строя rel из позиции в обходе (relPrefix +
        // "/" + имя), а не арифметикой над строками полного пути.
        static void Walk(string dir, string relPrefix, List<Entry> entries)
        {
            foreach (string filePath in Directory.GetFiles(dir))
            {
                string absolute = CanonicalizeForBothReads(filePath);
                Entry entry = new Entry();
                entry.AbsolutePath = absolute;
                entry.Rel = relPrefix + "/" + Path.GetFileName(filePath);
                entry.Size = new FileInfo(absolute).Length;
                entries.Add(entry);
            }

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                string name = new DirectoryInfo(subDir).Name;
                Walk(subDir, relPrefix + "/" + name, entries);
            }
        }

        // ВАЖНО: это чисто лексическая нормализация (Path.GetFullPath убирает "..",
        // ".", относительность), а не разыменование symlink/junction — аналога
        // Swift-стороннего resolvingSymlinksInPath() в .NET Framework 4.0 без P/Invoke
        // нет. Согласованность размера и байтов держится не на этом: она держится на
        // том, что ОДНА И ТА ЖЕ возвращённая строка кладётся и в Entry.AbsolutePath
        // (источник для File.ReadAllBytes в Blob()), и передаётся в FileInfo(...).Length
        // для BlobRef.Size — расхождение между объявленным размером и прочитанными
        // байтами структурно исключено именно поэтому, а не потому что путь "разрешён".
        static string CanonicalizeForBothReads(string path)
        {
            return Path.GetFullPath(path);
        }
    }
}
