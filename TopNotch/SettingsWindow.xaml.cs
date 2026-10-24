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
    /// <summary>
    /// The app's settings, in the shape macOS gives its own: a sidebar of pages beside the page
    /// itself. Three pages so far - General, which holds how the clock is written, which shape the
    /// island wears and whether it is outlined; API Keys; and About, where the app's own version
    /// lives and updates are looked for - and the structure is the point: a page is a sidebar row
    /// plus a pane.
    ///
    /// There is no Save button, because there is nothing to confirm. Every change is written to
    /// %LOCALAPPDATA%\TopNotch\settings.json the moment it is made and the app is told, so the
    /// notch has already redrawn itself by the time the window is closed - and the window says
    /// nothing about any of it, which is the point: there is nothing to wait for.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>
        /// The sidebar tag of the API keys page. Named here because the island asks for that page
        /// by name when a feature needs a key it hasn't got, and the name is also the row's Tag in
        /// the markup - the two have to agree.
        /// </summary>
        internal const string ApiKeysPage = "API Keys";

        /// <summary>The sidebar tag of the About page, for the same reason and kept the same way.</summary>
        internal const string AboutPage = "About";

        private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _ready;

        // A text field is the odd one out among the settings, which are all yes/no: there is no
        // moment that says the typing has finished. Writing on every keystroke would rewrite the
        // file once per character, so what is typed is written when it stops - or when the field
        // is left - and _committedKey is what makes the second of those a no-op if the first
        // already happened.
        private readonly DispatcherTimer _keyTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private string _committedKey = string.Empty;

        public SettingsWindow()
        {
            InitializeComponent();

            // Set once the window is standing, so that the handlers can tell the starting state
            // from a choice made by the user.
            Card24.IsChecked = Settings.Current.Use24HourClock;
            Card12.IsChecked = !Settings.Current.Use24HourClock;

            CardNotch.IsChecked = Settings.Current.IslandStyle == IslandStyle.Notch;
            CardIsland.IsChecked = Settings.Current.IslandStyle == IslandStyle.DynamicIsland;
            OutlineSwitch.IsChecked = Settings.Current.IslandOutline;
            FullscreenSwitch.IsChecked = Settings.Current.HideInFullscreen;

            LaunchCheckSwitch.IsChecked = Settings.Current.CheckForUpdatesOnLaunch;
            AboutVersion.Text = $"Version {UpdateCheck.Current}";

            // The start-up check may already have an answer. Showing it here is what keeps the
            // banner's "Settings > About has the details" true even when the banner itself was
            // dismissed, or never appeared because something was fullscreen at the time.
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
                // Closing the window is the other way of leaving the field, and an edit still
                // waiting on its timer would otherwise be lost.
                CommitApiKey();

                _sampleTimer.Stop();
                _keyTimer.Stop();
            };
        }

        // ---- Pages ----

        // The one place that knows which page is which pane. A page is three things that have to
        // agree - a row's Tag, a name and a pane - and two of them are in the markup, so adding one
        // is a row in the sidebar, a pane beside it, and a line here.
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

        // Whichever row is selected names the pane to show. The guard is for the starting
        // selection, which is applied while the XAML is still being read and this half of the
        // window does not exist yet.
        private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageTitle is null) return;

            string page = (Pages.SelectedItem as ListBoxItem)?.Tag as string ?? string.Empty;
            PageTitle.Text = page;

            foreach ((string name, UIElement pane) in Panes)
            {
                pane.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            }

            // The About page always shows the last answer there is, whichever check produced it -
            // and keeps showing it while this window is open, since nothing else changes it.
            if (page == AboutPage && !_checking && UpdateCheck.Last is { } last)
            {
                ShowUpdateResult(last);
            }
        }

        // Moves the sidebar to a named page. Used by the app when a feature has sent someone here
        // for something specific - being put on the page that explains and accepts the missing key,
        // rather than on whatever was looked at last, is the difference between an answer and a
        // treasure hunt. The field is focused for the same reason.
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

        // ---- API keys ----

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

        // A TextBox has no placeholder of its own, so one is drawn over it and taken away as soon
        // as there is anything to read.
        private void UpdateKeyHint() =>
            ApiKeyHint.Visibility = ApiKeyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // What the island will do with what is in the box, said plainly, because the consequence of
        // leaving the field empty is a feature that refuses to run and a prompt about it.
        private void UpdateKeyState() =>
            ApiKeyState.Text = Settings.Current.HasImgBbApiKey
                ? "Set. The Share Image tile uploads with this key."
                : "Not set. Dropping an image on the notch will ask for one first.";

        private void CommitApiKey()
        {
            if (ApiKeyBox.Text.Trim() == _committedKey) return;

            Settings.Current.Set(() => Settings.Current.ImgBbApiKey = ApiKeyBox.Text);

            // The setter trims, so the box is shown what was actually kept: a key pasted with a
            // trailing newline would otherwise stay looking unwritable while being perfectly fine.
            _committedKey = Settings.Current.ImgBbApiKey;
            if (ApiKeyBox.Text != _committedKey) ApiKeyBox.Text = _committedKey;

            UpdateKeyState();
        }

        private void OpenApiPage_Click(object sender, RoutedEventArgs e) => OpenUrl("https://api.imgbb.com/");

        // ---- About ----

        // The last address a check offered, so that the download button has somewhere to send the
        // person who presses it. Null until a newer release has been found.
        private string? _newerUrl;

        // A check is a round trip to GitHub, and the button stays where it is while it happens:
        // this is what stops a second press from starting a second one.
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
                ShowUpdateResult(await UpdateCheck.RunAsync());
            }
            finally
            {
                CheckUpdatesButton.Content = "Check Now";
                CheckUpdatesButton.IsEnabled = true;
                _checking = false;
            }
        }

        // What the check came to, said as a sentence rather than a status: the answer is either a
        // version to go and get, or a reason there is nothing to do. "No releases" is not a
        // failure - it is an app that has not been published yet - so it is not dressed as one.
        private void ShowUpdateResult(UpdateCheck.Result result)
        {
            _newerUrl = result.Url;
            DownloadButton.Visibility = result.Status == UpdateCheck.Status.Newer
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateState.Text = result.Status switch
            {
                UpdateCheck.Status.Newer =>
                    $"Version {result.Version} is available. This copy is {UpdateCheck.Current}.",
                UpdateCheck.Status.UpToDate =>
                    $"TopNotch {UpdateCheck.Current} is the newest version.",
                UpdateCheck.Status.NoReleases =>
                    "Nothing has been published yet, so this copy is the newest there is.",
                _ =>
                    "Couldn't reach GitHub. Check the connection and try again."
            };
        }

        // Nothing is downloaded or installed from here: an unsigned app cannot replace itself
        // without the system rightly objecting, so the update is a release page, opened in whatever
        // browser the machine uses.
        private void DownloadUpdate_Click(object sender, RoutedEventArgs e) =>
            OpenUrl(_newerUrl ?? UpdateCheck.Page);

        private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateCheck.Page);

        // The launch check is the app's own promise rather than this window's, so nothing happens
        // here beyond writing it down: the island reads it when it starts.
        private void LaunchCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.CheckForUpdatesOnLaunch =
                LaunchCheckSwitch.IsChecked == true);
        }

        // Every link on these pages goes the same way: handed to the shell, so that whatever the
        // machine uses for a browser answers. A machine with nothing to hand it to is not worth
        // reporting - the address is written on the page either way.
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

        // The window is drawn dark whatever the system is set to, so the title bar is asked for in
        // dark too, rather than being left light above a black page. Windows 10 has no such
        // attribute: there the call simply fails and the ordinary title bar is drawn.
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

        // Both cards are drawn from the real clock rather than from a fixed sample, so what the
        // two of them show is exactly the choice on offer - including midnight and noon, which
        // are where a wrong answer is easiest to spot.
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
            if (!_ready) return;   // the starting state, not a choice
            Settings.Current.Set(() => Settings.Current.Use24HourClock = ReferenceEquals(sender, Card24));
        }

        // The one setting that changes the island's shape rather than what is written on it. The
        // island is listening and redraws itself the moment this lands.
        private void Style_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.IslandStyle =
                ReferenceEquals(sender, CardIsland) ? IslandStyle.DynamicIsland : IslandStyle.Notch);
        }

        // The outline is a matter of taste rather than of shape: the island keeps where it is and
        // only its edge is redrawn, so this one is safe to flip while it is being looked at.
        private void Outline_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.IslandOutline = OutlineSwitch.IsChecked == true);
        }

        // Whether the island gets out of the way of a fullscreen app. Turning it off lets the island
        // back at once, even if something is fullscreen at that moment - the switch is the promise,
        // and the island's own watch is only the thing that carries it out.
        private void Fullscreen_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Settings.Current.Set(() => Settings.Current.HideInFullscreen = FullscreenSwitch.IsChecked == true);
        }

        // The search reads the pages themselves, not only their names: someone hunting for the
        // clock's format types "clock", or "24", or "ImgBB", and what they mean is a setting rather
        // than a title. A page whose text holds the query keeps its row - and if the page being
        // looked at is not one of them, the first that is gets selected, since otherwise a search
        // would answer with an empty list beside the unrelated page it was already showing.
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

        // A page matches on its name, or on anything written on it.
        private bool PageMatches(ListBoxItem row, string query)
        {
            if ((row.Tag as string)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return PaneFor(row.Tag as string) is { } pane
                   && PageText(pane).Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        // Everything a page says, gathered from the tree itself. Both panes are in that tree whether
        // or not they are on screen, so there is no list of words to keep in step with the markup: a
        // line written on a page is a line that can be searched for.
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
