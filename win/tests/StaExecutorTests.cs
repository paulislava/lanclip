using System;
using System.Threading;

namespace LanClip.Tests
{
    // Не входит в буквальный список файлов из брифа (там названы только SnapshotTests.cs
    // и FakeClipboard.cs), но брифом же явно требуется отдельно протестировать
    // StaExecutor (шаг 3) — заводим отдельный набор тестов и регистрируем его в Main
    // тестового раннера вместе с остальными.
    static class StaExecutorTests
    {
        public static void Register()
        {
            T.Run("invoke returns value from body", delegate
            {
                StaExecutor executor = new StaExecutor();
                try
                {
                    int result = executor.Invoke(new Func<int>(delegate { return 7; }));
                    T.Eq(7, result, "invoke result");
                }
                finally
                {
                    executor.Shutdown();
                }
            });

            T.Run("body runs on a dedicated sta thread", delegate
            {
                StaExecutor executor = new StaExecutor();
                try
                {
                    ApartmentState state = executor.Invoke(new Func<ApartmentState>(delegate
                    {
                        return Thread.CurrentThread.GetApartmentState();
                    }));
                    T.Eq(ApartmentState.STA, state, "apartment state");
                }
                finally
                {
                    executor.Shutdown();
                }
            });

            T.Run("exception from body propagates to caller", delegate
            {
                StaExecutor executor = new StaExecutor();
                try
                {
                    T.Throws<InvalidOperationException>(delegate
                    {
                        executor.Invoke(new Func<int>(delegate
                        {
                            throw new InvalidOperationException("бум");
                        }));
                    }, "exception propagated from Invoke<TR>");

                    T.Throws<InvalidOperationException>(delegate
                    {
                        executor.Invoke(new Action(delegate
                        {
                            throw new InvalidOperationException("бум");
                        }));
                    }, "exception propagated from Invoke(Action)");
                }
                finally
                {
                    executor.Shutdown();
                }
            });

            T.Run("invoke after shutdown throws", delegate
            {
                StaExecutor executor = new StaExecutor();
                executor.Shutdown();

                T.Throws<ObjectDisposedException>(delegate
                {
                    executor.Invoke(new Action(delegate { }));
                }, "invoke after shutdown throws");
            });

            T.Run("nested invoke from the sta thread itself does not deadlock", delegate
            {
                StaExecutor executor = new StaExecutor();
                try
                {
                    int result = executor.Invoke(new Func<int>(delegate
                    {
                        // Тело само вызывает Invoke на том же исполнителе — наивная
                        // очередь-с-единственным-обработчиком встала бы намертво:
                        // рабочий поток ждал бы сам себя. StaExecutor распознаёт
                        // вызов с собственного STA-потока и исполняет тело сразу,
                        // без постановки в очередь.
                        return executor.Invoke(new Func<int>(delegate { return 42; }));
                    }));
                    T.Eq(42, result, "nested invoke result");
                }
                finally
                {
                    executor.Shutdown();
                }
            });
        }
    }
}
