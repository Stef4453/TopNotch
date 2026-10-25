using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopNotch
{
    public partial class SettingsWindow : Window
    {
        internal const string ApiKeysPage = "API Keys";

        internal const string AboutPage = "About";

        private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _ready;

        private readonly DispatcherTimer _keyTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private string _committedKey = string.Empty;

        public SettingsWindow()
        {
            InitializeComponent();

            Card24.IsChecked = Settings.Current.Use24HourClock;
            Card12.IsChecked = !Settings.Current.Use24HourClock;

            CardNotch.IsChecked = Settings.Current.IslandStyle == IslandStyle.Notch;
            CardIsland.IsChecked = Settings.Current.IslandStyle == IslandStyle.DynamicIsland;
            OutlineSwitch.IsChecked = Settings.Current.IslandOutline;
            FullscreenSwitch.IsChecked = Settings.Current.HideInFullscreen;

            LaunchCheckSwitch.IsChecked = Settings.Current.CheckForUpdatesOnLaunch;
            BetaSwitch.IsChecked = Settings.Current.IncludePrereleaseUpdates;
            AboutVersion.Text = $"Version {UpdateCheck.CurrentLabel}";

            if (UpdateCheck.Last is { } last) ShowUpdateResult(last);

            _committedKey = Settings.Current.ImgBbApiKey;
            ApiKeyBox.Text = _committedKey;
            _ready = true;

            UpdateSamples();
            UpdateKeyState();

            _sampleTimer.Tick += (_, _) => UpdateSamples();
            _sampleTimer.Start();

            _keyTimer.Tick += (_, _) =>
            {
                _keyTimer.Stop();
                CommitApiKey();
            };

            Closed += (_, _) =>
            {
                CommitApiKey();

                _sampleTimer.Stop();
                _keyTimer.Stop();
            };
        }

        private IEnumerable<(string Name, UIElement Pane)> Panes
        {
            get
            {
                yield return ("General", GeneralPane);
                yield return (ApiKeysPage, ApiKeysPane);
                yield return ("About", AboutPane);
            }
        }

        private UIElement? PaneFor(string? page)
        {
            foreach ((string name, UIElement pane) in Panes)
            {
                if (name == page) return pane;
            }

            return null;
        }

        private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageTitle is null) return;

            string page = (Pages.SelectedItem as ListBoxItem)?.Tag as string ?? string.Empty;
            PageTitle.Text = page;

            foreach ((string name, UIElement pane) in Panes)
            {
                pane.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            }

            if (page == AboutPage && !_checking && UpdateCheck.Last is { } last)
            {
                ShowUpdateResult(last);
            }
        }

        internal void OpenPage(string page)
        {
            foreach (object item in Pages.Items)
            {
                if (item is not ListBoxItem row || (row.Tag as string) != page) continue;

                Pages.SelectedItem = row;
                if (page == ApiKeysPage) ApiKeyBox.Focus();
                return;
            }
        }

        private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateKeyHint();
            _keyTimer.Stop();
            _keyTimer.Start();
        }

        private void ApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _keyTimer.Stop();
            CommitApiKey();
        }

        private void UpdateKeyHint() =>
            ApiKeyHint.Visibility = ApiKeyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void UpdateKeyState() =>
            ApiKeyState.Text = Settings.Current.HasImgBbApiKey
                ? "Set. The Share Image tile uploads with this key."
                : "Not set. Dropping an image on the notch will ask for one first.";

        private void CommitApiKey()
        {
            if (ApiKeyBox.Text.Trim() == _committedKey) return;

            Settings.Current.Set(() => Settings.Current.ImgBbApiKey = ApiKeyBox.Text);

            _committedKey = Settings.Current.ImgBbApiKey;
            if (ApiKeyBox.Text != _committedKey) ApiKeyBox.Text = _committedKey;

            UpdateKeyState();
        }

        private void OpenApiPage_Click(object sender, RoutedEventArgs e) => OpenUrl("https://api.imgbb.com/");

        private string? _newerUrl;

        private bool _checking;

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_checking) return;

            _checking = true;
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = "Checking\u2026";
            DownloadButton.Visibility = Visibility.Collapsed;
            UpdateState.Text = "Asking GitHub\u2026";

            try
            {
                ShowUpdateResult(await UpdateCheck.RunAsync(
                    Settings.Current.IncludePrereleaseUpdates));
            }
            finally
            {
                CheckUpdatesButton.Content = "Check Now";
                CheckUpdatesButton.IsEnabled = true;
                _checking = false;
            }
        }

        private void ShowUpdateResult(UpdateCheck.Result result)
        {
            AboutVersion.Text = $"Version {UpdateCheck.CurrentLabel}";

            _newerUrl = result.Url;
            DownloadButton.Visibility = result.Status == UpdateCheck.Status.Newer
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateState.Text = result.Status switch
            {
                UpdateCheck.Status.Newer =>
                    $"Version {result.Version} is available. This copy is {UpdateCheck.CurrentLabel}.",
                UpdateCheck.Status.UpToDate =>
                    $"TopNotch {UpdateCheck.CurrentLabel} is the newest version.",
                UpdateCheck.Status.NoReleases =>
                    "Nothing has been published yet, so this copy is the newest there is.",
                _ =>
                    "Couldn't reach GitHub. Check the connection and try again."
            };
        }

        private void DownloadUpdate_Click(object sender, RoutedEventArgs e) =>
            OpenUrl(_newerUrl ?? UpdateCheck.Page);

        private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateCheck.Page);

        private void Beta_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            Settings.Current.Set(() => Settings.Current.IncludePrereleaseUpdates =
                BetaSwitch.IsChecked == true);

            if (UpdateCheck.Last is not null) CheckUpdates_Click(this, new RoutedEventArgs());
        }

        private void LaunchCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.CheckForUpdatesOnLaunch =
                LaunchCheckSwitch.IsChecked == true);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void UpdateSamples()
        {
            var now = DateTime.Now;
            string date = now.ToString("d MMMM", CultureInfo.InvariantCulture);

            Sample12Clock.Text = now.ToString("h:mm", CultureInfo.InvariantCulture);
            Sample12Meridiem.Text = " " + now.ToString("tt", CultureInfo.InvariantCulture);
            Sample24Clock.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
            Sample12Date.Text = date;
            Sample24Date.Text = date;
        }

        private void Format_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.Use24HourClock = ReferenceEquals(sender, Card24));
        }

        private void Style_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.IslandStyle =
                ReferenceEquals(sender, CardIsland) ? IslandStyle.DynamicIsland : IslandStyle.Notch);
        }

        private void Outline_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.IslandOutline = OutlineSwitch.IsChecked == true);
        }

        private void Fullscreen_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.HideInFullscreen = FullscreenSwitch.IsChecked == true);
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            ListBoxItem? firstMatch = null;

            foreach (object item in Pages.Items)
            {
                if (item is not ListBoxItem row) continue;

                bool show = query.Length == 0 || PageMatches(row, query);
                row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                if (show) firstMatch ??= row;
            }

            NoPages.Visibility = firstMatch is null ? Visibility.Visible : Visibility.Collapsed;

            if (firstMatch is not null && Pages.SelectedItem is ListBoxItem current
                && current.Visibility != Visibility.Visible)
            {
                Pages.SelectedItem = firstMatch;
            }
        }

        private bool PageMatches(ListBoxItem row, string query)
        {
            if ((row.Tag as string)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return PaneFor(row.Tag as string) is { } pane
                   && PageText(pane).Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private static string PageText(DependencyObject root)
        {
            var text = new StringBuilder();
            Gather(root, text);
            return text.ToString();

            static void Gather(DependencyObject node, StringBuilder into)
            {
                switch (node)
                {
                    case TextBlock block:
                        into.Append(' ').Append(block.Text);
                        break;
                    case TextBox box:
                        into.Append(' ').Append(box.Text);
                        break;
                    case ContentControl { Content: string label }:
                        into.Append(' ').Append(label);
                        break;
                }

                int children = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < children; i++)
                {
                    Gather(VisualTreeHelper.GetChild(node, i), into);
                }
            }
        }
    }
}
