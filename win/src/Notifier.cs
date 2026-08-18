using System;
using System.Drawing;
using System.Windows.Forms;

namespace LanClip
{
    // Абстракция над уведомлением пользователя об ошибках и статусе. Зеркало
    // mac/Sources/LanClipCore/Notifier.swift: Notifying.
    interface INotifier
    {
        void Error(string message);
        void Info(string message);
    }

    class NullNotifier : INotifier
    {
        public void Error(string message)
        {
        }

        public void Info(string message)
        {
        }
    }

    // INotifier поверх NotifyIcon.ShowBalloonTip — иконка в системном трее.
    //
    // В отличие от Mac (osascript-процесс на каждый вызов — там нет постоянного
    // "приложения" с иконкой), здесь держим один NotifyIcon на всё время жизни
    // serve: балун без видимой иконки в трее либо не покажется вовсе, либо
    // покажется без опознавательного значка. NotifyIcon и его балуны — обычные
    // WinForms-объекты и требуют, чтобы поток, создавший этот экземпляр, прогонял
    // цикл сообщений Windows (Application.Run() в Program.cs) — без него события
    // показа/скрытия балуна не доставляются.
    //
    // Находка I11 финального ревью: Error()/Info() вызываются из потока пула
    // (обработчик POST /pull в HttpServer) и из фонового потока обработчика
    // хоткея (Program.cs) — оба НЕ являются главным потоком, на котором создан
    // NotifyIcon и крутится Application.Run(). Кросс-поточный вызов WinForms-
    // компонента — неопределённое поведение (в лучшем случае тихо не сработает,
    // в худшем — уронит процесс), а на ПК это единственный канал сообщений об
    // ошибке (консоль скрыта у serve): отказ синтеза вставки или сети сообщается
    // пользователю именно балуном. marshalTarget — невидимый Control, чей HWND
    // создаётся в конструкторе (то есть на главном потоке, до Application.Run()) —
    // Control.Invoke маршалит вызов на поток-владелец через оконное сообщение,
    // тот же механизм, что использует WinForms для любого кросс-поточного доступа
    // к своим объектам.
    class TrayNotifier : INotifier, IDisposable
    {
        const int BalloonTimeoutMs = 5000;

        readonly NotifyIcon icon;
        readonly Control marshalTarget;

        public TrayNotifier()
        {
            marshalTarget = new Control();
            marshalTarget.CreateControl();

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Application;
            icon.Text = "lanclip";
            icon.Visible = true;
        }

        public void Error(string message)
        {
            Show("lanclip — ошибка", message, ToolTipIcon.Error);
        }

        public void Info(string message)
        {
            Show("lanclip", message, ToolTipIcon.Info);
        }

        void Show(string title, string message, ToolTipIcon kind)
        {
            if (marshalTarget.InvokeRequired)
            {
                marshalTarget.Invoke(new Action(delegate { ShowOnOwnerThread(title, message, kind); }));
                return;
            }
            ShowOnOwnerThread(title, message, kind);
        }

        void ShowOnOwnerThread(string title, string message, ToolTipIcon kind)
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = message;
            icon.BalloonTipIcon = kind;
            icon.ShowBalloonTip(BalloonTimeoutMs);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            marshalTarget.Dispose();
        }
    }
}
