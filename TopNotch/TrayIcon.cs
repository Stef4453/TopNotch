using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TopNotch
{
    internal sealed class TrayIcon : IDisposable
    {
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
        private readonly System.Drawing.Icon _icon;
        private readonly List<MenuItem> _items = new();
        private readonly int _taskbarCreated;
        private NOTIFYICONDATA _data;
        private bool _disposed;

        private sealed record MenuItem(string? Text, Action? Handler)
        {
            public static MenuItem Separator { get; } = new(null, null);
        }

        public TrayIcon()
        {
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

        public void AddItem(string text, Action handler) => _items.Add(new MenuItem(text, handler));

        public void AddSeparator() => _items.Add(MenuItem.Separator);

        public event Action? DoubleClicked;

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

                SetForegroundWindow(_sink.Handle);
                int chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                    cursor.x, cursor.y, _sink.Handle, IntPtr.Zero);
                PostMessage(_sink.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (chosen > 0 && chosen <= _items.Count && _items[chosen - 1].Handler is { } handler)
                {
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
