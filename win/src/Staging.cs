using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LanClip
{
    // Небезопасный относительный путь с чужой машины не прошёл проверку вложенности
    // в StagingBatch.Destination — Rel хранит исходное (недоверенное) значение, чтобы
    // вызывающая сторона могла залогировать/показать, что именно было отвергнуто.
    class StagingException : Exception
    {
        public readonly string Rel;

        public StagingException(string rel)
            : base("небезопасный относительный путь: " + rel)
        {
            Rel = rel;
        }
    }

    // Одна партия принятых по сети файлов: подпапка стейджинга с меткой времени.
    // Зеркало mac/Sources/LanClipCore/Staging.swift: StagingBatch (задача 9).
    class StagingBatch
    {
        public readonly string Root;

        public StagingBatch(string root)
        {
            Root = root;
        }

        // Строит путь назначения для относительного пути rel, приехавшего с чужой
        // машины, и создаёт промежуточные папки. rel — недоверенный ввод: сначала
        // прогоняется через RelPath.Normalize, а затем — ещё раз, уже как последний
        // рубеж перед записью на диск — проверяется, что итоговый путь лежит внутри
        // Root. Одной нормализации мало: она гарантирует форму строки, а не то, что
        // финальный путь не выпрыгнул за пределы партии.
        //
        // Эта последняя проверка обязана быть junction/reparse-point-aware, а не
        // просто лексической: Path.GetFullPath схлопывает только "."/".." в строке и
        // не разрешает NTFS-junction (аналог symlink на Windows). Если внутри партии
        // окажется каталог-junction (например, Root\sub в реальности указывает на
        // C:\Windows), кандидат Root\sub\evil.txt текстуально лежит внутри Root и
        // прошёл бы лексическую проверку, но запись на диск ушла бы по junction
        // наружу.
        //
        // Проверять нужно самого глубокого УЖЕ СУЩЕСТВУЮЩЕГО предка целевого каталога,
        // а не сам целевой каталог после его создания: Directory.CreateDirectory
        // одним вызовом создал бы недостающие компоненты пути ещё ДО проверки — и
        // если под junction есть хотя бы один несуществующий сегмент
        // (sub/deeper/evil.txt, где sub ведёт наружу, а deeper ещё не существует),
        // этот вызов физически создал бы deeper в чужом каталоге, и только потом
        // сработал бы отказ. Пройти сквозь junction можно лишь по уже существующему
        // компоненту — так что проверка такого предка закрывает дыру полностью:
        // компоненты, которые создаёт код сам (после проверки), это настоящие
        // каталоги, а не junction, новых путей наружу они не открывают.
        public string Destination(string rel)
        {
            string normalized = RelPath.Normalize(rel);
            if (normalized == null)
            {
                throw new StagingException(rel);
            }

            string relativeNative = normalized.Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.Combine(Root, relativeNative);
            string candidateDirectory = Path.GetDirectoryName(candidate);

            string existingAncestor = candidateDirectory;
            while (!Directory.Exists(existingAncestor))
            {
                string parent = Path.GetDirectoryName(existingAncestor);
                if (string.IsNullOrEmpty(parent) || parent == existingAncestor)
                {
                    break; // достигли корня файловой системы
                }
                existingAncestor = parent;
            }

            // root сам может лежать под junction, поэтому сравнение ведётся между
            // двумя РАЗРЕШЁННЫМИ (Path.GetFullPath) путями — иначе неразрешённый
            // root отвергал бы вообще всё. Path.GetFullPath — чисто лексическая
            // нормализация (как и на Mac-стороне standardizedFileURL), поэтому сама
            // по себе junction не разрешает: за это отвечает отдельная проверка
            // AnyComponentIsJunction ниже.
            string resolvedRoot = Path.GetFullPath(Root);
            string resolvedAncestor = Path.GetFullPath(existingAncestor);
            bool lexicallyInside = string.Equals(resolvedAncestor, resolvedRoot, StringComparison.OrdinalIgnoreCase)
                || resolvedAncestor.StartsWith(resolvedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);

            if (!lexicallyInside || AnyComponentIsJunction(resolvedRoot, resolvedAncestor))
            {
                throw new StagingException(rel);
            }

            // Проверка прошла: все существующие предки лежат внутри партии и не
            // проходят сквозь junction, поэтому теперь безопасно создать недостающие
            // компоненты одним вызовом.
            Directory.CreateDirectory(candidateDirectory);

            return candidate;
        }

        // Проходит от ancestor вверх до root включительно и проверяет каждый
        // компонент на признак reparse point (junction — аналог symlink на Windows).
        // Если хотя бы один компонент им является, доверять лексической проверке
        // выше нельзя: физический путь на диске может расходиться со строкой.
        static bool AnyComponentIsJunction(string root, string ancestor)
        {
            string current = ancestor;
            while (true)
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    return false; // дошли до корня файловой системы, не встретив root
                }
                current = parent;
            }
        }
    }

    // Управляет партиями стейджинга: создание новой партии с меткой времени и
    // периодическая уборка — партии старше MaxAgeDays и всё за пределами
    // KeepBatches последних партий удаляются. Зеркало mac/Sources/LanClipCore/Staging.swift:
    // Staging (задача 9).
    class Staging
    {
        public const int KeepBatches = 20;
        public const int MaxAgeDays = 7;

        // Формат метки партии: yyyyMMdd-HHmmss. Культура зафиксирована на инвариантную —
        // иначе на машине с другой локалью (12-часовой формат, локальные названия)
        // метка развалилась бы. Часовой пояс не фиксируется отдельно: DateTime.ToString
        // с этим шаблоном не содержит зоно-зависимых токенов и печатает ровно те
        // компоненты значения, которые в него положили — согласованность в тестах и
        // между двумя вызовами Stamp() держится на том, что вызывающая сторона сама
        // передаёт одинаково трактуемые DateTime (см. Program.cs/PullClient — задача 21,
        // они обязаны использовать один и тот же источник времени согласованно).
        const string StampFormat = "yyyyMMdd-HHmmss";

        readonly string root;
        readonly Func<DateTime> now;

        public Staging(string root, Func<DateTime> now)
        {
            this.root = root;
            this.now = now;
        }

        public static string Stamp(DateTime when)
        {
            return when.ToString(StampFormat, CultureInfo.InvariantCulture);
        }

        public static string DefaultRoot()
        {
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return Path.Combine(localAppData, "lanclip", "incoming");
        }

        // Создаёт новую партию с меткой текущего времени. Метка имеет секундную
        // точность — два вызова в течение одной секунды дали бы одинаковую папку,
        // поэтому при коллизии добавляется числовой суффикс "-2", "-3", ...
        public StagingBatch NewBatch()
        {
            string baseName = Stamp(now());
            string candidate = Path.Combine(root, baseName);
            int suffix = 2;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(root, baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture));
                suffix++;
            }

            Directory.CreateDirectory(candidate);
            return new StagingBatch(candidate);
        }

        // Удаляет партии старше MaxAgeDays дней и, независимо от возраста, всё за
        // пределами KeepBatches последних (по имени папки — метка сортируется как
        // строка в хронологическом порядке) партий. Каждое правило применяется само
        // по себе: партия может быть удалена и как устаревшая, и как избыточная.
        public void Cleanup()
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            List<string> batchNames = new List<string>();
            foreach (string entry in Directory.GetDirectories(root))
            {
                batchNames.Add(Path.GetFileName(entry));
            }
            // Метка сортируется как строка в том же порядке, что и хронологически
            // (yyyyMMdd-HHmmss), поэтому сравнение возраста и отбор "последних N"
            // ведутся по строке, без парсинга обратно в DateTime.
            batchNames.Sort(StringComparer.Ordinal);

            // Партия ровно на границе 7 дней не должна удаляться — порог считается как
            // "строго раньше now - 7 дней", а не "не позже".
            string ageCutoffStamp = Stamp(now().AddDays(-MaxAgeDays));

            HashSet<string> namesToKeepByRecency = new HashSet<string>();
            int keepStart = Math.Max(0, batchNames.Count - KeepBatches);
            for (int i = keepStart; i < batchNames.Count; i++)
            {
                namesToKeepByRecency.Add(batchNames[i]);
            }

            foreach (string name in batchNames)
            {
                string baseStamp = BaseStampOf(name);
                bool isTooOld = string.CompareOrdinal(baseStamp, ageCutoffStamp) < 0;
                bool isBeyondKeepLimit = !namesToKeepByRecency.Contains(name);

                if (!isTooOld && !isBeyondKeepLimit)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(Path.Combine(root, name), true);
                }
                catch (Exception)
                {
                    // Уборка терпима к отказу удаления отдельной партии — тот же принцип,
                    // что и на Mac (try? fileManager.removeItem).
                }
            }
        }

        // "20260818-123456-2" -> "20260818-123456": суффикс коллизии отбрасывается,
        // возраст считается по самой метке времени, а не по разрешителю коллизий.
        static string BaseStampOf(string name)
        {
            string[] parts = name.Split('-');
            if (parts.Length >= 2)
            {
                return parts[0] + "-" + parts[1];
            }
            return name;
        }
    }
}
