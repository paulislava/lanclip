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

            T.Run("propagated exception keeps its sta-side stack frame", delegate
            {
                // ExceptionDispatchInfo.Capture/Throw должен сохранить не только тип
                // и сообщение, но и сам стек, накопленный внутри STA-потока — иначе
                // отладка отказов буфера в задачах 19/22 потеряет ровно тот кадр,
                // который указывает, где внутри тела реально бросило.
                StaExecutor executor = new StaExecutor();
                try
                {
                    InvalidOperationException caught = null;
                    try
                    {
                        executor.Invoke(new Func<int>(delegate
                        {
                            return ThrowFromStaSide();
                        }));
                    }
                    catch (InvalidOperationException e)
                    {
                        caught = e;
                    }

                    T.True(caught != null, "exception was caught");
                    if (caught != null)
                    {
                        T.Eq("бум со стороны STA", caught.Message, "message preserved");
                        T.True(caught.StackTrace != null
                            && caught.StackTrace.IndexOf("ThrowFromStaSide") >= 0,
                            "stack trace still names the sta-side throwing frame");
                    }
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

        // Отдельный именованный метод (а не только анонимный делегат) специально
        // для того, чтобы его имя было видно в StackTrace пойманного исключения —
        // так тест на сохранение стека проверяет что-то конкретное, а не пустоту.
        static int ThrowFromStaSide()
        {
            throw new InvalidOperationException("бум со стороны STA");
        }
    }
}
