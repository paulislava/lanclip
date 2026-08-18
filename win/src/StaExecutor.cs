using System;
using System.Collections.Generic;
using System.Threading;

namespace LanClip
{
    // На Mac этого исполнителя нет и не было нужно: коллбэки HttpListener приходят в
    // MTA-потоках пула, а System.Windows.Forms.Clipboard требует STA-апартамент.
    // StaExecutor держит единственный выделенный STA-поток и маршалит на него все
    // операции с буфером — без этого доступ к Clipboard падал бы через раз.
    //
    // Вложенный вызов: если тело, исполняемое на STA-потоке исполнителя, само
    // вызывает Invoke на этом же исполнителе — наивная очередь с одним обработчиком
    // встала бы намертво (рабочий поток ждал бы сам себя, а обработать свой же
    // элемент очереди уже некому). Решено явной проверкой: Invoke распознаёт, что
    // текущий поток — это и есть STA-поток исполнителя, и в этом случае выполняет
    // тело немедленно, в обход очереди.
    class StaExecutor
    {
        readonly object gate = new object();
        readonly Queue<Action> queue = new Queue<Action>();
        readonly Thread thread;
        bool shuttingDown;

        public StaExecutor()
        {
            thread = new Thread(RunLoop);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Invoke(Action body)
        {
            // Явный аргумент типа — вывод типа из тела анонимного метода (delegate {})
            // по возвращаемому значению в C# не определён (в отличие от лямбд), поэтому
            // не полагаемся на выведение TR.
            Invoke<object>(delegate
            {
                body();
                return null;
            });
        }

        public TR Invoke<TR>(Func<TR> body)
        {
            if (Thread.CurrentThread == thread)
            {
                return body();
            }

            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                TR result = default(TR);
                Exception error = null;

                lock (gate)
                {
                    if (shuttingDown)
                    {
                        throw new ObjectDisposedException("StaExecutor", "исполнитель уже остановлен");
                    }

                    queue.Enqueue(delegate
                    {
                        try
                        {
                            result = body();
                        }
                        catch (Exception e)
                        {
                            error = e;
                        }
                        finally
                        {
                            done.Set();
                        }
                    });
                    Monitor.PulseAll(gate);
                }

                done.Wait();

                if (error != null)
                {
                    // Перебрасываем исходное исключение как есть вызывающему —
                    // тип и сообщение сохраняются, теряется только тот кусок стека,
                    // что был внутри STA-потока (ExceptionDispatchInfo появился
                    // только в .NET 4.5, здесь недоступен).
                    throw error;
                }
                return result;
            }
        }

        public void Shutdown()
        {
            // Тот же приём, что и в Invoke: если Shutdown позвали изнутри тела,
            // исполняемого на самом STA-потоке, Join() этого потока самого себя
            // заблокировал бы навсегда. Помечаем остановку и выходим — поток
            // завершится сам, как только текущая работа отработает.
            bool onOwnThread = Thread.CurrentThread == thread;

            lock (gate)
            {
                if (shuttingDown)
                {
                    return;
                }
                shuttingDown = true;
                Monitor.PulseAll(gate);
            }

            if (!onOwnThread)
            {
                thread.Join();
            }
        }

        void RunLoop()
        {
            while (true)
            {
                Action work;
                lock (gate)
                {
                    while (queue.Count == 0 && !shuttingDown)
                    {
                        Monitor.Wait(gate);
                    }
                    if (queue.Count == 0 && shuttingDown)
                    {
                        return;
                    }
                    work = queue.Dequeue();
                }
                work();
            }
        }
    }
}
