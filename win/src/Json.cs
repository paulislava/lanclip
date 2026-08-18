using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace LanClip
{
    static class Json
    {
        public static string Write(object value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Serialize(value);
        }

        public static Dictionary<string, object> Parse(string text)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            object result;
            try
            {
                result = serializer.DeserializeObject(text);
            }
            catch (Exception e)
            {
                throw new FormatException("некорректный JSON: " + e.Message, e);
            }

            Dictionary<string, object> dict = result as Dictionary<string, object>;
            if (dict == null)
            {
                throw new FormatException("ожидался JSON-объект, получено: " + text);
            }
            return dict;
        }

        public static string Str(Dictionary<string, object> o, string key, string fallback)
        {
            object value;
            if (o.TryGetValue(key, out value) && value is string)
            {
                return (string)value;
            }
            return fallback;
        }

        public static int Int(Dictionary<string, object> o, string key, int fallback)
        {
            object value;
            if (o.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    return Convert.ToInt32(value);
                }
                catch (Exception)
                {
                    return fallback;
                }
            }
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> o, string key, bool fallback)
        {
            object value;
            if (o.TryGetValue(key, out value) && value is bool)
            {
                return (bool)value;
            }
            return fallback;
        }

        public static List<object> Arr(Dictionary<string, object> o, string key)
        {
            object value;
            if (o.TryGetValue(key, out value) && value is object[])
            {
                return new List<object>((object[])value);
            }
            return new List<object>();
        }
    }
}
