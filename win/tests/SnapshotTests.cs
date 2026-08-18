using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/SnapshotTests.swift (задача 5). Бриф просит
    // ровно семь случаев — тест на симлинк-снаружи-папки туда сознательно не входит
    // (создание симлинков на Windows требует Developer Mode/повышенных прав и сделало
    // бы прогон нестабильным); сама защита от расхождения "размер vs байты" всё равно
    // заложена в Snapshot.cs — см. CanonicalizeForBothReads в SnapshotStore.
    static class SnapshotTests
    {
        public static void Register()
        {
            T.Run("empty clipboard yields empty manifest", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                SnapshotStore store = new SnapshotStore(clipboard);
                T.Eq("empty", store.Current().Manifest.Kind, "kind");
            });

            T.Run("text snapshot carries seq from change count", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("привет");
                SnapshotStore store = new SnapshotStore(clipboard);
                ClipSnapshot snapshot = store.Current();
                T.Eq("text", snapshot.Manifest.Kind, "kind");
                T.Eq("привет", snapshot.Manifest.Text, "text");
                T.Eq(clipboard.ChangeCount(), snapshot.Manifest.Seq, "seq");
            });

            T.Run("image blob returns png bytes", delegate
            {
                byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfImage(png);
                SnapshotStore store = new SnapshotStore(clipboard);
                ClipSnapshot snapshot = store.Current();
                T.Eq((long?)png.Length, snapshot.Manifest.TotalSize, "totalSize");
                BlobPayload payload = store.Blob(0, snapshot.Manifest.Seq);
                T.True(payload.FilePath == null, "image blob stays in-memory (.Data), not streamed from disk");
                T.True(BytesEqual(png, payload.Data), "blob bytes");
            });

            T.Run("single file uses bare name", delegate
            {
                string dir = MakeTempDir();
                try
                {
                    string file = MakeFile(dir, "отчёт.pdf", "данные");
                    List<string> files = new List<string>();
                    files.Add(file);

                    FakeClipboard clipboard = new FakeClipboard();
                    clipboard.Content = ClipContent.OfFiles(files);
                    SnapshotStore store = new SnapshotStore(clipboard);
                    ClipSnapshot snapshot = store.Current();

                    T.Eq(1, snapshot.Manifest.Blobs.Count, "blob count");
                    T.Eq("отчёт.pdf", snapshot.Manifest.Blobs[0].Rel, "rel");
                    // Находка I6 финального ревью: файловые блобы обязаны отдаваться как
                    // .FilePath (читаемые потоком с диска вызывающей стороной), а не как
                    // готовый byte[] в памяти — многогигабайтный файл в буфере не должен
                    // попадать в память процесса целиком просто потому, что кто-то
                    // запросил его блоб.
                    BlobPayload payload = store.Blob(0, snapshot.Manifest.Seq);
                    T.True(payload.Data == null, "file blob must be streamed (.FilePath), not loaded into memory");
                    T.Eq(file, payload.FilePath, "file path");
                    byte[] bytes = File.ReadAllBytes(payload.FilePath);
                    T.Eq("данные", Encoding.UTF8.GetString(bytes), "content");
                    T.Eq((long)bytes.Length, payload.FileSize, "reported size matches bytes on disk");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            T.Run("folder is walked recursively with relative paths", delegate
            {
                string dir = MakeTempDir();
                try
                {
                    MakeFile(dir, Path.Combine("папка", "a.txt"), "a");
                    MakeFile(dir, Path.Combine("папка", Path.Combine("вложенная", "b.txt")), "bb");

                    List<string> files = new List<string>();
                    files.Add(Path.Combine(dir, "папка"));

                    FakeClipboard clipboard = new FakeClipboard();
                    clipboard.Content = ClipContent.OfFiles(files);
                    SnapshotStore store = new SnapshotStore(clipboard);
                    ClipSnapshot snapshot = store.Current();

                    List<string> rels = new List<string>();
                    foreach (BlobRef b in snapshot.Manifest.Blobs)
                    {
                        rels.Add(b.Rel);
                    }
                    rels.Sort(StringComparer.Ordinal);

                    T.Eq(2, rels.Count, "blob count");
                    T.Eq("папка/a.txt", rels[0], "rel 0");
                    T.Eq("папка/вложенная/b.txt", rels[1], "rel 1");
                    T.Eq((long?)3L, snapshot.Manifest.TotalSize, "totalSize");
                }
                finally
                {
                    Directory.Delete(dir, true);
                }
            });

            T.Run("stale seq is rejected", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfText("первый");
                SnapshotStore store = new SnapshotStore(clipboard);
                int stale = store.Current().Manifest.Seq;

                clipboard.Content = ClipContent.OfImage(new byte[] { 1, 2, 3 });
                store.Current();

                T.Throws<StaleSeqException>(delegate
                {
                    store.Blob(0, stale);
                }, "stale seq rejected");
            });

            T.Run("out of range index returns null", delegate
            {
                FakeClipboard clipboard = new FakeClipboard();
                clipboard.Content = ClipContent.OfImage(new byte[] { 1 });
                SnapshotStore store = new SnapshotStore(clipboard);
                int seq = store.Current().Manifest.Seq;
                T.Eq(null, store.Blob(5, seq), "out of range index");
            });
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

        static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lanclip-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string MakeFile(string root, string relative, string body)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, body, new UTF8Encoding(false));
            return path;
        }
    }
}
