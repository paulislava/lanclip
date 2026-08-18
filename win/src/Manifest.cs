using System;
using System.Collections.Generic;

namespace LanClip
{
    class BlobRef
    {
        public int I;
        public string Rel;
        public long Size;
        public string Mime;
    }

    class Manifest
    {
        public string Kind;
        public int Seq;
        public string Text;
        public List<BlobRef> Blobs;
        public long TotalSize;

        public static Manifest Empty(int seq)
        {
            Manifest m = new Manifest();
            m.Kind = "empty";
            m.Seq = seq;
            return m;
        }

        public static Manifest OfText(string value, int seq)
        {
            Manifest m = new Manifest();
            m.Kind = "text";
            m.Seq = seq;
            m.Text = value;
            return m;
        }

        public static Manifest OfImage(long pngSize, int seq)
        {
            BlobRef blob = new BlobRef();
            blob.I = 0;
            blob.Rel = "clip.png";
            blob.Size = pngSize;
            blob.Mime = "image/png";

            Manifest m = new Manifest();
            m.Kind = "image";
            m.Seq = seq;
            m.Blobs = new List<BlobRef>();
            m.Blobs.Add(blob);
            m.TotalSize = pngSize;
            return m;
        }

        public static Manifest OfFiles(List<BlobRef> blobs, int seq)
        {
            Manifest m = new Manifest();
            m.Kind = "files";
            m.Seq = seq;
            m.Blobs = blobs;
            m.TotalSize = SumSizes(blobs);
            return m;
        }

        static long SumSizes(List<BlobRef> blobs)
        {
            long total = 0;
            foreach (BlobRef b in blobs)
            {
                total += b.Size;
            }
            return total;
        }

        public string ToJson()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["kind"] = Kind;
            obj["seq"] = Seq;

            if (Kind == "text")
            {
                obj["text"] = Text;
            }

            if (Kind == "image" || Kind == "files")
            {
                List<object> blobsJson = new List<object>();
                foreach (BlobRef b in Blobs)
                {
                    Dictionary<string, object> bj = new Dictionary<string, object>();
                    bj["i"] = b.I;
                    bj["rel"] = b.Rel;
                    bj["size"] = b.Size;
                    if (b.Mime != null)
                    {
                        bj["mime"] = b.Mime;
                    }
                    blobsJson.Add(bj);
                }
                obj["blobs"] = blobsJson;
                obj["totalSize"] = TotalSize;
            }

            return Json.Write(obj);
        }

        public static Manifest FromJson(string text)
        {
            Dictionary<string, object> obj = Json.Parse(text);
            string kind = Json.Str(obj, "kind", null);
            int seq = Json.Int(obj, "seq", 0);

            if (kind == "empty")
            {
                Manifest m = new Manifest();
                m.Kind = kind;
                m.Seq = seq;
                return m;
            }

            if (kind == "text")
            {
                Manifest m = new Manifest();
                m.Kind = kind;
                m.Seq = seq;
                m.Text = Json.Str(obj, "text", "");
                return m;
            }

            if (kind == "image" || kind == "files")
            {
                List<object> rawBlobs = Json.Arr(obj, "blobs");
                List<BlobRef> blobs = new List<BlobRef>();
                foreach (object item in rawBlobs)
                {
                    Dictionary<string, object> b = item as Dictionary<string, object>;
                    if (b == null)
                    {
                        throw new FormatException("элемент blobs должен быть JSON-объектом, получено: " + item);
                    }
                    BlobRef blob = new BlobRef();
                    blob.I = Json.Int(b, "i", 0);
                    blob.Rel = Json.Str(b, "rel", "");
                    blob.Size = ToLong(b, "size");
                    blob.Mime = Json.Str(b, "mime", null);
                    blobs.Add(blob);
                }

                Manifest m = new Manifest();
                m.Kind = kind;
                m.Seq = seq;
                m.Blobs = blobs;
                m.TotalSize = ToLong(obj, "totalSize");
                return m;
            }

            throw new FormatException("неизвестный kind манифеста: " + kind);
        }

        static long ToLong(Dictionary<string, object> o, string key)
        {
            object value;
            if (o.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToInt64(value);
            }
            return 0;
        }
    }
}
