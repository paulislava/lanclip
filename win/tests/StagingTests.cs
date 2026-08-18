using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LanClip.Tests
{
    // Зеркало mac/Tests/LanClipCoreTests/StagingTests.swift (задача 9/20).
    static class StagingTests
    {
        static DateTime FixedDate()
        {
            // 2026-08-18 12:34:56 — фиксированная дата, не зависящая от текущего
            // времени. Kind не важен: Stamp() форматирует по шаблону без
            // зоно-зависимых токенов, поэтому что положили, то и напечаталось.
            return new DateTime(2026, 8, 18, 12, 34, 56, DateTimeKind.Utc);
        }

        static string MakeRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "lanclip-staging-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            return root;
        }

        // .NET Framework's Directory.Delete(path, recursive: true) бросает
        // IOException ("параметр задан неверно"), если где-то внутри дерева
        // встречается junction — известное ограничение рекурсивного удаления,
        // не связанное с продакшн-кодом Staging (там junction внутри партии в
        // норме появиться неоткуда: Destination() как раз и не даёт создать
        // что-либо ЗА junction, а вот саму директорию-junction создают только
        // эти тесты, чтобы её сымитировать). Поэтому перед рекурсивным
        // удалением root сперва снимаем сам junction нерекурсивным Delete —
        // это просто отвязывает точку, не трогая её цель.
        static void CleanupWithJunction(string root, string junctionPath)
        {
            if (junctionPath != null && Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, false);
            }
            Directory.Delete(root, true);
        }

        static void CreateJunction(string linkPath, string targetPath)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "cmd.exe";
            info.Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"";
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;

            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string stderr = process.StandardError.ReadToEnd();
                    throw new InvalidOperationException("mklink /J упал: " + stderr);
                }
            }
        }

        public static void Register()
        {
            RegisterStampTests();
            RegisterNewBatchTests();
            RegisterDestinationTests();
            RegisterCleanupTests();
            RegisterDefaultRootTest();
        }

        static void RegisterStampTests()
        {
            T.Run("stamp formats fixed date as yyyyMMdd-HHmmss", delegate
            {
                T.Eq("20260818-123456", Staging.Stamp(FixedDate()), "stamp");
            });
        }

        static void RegisterNewBatchTests()
        {
            T.Run("new batch creates folder with stamp", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    T.Eq("20260818-123456", Path.GetFileName(batch.Root), "folder name");
                    T.True(Directory.Exists(batch.Root), "folder exists");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("two batches in same second get distinct folders", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch first = staging.NewBatch();
                    StagingBatch second = staging.NewBatch();

                    T.True(first.Root != second.Root, "distinct paths");
                    T.Eq("20260818-123456", Path.GetFileName(first.Root), "first folder name");
                    T.Eq("20260818-123456-2", Path.GetFileName(second.Root), "second folder name");
                    T.True(Directory.Exists(second.Root), "second folder exists");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("third batch in same second gets suffix three", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    staging.NewBatch();
                    staging.NewBatch();
                    StagingBatch third = staging.NewBatch();

                    T.Eq("20260818-123456-3", Path.GetFileName(third.Root), "third folder name");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }

        static void RegisterDestinationTests()
        {
            T.Run("destination creates intermediate folders", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    string destination = batch.Destination("sub/folder/file.txt");

                    string expected = Path.Combine(batch.Root, "sub", "folder", "file.txt");
                    T.Eq(expected, destination, "destination path");
                    T.True(Directory.Exists(Path.GetDirectoryName(destination)), "intermediate folder exists");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("destination rejects parent traversal", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    try
                    {
                        batch.Destination("../x");
                        T.True(false, "expected StagingException");
                    }
                    catch (StagingException e)
                    {
                        T.Eq("../x", e.Rel, "rejected rel");
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("destination rejects absolute escape attempt", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    T.Throws<StagingException>(delegate
                    {
                        batch.Destination("sub/../../../../etc/passwd");
                    }, "rejects escape via many ..");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("destination stays within root for plain name", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    string destination = batch.Destination("a.png");
                    T.True(Path.GetFullPath(destination).StartsWith(Path.GetFullPath(batch.Root)),
                        "destination within root");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            // Лексическая проверка (Path.GetFullPath) не разрешает junction: если внутри
            // партии есть каталог-junction, ведущий наружу, кандидат текстуально лежит
            // внутри Root, но на диске запись ушла бы за его пределы. Проверка обязана
            // распознавать reparse point — сравнивать одних лексически разрешённых строк
            // недостаточно.
            T.Run("destination rejects path through junction escaping root", delegate
            {
                string root = MakeRoot();
                string outside = Path.Combine(Path.GetTempPath(), "lanclip-staging-outside-" + Guid.NewGuid());
                Directory.CreateDirectory(outside);
                string junction = null;
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    junction = Path.Combine(batch.Root, "sub");
                    CreateJunction(junction, outside);

                    try
                    {
                        batch.Destination("sub/passwd");
                        T.True(false, "expected StagingException");
                    }
                    catch (StagingException e)
                    {
                        T.Eq("sub/passwd", e.Rel, "rejected rel");
                    }
                }
                finally
                {
                    CleanupWithJunction(root, junction);
                    Directory.Delete(outside, true);
                }
            });

            // Если под junction-побегом есть ещё хотя бы один несуществующий сегмент
            // (sub/deeper/evil.txt, sub ведёт наружу, deeper ещё не создан), безусловный
            // Directory.CreateDirectory по всему пути физически создал бы deeper СНАРУЖИ
            // партии ещё до того, как отказ успел бы сработать. Проверяем не только сам
            // отказ, но и то, что снаружи партии реально ничего не появилось.
            T.Run("destination rejects path through junction with uncreated segment beneath", delegate
            {
                string root = MakeRoot();
                string outside = Path.Combine(Path.GetTempPath(), "lanclip-staging-outside-" + Guid.NewGuid());
                Directory.CreateDirectory(outside);
                string junction = null;
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    junction = Path.Combine(batch.Root, "sub");
                    CreateJunction(junction, outside);

                    try
                    {
                        batch.Destination("sub/deeper/evil.txt");
                        T.True(false, "expected StagingException");
                    }
                    catch (StagingException e)
                    {
                        T.Eq("sub/deeper/evil.txt", e.Rel, "rejected rel");
                    }

                    string escapedDirectory = Path.Combine(outside, "deeper");
                    T.True(!Directory.Exists(escapedDirectory),
                        "каталог не должен физически создаваться снаружи партии");
                }
                finally
                {
                    CleanupWithJunction(root, junction);
                    Directory.Delete(outside, true);
                }
            });

            // Контроль к предыдущим двум: починка не должна запрещать обычные вложенные
            // пути без junction — только те, что реально выходят за пределы партии.
            T.Run("destination still succeeds for nested path without junctions", delegate
            {
                string root = MakeRoot();
                try
                {
                    Staging staging = new Staging(root, new Func<DateTime>(FixedDate));
                    StagingBatch batch = staging.NewBatch();

                    string destination = batch.Destination("sub/nested/file.txt");

                    string expected = Path.Combine(batch.Root, "sub", "nested", "file.txt");
                    T.Eq(expected, destination, "destination path");
                    T.True(Directory.Exists(Path.GetDirectoryName(destination)), "intermediate folder exists");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }

        static void RegisterCleanupTests()
        {
            T.Run("cleanup removes batches older than seven days", delegate
            {
                string root = MakeRoot();
                try
                {
                    DateTime current = FixedDate();
                    Staging staging = new Staging(root, delegate { return current; });

                    StagingBatch old = staging.NewBatch(); // day 0
                    current = FixedDate().AddDays(8);
                    StagingBatch fresh = staging.NewBatch();

                    staging.Cleanup();

                    T.True(!Directory.Exists(old.Root), "old batch removed");
                    T.True(Directory.Exists(fresh.Root), "fresh batch kept");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("cleanup keeps batch exactly at seven day boundary", delegate
            {
                string root = MakeRoot();
                try
                {
                    DateTime current = FixedDate();
                    Staging staging = new Staging(root, delegate { return current; });

                    StagingBatch boundary = staging.NewBatch(); // day 0
                    current = FixedDate().AddDays(7); // exactly +7 days
                    staging.NewBatch();

                    staging.Cleanup();

                    T.True(Directory.Exists(boundary.Root),
                        "партия ровно на границе 7 дней не должна удаляться");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            T.Run("cleanup keeps exactly twenty most recent of twenty five", delegate
            {
                string root = MakeRoot();
                try
                {
                    DateTime current = FixedDate();
                    Staging staging = new Staging(root, delegate { return current; });

                    List<StagingBatch> batches = new List<StagingBatch>();
                    for (int i = 0; i < 25; i++)
                    {
                        // Разносим партии по времени на минуту каждую — иначе все 25
                        // создались бы в одну секунду и порядок "последних 20" был бы не
                        // определён.
                        current = FixedDate().AddMinutes(i);
                        batches.Add(staging.NewBatch());
                    }

                    staging.Cleanup();

                    string[] remaining = Directory.GetDirectories(root);
                    T.Eq(20, remaining.Length, "должно остаться ровно 20 партий");

                    for (int i = 0; i < 5; i++)
                    {
                        T.True(!Directory.Exists(batches[i].Root),
                            "партия " + i + " должна быть удалена как избыточная");
                    }
                    for (int i = 5; i < 25; i++)
                    {
                        T.True(Directory.Exists(batches[i].Root),
                            "партия " + i + " должна остаться среди последних 20");
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }

        static void RegisterDefaultRootTest()
        {
            T.Run("default root points to LocalAppData incoming", delegate
            {
                string expected = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                    "lanclip", "incoming");
                T.Eq(expected, Staging.DefaultRoot(), "default root");
            });
        }
    }
}
