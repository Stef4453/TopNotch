using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TopNotch
{
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

            _tray = new TrayIcon();
            _tray.DoubleClicked += () => ShowSettings();
            _tray.AddItem("Settings\u2026", () => ShowSettings());
            _tray.AddSeparator();
            _tray.AddItem("Quit", Quit);

            if (Settings.IsFirstRun)
            {
                var setup = new SetupWindow();
                setup.ShowDialog();
            }
        }

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

            BringToFront(_settings);

            if (page is not null) _settings.OpenPage(page);
        }

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

        internal bool DeleteAllData()
        {
            try
            {
                if (Directory.Exists(Settings.Folder)) Directory.Delete(Settings.Folder, true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal bool Restart()
        {
            string? path = Environment.ProcessPath;
            if (path is null) return false;

            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }

            _island?.AllowShutdown();
            Shutdown();
            return true;
        }

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
