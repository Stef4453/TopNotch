using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TopNotch
{
    public partial class MainWindow : Window
    {
        private bool _expanded;
        private bool _hovered;

        private readonly MediaService _media = new();

        private readonly AudioService _audio = new();

        private readonly PrivacyService _privacy = new();

        private readonly SystemMonitor _system = new();

        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private DispatcherTimer? _topmostTimer;

        // True while the file picker is up. The island normally insists on being the topmost of
        // the topmost on every z-order change and every half second; while a dialog of its own is
        // open, that is the one thing it must not do. See PickAppFor.
        private bool _pickerOpen;

        private bool _updatingFromSystem;

        // Swipe transition state (text only)
        private int _swipeDirection = 1;      // 1 = next (slide left), -1 = previous (slide right)
        private bool _isSwipeAnimating;
        private string? _pendingTitle;
        private string? _pendingArtist;

        // Tab switching state. The order is the order of the strip, and the direction a switch
        // slides: a tab to the right of the last one comes in from the right.
        private enum Tab { Home, Media, History, Launchpad, Clipboard }

        private Tab _currentTab = Tab.Home;
        private bool _isTabAnimating;

        // A quick action waiting on the user to confirm it. Null when nothing is armed. The prompt
        // is not only for power actions - being asked for an API key uses the same two buttons -
        // so what is remembered is the consequence itself rather than which action it was.
        private enum PowerAction { Shutdown, Restart, Sleep, Lock }

        private Action? _pendingConfirm;

        // Notification history for the current session only (newest first).
        private readonly ObservableCollection<NotificationRecord> _history = new();

        private static BitmapImage LoadIcon(string name, int decodePixelWidth = 0)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri($"pack://application:,,,/Sprites/{name}");
            if (decodePixelWidth > 0)
            {
                img.DecodePixelWidth = decodePixelWidth;
            }
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private readonly BitmapImage _playIcon = LoadIcon("play.png");
        private readonly BitmapImage _pauseIcon = LoadIcon("pause.png");
        private readonly BitmapImage _linkIcon = LoadIcon("link.png");
        private readonly BitmapImage _imageIcon = LoadIcon("picture.png");
        private readonly BitmapImage _emptyArt = LoadIcon("empty.png", 128);
        private readonly BitmapImage _audioIcon = LoadIcon("audio.png", 128);
        private readonly BitmapImage _bluetoothAudioIcon = LoadIcon("bluetooth_audio.png", 128);


        public MainWindow()
        {
            InitializeComponent();

            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;

            // Default state until real media info arrives
            TrackTitle.Text = "No media playing";
            ArtistName.Text = "";
            SetMediaActive(false);
            UpdateTabHighlight(Tab.Home);   // Home tab is active on startup
            ShowTabContent(Tab.Home);
            UpdateClock();                  // so the clock isn't blank until the first tick

            // The settings window is the only writer of the settings; the island just keeps up.
            Settings.Changed += OnSettingsChanged;

            HistoryList.ItemsSource = _history;
            HistoryEmpty.Visibility = Visibility.Visible;   // nothing recorded yet

            // The tiles are the store's own list rather than a copy of it, so putting an app in a
            // slot is something the grid simply sees.
            LaunchpadList.ItemsSource = Launchpad.Current.Slots;

            // The board is the store's own list for the same reason the tiles are: a file left on
            // it is something the Canvas simply sees.
            ClipboardList.ItemsSource = Pinboard.Current.Files;

            _media.TrackChanged += (title, artist) => Dispatcher.Invoke(() =>
            {
                bool hasTrack = !string.IsNullOrWhiteSpace(title);
                SetMediaActive(hasTrack);

                string newTitle = hasTrack ? title : "No media playing";
                string newArtist = hasTrack ? artist : "";

                // If identical track is already displayed and not animating, do nothing
                if (!_isSwipeAnimating && TrackTitle.Text == newTitle && ArtistName.Text == newArtist)
                    return;

                _pendingTitle = newTitle;
                _pendingArtist = newArtist;
                if (!_isSwipeAnimating) AnimateTrackSwipe();
            });

            _media.ArtChanged += bytes => Dispatcher.Invoke(() =>
            {
                BitmapImage? bmp = null;
                if (bytes != null)
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.DecodePixelWidth = 128;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                }

                AlbumCover.Source = bmp ?? _emptyArt;
                PeekAlbumCover.Source = bmp ?? _emptyArt;
            });

            UpdateContentVisibility();

            // The island is built at the size everything inside it is measured against - the expanded
            // one - and then put where its shape rests, before the window is ever on screen. Left as
            // it was built it would be a black rectangle across the top of the screen until the first
            // hover took it away.
            _summonTimer.Tick += SummonTimer_Tick;
            _fullscreenTimer.Tick += FullscreenTimer_Tick;
            _hoverNameTimer.Tick += HoverNameTimer_Tick;
            Island.Width = PeekWidth;
            Island.Height = PeekHeight;
            ApplyStyle(instant: true);
            WatchFullscreen();
        }

        // Keep this overlay out of Alt-Tab and make it non-activatable, so it can never
        // take focus (and therefore never receives Alt+F4). Clicking it still delivers
        // normal mouse input to the controls.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            exStyle &= ~WS_EX_APPWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            // Force ourselves to the top of the topmost band whenever our z-order is
            // about to change...
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ForceTopmost();

            // ...and re-assert it periodically, since other apps (e.g. Apple Music
            // maximizing on launch) can raise themselves above a plain topmost window.
            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _topmostTimer.Tick += (s, args) => ForceTopmost();
            _topmostTimer.Start();
        }

        // Not static: while the picker is up the hook has to keep its hands off - see _pickerOpen.
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING && !_pickerOpen)
            {
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.hwndInsertAfter = HWND_TOPMOST;
                pos.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(pos, lParam, false);
            }
            return IntPtr.Zero;
        }

        private void ForceTopmost()
        {
            if (_pickerOpen) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;

            _audio.VolumeChanged += level => Dispatcher.BeginInvoke(() =>
            {
                // Don't fight the user while they're dragging the bar
                if (Volume.IsMouseCaptured ||
                    (Volume.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)) return;

                _updatingFromSystem = true;
                Volume.Value = Volume.Minimum + level * (Volume.Maximum - Volume.Minimum);
                _updatingFromSystem = false;
            });

            Volume.ValueChanged += (s, args) =>
            {
                if (_updatingFromSystem) return;   // the change came from Windows, don't send it back
                double ratio = (Volume.Value - Volume.Minimum) / (Volume.Maximum - Volume.Minimum);
                _audio.SetVolume((float)ratio);
            };

            _audio.Init();
            Closed += (s, args) =>
            {
                _topmostTimer?.Stop();
                _notifyTimer.Stop();
                _clockTimer.Stop();
                _audio.Dispose();
                _privacy.Dispose();
                _system.Dispose();
                _notification.Dispose();
            };

            _privacy.StateChanged += (mic, cam) => Dispatcher.BeginInvoke(() => UpdatePrivacyDots(mic, cam));
            _privacy.Init();

            _system.StatsChanged += (cpu, mem) => Dispatcher.BeginInvoke(() => UpdateSystemStats(cpu, mem));
            _system.Init();

            _clockTimer.Tick += (s, args) => UpdateClock();
            _clockTimer.Start();

            // Bars are sized from the track's real width, and that width isn't known until the
            // island has been laid out at least once, so re-apply the last reading on resize.
            CpuTrack.SizeChanged += (s, args) => UpdateSystemStats(_system.CpuPercent, _system.MemoryPercent);
            MemoryTrack.SizeChanged += (s, args) => UpdateSystemStats(_system.CpuPercent, _system.MemoryPercent);

            _audio.DeviceChanged += (deviceName, isBluetooth) => Dispatcher.BeginInvoke(() =>
            {
                string clean = deviceName.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
                UpdateDeviceDisplay(clean, isBluetooth);
            });

            _media.PlayingChanged += playing => Dispatcher.BeginInvoke(() =>
            {
                PlayPause.Source = playing ? _pauseIcon : _playIcon;

                // Waveform (expanded + peek) shows while audio is playing, but fades/slides in
                // and out instead of snapping on pause/stop.
                AnimateSlot(WaveSlot, WaveSlotWidth, playing);
                AnimateSlot(ExpandedWave, ExpandedWaveWidth, playing);
            });

            _notifyTimer.Tick += (s, args) => DismissNotification();

            _notification.NotificationRaised += info => Dispatcher.BeginInvoke(() =>
            {
                RecordNotification(info);
                ShowNotification(info);
            });
            await _notification.InitAsync();

            await _media.InitAsync();

            string currentAudio = _audio.DefaultDeviceName ?? "Connected Device";
            string cleanAudio = currentAudio.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
            UpdateDeviceDisplay(cleanAudio, _audio.IsCurrentDeviceBluetooth);

            // The check runs after everything else has been set up, so that a slow or unreachable
            // GitHub cannot delay the island appearing. It says nothing unless there is something
            // to say - see AnnounceUpdateAsync.
            if (Settings.Current.CheckForUpdatesOnLaunch) _ = AnnounceUpdateAsync();
        }

        private void Last_Click(object sender, RoutedEventArgs e)
        {
            _swipeDirection = -1;
            _media.Previous();
        }
        private void PlayPause_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _swipeDirection = 1;
            _media.Next();
        }

        // Dims and disables the album art when there's no active media
        private void SetMediaActive(bool active)
        {
            if (!active)
            {
                AlbumCover.Source = _emptyArt;
                PeekAlbumCover.Source = _emptyArt;
            }
            AlbumCover.IsEnabled = active;
            PeekAlbumCover.IsEnabled = active;
            AlbumCover.Opacity = active ? 1.0 : 0.3;
            PeekAlbumCover.Opacity = active ? 1.0 : 0.3;
        }

        private void UpdateDeviceDisplay(string deviceName, bool isBluetooth)
        {
            DeviceNameText.Text = deviceName;

            // The retracted island says the same thing the audio tab does, so there is no second
            // idea about what a device is called.
            PeekDevice.Text = deviceName;

            DeviceIcon.Source = isBluetooth ? _bluetoothAudioIcon : _audioIcon;
        }

        private void UpdatePrivacyDots(bool micActive, bool cameraActive)
        {
            AnimateSlot(MicSlot, MicSlotWidth, micActive);
            AnimateSlot(CameraSlot, CameraSlotWidth, cameraActive);
        }

        // ---- Home tab ----

        private static readonly Duration BarDuration = TimeSpan.FromMilliseconds(450);

        // Day-and-month with the month spelled out, deliberately in the invariant culture: the rest
        // of the UI is English, and "tt" is what guarantees a literal AM/PM designator instead of a
        // locale-specific one. Whether the designator is there at all is the user's call - General
        // in the settings window - which is why both formats are spelled out here rather than
        // being derived from the machine's own idea of a clock.
        private void UpdateClock()
        {
            var now = DateTime.Now;
            bool military = Settings.Current.Use24HourClock;

            // Time and designator are separate runs so the designator renders smaller; the run
            // supplies its own leading space to keep the two apart, and in 24-hour time it is
            // emptied outright rather than left to draw nothing.
            string time = now.ToString(military ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture);
            string meridiem = military ? "" : " " + now.ToString("tt", CultureInfo.InvariantCulture);
            string date = now.ToString("d MMMM", CultureInfo.InvariantCulture);

            // The timer runs every second but the text only changes once a minute, so skip the
            // assignment (and the layout pass it would trigger) when nothing changed.
            if (ClockTimeRun.Text != time) ClockTimeRun.Text = time;
            if (ClockMeridiemRun.Text != meridiem) ClockMeridiemRun.Text = meridiem;
            if (DateText.Text != date) DateText.Text = date;
        }

        // The settings window writes a change and announces it; the island's half of that is to
        // redraw the two things in it that are written as times - and to change its own shape, if
        // that is what was changed. Only a real change is acted on: the clock's format being
        // rewritten is no reason to move the island around the screen.
        private void OnSettingsChanged()
        {
            // A change of outline is not a change of shape: the island stays where it is and only
            // its edge is redrawn. Anything else about the shape means it has to be placed again.
            if (_appliedStyle != Settings.Current.IslandStyle) ApplyStyle();
            else if (_appliedOutline != Settings.Current.IslandOutline) ApplyOutline();

            UpdateClock();
            HistoryList.Items.Refresh();   // the history's own timestamps follow the same choice

            // The fullscreen watch is only worth running while the setting asks for it, and turning
            // it off has to let the island back at once rather than at the next slide of some
            // fullscreen app.
            WatchFullscreen();
        }

        private void UpdateSystemStats(double cpuPercent, double memoryPercent)
        {
            CpuPercentText.Text = $"{cpuPercent:0}%";
            MemoryPercentText.Text = $"{memoryPercent:0}%";

            AnimateBar(CpuFill, CpuTrack, cpuPercent);
            AnimateBar(MemoryFill, MemoryTrack, memoryPercent);
        }

        // The fill is a left-aligned rounded cap, so it's sized in pixels rather than scaled:
        // a ScaleTransform would squash the corner radius as the bar gets shorter.
        private static void AnimateBar(System.Windows.Controls.Border fill, System.Windows.Controls.Border track, double percent)
        {
            double full = track.ActualWidth;
            if (full <= 0) return;   // not laid out yet; SizeChanged will call us again

            double target = full * Math.Clamp(percent, 0, 100) / 100.0;
            fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(target, BarDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        // ---- Quick actions ----

        // Nothing fires on the first click any more: the island asks, and the choice waits in
        // _pendingPowerAction until it's confirmed or abandoned.
        private void Shutdown_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Shutdown);

        private void Restart_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Restart);

        private void Sleep_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Sleep);

        private void Lock_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Lock);

        // Hovering a tile names it, on the caption line that belongs to its own row - the one above the
        // top row, the one below the bottom row - so the label is always the closest text to the tile
        // it names and never appears between the two rows, where it would read as belonging to the
        // pair. Only one line is ever on screen, which is what lets the caption be a proper 12px.
        // The slide puts the line under the hovered column. Each line spans the whole block rather
        // than sitting in a 40px column, because a name wider than its column - "Shorten Link",
        // "Share Image" - gets clipped to the column it is handed.
        // Each animation starts from the line's current opacity rather than from a fixed value, or
        // moving from one tile to the next would blink the caption away before fading it back in.
        private void QuickAction_PointerEnter(object sender, MouseEventArgs e) =>
            ShowCaption((System.Windows.Controls.Button)sender);

        // The caption machinery, per tile. The pointer is its usual driver; a drag reaching the
        // island is the other, because an OLE drag keeps the mouse to itself and the enter events
        // a hover would have raised never arrive.
        private void ShowCaption(System.Windows.Controls.Button tile)
        {
            bool top = System.Windows.Controls.Grid.GetRow(tile) == 1;
            var line = top ? TopRowCaption : BottomRowCaption;
            var shift = top ? TopRowCaptionShift : BottomRowCaptionShift;

            line.Text = tile.Tag as string ?? string.Empty;
            shift.X = (System.Windows.Controls.Grid.GetColumn(tile) - 1) * 40;
            FadeCaption(line, 1, 120);
            FadeCaption(top ? BottomRowCaption : TopRowCaption, 0, 120);
        }

        private void QuickAction_PointerLeave(object sender, MouseEventArgs e) => HideCaptions();

        private void HideCaptions()
        {
            FadeCaption(TopRowCaption, 0, 180);
            FadeCaption(BottomRowCaption, 0, 180);
        }

        private static void FadeCaption(System.Windows.Controls.TextBlock line, double to, int milliseconds) =>
            line.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(line.Opacity, to, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

        private static string ActionName(PowerAction action) => action switch
        {
            PowerAction.Shutdown => "Shut down",
            PowerAction.Restart => "Restart",
            PowerAction.Sleep => "Sleep",
            _ => "Lock",
        };

        // Short enough that the prompt reads as a response to the click rather than a transition.
        private static readonly TimeSpan ConfirmFade = TimeSpan.FromMilliseconds(160);

        private void AskToConfirm(PowerAction action)
        {
            string name = ActionName(action);
            AskToConfirm(name + "?", name, () => RunPowerAction(action));
        }

        // One prompt, two callers: a power action about to happen, and a feature that cannot run
        // without an API key. Both need the same thing - a line of text and two buttons - so the
        // prompt takes its own wording and its own consequence rather than knowing about either.
        private void AskToConfirm(string prompt, string accept, Action consequence)
        {
            _pendingConfirm = consequence;

            ConfirmPrompt.Text = prompt;
            ConfirmAccept.Content = accept;

            // Drop any fade-out still running first: its Completed handler would otherwise
            // collapse the overlay again a moment after showing it. The overlay is hit-testable
            // from the first frame, so the buttons underneath stay blocked throughout the fade.
            ConfirmOverlay.BeginAnimation(OpacityProperty, null);
            ConfirmOverlay.Visibility = Visibility.Visible;
            ConfirmOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        // Also reached by clicking off the prompt, so the island is never left armed out of sight.
        private void DismissConfirm()
        {
            _pendingConfirm = null;

            var fade = new DoubleAnimation(0, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) => ConfirmOverlay.Visibility = Visibility.Collapsed;

            ConfirmOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => DismissConfirm();

        private void ConfirmAccept_Click(object sender, RoutedEventArgs e)
        {
            var consequence = _pendingConfirm;
            if (consequence is null) return;

            DismissConfirm();   // drop the prompt first, so nothing is left showing if this fails
            consequence();
        }

        private static void RunPowerAction(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: DoShutdown(); break;
                case PowerAction.Restart: DoRestart(); break;
                case PowerAction.Sleep: DoSleep(); break;
                case PowerAction.Lock: DoLock(); break;
            }
        }

        // Shut down and restart go through shutdown.exe rather than ExitWindowsEx: the tool asks
        // every app to close properly instead of killing them. "/t 0" skips the countdown, and
        // there is deliberately no "/f", which would force apps closed without saving.
        private static void DoShutdown() => StartDetached("shutdown.exe", "/s /t 0");

        private static void DoRestart() => StartDetached("shutdown.exe", "/r /t 0");

        // shutdown.exe has no sleep switch, so this calls the API that rundll32 would otherwise
        // wrap. On a worker thread because the call doesn't return until the system has begun the
        // transition, and parking the dispatcher for that long would freeze the island.
        private static void DoSleep() => Task.Run(() => SetSuspendState(false, false, false));

        private static void DoLock() => LockWorkStation();

        private static void StartDetached(string fileName, string arguments) =>
            Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,   // no console window flash
            });

        // Peek items live in clipped, fixed-width "slots" so showing or hiding one slides the
        // whole right-aligned row smoothly. Each slot's width (which includes its leading gap as
        // padding) and opacity are animated together; Width = 0 leaves no stray gap behind.
        private const double MicSlotWidth = 8;
        private const double CameraSlotWidth = 13;   // 5px leading gap + 8px dot
        private const double WaveSlotWidth = 22;     // 6px leading gap + 16px waveform
        private const double ExpandedWaveWidth = 16;
        private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(220);

        private static void AnimateSlot(FrameworkElement slot, double fullWidth, bool show)
        {
            var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

            if (show)
                slot.Visibility = Visibility.Visible;

            var width = new DoubleAnimation
            {
                From = show ? 0 : fullWidth,
                To = show ? fullWidth : 0,
                Duration = RevealDuration,
                EasingFunction = ease
            };
            var opacity = new DoubleAnimation
            {
                From = show ? 0 : 1,
                To = show ? 1 : 0,
                Duration = RevealDuration,
                EasingFunction = ease
            };

            if (!show)
                width.Completed += (_, _) => slot.Visibility = Visibility.Collapsed;

            slot.BeginAnimation(FrameworkElement.WidthProperty, width);
            slot.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        // Tab switching

        // The active tab is marked twice over: brighter than the others, and underlined. All the
        // underlines are laid out from the start and only their opacity changes, so switching tabs
        // never re-measures the strip and the labels stay put.
        private void UpdateTabHighlight(Tab active)
        {
            var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A));
            HomeTab.Foreground = active == Tab.Home ? System.Windows.Media.Brushes.White : inactive;
            MediaTab.Foreground = active == Tab.Media ? System.Windows.Media.Brushes.White : inactive;
            HistoryTab.Foreground = active == Tab.History ? System.Windows.Media.Brushes.White : inactive;
            LaunchpadTab.Foreground = active == Tab.Launchpad ? System.Windows.Media.Brushes.White : inactive;
            ClipboardTab.Foreground = active == Tab.Clipboard ? System.Windows.Media.Brushes.White : inactive;

            FadeUnderline(HomeUnderline, active == Tab.Home);
            FadeUnderline(MediaUnderline, active == Tab.Media);
            FadeUnderline(HistoryUnderline, active == Tab.History);
            FadeUnderline(LaunchpadUnderline, active == Tab.Launchpad);
            FadeUnderline(ClipboardUnderline, active == Tab.Clipboard);
        }

        private static void FadeUnderline(UIElement underline, bool on) =>
            underline.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        private void ShowTabContent(Tab tab)
        {
            HomeContent.Visibility = tab == Tab.Home ? Visibility.Visible : Visibility.Collapsed;
            MediaContent.Visibility = tab == Tab.Media ? Visibility.Visible : Visibility.Collapsed;
            HistoryContent.Visibility = tab == Tab.History ? Visibility.Visible : Visibility.Collapsed;
            LaunchpadContent.Visibility = tab == Tab.Launchpad ? Visibility.Visible : Visibility.Collapsed;
            ClipboardContent.Visibility = tab == Tab.Clipboard ? Visibility.Visible : Visibility.Collapsed;

            // Opening the Launchpad is the one moment there is something to explain about it.
            if (tab == Tab.Launchpad) ShowLaunchpadIntroIfNew();

            // The board holds its files inside itself when it comes into view, which is when it has
            // the size it is really going to be rather than one it is passing through on the way
            // open. Anything written down outside the board is brought back inside it here.
            if (tab == Tab.Clipboard) Pinboard.Current.HoldAll();
        }

        private async void Tab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // don't let the click bubble to Island_Click and toggle expand

            Tab target = sender == HomeTab ? Tab.Home
                       : sender == MediaTab ? Tab.Media
                       : sender == HistoryTab ? Tab.History
                       : sender == LaunchpadTab ? Tab.Launchpad
                       : Tab.Clipboard;
            if (target == _currentTab || _isTabAnimating) return;

            Tab previous = _currentTab;
            _currentTab = target;
            UpdateTabHighlight(target);

            _isTabAnimating = true;

            // Slight, noticeable delay before actually switching
            await Task.Delay(110);

            if (target == Tab.Media)
            {
                string currentAudio = _audio.DefaultDeviceName ?? "Connected Device";
                string cleanAudio = currentAudio.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
                UpdateDeviceDisplay(cleanAudio, _audio.IsCurrentDeviceBluetooth);
            }

            if (!_expanded)
            {
                ShowTabContent(target);
                TabContentShift.BeginAnimation(TranslateTransform.XProperty, null);
                TabContentHost.BeginAnimation(OpacityProperty, null);
                TabContentShift.X = 0;
                TabContentHost.Opacity = 1;
                _isTabAnimating = false;
                return;
            }

            AnimateTabSwitch(target, target > previous ? 1 : -1);
        }

        private void AnimateTabSwitch(Tab target, int dir)
        {
            double distance = 180;
            var outDuration = TimeSpan.FromMilliseconds(140);
            var inDuration = TimeSpan.FromMilliseconds(180);

            var slideOut = new DoubleAnimation(dir * -distance, outDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var fadeOut = new DoubleAnimation(0, outDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            slideOut.Completed += (s, e) =>
            {
                // Switch tab visibility at midpoint
                ShowTabContent(target);

                // Reset position to incoming side
                TabContentShift.BeginAnimation(TranslateTransform.XProperty, null);
                TabContentHost.BeginAnimation(OpacityProperty, null);
                TabContentShift.X = dir * distance;
                TabContentHost.Opacity = 0;

                // Slide in
                var slideIn = new DoubleAnimation(0, inDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeIn = new DoubleAnimation(1, inDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                slideIn.Completed += (s2, e2) =>
                {
                    _isTabAnimating = false;
                };

                TabContentShift.BeginAnimation(TranslateTransform.XProperty, slideIn);
                TabContentHost.BeginAnimation(OpacityProperty, fadeIn);
            };

            TabContentShift.BeginAnimation(TranslateTransform.XProperty, slideOut);
            TabContentHost.BeginAnimation(OpacityProperty, fadeOut);
        }

        // ---- Island shape and motion ----

        private const double ExpandedWidth = 460;
        private const double ExpandedHeight = 142;
        private const double PeekWidth = 160;
        private const double PeekHeight = 32;

        // The island's top three pixels are off the top of the screen, which is what makes the notch
        // borderless. The markup gives the zone holding it a -3 margin; everything here is measured
        // from there.
        private const double EdgeOverlap = 3;

        // Where each shape rests, and where it sits when it is out. The notch keeps a sliver of
        // itself showing - that sliver is the notch, and a notch that came and went would be a
        // strange thing to have on a desktop - so "away" for it is twelve pixels of black hanging
        // off the top of the screen. The floating pill goes right off the screen instead, because a
        // pill with nothing to show is a black blob over whatever you were doing.
        private const double NotchRestY = -20;
        private const double PillGap = 10;   // clear air between the pill and the top of the screen

        private double HoverY => Floating ? EdgeOverlap + PillGap : 0;

        // Away is not a fixed offset: the island is asked to leave the screen while it may still be
        // as tall as a panel on its way back to being a capsule, and how far off it has to go
        // depends on how tall it is at the time.
        private double RestY => Floating ? EdgeOverlap - Math.Max(Island.Height, PeekHeight) - 10
                                         : NotchRestY;

        // Where the island goes when an app has the whole screen. Deeper than either shape's own
        // resting place, and measured from the tallest the island ever is, because a fullscreen app
        // has asked for all of the screen and the notch's usual sliver is still part of it.
        private const double AwayY = EdgeOverlap - ExpandedHeight;

        // Where the island belongs, given everything that is on it: away for a fullscreen app, out
        // for something that has to be read or reached for, and at rest otherwise.
        private double HomeY => _away ? AwayY : Held ? HoverY : RestY;

        // A notification banner is as long as the expanded island but a fixed 66 tall. It used to be
        // "half the island", which stopped meaning anything once the island grew to make room for the
        // quick-action captions - the banner is its own thing and should not follow that.
        private const double NotificationWidth = ExpandedWidth;
        private const double NotificationHeight = 66;

        // Which of the two shapes is being worn. Read from the settings rather than cached: the only
        // thing that ever changes it is a change made in the settings window, which says so.
        private static bool Floating => Settings.Current.IslandStyle == IslandStyle.DynamicIsland;

        // Whether the floating pill is out of its hole. Kept as a flag rather than read back off the
        // transform, because the transform is in flight for the length of every animation and both
        // the peek's contents and the pointer's arrival are decided in its first frame.
        private bool _pillOut;

        // Whether it is on its way back in. The peek's contents stay on screen for the whole
        // journey: a pill that empties itself and then slides away reads as a black rectangle
        // leaving, which is the one thing this shape is meant not to look like. So the flag that
        // ends the peek is cleared when the slide finishes rather than when it starts - and this
        // one is also what stops the summon timer asking for the same journey ten times a second.
        private bool _pillLeaving;

        // The shape the island is currently wearing, so that a change to something else - the clock's
        // format - does not reshuffle it.
        private IslandStyle _appliedStyle;

        // Whether that shape was last drawn with its hairline on, held for the same reason.
        private bool _appliedOutline;

        // The floating pill shows nothing at rest, which also means there is not a pixel of ours for
        // the pointer to touch: nothing in the window can tell us the pointer has arrived. So the
        // pointer is asked for instead, which costs a few reads a second and cannot swallow a click
        // the way an invisible strip along the top of the screen would.
        private readonly DispatcherTimer _summonTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };

        // Whether a fullscreen app has the screen, and the watch that asks. The shell has no event
        // for "the foreground went fullscreen", so it is asked four times a second: often enough
        // that the island is gone before anything important has been drawn, and cheap enough that
        // nobody could tell it was being asked.
        private bool _away;

        private readonly DispatcherTimer _fullscreenTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        // How much of the top of the screen belongs to the island: wide enough to be found without
        // aiming, and no wider than the thing being aimed at.
        private const double ReachWidth = 280;

        private void SummonTimer_Tick(object? sender, EventArgs e)
        {
            // A fullscreen app has the screen: nothing reaches for the island over it, and nothing
            // puts it anywhere while it is away.
            if (_away) return;

            bool reaching = PointerAtIsland();

            // Out covers both the pill that is here and the one on its way back in: a pointer that
            // arrives mid-retract turns it around, rather than letting it carry on out from under
            // the hand that reached for it. Nothing is asked for twice, either - the timer runs ten
            // times a second and a slide takes three - so each of these waits for the other.
            if (reaching && (!_pillOut || _pillLeaving))
            {
                ShowIsland();
                UpdateContentVisibility();
            }
            else if (!reaching && _pillOut && !_pillLeaving && !Held)
            {
                HideIsland();
            }
        }

        // Whether the pointer is reaching for the island. Two things count: being in the strip along
        // the top of the screen the pill drops into, and - while a drag is under way - being anywhere
        // the pill would be, because a drag cannot hover and the pill has to be there before a
        // picture can be dropped on it.
        private static bool PointerAtIsland()
        {
            if (!GetCursorPos(out POINT point)) return false;
            if (Math.Abs(point.X - SystemParameters.PrimaryScreenWidth / 2) > ReachWidth / 2) return false;

            if (point.Y <= EdgeOverlap + PillGap) return true;

            return point.Y <= EdgeOverlap + PillGap + PeekHeight
                   && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }

        // On screen for a reason of its own: the pointer is on it, it has been opened, or it is
        // showing something that has to be read. Anything else and the floating pill is free to go.
        private bool Held => _hovered || _expanded || _notifying || _dismissing || _urlInputOpen
                             || ConfirmOverlay.Visibility == Visibility.Visible;

        private readonly NotificationService _notification = new();
        private readonly DispatcherTimer _notifyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private bool _notifying;    // a banner currently owns the island
        private bool _dismissing;    // banner is fading out, but still painted
        private double _restoreWidth;    // island size to return to after a banner
        private double _restoreHeight;

        private void Zone_MouseEnter(object sender, MouseEventArgs e)
        {
            // Nothing is answered while a fullscreen app has the screen.
            if (_away) return;

            _hovered = true;
            UpdateContentVisibility();
            if (_notifying) return;   // the banner owns the island until it's dismissed
            ShowIsland();
        }

        private void Zone_MouseLeave(object sender, MouseEventArgs e)
        {
            _hovered = false;
            UpdateContentVisibility();
            if (_notifying) return;

            // The floating pill is not put away on the strength of the pointer leaving it: what
            // matters is where the pointer went, which hover alone cannot say. The summon timer
            // knows, and it is the only thing that takes the pill away.
            if (Floating) return;

            HideIsland();
        }

        // The island is either out where it can be seen and reached, or away. Everything that moves
        // it asks for one of those two rather than for a coordinate, so that where "away" is can
        // depend on the shape and on how tall the island is at the time.
        private void ShowIsland()
        {
            // A fullscreen app has the screen: nothing brings the island back over it.
            if (_away) return;

            _pillLeaving = false;
            _pillOut = true;
            SlideIsland(HoverY);
        }

        // Nothing is put away while the panel is open: an opened island stays open until it is
        // clicked shut, and the pointer wandering off is not a reason to take it away from someone
        // who is reading it.
        private void HideIsland()
        {
            // Once is enough: the summon timer asks for this every 110 milliseconds while the
            // pointer is away, and a second slide would start the journey again from wherever this
            // one has got to, which is how a retract ends up making no headway at all.
            if (_expanded || _pillLeaving) return;
            _pillLeaving = true;

            SlideIsland(HomeY, () =>
            {
                // A fresh arrival may have taken the island over while this slide was running, and
                // its own flag is what says whether it did.
                if (!_pillLeaving) return;

                _pillLeaving = false;
                _pillOut = false;
                UpdateContentVisibility();
            });
        }

        private void SlideIsland(double targetY, Action? done = null)
        {
            // The two shapes arrive and leave differently. The notch is already touching the top of
            // the screen, so all it has to do is move. The pill has further to come and further to
            // go, and the motion is most of what makes that read as arriving and being taken away
            // rather than as a black rectangle changing place: it springs in, and it whips away.
            TimeSpan duration;
            EasingFunctionBase ease;

            if (!Floating)
            {
                duration = TimeSpan.FromMilliseconds(250);
                ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            }
            else if (targetY == HoverY)
            {
                duration = TimeSpan.FromMilliseconds(340);
                ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
            }
            else
            {
                duration = TimeSpan.FromMilliseconds(300);
                ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            }

            var slide = new DoubleAnimation(targetY, duration) { EasingFunction = ease };
            if (done is not null) slide.Completed += (_, _) => done();

            IslandShift.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // ---- Fullscreen ----

        // Asks the shell whether an app has the whole screen, and moves the island to where that
        // answer says it should be. Only a change of answer does anything: the answers arrive several
        // times a second, and moving the island on each one would be a slide that never stopped
        // starting.
        private void FullscreenTimer_Tick(object? sender, EventArgs e)
        {
            bool away = Fullscreen.Active;
            if (away == _away) return;

            StepAside(away);
        }

        // The watch runs only while the setting asks for it. Turning the setting off lets the island
        // back at once, even if something is fullscreen at that moment: the switch is the promise,
        // and this is the only thing that carries it out.
        private void WatchFullscreen()
        {
            if (!Settings.Current.HideInFullscreen)
            {
                _fullscreenTimer.Stop();
                if (_away) StepAside(false);
                return;
            }

            if (!_fullscreenTimer.IsEnabled) _fullscreenTimer.Start();
        }

        // An app has the whole screen, or has given it back. Away, the island leaves the screen
        // entirely rather than keeping the notch's sliver - a sliver is meant to be part of the
        // screen's furniture, and a fullscreen app is looking at all of the screen - and it stops
        // answering the pointer until the screen is given back.
        //
        // Nothing is thrown away on the way out: an island that was open is still open when it
        // returns, and a banner that was up has already put itself away. The one thing let go of is
        // the link box, because it is holding the keyboard, and a fullscreen app must have that.
        private void StepAside(bool away)
        {
            _away = away;

            _hovered = false;
            ForgetHoverName();
            HideUrlInput();

            _pillLeaving = false;
            _pillOut = false;

            if (away)
            {
                // Quick and plain, without the spring it arrives with: this is not the island being
                // handed to somebody, it is the island getting out of the way.
                var leave = new DoubleAnimation(AwayY, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                IslandShift.BeginAnimation(TranslateTransform.YProperty, leave);
            }
            else
            {
                SlideIsland(HomeY);
            }

            UpdateContentVisibility();
        }

        private void AnimateIslandSize(double width, double height)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            var duration = TimeSpan.FromMilliseconds(350);

            Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
        }

        private void UpdateContentVisibility()
        {
            ForgetHoverName();

            // A live (or still-fading) banner overrides everything else; otherwise the expanded
            // tabbed UI shows, or the peek panel when retracted and hovered.
            bool banner = _notifying || _dismissing;

            // The floating pill has no hover of its own to wait for - being out at all is what it
            // means to be present - so its peek shows for as long as it is on screen.
            bool peeking = _hovered || (Floating && _pillOut);

            NotificationContent.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContent.Visibility = (!banner && _expanded) ? Visibility.Visible : Visibility.Collapsed;
            PeekContent.Visibility = (!banner && !_expanded && peeking) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Shape ----

        // The hairline the island is drawn with, when it is drawn with one: black against a dark
        // desktop would otherwise have no edge of its own at all.
        private static readonly Brush OutlineEdge = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D));

        // Everything that differs between the two shapes is decided here: how round the island is,
        // whether it floats clear of the screen, and whether anything has to watch for the pointer.
        // Called once when the island is built and again whenever the settings window changes the
        // choice, so that flipping it lands on the widget straight away rather than at the next start.
        private void ApplyStyle(bool instant = false)
        {
            _appliedStyle = Settings.Current.IslandStyle;

            // The notch is square where it runs off the top of the screen - there is no corner there
            // to round - while the pill is round all the way round. Sixteen is the most that leaves
            // the retracted pill a capsule, which is the shape the whole idea is borrowed from. The
            // two overlays paint over the island's own corners, so they have to match it.
            var corner = Floating ? new CornerRadius(PeekHeight / 2) : new CornerRadius(0, 0, 20, 20);

            Island.CornerRadius = corner;
            ConfirmOverlay.CornerRadius = corner;
            UrlOverlay.CornerRadius = corner;

            ApplyOutline();

            if (Floating) _summonTimer.Start();
            else _summonTimer.Stop();

            // Put it where the new shape rests. Anything with a reason of its own to be on screen
            // stays there, which is the same rule the pointer is held to: the island is out to be
            // looked at, or it is away.
            //
            // This does not go through the pair below. Those ask for a journey and refuse one
            // already under way, which is right for a pointer arriving and leaving, but a change of
            // shape is neither: the island has to reach the new shape's resting place whatever it
            // happened to be doing when the choice was made.
            _pillLeaving = false;
            _pillOut = Held && !_away;

            // Where it belongs also depends on whether an app has the screen: a fullscreen app is
            // not a reason for the island to be out, whatever else was going on.
            if (instant)
            {
                IslandShift.BeginAnimation(TranslateTransform.YProperty, null);
                IslandShift.Y = HomeY;
            }
            else
            {
                SlideIsland(HomeY);
            }

            UpdateContentVisibility();
        }

        // The hairline, on or off, for whichever shape is worn. A shadow would do the job more
        // handsomely, but a shadow is drawn outside the island, and outside the island is where this
        // window's pixels have to stay clear: it is the transparent ones that let a click through to
        // whatever is behind. So the edge is drawn inside, and it follows the rounded corners with it.
        private void ApplyOutline()
        {
            bool on = Settings.Current.IslandOutline;
            _appliedOutline = on;

            Island.BorderThickness = on ? new Thickness(1) : new Thickness(0);
            Island.BorderBrush = on ? OutlineEdge : null;
        }

        private void Island_Click(object sender, MouseButtonEventArgs e)
        {
            // Nothing on the island can be reached while a fullscreen app has the screen.
            if (_away) return;

            if (_notifying)
            {
                DismissNotification();   // clicking the banner dismisses it early
                return;
            }

            // Open, the link box owns the island the same way the prompt does below.
            if (_urlInputOpen)
            {
                HideUrlInput();
                return;
            }

            // The prompt owns the island while it's up — including while it fades out, since it
            // stays hit-testable until the fade finishes — so a click off the buttons abandons the
            // action instead of collapsing the island out from under it.
            if (ConfirmOverlay.Visibility == Visibility.Visible)
            {
                DismissConfirm();
                return;
            }

            _expanded = !_expanded;
            UpdateContentVisibility();
            AnimateIslandSize(_expanded ? ExpandedWidth : PeekWidth, _expanded ? ExpandedHeight : PeekHeight);
        }

        // ---- The name of what the pointer is on ----

        // WPF's own tooltips never open here. The island is a window that is deliberately never
        // activated - it is not in the taskbar or Alt-Tab, and clicking it does not take the
        // keyboard away from whatever had it - and the tooltip service will not show one without an
        // active window. So the name of the thing under the pointer is drawn by the island itself:
        // the same dark plate a tooltip would have been, in the same place, and only after the
        // pointer has rested on something for long enough to be asking what it is.
        //
        // The names are not gathered here: the item templates already put one on each tile as its
        // tool tip, which is where a name belongs, and this reads it back. The tool tip service is
        // switched off on those same elements so that WPF never draws its own as well - one plate,
        // drawn by the island, in the island's own style.
        private readonly DispatcherTimer _hoverNameTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };

        private string? _hoverName;   // what the pointer has come to rest on, if anything
        private Point _hoverAt;       // where it was when it settled there

        private void Island_MouseMove(object sender, MouseEventArgs e)
        {
            // Nothing is named while a button is down: dragging across the board or the launchpad is
            // not a person asking what any of it is called.
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                RestedOn(null, e);
                return;
            }

            // Found by walking up from whatever was hit, so that every item in the island is covered
            // by this one rule rather than each kind of tile wiring up a hover of its own.
            string? name = null;

            for (var at = Island.InputHitTest(e.GetPosition(Island)) as DependencyObject;
                 at is not null && at != Island;
                 at = ParentOf(at))
            {
                if (at is FrameworkElement { ToolTip: string tip } && tip.Length > 0)
                {
                    name = tip;
                    break;
                }
            }

            RestedOn(name, e);
        }

        private void Island_MouseLeave(object sender, MouseEventArgs e) => RestedOn(null, e);

        // The step up the tree from something that was hit. Almost everything in the island is a
        // visual, but text is not: a line of it is a Run, which is a content element, and content
        // elements have no place in the visual tree at all - asking that tree for their parent
        // throws rather than answering, which is what used to take the whole window down the moment
        // the pointer crossed a line of text. Text is stepped over to the element that holds it,
        // and the walk carries on from there. Anything else the walk cannot climb stops it, which
        // is the same answer as reaching the top.
        private static DependencyObject? ParentOf(DependencyObject at)
        {
            if (at is FrameworkContentElement text) return text.Parent;

            return at is Visual ? VisualTreeHelper.GetParent(at) : null;
        }

        // Records what the pointer has settled on and starts the wait. Going from one thing to
        // another starts it again; moving about within the same thing does not, which is what lets
        // the name stay up while the hand settles.
        private void RestedOn(string? name, MouseEventArgs e)
        {
            if (name == _hoverName)
            {
                if (name is not null) _hoverAt = e.GetPosition(HoverCanvas);
                return;
            }

            _hoverName = name;
            _hoverNameTimer.Stop();
            HideHoverName();

            if (name is null) return;

            _hoverAt = e.GetPosition(HoverCanvas);
            _hoverNameTimer.Start();
        }

        private void HoverNameTimer_Tick(object? sender, EventArgs e)
        {
            _hoverNameTimer.Stop();
            if (_hoverName is not { } name) return;

            HoverNameText.Text = name;

            // Measured before it is placed, because where it fits depends on how big it is. It is
            // made visible for the measure and placed in the same frame, so it is never painted in
            // the wrong place.
            HoverName.Visibility = Visibility.Visible;
            HoverName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = HoverName.DesiredSize;

            // Below and to the right of the pointer, as a tooltip is, and slid back inside the
            // island where that would hang it over the edge.
            Canvas.SetLeft(HoverName, Math.Max(0, Math.Min(_hoverAt.X + 14, HoverCanvas.ActualWidth - size.Width)));
            Canvas.SetTop(HoverName, Math.Max(0, Math.Min(_hoverAt.Y + 20, HoverCanvas.ActualHeight - size.Height)));
        }

        private void HideHoverName()
        {
            if (HoverName.Visibility != Visibility.Visible) return;
            HoverName.Visibility = Visibility.Collapsed;
        }

        // A change of what the island is showing is a change of what the pointer is over, and the
        // name of the old thing has no business floating over the new one.
        private void ForgetHoverName()
        {
            _hoverName = null;
            _hoverNameTimer.Stop();
            HideHoverName();
        }

        // ---- Notifications ----

        // Adds a notification to the session history tab. Kept separate from the banner so that
        // one arriving while another is on screen is still catalogued.
        private void RecordNotification(NotificationInfo info)
        {
            var icon = info.IconPng is { Length: > 0 } ? CreateBitmap(info.IconPng, 48) : null;
            _history.Insert(0, new NotificationRecord(info.AppName, info.Title, info.Body, icon, DateTime.Now));

            HistoryEmpty.Visibility = Visibility.Collapsed;
            HistoryScroller.NotifyInsertedAtTop(_history.Count);
        }

        private void ShowNotification(NotificationInfo info) => ShowBanner(
            info.Title,
            info.Body,
            info.IconPng is { Length: > 0 } ? CreateBitmap(info.IconPng, 96) : null);

        // Puts a transient message in the banner. Notifications and the app's own status messages
        // (such as the outcome of shortening a link) share it; only notifications go through
        // RecordNotification, so status messages never turn up in the history tab.
        private void ShowBanner(string title, string body, ImageSource? icon)
        {
            // A fullscreen app has the screen, and a banner over it is exactly the interruption that
            // stepping aside is meant to avoid. The notification is not lost: it is written into the
            // history before it is ever shown here, and the app's own status messages can wait for a
            // moment when somebody is looking at the desktop.
            if (_away) return;

            // A banner takes the island over, so an open link box can't stay: close it properly,
            // releasing the keyboard, rather than leaving it stranded underneath.
            if (_urlInputOpen) HideUrlInput();

            NotifTitle.Text = title;
            NotifBody.Text = body;

            if (icon is not null)
            {
                NotifIcon.Source = icon;
                NotifIcon.Visibility = Visibility.Visible;
                NotifTextHost.Margin = new Thickness(12, 0, 0, 0);
            }
            else
            {
                NotifIcon.Source = null;
                NotifIcon.Visibility = Visibility.Collapsed;
                NotifTextHost.Margin = new Thickness(0);
            }

            bool alreadyShowing = _notifying;

            if (!alreadyShowing)
            {
                // Remember the island's current size so we can put it back afterwards.
                _restoreWidth = double.IsNaN(Island.Width) ? ExpandedWidth : Island.Width;
                _restoreHeight = double.IsNaN(Island.Height) ? ExpandedHeight : Island.Height;

                AnimateIslandSize(NotificationWidth, NotificationHeight);
                ShowIsland();   // a resting island should come fully on screen
            }

            _notifying = true;
            _dismissing = false;
            UpdateContentVisibility();

            NotificationContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

            _notifyTimer.Stop();
            _notifyTimer.Start();
        }

        private void DismissNotification()
        {
            _notifyTimer.Stop();
            if (!_notifying) return;

            _notifying = false;
            _dismissing = true;          // keep the banner painted while it fades
            AnimateIslandSize(_restoreWidth, _restoreHeight);
            if (_hovered) ShowIsland(); else HideIsland();
            UpdateContentVisibility();

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                if (_notifying) return;  // a newer notification took over mid-fade
                _dismissing = false;
                UpdateContentVisibility();
            };
            NotificationContent.BeginAnimation(OpacityProperty, fade);
        }

        // ---- Updates ----

        // The start-up half of the update check, and the whole of its restraint: it only ever says
        // anything when a newer release exists. An up-to-date copy, a project with nothing published
        // yet, and a machine that cannot reach GitHub all look exactly alike from here - like
        // nothing happened - which is the right amount of noise for an app starting up.
        //
        // What it says is a banner, and the banner names the place to act on it rather than being a
        // button: the island's banners are for reading and dismissing, and the Settings window is
        // already the one place that changes anything.
        private async Task AnnounceUpdateAsync()
        {
            UpdateCheck.Result result = await UpdateCheck.RunAsync();
            if (result.Status != UpdateCheck.Status.Newer) return;

            // Away for a fullscreen app, the banner is suppressed along with every other one: the
            // news is not lost, though - About still finds it, and the next start will say it again.
            ShowBanner($"TopNotch {result.Version} is available",
                       "Settings \u203A About has the details, and a way to get it.", null);
        }

        // ---- Link shortener ----

        // The clipboard is still the fast path - copy a link, press the button, press Enter - and it
        // seeds the box. Anything else is typed or pasted in. Either way the banner reports the
        // outcome, so a failure is never silent.
        private void ShortenUrl_Click(object sender, RoutedEventArgs e) => ShowUrlInput();

        private void UrlCancel_Click(object sender, RoutedEventArgs e) => HideUrlInput();

        private void UrlShorten_Click(object sender, RoutedEventArgs e) => SubmitUrl();

        private void UrlOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SubmitUrl();       // Enter in the box is the whole interaction
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideUrlInput();
            }
        }

        // Keeps the placeholder in step with the box. The null check matters because the control
        // raises TextChanged while the window is still being built, before the field is assigned.
        private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            UpdateUrlHint();

        private void UpdateUrlHint()
        {
            if (UrlHint is null) return;
            UrlHint.Visibility = UrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowUrlInput()
        {
            // Whatever is on the clipboard goes in as a starting point, but only if it really is a
            // link: that keeps the one-click flow going (copy a link, press the button, press
            // Enter). With nothing useful on the clipboard the box is left as it was, so a
            // message arriving mid-typing never costs more than reopening it.
            if (ClipboardText() is { } text && TryAsHttpUrl(text, out _))
                UrlBox.Text = text.Trim();

            // Drop any fade-out still running first, or its Completed handler would collapse the
            // overlay again a moment after showing it.
            UrlOverlay.BeginAnimation(OpacityProperty, null);
            UrlOverlay.Visibility = Visibility.Visible;
            UrlOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            _urlInputOpen = true;
            UpdateUrlHint();
            TakeKeyboard();

            // Focus only sticks once the window is genuinely active, and Activate() above has only
            // asked for that; going through the dispatcher lets the activation land first.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                UrlBox.Focus();
                Keyboard.Focus(UrlBox);
                UrlBox.SelectAll();   // typing replaces the seed rather than appending to it
            });
        }

        private void HideUrlInput()
        {
            if (!_urlInputOpen) return;

            _urlInputOpen = false;
            ReleaseKeyboard();

            var fade = new DoubleAnimation(0, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) => UrlOverlay.Visibility = Visibility.Collapsed;
            UrlOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private async void SubmitUrl()
        {
            string typed = UrlBox.Text;

            // Close the box first: the banner takes the island over, and the text stays where it
            // was, so a rejected address can be corrected by reopening and editing it.
            HideUrlInput();

            if (!TryAsHttpUrl(typed, out string url))
            {
                ShowBanner("That doesn't look like a link", "Type or paste an address, e.g. example.com", _linkIcon);
                return;
            }

            // Pressing twice in a row is the easy mistake: the first press leaves the short link
            // on the clipboard, so the second would be shortening our own output.
            if (UrlShortener.IsOwnLink(url))
            {
                ShowBanner("Already shortened", url, _linkIcon);
                return;
            }

            ShowBanner("Shortening link...", url, _linkIcon);

            var result = await UrlShortener.ShortenAsync(url);

            if (!result.Ok)
            {
                ShowBanner("Couldn't shorten the link", result.Error, _linkIcon);
                return;
            }

            // Another process can have the clipboard open; the link is still worth reporting, so
            // say what actually happened rather than claiming it was copied.
            ShowBanner(
                ClipboardSetText(result.Link) ? "Link copied" : "Link shortened",
                result.Link,
                _linkIcon);
        }

        // Wherever an address comes from - the box or the clipboard - it is read the same way. A
        // bare host is what people actually type ("github.com/some/repo"), so HTTPS is assumed for
        // it; anything with a space in it is prose, not an address, and is left alone.
        private static bool TryAsHttpUrl(string? text, out string url)
        {
            url = string.Empty;

            string candidate = text?.Trim() ?? string.Empty;
            if (candidate.Length == 0 || candidate.Contains(' ')) return false;

            bool assumedScheme = !candidate.Contains("://", StringComparison.Ordinal);
            if (assumedScheme) candidate = "https://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            // A guessed scheme only counts when there is a host to speak of: otherwise "hello"
            // would become https://hello and go off to the network as though it meant something.
            if (assumedScheme && !uri.Host.Contains('.')) return false;

            url = candidate;
            return true;
        }

        // The clipboard is shared with every other process, so both a read and a write can fail
        // for reasons that have nothing to do with us.
        private static string? ClipboardText()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                return null;
            }
        }

        // ---- A drop on the island ----

        // Whether the island was open when a drag arrived, so that a drag which is taken
        // somewhere else leaves the notch as it found it instead of having opened it for nothing.
        private bool _wasExpandedBeforeDrag = true;

        private void ShareImage_Click(object sender, RoutedEventArgs e)
        {
            // The click is the explanation, not the feature: a picture arrives by being dropped.
            // Without a key there is nothing to explain yet, so that is what is asked for first.
            if (!Settings.Current.HasImgBbApiKey)
            {
                AskForKey();
                return;
            }

            ShowBanner("Share an image",
                "Drop a picture on the notch and the link lands on your clipboard", _imageIcon);
        }

        // The whole island takes a picture's drop rather than the 36x30 tile the action belongs to:
        // a picture is dragged by its own ghost under the pointer, and landing that on a tile that
        // size would be a game of skill. The tile's caption - shown for the length of the drag - is
        // what says which action the drop will be taken as.
        //
        // The Launchpad is the other way round. There the tiles are the point, and the grid is the
        // map of where a dropped file will land, so a file dropped on that tab goes to the tile it
        // was aimed at, which is lit while the pointer is over it.
        private void Island_DragOver(object sender, DragEventArgs e)
        {
            if (_currentTab == Tab.Launchpad && DroppedApp(e) is not null)
            {
                // A file arriving on a tab that has never been opened is the tab explaining
                // itself. The page of instructions gives way to the thing it describes.
                HideLaunchpadIntro(fade: false);

                e.Effects = DragDropEffects.Copy;
                e.Handled = true;

                OpenForDrop();
                HighlightSlot(SlotAt(e));
                return;
            }

            // Files already on the board do not count: a file carried out of the island and brought
            // back to it is still on the board, and the island should say so rather than offer to
            // take it a second time.
            bool arriving = DroppedFiles(e)?.Any(file => !Pinboard.Current.Has(file)) == true;

            if (_currentTab == Tab.Clipboard && (arriving || WebImage.Carried(e.Data)))
            {
                // Any file at all is taken here, and taken anywhere on the panel: the board is the
                // whole of it, so the whole of it lights up rather than one tile of it. A picture
                // dragged out of a page counts as a file too, once the page has handed it over.
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;

                OpenForDrop();
                HighlightBoard(true);
                return;
            }

            if (DroppedImage(e) is null && !WebImage.Carried(e.Data))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                HighlightSlot(null);
                return;
            }

            // On the board, a file that is already there is not an offer to upload anywhere: it is
            // simply already there, and the island says so rather than turning the same drag into
            // something else on its way past.
            if (_currentTab == Tab.Clipboard && DroppedFiles(e) is not null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                HighlightSlot(null);
                HighlightBoard(false);
                return;
            }

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;

            OpenForDrop();

            // This fires continuously while the pointer moves. Re-showing the caption each time is
            // also what keeps it up: any tile the pointer crosses would otherwise have cleared it.
            ShowCaption(ShareImageButton);
        }

        // Opened on the way in. The tiles a drop is aimed at live on the expanded island, and a drag
        // cannot hover, so otherwise a picture - or an app - would be coming down on a bare 160px
        // sliver with nothing on it to aim at.
        private void OpenForDrop()
        {
            if (_expanded) return;

            _wasExpandedBeforeDrag = false;
            _expanded = true;
            UpdateContentVisibility();
            AnimateIslandSize(ExpandedWidth, ExpandedHeight);
            ShowIsland();   // it sits off the top of the screen while collapsed
        }

        private void Island_DragLeave(object sender, DragEventArgs e)
        {
            EndDrag();
        }

        // Puts the island back the way a drag found it. Two ways a drag can end with no picture to
        // show for it: leaving the island again, and being dropped on it carrying something else
        // entirely (a drag that wandered in mid-flight, text, a folder). Both leave an open panel
        // behind otherwise, which on the notch reads as untidy and on the floating pill reads as a
        // black rectangle parked over the middle of the desktop.
        private void EndDrag()
        {
            HideCaptions();
            HighlightSlot(null);
            HighlightBoard(false);

            if (_wasExpandedBeforeDrag) return;
            _wasExpandedBeforeDrag = true;

            _expanded = false;
            UpdateContentVisibility();
            AnimateIslandSize(PeekWidth, PeekHeight);
            HideIsland();
        }

        private void Island_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (_currentTab == Tab.Launchpad && DroppedApp(e) is { } app)
            {
                LaunchpadSlot? slot = SlotAt(e);
                HighlightSlot(null);

                // A drop that landed between two tiles has nowhere to go: the tiles are the targets,
                // and the one that lit up on the way in is the one that was aimed at.
                if (slot is null)
                {
                    EndDrag();
                    return;
                }

                _wasExpandedBeforeDrag = true;   // the panel is in use, so it stays open

                int index = Launchpad.Current.Slots.IndexOf(slot);
                if (index >= 0) Launchpad.Current.Place(index, app);
                return;
            }

            if (_currentTab == Tab.Clipboard && DroppedFiles(e) is { } files)
            {
                HighlightBoard(false);
                _wasExpandedBeforeDrag = true;   // the panel is in use, so it stays open

                // Where the pointer was let go, which is where the file appears - in the middle of
                // it rather than hanging off the corner of it.
                Point at = e.GetPosition(ClipboardList);
                Pinboard.Current.Add(files, at.X, at.Y);
                return;
            }

            // A picture out of a page, dropped on the board: the page has to hand it over before
            // there is a file to put there.
            if (_currentTab == Tab.Clipboard && WebImage.Carried(e.Data))
            {
                HighlightBoard(false);
                _wasExpandedBeforeDrag = true;

                TakePicturedFileAsync(e.Data, e.GetPosition(ClipboardList));
                return;
            }

            if (DroppedImage(e) is not { } path)
            {
                // The same picture dropped anywhere else is the one the island offers to upload,
                // which needs a file on disk for the same reason.
                if (WebImage.Carried(e.Data))
                {
                    _wasExpandedBeforeDrag = true;
                    SharePicturedFileAsync(e.Data);
                    return;
                }

                EndDrag();
                return;
            }

            _wasExpandedBeforeDrag = true;   // the upload's banner is about to own the island
            ShareImageAsync(path);
        }

        // A picture dragged out of a page is not a file until it has been fetched and written down,
        // which is the one drop on the island that cannot be answered at once. Nothing is said
        // while it works: on the board the tile appearing where the picture was let go is the whole
        // of the answer, and there is only something to say when the page does not give it up.
        private async void TakePicturedFileAsync(IDataObject data, Point at)
        {
            string? path = await WebImage.SaveAsync(data);
            if (path is null)
            {
                ShowBanner("Couldn't take the picture", "The page did not hand it over", _imageIcon);
                return;
            }

            Pinboard.Current.Add([path], at.X, at.Y);
        }

        // The same picture dropped on any other tab, which is the island's offer to upload it: the
        // uploader deals in files, so the picture is written down first and then offered as one.
        private async void SharePicturedFileAsync(IDataObject data)
        {
            ShowBanner("Taking the picture", "From the page it was dragged out of", _imageIcon);

            string? path = await WebImage.SaveAsync(data);
            if (path is null)
            {
                ShowBanner("Couldn't take the picture", "The page did not hand it over", _imageIcon);
                return;
            }

            ShareImageAsync(path);
        }

        // Only the first file is looked at. A drop of several is far more likely to be a folder's
        // worth of things dragged by accident than a deliberate batch, and one link is what the
        // caller is prepared to show.
        private static string? DroppedImage(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;
            return ImgBbUploader.LooksLikeImage(files[0]) ? files[0] : null;
        }

        // What a file drop is carrying, if it is the kind of thing the Launchpad can keep: a
        // program, a shortcut to one, or one of the batch files people keep beside them. A folder or
        // a document is not an app, and a drop effect of None is the only vocabulary a drag has for
        // saying so - so a document dragged across the Launchpad gets the cursor it deserves.
        private static string? DroppedApp(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;

            string path = files[0];
            string extension = Path.GetExtension(path).ToLowerInvariant();

            bool openable = extension is ".exe" or ".lnk" or ".bat" or ".cmd" or ".com" or ".msc"
                                     or ".url" or ".appref-ms";

            return openable && File.Exists(path) ? path : null;
        }

        private async void ShareImageAsync(string path)
        {
            if (!Settings.Current.HasImgBbApiKey)
            {
                AskForKey();
                return;
            }

            ShowBanner("Uploading image...", Path.GetFileName(path), _imageIcon);

            var result = await ImgBbUploader.UploadAsync(Settings.Current.ImgBbApiKey, path);

            if (!result.Ok)
            {
                ShowBanner("Couldn't share the image", result.Error, _imageIcon);
                return;
            }

            // As with a shortened link, another process can have the clipboard open; the link is
            // still worth reporting, so say what actually happened.
            ShowBanner(
                ClipboardSetText(result.Link) ? "Link copied" : "Image uploaded",
                result.Link,
                _imageIcon);
        }

        // The one way in for a feature whose key hasn't been set. The prompt's second button is the
        // fix itself, so nobody has to be told which window to open - and it opens on the page that
        // explains how to get a key, rather than on whatever was looked at last.
        private void AskForKey() => AskToConfirm(
            "Add an ImgBB API key",
            "Open Settings",
            () => (Application.Current as App)?.ShowSettings(SettingsWindow.ApiKeysPage));

        // ---- The Clipboard board ----

        // The file being dragged, if one is, and where it was picked up: how far the pointer was
        // from the file's top left corner, which is what keeps the file from jumping under the
        // pointer the moment it is taken hold of. Where it started from is remembered separately,
        // because a click is a drag that went nowhere, and one of those is not worth writing down.
        private PinnedFile? _draggingFile;
        private Point _dragGrab;
        private Point _dragFrom;

        // Raised by the board rather than by the file, because the capture is the board's: once the
        // mouse belongs to it, every move and every release arrives there and nowhere else.
        private int _dragZ;

        // Taking hold of a file. A file is not a button: nothing happens on the way down, and the
        // two clicks that open it are the mouse's other gesture, so this is where the two part
        // company - a second click opens the file, the first is the start of a drag.
        private void ClipboardFile_Down(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PinnedFile file) return;

            e.Handled = true;   // don't let the click reach Island_Click and shut the panel

            if (e.ClickCount == 2)
            {
                ReleaseFileDrag();
                OpenFile(file.Path, file.Name, file.Preview);
                return;
            }

            _draggingFile = file;
            _dragFrom = new Point(file.X, file.Y);

            Point grab = e.GetPosition(ClipboardList);
            _dragGrab = new Point(grab.X - file.X, grab.Y - file.Y);

            file.IsDragging = true;

            // Held above everything else on the board for as long as it is being moved, so that a
            // file can always be dragged out from under another one - and only for that long, since
            // the list on disk stays the order the files were put there.
            if (sender is DependencyObject element
                && ItemsControl.ContainerFromElement(ClipboardList, element) is UIElement container)
                Panel.SetZIndex(container, ++_dragZ);

            Mouse.Capture(ClipboardList);
        }

        private void ClipboardList_Move(object sender, MouseEventArgs e)
        {
            if (_draggingFile is null) return;

            // Let go of outside the window, or the capture taken by something else, and the file
            // is put down where it is rather than left being carried forever.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ReleaseFileDrag();
                return;
            }

            // Carried off the island, the file has left the only place it lives: from here it is a
            // Windows drag - the same one Explorer starts - and it can end anywhere a file can.
            if (!OnIsland(e))
            {
                DragFileOut(_draggingFile);
                return;
            }

            Point at = e.GetPosition(ClipboardList);
            Pinboard.Current.Move(_draggingFile, at.X - _dragGrab.X, at.Y - _dragGrab.Y);
        }

        // Whether the pointer is still on the island at all. The island is the whole of where a
        // file here can be, so this is the line between moving one about and taking it away.
        private bool OnIsland(MouseEventArgs e)
        {
            Point here = e.GetPosition(this);
            Point corner = Island.TranslatePoint(new Point(0, 0), this);
            return new Rect(corner, Island.RenderSize).Contains(here);
        }

        // Hands the file over to Windows, which is the only way a file can be dragged to somewhere
        // that is not here. The call does not return until the drag ends.
        //
        // What is handed over is a copy, never the file itself: the board keeps the file and keeps
        // its place on it, and what the place it was dropped on does with the copy is that place's
        // business. A drag that was abandoned ends in exactly the same state, so either way the file
        // goes back to where it was picked up from - which is where it would have stayed had nobody
        // touched it. Nothing was written to disk on the way, so nothing has to be written back.
        private void DragFileOut(PinnedFile file)
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { file.Path });

            // The board is done with it - the capture goes back before Windows takes its own, and
            // the file stops saying it is being carried. The field is cleared first, because giving
            // the capture back is what raises the handler that would otherwise tidy up after this.
            _draggingFile = null;
            Mouse.Capture(null);
            file.IsDragging = false;

            DragDrop.DoDragDrop(ClipboardList, data, DragDropEffects.Copy);

            file.X = _dragFrom.X;
            file.Y = _dragFrom.Y;
        }

        private void ClipboardList_Up(object sender, MouseButtonEventArgs e)
        {
            if (_draggingFile is null) return;

            e.Handled = true;   // a drag that went nowhere is not a click on the island either
            ReleaseFileDrag();
        }

        private void ClipboardList_LostCapture(object sender, MouseEventArgs e) => ReleaseFileDrag();

        // Puts the file down: the capture goes back, the file stops saying it is being carried, and
        // the file is written - but only if it ended up somewhere other than where it started.
        private void ReleaseFileDrag()
        {
            var file = _draggingFile;
            if (file is null) return;

            // Let go of the field before the capture, because giving the capture back is itself
            // what raises the handler that calls this.
            _draggingFile = null;

            Mouse.Capture(null);
            file.IsDragging = false;

            if (file.X != _dragFrom.X || file.Y != _dragFrom.Y) Pinboard.Current.Save();
        }

        // Taking a file off the board. The badge owns the click, so it never reaches the file
        // underneath it and never starts a drag.
        //
        // A picture taken off a page is the app's own copy, and there is no other copy of it on the
        // disk, so it goes with the pin - unless the same file is pinned more than once, in which
        // case the other pin still needs it. Everything else on the board is a file of the person's
        // own, and taking it off the board leaves it where it was.
        private void ClipboardRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if ((sender as FrameworkElement)?.DataContext is not PinnedFile file) return;

            Pinboard.Current.Remove(file);
            ForgetHoverName();   // the thing it was naming is no longer there

            bool stillPinned = Pinboard.Current.Files
                .Any(pinned => string.Equals(pinned.Path, file.Path, StringComparison.OrdinalIgnoreCase));

            if (!stillPinned) WebImage.Discard(file.Path);
        }

        // The board's size, from the board's own layout - which is the only thing that knows how big
        // it actually is. It is also where a place written by hand that the panel has no room for
        // any more is brought back inside it.
        private void ClipboardList_SizeChanged(object sender, SizeChangedEventArgs e) =>
            Pinboard.Current.Resize(ClipboardList.ActualWidth, ClipboardList.ActualHeight);

        private static readonly Brush BoardHotOutline =
            new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

        // The whole board lights up while a file is over it, because the whole board is where that
        // file will land: a file dropped here is not dropped on anything in particular.
        private void HighlightBoard(bool on) =>
            ClipboardBoard.BorderBrush = on ? BoardHotOutline : Brushes.Transparent;

        // What a file drop is carrying, if it is carrying files at all. The Launchpad is choosy
        // about what it takes, because a tile has to be able to open what is on it; the board is
        // not, because a file is opened by whatever Windows opens it with.
        private static string[]? DroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

            string[] present = [.. files.Where(File.Exists)];
            return present.Length > 0 ? present : null;
        }

        // Opens a file with whatever Windows opens it with - an app from the Launchpad, a document
        // from the board - and, when it will not open, says so rather than quietly doing nothing.
        // The tile it was opened from stays either way: a file that has been moved since it was put
        // here, or one Windows no longer knows how to open, is still the file that was put here.
        private void OpenFile(string path, string name, ImageSource? icon)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException
                                        or UnauthorizedAccessException or NotSupportedException)
            {
                ShowBanner($"Couldn't open {name}", path, icon);
            }
        }

        // ---- The Launchpad ----

        // The tile a dragged file is over, if it is over one at all.
        //
        // A drop arrives at the island as a whole - see the note on the drag handler - so
        // hit-testing it is what ties a drop to the tile it was aimed at. The walk up the tree stops
        // at the tab: everything below that belongs to a tile and everything above it belongs to the
        // island, so a hit on the tab itself - the gaps between tiles, the margins around them - is
        // a miss.
        private LaunchpadSlot? SlotAt(DragEventArgs e)
        {
            if (LaunchpadContent.Visibility != Visibility.Visible) return null;

            var element = HoverZone.InputHitTest(e.GetPosition(HoverZone)) as DependencyObject;
            while (element is not null && !ReferenceEquals(element, LaunchpadContent))
            {
                if (element is FrameworkElement { DataContext: LaunchpadSlot slot }) return slot;
                element = ParentOf(element);
            }

            return null;
        }

        // Exactly one tile wears this at a time: the tile under the file being dragged, which is the
        // only thing on the island that says where the drop will land.
        private LaunchpadSlot? _dropSlot;

        private void HighlightSlot(LaunchpadSlot? slot)
        {
            if (ReferenceEquals(_dropSlot, slot)) return;

            if (_dropSlot is not null) _dropSlot.IsDropTarget = false;
            _dropSlot = slot;
            if (_dropSlot is not null) _dropSlot.IsDropTarget = true;
        }

        // Opens what a tile holds. An empty tile has nothing to open, so a click on one is taken
        // as the other way of filling it - the same question a drop answers, asked in the way a
        // file is normally chosen rather than dragged.
        private void LaunchpadTile_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // don't let the click bubble to Island_Click and shut the panel

            if ((sender as FrameworkElement)?.DataContext is not LaunchpadSlot slot) return;

            if (slot.IsEmpty)
            {
                PickAppFor(slot);
                return;
            }

            OpenFile(slot.Path, slot.Name, slot.Icon);
        }

        // Filling an empty tile by hand: the same question a drop answers, asked with the picker
        // rather than with the mouse. Only the file matters - which tile it goes in needs no
        // asking, it is the one that was clicked - and it goes in the way a drop would, shortcut
        // and all.
        private void PickAppFor(LaunchpadSlot slot)
        {
            var picker = new OpenFileDialog
            {
                Title = "Choose an app for this tile",
                Filter = "Apps (*.exe;*.lnk;*.bat;*.cmd;*.msc;*.url;*.appref-ms)"
                       + "|*.exe;*.lnk;*.bat;*.cmd;*.msc;*.url;*.appref-ms"
                       + "|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            // The island is deliberately unable to hold the foreground - that is what keeps it out
            // of Alt+Tab and off the taskbar - and a dialog shown by a process that is not in
            // front of anything tends to open behind what is. The keyboard is borrowed for as long
            // as the picker is up, and handed back to whoever had it when it closes.
            //
            // The island also gives up the topmost band for that time. It sits above everything by
            // design, which would otherwise leave the picker's own title bar underneath the widget
            // - a dialog with the island lying across the top of it. A window below the one that
            // asked for it, on the other hand, is exactly where a picker belongs, and the band is
            // claimed back the moment the dialog closes.
            _pickerOpen = true;
            Topmost = false;

            TakeKeyboard();
            bool? chosen;
            try
            {
                chosen = picker.ShowDialog(this);
            }
            finally
            {
                ReleaseKeyboard();

                _pickerOpen = false;
                Topmost = true;
                ForceTopmost();
            }

            if (chosen != true) return;

            int index = Launchpad.Current.Slots.IndexOf(slot);
            if (index >= 0) Launchpad.Current.Place(index, picker.FileName);
        }

        // The Launchpad explains itself the first time the tab is ever opened, and only then. The
        // flag is written as the page appears rather than as it is put away, so that it really is
        // once: a tour that returned because the island was closed over it would be a tour that
        // never ends. After this, opening the tab opens the apps.
        private void ShowLaunchpadIntroIfNew()
        {
            if (Settings.Current.LaunchpadIntroSeen) return;

            Settings.Current.Set(() => Settings.Current.LaunchpadIntroSeen = true);

            // Both of these are only ever done by something that put the page away, and both have
            // to be undone here rather than left to the next reading of the flag.
            LaunchpadIntro.BeginAnimation(OpacityProperty, null);
            LaunchpadIntro.IsHitTestVisible = true;
            LaunchpadIntro.Visibility = Visibility.Visible;
        }

        // Reading it is agreeing with it: a click anywhere on the page - the plate or the button -
        // puts it away and leaves the grid underneath in reach. That click is also the first one
        // somebody aiming at a tile will make, and it should not close the island instead.
        private void LaunchpadIntro_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            HideLaunchpadIntro(fade: true);
        }

        // The page is faded away rather than switched off. It is a black plate lying over a grid
        // of black tiles, so what the fade actually draws is the page's writing going and the
        // tiles coming, one over the other - where cutting it would show both change between two
        // frames. It is also out of the way of the mouse the moment the fade starts: this is the
        // first click a new user makes, and the click after it is meant for a tile.
        //
        // A drag is the one case that skips the fade. The file is already coming down and there
        // has to be a tile under it to land on.
        private void HideLaunchpadIntro(bool fade)
        {
            if (LaunchpadIntro.Visibility != Visibility.Visible) return;

            LaunchpadIntro.IsHitTestVisible = false;

            if (!fade)
            {
                LaunchpadIntro.Visibility = Visibility.Collapsed;
                return;
            }

            var fadeOut = new DoubleAnimation(0, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) => LaunchpadIntro.Visibility = Visibility.Collapsed;

            LaunchpadIntro.BeginAnimation(OpacityProperty, fadeOut);
        }

        // Empties a tile, leaving it for something else to be dropped on. The badge that does this
        // lives inside the tile's own button, so the click is dealt with here before the tile gets
        // to see it - which is what stops it opening the very app it is about to take away.
        private void LaunchpadRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if ((sender as FrameworkElement)?.DataContext is not LaunchpadSlot slot || slot.IsEmpty) return;

            int index = Launchpad.Current.Slots.IndexOf(slot);
            if (index >= 0) Launchpad.Current.Clear(index);

            ForgetHoverName();   // the thing it was naming is no longer there
        }

        // ---- Taking the keyboard ----

        // The island is a non-activating window on purpose (WS_EX_NOACTIVATE), and that is also
        // what stops it ever receiving typed keys. The link box is the one place that needs real
        // keyboard input, so the flag comes off for as long as it is open; whatever was in front
        // gets the foreground back when it closes. WS_EX_TOOLWINDOW stays set throughout, so the
        // island keeps out of Alt+Tab even while it is holding the keyboard.
        private IntPtr _focusReturn;
        private bool _urlInputOpen;
        private bool _quitting;

        private void TakeKeyboard()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            _focusReturn = GetForegroundWindow();

            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
            Activate();
            SetForegroundWindow(hwnd);   // Activate alone can be refused
        }

        private void ReleaseKeyboard()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);

            if (_focusReturn != IntPtr.Zero)
            {
                SetForegroundWindow(_focusReturn);
                _focusReturn = IntPtr.Zero;
            }
        }

        // Called by the tray's Quit before it shuts the app down. The only close this window ever
        // holds back is the link box's, and that has to give way to a deliberate exit - otherwise
        // quitting with the box open would leave nothing on screen and no way to ask again.
        internal void AllowShutdown() => _quitting = true;

        // Holding the keyboard is the only thing that can bring the island an Alt+F4, and losing
        // the whole widget to a key aimed at the app behind it would be a poor trade: closing the
        // box is the sensible reading of it.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_urlInputOpen && !_quitting)
            {
                e.Cancel = true;
                HideUrlInput();
                return;
            }

            base.OnClosing(e);
        }

        private static bool ClipboardSetText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        private static BitmapImage CreateBitmap(byte[] bytes, int decodeWidth = 0)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // Swipe transition for track title and artist text changes

        private void AnimateTrackSwipe()
        {
            _isSwipeAnimating = true;
            int dir = _swipeDirection;
            double distance = 140;
            var outDuration = TimeSpan.FromMilliseconds(130);
            var inDuration = TimeSpan.FromMilliseconds(170);

            var slideOut = new DoubleAnimation(dir * -distance, outDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var fadeOut = new DoubleAnimation(0, outDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            slideOut.Completed += (s, e) =>
            {
                // Apply buffered track text
                if (_pendingTitle != null)
                {
                    TrackTitle.Text = _pendingTitle;
                    _pendingTitle = null;
                }
                if (_pendingArtist != null)
                {
                    ArtistName.Text = _pendingArtist;
                    _pendingArtist = null;
                }

                // Release animation hold, snap to incoming side
                TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, null);
                TrackInfoPanel.BeginAnimation(OpacityProperty, null);
                TrackInfoShift.X = dir * distance;
                TrackInfoPanel.Opacity = 0;

                // Slide in
                var slideIn = new DoubleAnimation(0, inDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeIn = new DoubleAnimation(1, inDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                slideIn.Completed += (s2, e2) =>
                {
                    _isSwipeAnimating = false;

                    // If another track change arrived mid-animation, chain it while
                    // keeping the current direction, so rapid presses of the same
                    // button keep swiping the same way. Only fall back to the default
                    // direction once we're idle again (for system-level changes).
                    if (_pendingTitle != null || _pendingArtist != null)
                        AnimateTrackSwipe();
                    else
                        _swipeDirection = 1;
                };

                TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, slideIn);
                TrackInfoPanel.BeginAnimation(OpacityProperty, fadeIn);
            };

            TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, slideOut);
            TrackInfoPanel.BeginAnimation(OpacityProperty, fadeOut);
        }

        // ---- Quick action targets ----

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        // ---- Overlay window styles ----

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // ---- Foreground hand-off, for the link box ----

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---- Always-on-top enforcement ----

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
