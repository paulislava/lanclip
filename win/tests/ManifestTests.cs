using System;
using System.Collections.Generic;

namespace LanClip.Tests
{
    static class ManifestTests
    {
        public static void Register()
        {
            T.Run("empty omits optional keys", delegate
            {
                Dictionary<string, object> obj = Json.Parse(Manifest.Empty(41).ToJson());
                T.Eq("empty", Json.Str(obj, "kind", null), "kind");
                T.Eq(41, Json.Int(obj, "seq", -1), "seq");
                T.True(!obj.ContainsKey("text"), "no text key");
                T.True(!obj.ContainsKey("blobs"), "no blobs key");
                T.True(!obj.ContainsKey("totalSize"), "no totalSize key");
            });

            T.Run("text carries text only", delegate
            {
                Dictionary<string, object> obj = Json.Parse(Manifest.OfText("привет 👋", 42).ToJson());
                T.Eq("text", Json.Str(obj, "kind", null), "kind");
                T.Eq("привет 👋", Json.Str(obj, "text", null), "text");
                T.True(!obj.ContainsKey("blobs"), "no blobs key");
                T.True(!obj.ContainsKey("totalSize"), "no totalSize key");
            });

            T.Run("image describes single png blob", delegate
            {
                Manifest manifest = Manifest.OfImage(48213, 43);
                T.Eq((long?)48213L, manifest.TotalSize, "totalSize");
                T.Eq(1, manifest.Blobs.Count, "blob count");
                BlobRef blob = manifest.Blobs[0];
                T.Eq(0, blob.I, "blob.i");
                T.Eq("clip.png", blob.Rel, "blob.rel");
                T.Eq(48213L, blob.Size, "blob.size");
                T.Eq("image/png", blob.Mime, "blob.mime");
            });

            T.Run("files sums total size", delegate
            {
                List<BlobRef> blobs = new List<BlobRef>();
                BlobRef a = new BlobRef();
                a.I = 0; a.Rel = "отчёт.pdf"; a.Size = 91234;
                BlobRef b = new BlobRef();
                b.I = 1; b.Rel = "img/a.png"; b.Size = 5120;
                blobs.Add(a);
                blobs.Add(b);

                Manifest manifest = Manifest.OfFiles(blobs, 44);
                T.Eq((long?)96354L, manifest.TotalSize, "totalSize");
                T.Eq("files", manifest.Kind, "kind");
            });

            T.Run("round trips through json", delegate
            {
                List<BlobRef> blobs = new List<BlobRef>();
                BlobRef only = new BlobRef();
                only.I = 0; only.Rel = "папка/файл.txt"; only.Size = 7;
                blobs.Add(only);

                Manifest original = Manifest.OfFiles(blobs, 9);
                Manifest decoded = Manifest.FromJson(original.ToJson());

                T.Eq(original.Kind, decoded.Kind, "roundtrip kind");
                T.Eq(original.Seq, decoded.Seq, "roundtrip seq");
                T.Eq(original.TotalSize, decoded.TotalSize, "roundtrip totalSize");
                T.Eq(original.Blobs.Count, decoded.Blobs.Count, "roundtrip blob count");
                T.Eq(original.Blobs[0].I, decoded.Blobs[0].I, "roundtrip blob.i");
                T.Eq(original.Blobs[0].Rel, decoded.Blobs[0].Rel, "roundtrip blob.rel");
                T.Eq(original.Blobs[0].Size, decoded.Blobs[0].Size, "roundtrip blob.size");
            });

            T.Run("decodes manifest from foreign agent", delegate
            {
                Manifest manifest = Manifest.FromJson("{\"kind\":\"text\",\"seq\":5,\"text\":\"hi\"}");
                T.Eq("text", manifest.Kind, "foreign kind");
                T.Eq("hi", manifest.Text, "foreign text");
                T.True(manifest.Blobs == null, "foreign blobs null");
            });

            T.Run("rejects unknown kind", delegate
            {
                T.Throws<FormatException>(delegate
                {
                    Manifest.FromJson("{\"kind\":\"video\",\"seq\":1}");
                }, "unknown kind");
            });

            T.Run("rejects blobs element that is not an object", delegate
            {
                // Сосед прислал мусор с провода вместо BlobRef-объекта — должен
                // получиться тот же FormatException, что и на прочих битых
                // манифестах, а не InvalidCastException из-за прямого каста.
                T.Throws<FormatException>(delegate
                {
                    Manifest.FromJson("{\"kind\":\"files\",\"seq\":1,\"blobs\":[\"oops\"]}");
                }, "blobs element not an object");
            });
        }
    }
}
