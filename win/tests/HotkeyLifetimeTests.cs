using System;

namespace LanClip.Tests
{
    // Регрессия на молчаливую потерю регистрации хоткея.
    //
    // Дефект: `Hotkey` лежал в локальной переменной, управление уходило в
    // `Application.Run()`, сборщик мусора считал переменную мёртвой, финализатор
    // `NativeWindow` уничтожал HWND — и `RegisterHotKey` снимался при живом
    // процессе. Пользователь видел «хоткей не работает», агент при этом отвечал
    // на HTTP и не сообщал ни о чём.
    //
    // ЧТО ЭТОТ ТЕСТ ПОКРЫВАЕТ, А ЧТО НЕТ. Он проверяет механику: удерживаемая
    // регистрация переживает принудительную сборку с дожиданием финализаторов.
    // Исходный дефект был в другом — в ОТСУТСТВИИ удержания, и воспроизвести его
    // тестом, не запуская сам агент, нельзя. Настоящая защита от регресса —
    // статическое поле `Program.hotkeyHolder`: пока ссылка на объект достижима
    // из корня, финализатор `NativeWindow` не выполнится. Если кто-то вернёт
    // хоткей в локальную переменную, этот тест ничего не заметит — заметит
    // пользователь, у которого хоткей перестанет работать через несколько часов.
    // Занимаем не рабочую Ctrl+Shift+V, а Ctrl+Shift+F24 — чтобы прогон тестов не
    // отбирал хоткей у работающего агента.
    static class HotkeyLifetimeTests
    {
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_F24 = 0x87;
        const int ProbeId = 9931;
        const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        const int ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION = 1459;

        public static void Register()
        {
            T.Run("регистрация хоткея выживает после сборки мусора", delegate
            {
                // Регистрация хоткея требует интерактивной оконной станции. Прогон
                // набора идёт по SSH, а это нулевая сессия — там RegisterHotKey
                // отвечает 1459 (ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION) на любую
                // комбинацию. Проверено: и на Ctrl+Shift+F24, и на Ctrl+Shift+V.
                // Поэтому здесь честно сообщаем, что проверка не выполнялась, а не
                // делаем вид, что прошла. Чтобы прогнать её по-настоящему, запусти
                // lanclip-tests.exe в интерактивной сессии — как это описано в
                // ai/ERRORS.md для тестов буфера.
                if (!Native.RegisterHotKey(IntPtr.Zero, ProbeId + 1, MOD_CONTROL | MOD_SHIFT, VK_F24))
                {
                    int probeErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (probeErr == ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION)
                    {
                        Console.WriteLine("HotkeyLifetimeTests: ПРОПУЩЕН — нужна интерактивная сессия "
                            + "(RegisterHotKey вернул 1459). Запусти lanclip-tests.exe в сессии Павла.");
                        return;
                    }
                }
                else
                {
                    Native.UnregisterHotKey(IntPtr.Zero, ProbeId + 1);
                }

                object holder = MakeAndRegister();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                bool freeAfterGc = Native.RegisterHotKey(IntPtr.Zero, ProbeId, MOD_CONTROL | MOD_SHIFT, VK_F24);
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (freeAfterGc) { Native.UnregisterHotKey(IntPtr.Zero, ProbeId); }

                // Пока ссылка живая, комбинация обязана оставаться занятой.
                T.True(!freeAfterGc, "комбинация занята после сборки мусора (иначе регистрация потеряна)");
                T.Eq(ERROR_HOTKEY_ALREADY_REGISTERED, err, "код ошибки — hotkey already registered");

                GC.KeepAlive(holder);
                ((IDisposable)holder).Dispose();
            });
        }

        static object MakeAndRegister()
        {
            HotkeyProbe probe = new HotkeyProbe();
            probe.Register();
            return probe;
        }
    }

    // Мини-копия механики Hotkey на другой комбинации: нужен ровно тот же
    // жизненный цикл NativeWindow, а не поведение продукта целиком.
    class HotkeyProbe : IDisposable
    {
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_F24 = 0x87;
        const int Id = 9932;

        readonly Receiver receiver = new Receiver();
        bool registered;

        public void Register()
        {
            receiver.CreateHandle(new System.Windows.Forms.CreateParams());
            if (!Native.RegisterHotKey(receiver.Handle, Id, MOD_CONTROL | MOD_SHIFT, VK_F24))
            {
                throw new InvalidOperationException("не удалось занять Ctrl+Shift+F24 для теста");
            }
            registered = true;
        }

        public void Dispose()
        {
            if (registered) { Native.UnregisterHotKey(receiver.Handle, Id); registered = false; }
            if (receiver.Handle != IntPtr.Zero) { receiver.DestroyHandle(); }
        }

        class Receiver : System.Windows.Forms.NativeWindow { }
    }

    static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
