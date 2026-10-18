using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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

        private bool _updatingFromSystem;

        // Swipe transition state (text only)
        private int _swipeDirection = 1;      // 1 = next (slide left), -1 = previous (slide right)
        private bool _isSwipeAnimating;
        private string? _pendingTitle;
        private string? _pendingArtist;

        // Tab switching state
        private enum Tab { Home, Media, History }

        private Tab _currentTab = Tab.Home;
        private bool _isTabAnimating;

        // A quick action waiting on the user to confirm it. Null when nothing is armed.
        private enum PowerAction { Shutdown, Restart, Sleep, Lock }

        private PowerAction? _pendingPowerAction;

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

            HistoryList.ItemsSource = _history;
            HistoryEmpty.Visibility = Visibility.Visible;   // nothing recorded yet

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

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
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
            DeviceIcon.Source = isBluetooth ? _bluetoothAudioIcon : _audioIcon;
        }

        private void UpdatePrivacyDots(bool micActive, bool cameraActive)
        {
            AnimateSlot(MicSlot, MicSlotWidth, micActive);
            AnimateSlot(CameraSlot, CameraSlotWidth, cameraActive);
        }

        // ---- Home tab ----

        private static readonly Duration BarDuration = TimeSpan.FromMilliseconds(450);

        // Day-and-month with the month spelled out, and a 12-hour clock, deliberately in the
        // invariant culture: the rest of the UI is English, and "tt" is what guarantees a literal
        // AM/PM designator instead of a locale-specific one.
        private void UpdateClock()
        {
            var now = DateTime.Now;

            // Time and designator are separate runs so the designator renders smaller; the run
            // supplies its own leading space to keep the two apart.
            string time = now.ToString("h:mm", CultureInfo.InvariantCulture);
            string meridiem = " " + now.ToString("tt", CultureInfo.InvariantCulture);
            string date = now.ToString("d MMMM", CultureInfo.InvariantCulture);

            // The timer runs every second but the text only changes once a minute, so skip the
            // assignment (and the layout pass it would trigger) when nothing changed.
            if (ClockTimeRun.Text != time) ClockTimeRun.Text = time;
            if (ClockMeridiemRun.Text != meridiem) ClockMeridiemRun.Text = meridiem;
            if (DateText.Text != date) DateText.Text = date;
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
        private void QuickAction_PointerEnter(object sender, MouseEventArgs e)
        {
            var button = (System.Windows.Controls.Button)sender;
            bool top = System.Windows.Controls.Grid.GetRow(button) == 1;
            var line = top ? TopRowCaption : BottomRowCaption;
            var shift = top ? TopRowCaptionShift : BottomRowCaptionShift;

            line.Text = button.Tag as string ?? string.Empty;
            shift.X = (System.Windows.Controls.Grid.GetColumn(button) - 1) * 40;
            FadeCaption(line, 1, 120);
            FadeCaption(top ? BottomRowCaption : TopRowCaption, 0, 120);
        }

        private void QuickAction_PointerLeave(object sender, MouseEventArgs e)
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
            _pendingPowerAction = action;

            string name = ActionName(action);
            ConfirmPrompt.Text = name + "?";
            ConfirmAccept.Content = name;

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
            _pendingPowerAction = null;

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
            if (_pendingPowerAction is not PowerAction action) return;

            DismissConfirm();   // drop the prompt first, so nothing is left showing if this fails
            RunPowerAction(action);
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

        // The active tab is marked twice over: brighter than the others, and underlined. All three
        // underlines are laid out from the start and only their opacity changes, so switching tabs
        // never re-measures the strip and the labels stay put.
        private void UpdateTabHighlight(Tab active)
        {
            var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A));
            HomeTab.Foreground = active == Tab.Home ? System.Windows.Media.Brushes.White : inactive;
            MediaTab.Foreground = active == Tab.Media ? System.Windows.Media.Brushes.White : inactive;
            HistoryTab.Foreground = active == Tab.History ? System.Windows.Media.Brushes.White : inactive;

            FadeUnderline(HomeUnderline, active == Tab.Home);
            FadeUnderline(MediaUnderline, active == Tab.Media);
            FadeUnderline(HistoryUnderline, active == Tab.History);
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
        }

        private async void Tab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // don't let the click bubble to Island_Click and toggle expand

            Tab target = sender == HomeTab ? Tab.Home
                       : sender == MediaTab ? Tab.Media
                       : Tab.History;
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

        // Island animation

        private const double RestY = -20;
        private const double HoverY = 0;
        private const double ExpandedWidth = 460;
        private const double ExpandedHeight = 142;
        private const double PeekWidth = 160;
        private const double PeekHeight = 32;

        // A notification banner is as long as the expanded island but a fixed 66 tall. It used to be
        // "half the island", which stopped meaning anything once the island grew to make room for the
        // quick-action captions - the banner is its own thing and should not follow that.
        private const double NotificationWidth = ExpandedWidth;
        private const double NotificationHeight = 66;

        private readonly NotificationService _notification = new();
        private readonly DispatcherTimer _notifyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private bool _notifying;    // a banner currently owns the island
        private bool _dismissing;    // banner is fading out, but still painted
        private double _restoreWidth;    // island size to return to after a banner
        private double _restoreHeight;

        private void Zone_MouseEnter(object sender, MouseEventArgs e)
        {
            _hovered = true;
            UpdateContentVisibility();
            if (_notifying) return;   // the banner owns the island until it's dismissed
            MoveIsland(HoverY);
        }

        private void Zone_MouseLeave(object sender, MouseEventArgs e)
        {
            _hovered = false;
            UpdateContentVisibility();
            if (_notifying) return;
            MoveIsland(RestY);
        }

        // Slides the island into view (hover) or up out of sight (rest).
        private void MoveIsland(double targetY)
        {
            if (!_expanded)
                SlideIsland(targetY);
        }

        private void SlideIsland(double targetY)
        {
            var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };
            IslandShift.BeginAnimation(TranslateTransform.YProperty, anim);
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
            // A live (or still-fading) banner overrides everything else; otherwise the expanded
            // tabbed UI shows, or the peek panel when retracted and hovered.
            bool banner = _notifying || _dismissing;

            NotificationContent.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContent.Visibility = (!banner && _expanded) ? Visibility.Visible : Visibility.Collapsed;
            PeekContent.Visibility = (!banner && !_expanded && _hovered) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Island_Click(object sender, MouseButtonEventArgs e)
        {
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
                SlideIsland(HoverY);   // a resting island should come fully on screen
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
            MoveIsland(_hovered ? HoverY : RestY);
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

        // ---- Taking the keyboard ----

        // The island is a non-activating window on purpose (WS_EX_NOACTIVATE), and that is also
        // what stops it ever receiving typed keys. The link box is the one place that needs real
        // keyboard input, so the flag comes off for as long as it is open; whatever was in front
        // gets the foreground back when it closes. WS_EX_TOOLWINDOW stays set throughout, so the
        // island keeps out of Alt+Tab even while it is holding the keyboard.
        private IntPtr _focusReturn;
        private bool _urlInputOpen;

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

        // Holding the keyboard is the only thing that can bring the island an Alt+F4, and losing
        // the whole widget to a key aimed at the app behind it would be a poor trade: closing the
        // box is the sensible reading of it.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_urlInputOpen)
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
