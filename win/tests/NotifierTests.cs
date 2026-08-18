using System;
using System.Threading;
using System.Windows.Forms;

namespace LanClip.Tests
{
    // TrayNotifier (Notifier.cs) маршалит Error()/Info() на поток-владелец
    // NotifyIcon через Control.Invoke — находка I11 финального ревью: раньше эти
    // методы вызывались напрямую из потока пула (обработчик POST /pull) и из
    // фонового потока обработчика хоткея, а NotifyIcon создан на главном потоке и
    // завязан на его цикл сообщений — кросс-поточный вызов WinForms-компонента
    // является неопределённым поведением.
    //
    // TrayNotifier целиком здесь НЕ тестируется: его конструктор создаёт
    // настоящий NotifyIcon с Visible=true, и прогон теста показал бы реальную
    // иконку/балун на рабочем столе Павла. Вместо этого проверяется сам
    // механизм маршалинга (Control.Invoke на выделенном потоке со своим циклом
    // сообщений), от которого зависит TrayNotifier.Show() — если этот
    // механизм работает, значит и починка I11 работает, а показывать реальный
    // балун для доказательства этого не нужно.
    static class NotifierTests
    {
        public static void Register()
        {
            T.Run("Control.Invoke marshals a delegate onto the thread that owns its handle", delegate
            {
                int ownerThreadId = 0;
                Control control = null;
                ManualResetEventSlim ready = new ManualResetEventSlim(false);

                Thread ownerThread = new Thread(new ThreadStart(delegate
                {
                    control = new Control();
                    control.CreateControl();
                    ownerThreadId = Thread.CurrentThread.ManagedThreadId;
                    ready.Set();
                    Application.Run();
                }));
                ownerThread.IsBackground = true;
                ownerThread.SetApartmentState(ApartmentState.STA);
                ownerThread.Start();

                if (!ready.Wait(5000))
                {
                    T.True(false, "owner thread did not start its message loop in time");
                    return;
                }

                bool invokeRequiredFromOutside = control.InvokeRequired;
                int observedThreadId = -1;
                control.Invoke(new Action(delegate
                {
                    observedThreadId = Thread.CurrentThread.ManagedThreadId;
                }));

                T.True(invokeRequiredFromOutside,
                    "calling Invoke from a thread other than the owner must report InvokeRequired == true");
                T.Eq(ownerThreadId, observedThreadId,
                    "the delegate must run on the control's owner thread, not on the calling (test) thread");

                control.Invoke(new Action(delegate { Application.ExitThread(); }));
                ownerThread.Join(5000);
            });
        }
    }
}
