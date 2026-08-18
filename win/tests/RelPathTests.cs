using System;

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
        }
    }
}
