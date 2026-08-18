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
    //  2. Размер и отдаваемые байты берутся с одного и того же разрешённого пути:
    //     Expand() резолвит путь один раз (ResolvePath) и кладёт этот же путь и в
    //     BlobRef.Size (через FileInfo), и в Sources (для последующего чтения в Blob()).
    //     Расхождение между "что посчитали" и "что прочитали" структурно невозможно —
    //     это один и тот же путь.

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

                    sources[blobs.Count] = entry.ResolvedPath;
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
            public string ResolvedPath;
            public string Rel;
            public long Size;
        }

        static List<Entry> Expand(string path)
        {
            List<Entry> entries = new List<Entry>();

            if (File.Exists(path))
            {
                string resolved = ResolvePath(path);
                Entry entry = new Entry();
                entry.ResolvedPath = resolved;
                entry.Rel = Path.GetFileName(path);
                entry.Size = new FileInfo(resolved).Length;
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
                string resolved = ResolvePath(filePath);
                Entry entry = new Entry();
                entry.ResolvedPath = resolved;
                entry.Rel = relPrefix + "/" + Path.GetFileName(filePath);
                entry.Size = new FileInfo(resolved).Length;
                entries.Add(entry);
            }

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                string name = new DirectoryInfo(subDir).Name;
                Walk(subDir, relPrefix + "/" + name, entries);
            }
        }

        // Разрешает путь один раз; и размер (FileInfo), и байты (File.ReadAllBytes в
        // Blob()) берутся именно из этой строки — расхождение между объявленным
        // размером и прочитанным содержимым структурно исключено.
        static string ResolvePath(string path)
        {
            return Path.GetFullPath(path);
        }
    }
}
