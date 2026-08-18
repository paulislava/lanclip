using System;
using System.Text;

namespace LanClip.Tests
{
    static class RelPathTests
    {
        public static void Register()
        {
            T.Run("keeps simple path", delegate
            {
                T.Eq("img/a.png", RelPath.Normalize("img/a.png"), "simple path");
            });

            T.Run("converts backslashes and collapses separators", delegate
            {
                T.Eq("img/sub/a.png", RelPath.Normalize("img\\\\sub//a.png"), "backslashes+separators");
            });

            T.Run("rejects traversal", delegate
            {
                T.Eq(null, RelPath.Normalize("../secret"), "leading ..");
                T.Eq(null, RelPath.Normalize("img/../../secret"), "embedded ..");
            });

            T.Run("strips absolute prefix", delegate
            {
                T.Eq("etc/passwd", RelPath.Normalize("/etc/passwd"), "absolute prefix");
            });

            T.Run("sanitizes drive letter and illegal chars", delegate
            {
                T.Eq("C_/Users/a_b.txt", RelPath.Normalize("C:\\\\Users\\\\a?b.txt"), "drive letter+illegal chars");
            });

            T.Run("keeps cyrillic and spaces", delegate
            {
                T.Eq("папка/мой отчёт.pdf", RelPath.Normalize("папка/мой отчёт.pdf"), "cyrillic+spaces");
            });

            T.Run("trims trailing dots and spaces", delegate
            {
                T.Eq("name", RelPath.Normalize("name. ."), "trailing dots/spaces");
            });

            T.Run("suffixes reserved windows names", delegate
            {
                T.Eq("NUL_", RelPath.Normalize("NUL"), "NUL");
                T.Eq("com1.txt_", RelPath.Normalize("com1.txt"), "com1.txt");
            });

            T.Run("rejects reserved name exceeding limit after suffix", delegate
            {
                string overLimit = "nul." + new string('a', 146);
                T.Eq(null, RelPath.Normalize(overLimit), "reserved name over limit after suffix");
            });

            T.Run("accepts reserved name within limit after suffix", delegate
            {
                string withinLimit = "nul." + new string('a', 145);
                T.Eq(withinLimit + "_", RelPath.Normalize(withinLimit), "reserved name within limit after suffix");
            });

            T.Run("rejects overlong component and path", delegate
            {
                T.Eq(null, RelPath.Normalize(new string('a', 151)), "overlong component");

                string[] parts = new string[10];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = new string('b', 45);
                }
                string deep = string.Join("/", parts);
                T.Eq(null, RelPath.Normalize(deep), "overlong total path");
            });

            T.Run("rejects empty result", delegate
            {
                T.Eq(null, RelPath.Normalize(""), "empty string");
                T.Eq(null, RelPath.Normalize("./././"), "only dot components");
            });

            // Длина компонента и итогового пути считается в байтах UTF-8, а не в
            // кодовых единицах UTF-16 (string.Length) и не в графемных кластерах
            // (Swift .count) — иначе платформы расходятся на одном и том же входе:
            // "\U0001F44B" ("👋") — 1 графема, 2 code unit-а UTF-16, но 4 байта UTF-8.

            T.Run("accepts emoji component at exact byte limit", delegate
            {
                // 37 * 4 = 148 байт + "ab" (2 байта) = 150 байт — ровно на границе.
                string component = Repeat("\U0001F44B", 37) + "ab";
                T.Eq(component, RelPath.Normalize(component), "emoji component at byte limit");
            });

            T.Run("rejects emoji component one byte over limit", delegate
            {
                // 38 эмодзи = 152 байта UTF-8, но лишь 76 code unit-ов UTF-16 —
                // старая C#-реализация (string.Length <= 150) это принимала.
                string component = Repeat("\U0001F44B", 38);
                T.Eq(null, RelPath.Normalize(component), "emoji component over byte limit");
            });

            T.Run("rejects emoji component reported by review", delegate
            {
                // Ровно тот вход, на котором платформы расходились: 76 эмодзи —
                // 152 code unit-а UTF-16 (C# .Length > 150 — отвергал), но 76 графем
                // (Swift .count <= 150 — принимал). В байтах UTF-8 это 304 байта:
                // обе стороны теперь одинаково отвергают.
                string component = Repeat("\U0001F44B", 76);
                T.Eq(null, RelPath.Normalize(component), "emoji component reported by review");
            });

            T.Run("rejects cyrillic component exceeding byte limit", delegate
            {
                // 80 кириллических букв — 80 code unit-ов UTF-16 и 80 графем (обе
                // старые реализации это принимали), но 160 байт UTF-8 — отказ.
                // Осознанное ужесточение, не регрессия.
                string component = Repeat("а", 80);
                T.Eq(null, RelPath.Normalize(component), "cyrillic component over byte limit");
            });
        }

        static string Repeat(string value, int count)
        {
            StringBuilder builder = new StringBuilder(value.Length * count);
            for (int i = 0; i < count; i++)
            {
                builder.Append(value);
            }
            return builder.ToString();
        }
    }
}
