using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LanClip
{
    static class RelPath
    {
        const int MaxComponent = 150;
        // internal (не private): значение читает win/tests/RelPathTests.cs, чтобы
        // проверить бюджет "корень партии + MaxTotal <= легаси MAX_PATH" одним
        // числом, а не задваивать константу в тестовом файле, которая рискует
        // разъехаться с реальной при следующей правке.
        // Находка I8 финального ревью: было 400, что не влезает в легаси
        // MAX_PATH (260 символов, включая диск и завершающий NUL) вместе со
        // стейджинг-корнем (%LOCALAPPDATA%\lanclip\incoming\<метка>\, на
        // реальной машине ~66 символов, плюс запас на более длинное имя
        // пользователя). rel длиной 195-400 нормализацию проходил, но на
        // приёме падал необёрнутым PathTooLongException. Выбор 150 (тот же
        // предел, что и MaxComponent, а не отдельно придуманное число) с
        // запасом в ~100 символов на корень партии укладывается в 260 с
        // большим запасом. Предел обязан совпадать на Mac и Windows побитово —
        // это часть протокола (см. mac/Sources/LanClipCore/RelPath.swift), а
        // не деталь реализации одной стороны.
        internal const int MaxTotal = 150;

        static readonly HashSet<char> Illegal = BuildIllegal();
        static readonly HashSet<string> Reserved = BuildReserved();

        static HashSet<char> BuildIllegal()
        {
            HashSet<char> chars = new HashSet<char>();
            chars.Add('<');
            chars.Add('>');
            chars.Add(':');
            chars.Add('"');
            chars.Add('|');
            chars.Add('?');
            chars.Add('*');
            chars.Add('\0');
            return chars;
        }

        static HashSet<string> BuildReserved()
        {
            HashSet<string> names = new HashSet<string>();
            names.Add("con");
            names.Add("prn");
            names.Add("aux");
            names.Add("nul");
            for (int n = 1; n <= 9; n++)
            {
                names.Add("com" + n);
                names.Add("lpt" + n);
            }
            return names;
        }

        // Семь правил в строго этом порядке (см. mac/Sources/LanClipCore/RelPath.swift —
        // эталон, с которым эта реализация обязана совпадать бит в бит):
        // 1. \ -> /
        // 2. разбить и выбросить пустые компоненты и "."
        // 3. любой компонент ".." -> отказ
        // 4. санитизация запрещённых символов и управляющих символов в "_"
        // 5. обрезка хвостовых точек и пробелов
        // 6. суффикс "_" к зарезервированным именам — ДО проверки длины компонента
        // 7. отказ при компоненте длиннее MaxComponent или итоговом пути длиннее
        //    MaxTotal — длина считается в БАЙТАХ UTF-8 (Encoding.UTF8.GetByteCount),
        //    не в кодовых единицах UTF-16 (string.Length) и не в графемах, как на
        //    Swift-стороне (String.count/utf8.count) — иначе платформы расходятся
        //    на одном и том же входе (например, эмодзи или иероглифы из
        //    дополнительных плоскостей Unicode: 1 графема = 2 code unit-а UTF-16 =
        //    4 байта UTF-8).
        public static string Normalize(string raw)
        {
            string unified = raw.Replace("\\", "/");
            string[] pieces = unified.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> components = new List<string>();

            foreach (string piece in pieces)
            {
                if (piece == ".")
                {
                    continue;
                }
                if (piece == "..")
                {
                    return null;
                }
                string safe = Sanitize(piece);
                if (safe == null)
                {
                    return null;
                }
                components.Add(safe);
            }

            if (components.Count == 0)
            {
                return null;
            }

            string joined = string.Join("/", components.ToArray());
            if (Encoding.UTF8.GetByteCount(joined) > MaxTotal)
            {
                return null;
            }
            return joined;
        }

        static string Sanitize(string component)
        {
            StringBuilder builder = new StringBuilder(component.Length);
            foreach (char c in component)
            {
                if (Illegal.Contains(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Control)
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(c);
                }
            }

            string cleaned = builder.ToString();
            while (cleaned.Length > 0 && (cleaned[cleaned.Length - 1] == '.' || cleaned[cleaned.Length - 1] == ' '))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 1);
            }

            if (cleaned.Length == 0)
            {
                return null;
            }

            int dotIndex = cleaned.IndexOf('.');
            string baseName = dotIndex >= 0 ? cleaned.Substring(0, dotIndex) : cleaned;
            if (Reserved.Contains(baseName.ToLowerInvariant()))
            {
                cleaned = cleaned + "_";
            }

            if (Encoding.UTF8.GetByteCount(cleaned) > MaxComponent)
            {
                return null;
            }
            return cleaned;
        }
    }
}
