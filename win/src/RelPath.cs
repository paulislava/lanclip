using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LanClip
{
    static class RelPath
    {
        const int MaxComponent = 150;
        const int MaxTotal = 400;

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
        // 7. отказ при компоненте длиннее MaxComponent или итоговом пути длиннее MaxTotal
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
            if (joined.Length > MaxTotal)
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

            if (cleaned.Length > MaxComponent)
            {
                return null;
            }
            return cleaned;
        }
    }
}
