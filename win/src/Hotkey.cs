using System;
using System.Collections.Generic;
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

    // SendInput отклонил хотя бы одно синтетическое событие клавиатуры — буфер уже
    // наполнен (Pull() к этому моменту отработал), но нажатие клавиш физически не
    // дошло. Отдельный тип, а не молчаливое поглощение (см. ревью задачи 24): вызов
    // без этого исключения выглядел бы как "хоткей не работает" без единой зацепки,
    // почему — ровно то, что этот проект ловит на каждом шагу для сетевых ошибок.
    class PasteException : Exception
    {
        public PasteException(string message)
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
    // работает», хотя сам хоткей сработал и буфер уже наполнен.
    //
    // Поэтому агент ЖДЁТ, пока пользователь сам отпустит Ctrl и Shift, и только затем
    // посылает правый Ctrl down, V down, V up, правый Ctrl up (почему именно правый —
    // см. комментарий в Send()). Подделывать отпускание нельзя:
    // приложение ведёт свой учёт модификаторов по потоку событий, и искусственный
    // keyUp для клавиши, которую человек держит, оставляет его в состоянии «Ctrl
    // отпущен» навсегда — парного keyDown уже не будет. Следующий Ctrl+A становится
    // для приложения обычной «a». Проверяются и общий виртуальный код, и оба
    // конкретных: ремапы клавиш (у Павла в PowerToys левый Ctrl и левый Alt поменяны
    // местами) делают проверку только общего кода недостаточной.
    static class Paste
    {
        const int VK_CONTROL = 0x11;
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;
        const int VK_SHIFT = 0x10;
        const int VK_LSHIFT = 0xA0;
        const int VK_RSHIFT = 0xA1;
        const int VK_V = 0x56;

        // Сколько ждать, пока пользователь сам отпустит модификаторы. Полторы
        // секунды: нажатие хоткея и отпускание клавиш разделены реакцией человека,
        // а сам pull к этому моменту уже завершён (он идёт до синтеза).
        const int ModifierReleaseTimeoutMs = 1500;
        const int ModifierPollIntervalMs = 15;

        // Небольшая пауза после того, как модификаторы отпущены: приложение должно
        // успеть обработать пришедшие keyUp прежде, чем получит наш Ctrl+V.
        const int AfterReleaseSettleMs = 20;

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

        // Зеркало нативного MOUSEINPUT — не используется этим кодом напрямую (мы шлём
        // только клавиатурный ввод), но обязана присутствовать в union ниже: реальный
        // Win32 INPUT — это union из MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT, а её размер
        // (наибольший среди трёх на x64 — 32 байта) и определяет истинный размер INPUT
        // (40 байт на x64). Без неё Marshal.SizeOf(typeof(INPUT)) считал бы union по
        // одному только KEYBDINPUT (24 байта) и отдавал бы в cbSize заниженное число —
        // SendInput документированно отклоняет вызов целиком, если cbSize не совпадает
        // с реальным размером структуры (найдено ревью задачи 24: каждый вызов из
        // Send() отклонялся молча).
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Зеркало нативного HARDWAREINPUT — как и MOUSEINPUT, не используется напрямую,
        // нужна только для того, чтобы union целиком совпадал с реальным Win32 INPUT.
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        // Бросает PasteException, если SendInput отклонил хотя бы одно из десяти
        // событий последовательности — но только после того, как вся
        // последовательность целиком отправлена (см. TrySendKeyEvent): обрыв на
        // первом отказе мог бы оставить Ctrl/Shift синтетически зажатыми без
        // парного keyUp, а это хуже, чем ошибка, о которой узнают постфактум.
        public static void Send()
        {
            List<string> failures = new List<string>();

            // Ждём, пока пользователь сам отпустит Ctrl и Shift, вместо того чтобы
            // подделывать их отпускание.
            //
            // Прежняя версия посылала keyUp для модификаторов, которые пользователь
            // физически держит. Это ложь о состоянии клавиатуры, и она обходится
            // дорого: приложение ведёт свой учёт модификаторов по потоку событий,
            // получает «Ctrl отпущен», а парного keyDown не получит никогда — клавишу
            // ведь не отпускали. Дальше приложение считает Ctrl отпущенным, и
            // следующий Ctrl+A превращается для него в обычную «a». Именно так у
            // Павла и ломался Ctrl+A после каждой попытки вставки.
            //
            // Ждать здесь можно спокойно: pull к этому моменту уже завершён, и
            // задержка равна реакции человека, а не сети.
            if (!WaitForModifiersRelease())
            {
                throw new PasteException(
                    "Ctrl и Shift всё ещё зажаты через " + ModifierReleaseTimeoutMs
                    + " мс — вставка не выполнена, чтобы не рассылать ложное состояние клавиш. "
                    + "Буфер обновлён, вставьте вручную (Ctrl+V).");
            }

            Thread.Sleep(AfterReleaseSettleMs);

            // Синтезируем ПРАВЫМ Ctrl, а не общим кодом и не левым.
            //
            // Причина конкретная и проверена на этой машине. В PowerToys у Павла
            // Keyboard Manager меняет местами левый Ctrl и левый Alt:
            //     162 (LCONTROL) -> 164 (LMENU)
            //     164 (LMENU)    -> 162 (LCONTROL)
            // Общий VK_CONTROL при инъекции превращается драйвером в левый Ctrl, тот
            // попадает под ремап и доходит до приложения как Alt. Пользователь видел
            // ровно это: буфер наполняется, а вставки нет, потому что вместо Ctrl+V
            // приложение получало Alt+V.
            //
            // Правый Ctrl (163) в таблице ремапов не встречается, поэтому проходит
            // хук нетронутым. Важно, что это работает и с включённым Диспетчером
            // клавиатуры, и с выключенным — в отличие от «синтезировать Alt+V»,
            // которое опирается на наличие ремапа и ломается, когда его отключают.
            TrySendKeyDown(VK_RCONTROL, failures);
            TrySendKeyDown(VK_V, failures);
            TrySendKeyUp(VK_V, failures);
            TrySendKeyUp(VK_RCONTROL, failures);

            if (failures.Count > 0)
            {
                throw new PasteException("SendInput отклонил " + failures.Count
                    + " из 10 синтетических событий клавиатуры: " + string.Join("; ", failures.ToArray()));
            }
        }

        // true — модификаторы отпущены (или их и не держали), false — истёк таймаут.
        // Проверяются и общий виртуальный код, и оба конкретных: раскладка и
        // возможные ремапы клавиш (например, Keyboard Manager из PowerToys, где у
        // Павла левый Ctrl и левый Alt поменяны местами) делают проверку только
        // общего кода недостаточной.
        static bool WaitForModifiersRelease()
        {
            int waited = 0;
            while (waited <= ModifierReleaseTimeoutMs)
            {
                if (!AnyModifierDown()) { return true; }
                Thread.Sleep(ModifierPollIntervalMs);
                waited += ModifierPollIntervalMs;
            }
            return !AnyModifierDown();
        }

        static bool AnyModifierDown()
        {
            return IsDown(VK_CONTROL) || IsDown(VK_LCONTROL) || IsDown(VK_RCONTROL)
                || IsDown(VK_SHIFT) || IsDown(VK_LSHIFT) || IsDown(VK_RSHIFT);
        }

        // GetAsyncKeyState отражает физическое состояние клавиши, а не состояние
        // очереди сообщений — именно это нам и нужно: мы ждём, пока человек реально
        // разожмёт пальцы.
        static bool IsDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        static void TrySendKeyDown(int vk, List<string> failures)
        {
            TrySendKeyEvent(vk, 0, failures);
        }

        static void TrySendKeyUp(int vk, List<string> failures)
        {
            TrySendKeyEvent(vk, KEYEVENTF_KEYUP, failures);
        }

        static void TrySendKeyEvent(int vk, uint flags, List<string> failures)
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
                // SendInput документированно отклоняет вызов целиком (возвращает 0), если
                // cbSize не совпадает с реальным размером структуры, а также может отклонить
                // отдельные события из-за UIPI (более защищённый процесс на переднем плане) —
                // в обоих случаях считаем это отказом и продолжаем последовательность (не
                // бросаем здесь же), а копим причину для Send(), который решает, поднимать
                // ли исключение только после того, как отправлено всё до конца.
                int error = Marshal.GetLastWin32Error();
                string direction = (flags & KEYEVENTF_KEYUP) != 0 ? "up" : "down";
                failures.Add("vk=0x" + vk.ToString("X2") + " " + direction + " (Win32 error " + error + ")");
            }
        }
    }
}
