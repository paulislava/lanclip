using System.Collections.Generic;

namespace LanClip
{
    // Итог операции POST /pull: что агент забрал у соседа и записал в локальный
    // буфер. Зеркало mac/Sources/LanClipCore/HttpServer.swift: PullResult.
    //
    // Объявлен здесь (задача 19), а не в задаче 21, потому что HttpServer
    // принимает Func<PullResult> pull и без этого типа не соберётся — полная
    // реализация цикла pull (PullClient) появится в задаче 21 и будет
    // переиспользовать этот же тип, а не объявлять его заново.
    class PullResult
    {
        public string Kind;
        public int FileCount;
        public long Bytes;

        public string ToJson()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["kind"] = Kind;
            obj["fileCount"] = FileCount;
            obj["bytes"] = Bytes;
            return Json.Write(obj);
        }
    }
}
