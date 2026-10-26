using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopNotch
{
    public partial class SetupWindow : Window
    {
        private static readonly Brush OutlineEdge = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D));

        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _ready;

        public SetupWindow()
        {
            InitializeComponent();

            CardNotch.IsChecked = Settings.Current.IslandStyle == IslandStyle.Notch;
            CardIsland.IsChecked = Settings.Current.IslandStyle == IslandStyle.DynamicIsland;
            OutlineSwitch.IsChecked = Settings.Current.IslandOutline;
            ClockSwitch.IsChecked = Settings.Current.Use24HourClock;
            FullscreenSwitch.IsChecked = Settings.Current.HideInFullscreen;

            _ready = true;

            UpdatePreview();
            UpdateClock();

            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();

            Closed += (_, _) => _clockTimer.Stop();
        }

        private void Style_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            IslandStyle style = CardIsland.IsChecked == true
                ? IslandStyle.DynamicIsland
                : IslandStyle.Notch;

            Settings.Current.Set(() => Settings.Current.IslandStyle = style);
            UpdatePreview();
        }

        private void Outline_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            Settings.Current.Set(() => Settings.Current.IslandOutline = OutlineSwitch.IsChecked == true);
            UpdatePreview();
        }

        private void Clock_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            Settings.Current.Set(() => Settings.Current.Use24HourClock = ClockSwitch.IsChecked == true);
            UpdateClock();
        }

        private void Fullscreen_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            Settings.Current.Set(() => Settings.Current.HideInFullscreen =
                FullscreenSwitch.IsChecked == true);
        }

        private void UpdatePreview()
        {
            bool floating = Settings.Current.IslandStyle == IslandStyle.DynamicIsland;
            bool outline = Settings.Current.IslandOutline;

            PreviewIsland.CornerRadius = floating
                ? new CornerRadius(16)
                : new CornerRadius(0, 0, 20, 20);

            PreviewIsland.Margin = floating ? new Thickness(0, 10, 0, 0) : new Thickness(0);
            PreviewIsland.BorderThickness = outline ? new Thickness(1) : new Thickness(0);
            PreviewIsland.BorderBrush = outline ? OutlineEdge : null;
        }

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            bool military = Settings.Current.Use24HourClock;

            PreviewTime.Text = now.ToString(military ? "HH:mm" : "h:mm",
                CultureInfo.InvariantCulture);

            PreviewMeridiem.Text = military
                ? string.Empty
                : " " + now.ToString("tt", CultureInfo.InvariantCulture);

            PreviewDate.Text = now.ToString("d MMMM", CultureInfo.InvariantCulture);
        }

        private void Finish_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
