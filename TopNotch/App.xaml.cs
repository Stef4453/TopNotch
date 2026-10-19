using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TopNotch
{
    /// <summary>
    /// A tray app first and a widget second: the island window is something the app shows, not
    /// something it is. Nothing about closing a window ends the app - only Quit does.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _island;
        private SettingsWindow? _settings;
        private TrayIcon? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _island = new MainWindow();
            MainWindow = _island;
            _island.Show();

            // The tray icon is the app's front door: double-clicking it or its Settings command
            // opens the one window that is not the widget.
            _tray = new TrayIcon();
            _tray.DoubleClicked += () => ShowSettings();
            _tray.AddItem("Settings\u2026", () => ShowSettings());
            _tray.AddSeparator();
            _tray.AddItem("Quit", Quit);
        }

        // One settings window at a time; asking for it again brings the open one forward rather
        // than stacking a second copy of the same settings on top of the first. A page can be
        // named - the island asks for the API keys when a feature needs one it hasn't got - which
        // also moves an already-open window to that page rather than leaving it where it was.
        internal void ShowSettings(string? page = null)
        {
            if (_settings is null)
            {
                _settings = new SettingsWindow();
                _settings.Closed += (_, _) => _settings = null;
                _settings.Show();
            }
            else
            {
                if (_settings.WindowState == WindowState.Minimized)
                    _settings.WindowState = WindowState.Normal;

                _settings.Activate();
            }

            // The window is shown before it is brought forward: Show() is what creates the handle
            // to hand to SetForegroundWindow.
            BringToFront(_settings);

            if (page is not null) _settings.OpenPage(page);
        }

        // A window opened from the island is created behind whatever is in front, because the
        // island is built never to hold the foreground itself - so without this, "Open Settings"
        // would look like it had done nothing at all.
        //
        // Two halves, because they answer different questions. Being the active window and being
        // the window on top are separate states, and a window can be the first without being the
        // second: the Topmost flip is what settles the z-order (a window that has just left the
        // topmost band sits at the head of the ordinary one), and the rest is what hands over the
        // keyboard, which Windows only lets the foreground thread do - hence the two input queues
        // being attached for the moment it takes to ask.
        private static void BringToFront(Window window)
        {
            window.Topmost = true;
            window.Topmost = false;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            uint theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint ours = GetCurrentThreadId();
            bool attach = theirs != 0 && theirs != ours;

            if (attach) AttachThreadInput(theirs, ours, true);
            SetForegroundWindow(handle);
            if (attach) AttachThreadInput(theirs, ours, false);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachTo, uint attach, bool join);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // Quit has to be unconditional. The island talks its way out of a close while the link
        // box is open, and a quit that quietly did nothing would leave the app running with no
        // window and no way back to it.
        private void Quit()
        {
            _island?.AllowShutdown();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            base.OnExit(e);
        }
    }
}
