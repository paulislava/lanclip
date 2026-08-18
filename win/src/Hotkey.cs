using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LanClip
{
    class HotkeyException : Exception
    {
        public HotkeyException(string message)
            : base(message)
        {
        }
    }

    // Глобальный хоткей Ctrl+Shift+V через Win32 RegisterHotKey на скрытом окне-приёмнике
    // сообщений. Зеркало mac/Sources/lanclipd/MacHotkey.swift (там — Carbon
    // RegisterEventHotKey на RunLoop.current; здесь тот же принцип на цикле сообщений
    // Windows). Register() создаёт HWND, но не запускает цикл сообщений сам — это
    // ответственность вызывающей стороны (Program.cs вызывает Application.Run() уже
    // после Register()), иначе WM_HOTKEY никогда не будет доставлено ни на одном окне
    // процесса, включая и это.
    class Hotkey : IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 1;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_V = 0x56;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly Action onPress;
        readonly Receiver receiver;
        bool registered;

        public Hotkey(Action onPress)
        {
            this.onPress = onPress;
            receiver = new Receiver(this);
        }

        // Создаёт скрытое окно (CreateParams по умолчанию не выставляет WS_VISIBLE —
        // оно никогда не показывается и не участвует в Alt-Tab/панели задач) и
        // регистрирует на нём Ctrl+Shift+V. Вызывать ровно один раз за время жизни
        // экземпляра — как и на Mac-стороне, идемпотентности не гарантирует.
        public void Register()
        {
            CreateParams cp = new CreateParams();
            receiver.CreateHandle(cp);

            if (!RegisterHotKey(receiver.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT, VK_V))
            {
                int error = Marshal.GetLastWin32Error();
                throw new HotkeyException(
                    "не удалось зарегистрировать глобальный хоткей Ctrl+Shift+V (код ошибки Win32: " + error + ")");
            }
            registered = true;
        }

        // Снимает хоткей и уничтожает скрытое окно. Безопасно вызывать повторно и
        // без предшествующего успешного Register().
        public void Dispose()
        {
            if (registered)
            {
                UnregisterHotKey(receiver.Handle, HotkeyId);
                registered = false;
            }
            if (receiver.Handle != IntPtr.Zero)
            {
                receiver.DestroyHandle();
            }
        }

        // NativeWindow, а не Form: нужен только HWND под RegisterHotKey и приём
        // WM_HOTKEY — полноценное окно с рамкой/панелью задач не нужно.
        class Receiver : NativeWindow
        {
            readonly Hotkey owner;

            public Receiver(Hotkey owner)
            {
                this.owner = owner;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
                {
                    owner.onPress();
                }
                base.WndProc(ref m);
            }
        }
    }

    // Синтез вставки Ctrl+V через SendInput, с явным сбросом физически зажатых
    // модификаторов перед этим. Зеркало mac/Sources/lanclipd/MacHotkey.swift:
    // synthesizePaste().
    //
    // В момент срабатывания хоткея Ctrl и Shift физически нажаты пользователем — это
    // часть комбинации Ctrl+Shift+V. Если просто послать Ctrl+V поверх них,
    // приложение-получатель увидит Ctrl+Shift+V и, скорее всего, вставит как обычный
    // текст либо вовсе проигнорирует событие — это выглядело бы как «хоткей не
    // работает», хотя сам хоткей сработал и буфер уже наполнен. Поэтому сперва явно
    // посылается keyUp для VK_CONTROL/VK_LCONTROL/VK_RCONTROL и
    // VK_SHIFT/VK_LSHIFT/VK_RSHIFT (общий виртуальный код клавиши и оба конкретных —
    // на случай, если приёмник следит именно за левой/правой клавишей отдельно), даётся
    // пауза 20мс, чтобы приёмник успел обработать снятие модификаторов, и только затем
    // идёт Ctrl down, V down, V up, Ctrl up.
    static class Paste
    {
        const int VK_CONTROL = 0x11;
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;
        const int VK_SHIFT = 0x10;
        const int VK_LSHIFT = 0xA0;
        const int VK_RSHIFT = 0xA1;
        const int VK_V = 0x56;

        const int ModifierResetDelayMs = 20;

        const int INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void Send()
        {
            // Сброс физически зажатых модификаторов — см. комментарий класса.
            SendKeyUp(VK_CONTROL);
            SendKeyUp(VK_LCONTROL);
            SendKeyUp(VK_RCONTROL);
            SendKeyUp(VK_SHIFT);
            SendKeyUp(VK_LSHIFT);
            SendKeyUp(VK_RSHIFT);

            Thread.Sleep(ModifierResetDelayMs);

            SendKeyDown(VK_CONTROL);
            SendKeyDown(VK_V);
            SendKeyUp(VK_V);
            SendKeyUp(VK_CONTROL);
        }

        static void SendKeyDown(int vk)
        {
            SendKeyEvent(vk, 0);
        }

        static void SendKeyUp(int vk)
        {
            SendKeyEvent(vk, KEYEVENTF_KEYUP);
        }

        static void SendKeyEvent(int vk, uint flags)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0] = new INPUT();
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = (ushort)vk;
            inputs[0].u.ki.wScan = 0;
            inputs[0].u.ki.dwFlags = flags;
            inputs[0].u.ki.time = 0;
            inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;

            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 1)
            {
                // SendInput best-effort: если система заблокировала синтетический ввод
                // (например, UIPI — процесс с более высоким уровнем целостности перехватил
                // фокус), продолжать посылать остальные события всё равно правильнее, чем
                // бросать исключение на середине последовательности модификаторов — иначе
                // Ctrl/Shift могли бы остаться зажатыми синтетически без парного keyUp.
            }
        }
    }
}
