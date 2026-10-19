using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TopNotch
{
    /// <summary>
    /// The app's presence in the notification area: a placeholder icon with a right-click menu.
    /// It is built directly on Shell_NotifyIcon rather than through WinForms' NotifyIcon, to keep
    /// the widget's only extra dependency to a placeholder .ico.
    ///
    /// The menu is the only piece of the app that lives outside the island, so anything that is
    /// not the widget itself belongs here - quitting today, settings next.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        // Posted to our window when the icon is clicked. WM_APP + 1 is the conventional choice,
        // and the shell hands us the mouse message in the low word of lParam.
        private const int WM_TRAY = 0x8000 + 1;

        private const int WM_NULL = 0x0000;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const int NIM_ADD = 0x0000;
        private const int NIM_DELETE = 0x0002;

        private const int NIF_MESSAGE = 0x0001;
        private const int NIF_ICON = 0x0002;
        private const int NIF_TIP = 0x0004;

        private const int MF_STRING = 0x0000;
        private const int MF_SEPARATOR = 0x0800;

        private const int TPM_RIGHTBUTTON = 0x0002;
        private const int TPM_NONOTIFY = 0x0080;
        private const int TPM_RETURNCMD = 0x0100;

        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        private readonly HwndSource _sink;
        private readonly System.Drawing.Icon _icon;   // kept alive: the shell holds its handle
        private readonly List<MenuItem> _items = new();
        private readonly int _taskbarCreated;
        private NOTIFYICONDATA _data;
        private bool _disposed;

        /// <summary>A menu row: text and its command, or a separator, which has neither.</summary>
        private sealed record MenuItem(string? Text, Action? Handler)
        {
            public static MenuItem Separator { get; } = new(null, null);
        }

        public TrayIcon()
        {
            // A window of our own to receive the shell's callback. It is never shown; being a
            // real top-level window rather than a message-only one is what lets it see the
            // TaskbarCreated broadcast, so the icon can put itself back if Explorer restarts.
            _sink = new HwndSource(new HwndSourceParameters("TopNotchTray")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0
            });
            _sink.AddHook(WndProc);

            _icon = LoadIcon(GetSystemMetrics(SM_CXSMICON));

            _data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _sink.Handle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _icon.Handle,
                szTip = "TopNotch"
            };

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            Shell_NotifyIcon(NIM_ADD, ref _data);
        }

        /// <summary>
        /// Adds a command to the menu, in the order added. Kept open-ended on purpose: the menu
        /// is built fresh every time it is shown, so items can be added or changed at any point.
        /// </summary>
        public void AddItem(string text, Action handler) => _items.Add(new MenuItem(text, handler));

        /// <summary>A hairline between groups of commands.</summary>
        public void AddSeparator() => _items.Add(MenuItem.Separator);

        /// <summary>Raised when the icon itself is double-clicked, which is how the settings
        /// window is normally opened.</summary>
        public event Action? DoubleClicked;

        // The placeholder logo, loaded at the size the tray actually asks for so it is not
        // rescaled into mush. Giving the app a real logo is a matter of replacing that one file;
        // nothing here assumes anything about what it draws.
        private static System.Drawing.Icon LoadIcon(int size)
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Sprites/tray.ico"))
                           ?? throw new InvalidOperationException("Sprites/tray.ico is missing.");
            return new System.Drawing.Icon(resource.Stream, size, size);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                // Explorer restarted and took our icon with it.
                Shell_NotifyIcon(NIM_ADD, ref _data);
                handled = true;
            }
            else if (msg == WM_TRAY)
            {
                int notification = (int)(lParam.ToInt64() & 0xFFFF);

                if (notification is WM_RBUTTONUP or WM_CONTEXTMENU)
                {
                    ShowMenu();
                    handled = true;
                }
                else if (notification == WM_LBUTTONDBLCLK)
                {
                    DoubleClicked?.Invoke();
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            try
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    AppendMenu(menu, item.Text is null ? MF_SEPARATOR : MF_STRING, i + 1, item.Text);
                }

                GetCursorPos(out POINT cursor);

                // Windows dismisses a menu by giving it the foreground, and only then: without
                // this the menu can survive a click somewhere else on screen.
                SetForegroundWindow(_sink.Handle);
                int chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                    cursor.x, cursor.y, _sink.Handle, IntPtr.Zero);
                PostMessage(_sink.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (chosen > 0 && chosen <= _items.Count && _items[chosen - 1].Handler is { } handler)
                {
                    // Handed back to the dispatcher rather than run here: quitting from inside
                    // the menu's own modal loop would tear the app down while Windows is still
                    // unwinding it.
                    _sink.Dispatcher.BeginInvoke(handler);
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _icon.Dispose();
            _sink.RemoveHook(WndProc);
            _sink.Dispose();
        }

        // ---- Notification area ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, int uFlags, IntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, int uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string lpString);
    }
}
