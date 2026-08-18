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

            // MARK: - Мелкая находка финального ревью: ".con" нормализовался по-разному на двух платформах

            // Реальный Windows считает "имя до первой точки" пустой строкой для
            // файла с ведущей точкой (не "con") — эта реализация уже вела себя
            // так (IndexOf('.') == 0 -> baseName == ""), Swift-сторонняя раньше
            // расходилась (split отбрасывал пустую ведущую подпоследовательность,
            // получая base == "con"). Тест фиксирует уже верное поведение и
            // защищает от регрессии при будущей правке.
            T.Run("leading dot before reserved name is not treated as reserved", delegate
            {
                T.Eq(".con", RelPath.Normalize(".con"), "single leading dot");
                T.Eq("..con", RelPath.Normalize("..con"), "double leading dot");
            });

            T.Run("reserved name without leading dot is still suffixed", delegate
            {
                T.Eq("con_", RelPath.Normalize("con"), "no leading dot");
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

            // MARK: - I8: предел обязан реально влезать в MAX_PATH вместе с корнем партии

            // Регрессия находки I8: MaxTotal раньше был 400 — rel такой длины
            // нормализацию проходил, но на приёме падал необёрнутым
            // PathTooLongException, потому что root + "\" + rel превышал легаси
            // MAX_PATH (260 символов). 100 символов — задокументированный в самом
            // предельном значении запас на корень партии (реально измеренный
            // корень на машине Павла — около 66 символов); тест провалится, если
            // кто-то снова поднимет MaxTotal, не пересчитав этот бюджет.
            T.Run("MaxTotal leaves room for legacy MAX_PATH under the staging root", delegate
            {
                const int legacyWindowsMaxPath = 260;
                const int assumedStagingRootBudget = 100;
                const int pathSeparator = 1;
                T.True(assumedStagingRootBudget + pathSeparator + RelPath.MaxTotal <= legacyWindowsMaxPath,
                    "MaxTotal + assumed root budget must fit under legacy MAX_PATH");
            });

            // Ровно тот сценарий, который раньше проходил нормализацию, но падал на
            // приёме: два компонента по 100 байт (каждый сам по себе меньше
            // MaxComponent=150, поэтому по-компонентной проверке никогда не был бы
            // отвергнут), но суммарно 201 байт — между старым пределом 400
            // (проходил) и новым 150 (обязан отвергаться).
            T.Run("rejects multi-component path that used to pass normalization but overflowed MAX_PATH", delegate
            {
                string segment = new string('a', 100);
                string onceAcceptedNowRejected = segment + "/" + segment;
                T.Eq(null, RelPath.Normalize(onceAcceptedNowRejected), "201-byte multi-component path");
            });

            // MARK: - Живой дефект: имя с "ё" приезжает разложенным (NFD) с macOS

            // "отчёт.txt" в разложенной форме (NFD): "о","т","ч","е" + U+0308
            // (комбинирующий диакритик) + "т",".txt" — именно так macOS отдаёт
            // имена файлов из файловой системы. Ожидаемый результат —
            // предсоставленная форма (NFC) с "ё" = U+0451 одним кодпоинтом.
            // Сравниваем через T.Eq строковое равенство, которое для символов
            // из базовой плоскости (BMP, как здесь) эквивалентно сравнению
            // кодовых точек по code unit-ам UTF-16 — визуально "отчёт.txt" и
            // разложенная форма неотличимы, поэтому дополнительно проверяем
            // Length (число кодпоинтов), чтобы тест не мог пройти на
            // случайном совпадении отображения.
            T.Run("decomposed cyrillic yo is recomposed to NFC", delegate
            {
                string nfd = "отчёт.txt";
                string expectedNfc = "отчёт.txt";
                string result = RelPath.Normalize(nfd);
                T.Eq(expectedNfc, result, "NFD -> NFC recomposition");
                T.Eq(9, result == null ? -1 : result.Length, "NFC result has 9 code points");
            });

            T.Run("already precomposed cyrillic yo is unchanged", delegate
            {
                string nfc = "отчёт.txt";
                T.Eq(nfc, RelPath.Normalize(nfc), "NFC input stays NFC");
            });

            // Предел длины считается от нормализованной (NFC) формы: имя в NFD
            // весит больше MaxTotal байт UTF-8 (комбинирующие диакритики
            // добавляют байты на каждую букву), а после схлопывания в NFC
            // укладывается в предел. Без нормализации до подсчёта длины это
            // имя было бы неправомерно отвергнуто.
            T.Run("MaxTotal is measured after NFC normalization", delegate
            {
                // "ё" в NFD = "е" (2 байта UTF-8) + U+0308 (2 байта) = 4 байта
                // на букву. В NFC "ё" = U+0451 = 2 байта. 40 повторов: NFD =
                // 160 байт (> 150, отвергалось бы без нормализации), NFC = 80
                // байт (укладывается).
                string nfdYo = "ё";
                string nfdName = Repeat(nfdYo, 40);
                T.True(Encoding.UTF8.GetByteCount(nfdName) > RelPath.MaxTotal, "NFD name exceeds MaxTotal in bytes before normalization");

                string result = RelPath.Normalize(nfdName);
                string expectedNfc = Repeat("ё", 40);
                T.Eq(expectedNfc, result, "NFD name accepted and recomposed to NFC");
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
