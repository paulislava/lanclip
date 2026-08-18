using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Находка I9 финального ревью: раньше принимал через Convert.ToInt32 что
        // угодно, лишь бы конвертация не бросила — строку "5" (Convert.ToInt32
        // парсит её как int.Parse), дробное 1.5 (Convert.ToInt32 округляет вместо
        // отказа). Swift-сторонний JSONDecoder строгий: {"seq":"5"} или
        // {"seq":1.5} для поля Int — DecodingError, а не тихо принятое значение.
        // Множество манифестов, принимаемых одной стороной и отвергаемых другой,
        // было непустым. Теперь — только настоящее целое число JSON (int/long
        // от JavaScriptSerializer) или явный отказ; отсутствие ключа по-прежнему
        // даёт fallback, это легитимный случай "поле не прислали", а не порча.
        public static int Int(Dictionary<string, object> o, string key, int fallback)
        {
            object value;
            if (!o.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            long strict = ToStrictLong(value, key);
            if (strict < int.MinValue || strict > int.MaxValue)
            {
                throw new FormatException("поле \"" + key + "\" не помещается в 32-битное целое: " + strict);
            }
            return (int)strict;
        }

        // Зеркало Int(...) выше для 64-битных полей (blob.size, totalSize) — те же
        // причины и тот же контракт "строгий тип или отказ".
        public static long Long(Dictionary<string, object> o, string key, long fallback)
        {
            object value;
            if (!o.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            return ToStrictLong(value, key);
        }

        // null означает "поле отсутствовало в JSON (или было явным null)", а не
        // "поле было и равнялось нулю" — вызывающая сторона (Manifest.TotalSize)
        // обязана уметь отличить одно от другого, поэтому в отличие от Long(...)
        // здесь нет fallback-значения по умолчанию.
        public static long? NullableLong(Dictionary<string, object> o, string key)
        {
            object value;
            if (!o.TryGetValue(key, out value) || value == null)
            {
                return null;
            }
            return ToStrictLong(value, key);
        }

        static long ToStrictLong(object value, string key)
        {
            if (value is int)
            {
                return (int)value;
            }
            if (value is long)
            {
                return (long)value;
            }
            throw new FormatException("поле \"" + key + "\" обязано быть целым числом JSON, получено: "
                + DescribeMismatchedValue(value));
        }

        static string DescribeMismatchedValue(object value)
        {
            if (value is string)
            {
                return "строка \"" + value + "\"";
            }
            if (value is double)
            {
                return "дробное число " + Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (value is bool)
            {
                return "булево " + value;
            }
            return value.GetType().Name;
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
