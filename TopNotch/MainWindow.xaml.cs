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

        private bool _pickerOpen;

        private bool _updatingFromSystem;

        private int _swipeDirection = 1;
        private bool _isSwipeAnimating;
        private string? _pendingTitle;
        private string? _pendingArtist;

        private enum Tab { Home, Media, History, Launchpad, Clipboard }

        private Tab _currentTab = Tab.Home;
        private bool _isTabAnimating;

        private enum PowerAction { Shutdown, Restart, Sleep, Lock }

        private Action? _pendingConfirm;

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

            TrackTitle.Text = "No media playing";
            ArtistName.Text = "";
            SetMediaActive(false);
            UpdateTabHighlight(Tab.Home);
            ShowTabContent(Tab.Home);
            UpdateClock();

            Settings.Changed += OnSettingsChanged;

            HistoryList.ItemsSource = _history;
            HistoryEmpty.Visibility = Visibility.Visible;

            LaunchpadList.ItemsSource = Launchpad.Current.Slots;

            ClipboardList.ItemsSource = Pinboard.Current.Files;

            _media.TrackChanged += (title, artist) => Dispatcher.Invoke(() =>
            {
                bool hasTrack = !string.IsNullOrWhiteSpace(title);
                SetMediaActive(hasTrack);

                string newTitle = hasTrack ? title : "No media playing";
                string newArtist = hasTrack ? artist : "";

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

            _summonTimer.Tick += SummonTimer_Tick;
            _fullscreenTimer.Tick += FullscreenTimer_Tick;
            _hoverNameTimer.Tick += HoverNameTimer_Tick;
            Island.Width = PeekWidth;
            Island.Height = PeekHeight;
            ApplyStyle(instant: true);
            WatchFullscreen();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            exStyle &= ~WS_EX_APPWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ForceTopmost();

            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _topmostTimer.Tick += (s, args) => ForceTopmost();
            _topmostTimer.Start();
        }

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
                if (Volume.IsMouseCaptured ||
                    (Volume.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)) return;

                _updatingFromSystem = true;
                Volume.Value = Volume.Minimum + level * (Volume.Maximum - Volume.Minimum);
                _updatingFromSystem = false;
            });

            Volume.ValueChanged += (s, args) =>
            {
                if (_updatingFromSystem) return;
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

            PeekDevice.Text = deviceName;

            DeviceIcon.Source = isBluetooth ? _bluetoothAudioIcon : _audioIcon;
        }

        private void UpdatePrivacyDots(bool micActive, bool cameraActive)
        {
            AnimateSlot(MicSlot, MicSlotWidth, micActive);
            AnimateSlot(CameraSlot, CameraSlotWidth, cameraActive);
        }

        private static readonly Duration BarDuration = TimeSpan.FromMilliseconds(450);

        private void UpdateClock()
        {
            var now = DateTime.Now;
            bool military = Settings.Current.Use24HourClock;

            string time = now.ToString(military ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture);
            string meridiem = military ? "" : " " + now.ToString("tt", CultureInfo.InvariantCulture);
            string date = now.ToString("d MMMM", CultureInfo.InvariantCulture);

            if (ClockTimeRun.Text != time) ClockTimeRun.Text = time;
            if (ClockMeridiemRun.Text != meridiem) ClockMeridiemRun.Text = meridiem;
            if (DateText.Text != date) DateText.Text = date;
        }

        private void OnSettingsChanged()
        {
            if (_appliedStyle != Settings.Current.IslandStyle) ApplyStyle();
            else if (_appliedOutline != Settings.Current.IslandOutline) ApplyOutline();

            UpdateClock();
            HistoryList.Items.Refresh();

            WatchFullscreen();
        }

        private void UpdateSystemStats(double cpuPercent, double memoryPercent)
        {
            CpuPercentText.Text = $"{cpuPercent:0}%";
            MemoryPercentText.Text = $"{memoryPercent:0}%";

            AnimateBar(CpuFill, CpuTrack, cpuPercent);
            AnimateBar(MemoryFill, MemoryTrack, memoryPercent);
        }

        private static void AnimateBar(System.Windows.Controls.Border fill, System.Windows.Controls.Border track, double percent)
        {
            double full = track.ActualWidth;
            if (full <= 0) return;

            double target = full * Math.Clamp(percent, 0, 100) / 100.0;
            fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(target, BarDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void Shutdown_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Shutdown);

        private void Restart_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Restart);

        private void Sleep_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Sleep);

        private void Lock_Click(object sender, RoutedEventArgs e) => AskToConfirm(PowerAction.Lock);

        private void QuickAction_PointerEnter(object sender, MouseEventArgs e) =>
            ShowCaption((System.Windows.Controls.Button)sender);

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

        private static readonly TimeSpan ConfirmFade = TimeSpan.FromMilliseconds(160);

        private void AskToConfirm(PowerAction action)
        {
            string name = ActionName(action);
            AskToConfirm(name + "?", name, () => RunPowerAction(action));
        }

        private void AskToConfirm(string prompt, string accept, Action consequence)
        {
            _pendingConfirm = consequence;

            ConfirmPrompt.Text = prompt;
            ConfirmAccept.Content = accept;

            ConfirmOverlay.BeginAnimation(OpacityProperty, null);
            ConfirmOverlay.Visibility = Visibility.Visible;
            ConfirmOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

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

            DismissConfirm();
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

        private static void DoShutdown() => StartDetached("shutdown.exe", "/s /t 0");

        private static void DoRestart() => StartDetached("shutdown.exe", "/r /t 0");

        private static void DoSleep() => Task.Run(() => SetSuspendState(false, false, false));

        private static void DoLock() => LockWorkStation();

        private static void StartDetached(string fileName, string arguments) =>
            Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

        private const double MicSlotWidth = 8;
        private const double CameraSlotWidth = 13;
        private const double WaveSlotWidth = 22;
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

            if (tab == Tab.Launchpad) ShowLaunchpadIntroIfNew();

            if (tab == Tab.Clipboard) Pinboard.Current.HoldAll();
        }

        private async void Tab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

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
                ShowTabContent(target);

                TabContentShift.BeginAnimation(TranslateTransform.XProperty, null);
                TabContentHost.BeginAnimation(OpacityProperty, null);
                TabContentShift.X = dir * distance;
                TabContentHost.Opacity = 0;

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

        private const double ExpandedWidth = 460;
        private const double ExpandedHeight = 142;
        private const double PeekWidth = 160;
        private const double PeekHeight = 32;

        private const double EdgeOverlap = 3;

        private const double NotchRestY = -20;
        private const double PillGap = 10;

        private double HoverY => Floating ? EdgeOverlap + PillGap : 0;

        private double RestY => Floating ? EdgeOverlap - Math.Max(Island.Height, PeekHeight) - 10
                                         : NotchRestY;

        private const double AwayY = EdgeOverlap - ExpandedHeight;

        private double HomeY => _away ? AwayY : Held ? HoverY : RestY;

        private const double NotificationWidth = ExpandedWidth;
        private const double NotificationHeight = 66;

        private static bool Floating => Settings.Current.IslandStyle == IslandStyle.DynamicIsland;

        private bool _pillOut;

        private bool _pillLeaving;

        private IslandStyle _appliedStyle;

        private bool _appliedOutline;

        private readonly DispatcherTimer _summonTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };

        private bool _away;

        private readonly DispatcherTimer _fullscreenTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        private const double ReachWidth = 280;

        private void SummonTimer_Tick(object? sender, EventArgs e)
        {
            if (_away) return;

            bool reaching = PointerAtIsland();

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

        private static bool PointerAtIsland()
        {
            if (!GetCursorPos(out POINT point)) return false;
            if (Math.Abs(point.X - SystemParameters.PrimaryScreenWidth / 2) > ReachWidth / 2) return false;

            if (point.Y <= EdgeOverlap + PillGap) return true;

            return point.Y <= EdgeOverlap + PillGap + PeekHeight
                   && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }

        private bool Held => _hovered || _expanded || _notifying || _dismissing || _urlInputOpen
                             || ConfirmOverlay.Visibility == Visibility.Visible;

        private readonly NotificationService _notification = new();
        private readonly DispatcherTimer _notifyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private bool _notifying;
        private bool _dismissing;
        private double _restoreWidth;
        private double _restoreHeight;

        private void Zone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_away) return;

            _hovered = true;
            UpdateContentVisibility();
            if (_notifying) return;
            ShowIsland();
        }

        private void Zone_MouseLeave(object sender, MouseEventArgs e)
        {
            _hovered = false;
            UpdateContentVisibility();
            if (_notifying) return;

            if (Floating) return;

            HideIsland();
        }

        private void ShowIsland()
        {
            if (_away) return;

            _pillLeaving = false;
            _pillOut = true;
            SlideIsland(HoverY);
        }

        private void HideIsland()
        {
            if (_expanded || _pillLeaving) return;
            _pillLeaving = true;

            SlideIsland(HomeY, () =>
            {
                if (!_pillLeaving) return;

                _pillLeaving = false;
                _pillOut = false;
                UpdateContentVisibility();
            });
        }

        private void SlideIsland(double targetY, Action? done = null)
        {
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

        private void FullscreenTimer_Tick(object? sender, EventArgs e)
        {
            bool away = Fullscreen.Active;
            if (away == _away) return;

            StepAside(away);
        }

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

            bool banner = _notifying || _dismissing;

            bool peeking = _hovered || (Floating && _pillOut);

            NotificationContent.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContent.Visibility = (!banner && _expanded) ? Visibility.Visible : Visibility.Collapsed;
            PeekContent.Visibility = (!banner && !_expanded && peeking) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static readonly Brush OutlineEdge = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D));

        private void ApplyStyle(bool instant = false)
        {
            _appliedStyle = Settings.Current.IslandStyle;

            var corner = Floating ? new CornerRadius(PeekHeight / 2) : new CornerRadius(0, 0, 20, 20);

            Island.CornerRadius = corner;
            ConfirmOverlay.CornerRadius = corner;
            UrlOverlay.CornerRadius = corner;

            ApplyOutline();

            if (Floating) _summonTimer.Start();
            else _summonTimer.Stop();

            _pillLeaving = false;
            _pillOut = Held && !_away;

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

        private void ApplyOutline()
        {
            bool on = Settings.Current.IslandOutline;
            _appliedOutline = on;

            Island.BorderThickness = on ? new Thickness(1) : new Thickness(0);
            Island.BorderBrush = on ? OutlineEdge : null;
        }

        private void Island_Click(object sender, MouseButtonEventArgs e)
        {
            if (_away) return;

            if (_notifying)
            {
                DismissNotification();
                return;
            }

            if (_urlInputOpen)
            {
                HideUrlInput();
                return;
            }

            if (ConfirmOverlay.Visibility == Visibility.Visible)
            {
                DismissConfirm();
                return;
            }

            _expanded = !_expanded;
            UpdateContentVisibility();
            AnimateIslandSize(_expanded ? ExpandedWidth : PeekWidth, _expanded ? ExpandedHeight : PeekHeight);
        }

        private readonly DispatcherTimer _hoverNameTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };

        private string? _hoverName;
        private Point _hoverAt;

        private void Island_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                RestedOn(null, e);
                return;
            }

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

        private static DependencyObject? ParentOf(DependencyObject at)
        {
            if (at is FrameworkContentElement text) return text.Parent;

            return at is Visual ? VisualTreeHelper.GetParent(at) : null;
        }

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

            HoverName.Visibility = Visibility.Visible;
            HoverName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = HoverName.DesiredSize;

            Canvas.SetLeft(HoverName, Math.Max(0, Math.Min(_hoverAt.X + 14, HoverCanvas.ActualWidth - size.Width)));
            Canvas.SetTop(HoverName, Math.Max(0, Math.Min(_hoverAt.Y + 20, HoverCanvas.ActualHeight - size.Height)));
        }

        private void HideHoverName()
        {
            if (HoverName.Visibility != Visibility.Visible) return;
            HoverName.Visibility = Visibility.Collapsed;
        }

        private void ForgetHoverName()
        {
            _hoverName = null;
            _hoverNameTimer.Stop();
            HideHoverName();
        }

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

        private void ShowBanner(string title, string body, ImageSource? icon)
        {
            if (_away) return;

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
                _restoreWidth = double.IsNaN(Island.Width) ? ExpandedWidth : Island.Width;
                _restoreHeight = double.IsNaN(Island.Height) ? ExpandedHeight : Island.Height;

                AnimateIslandSize(NotificationWidth, NotificationHeight);
                ShowIsland();
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
            _dismissing = true;
            AnimateIslandSize(_restoreWidth, _restoreHeight);
            if (_hovered) ShowIsland(); else HideIsland();
            UpdateContentVisibility();

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                if (_notifying) return;
                _dismissing = false;
                UpdateContentVisibility();
            };
            NotificationContent.BeginAnimation(OpacityProperty, fade);
        }

        private async Task AnnounceUpdateAsync()
        {
            UpdateCheck.Result result = await UpdateCheck.RunAsync();
            if (result.Status != UpdateCheck.Status.Newer) return;

            ShowBanner($"TopNotch {result.Version} is available",
                       "Settings \u203A About has the details, and a way to get it.", null);
        }

        private void ShortenUrl_Click(object sender, RoutedEventArgs e) => ShowUrlInput();

        private void UrlCancel_Click(object sender, RoutedEventArgs e) => HideUrlInput();

        private void UrlShorten_Click(object sender, RoutedEventArgs e) => SubmitUrl();

        private void UrlOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SubmitUrl();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideUrlInput();
            }
        }

        private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            UpdateUrlHint();

        private void UpdateUrlHint()
        {
            if (UrlHint is null) return;
            UrlHint.Visibility = UrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowUrlInput()
        {
            if (ClipboardText() is { } text && TryAsHttpUrl(text, out _))
                UrlBox.Text = text.Trim();

            UrlOverlay.BeginAnimation(OpacityProperty, null);
            UrlOverlay.Visibility = Visibility.Visible;
            UrlOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ConfirmFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            _urlInputOpen = true;
            UpdateUrlHint();
            TakeKeyboard();

            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                UrlBox.Focus();
                Keyboard.Focus(UrlBox);
                UrlBox.SelectAll();
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

            HideUrlInput();

            if (!TryAsHttpUrl(typed, out string url))
            {
                ShowBanner("That doesn't look like a link", "Type or paste an address, e.g. example.com", _linkIcon);
                return;
            }

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

            ShowBanner(
                ClipboardSetText(result.Link) ? "Link copied" : "Link shortened",
                result.Link,
                _linkIcon);
        }

        private static bool TryAsHttpUrl(string? text, out string url)
        {
            url = string.Empty;

            string candidate = text?.Trim() ?? string.Empty;
            if (candidate.Length == 0 || candidate.Contains(' ')) return false;

            bool assumedScheme = !candidate.Contains("://", StringComparison.Ordinal);
            if (assumedScheme) candidate = "https://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            if (assumedScheme && !uri.Host.Contains('.')) return false;

            url = candidate;
            return true;
        }

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

        private bool _wasExpandedBeforeDrag = true;

        private void ShareImage_Click(object sender, RoutedEventArgs e)
        {
            if (!Settings.Current.HasImgBbApiKey)
            {
                AskForKey();
                return;
            }

            ShowBanner("Share an image",
                "Drop a picture on the notch and the link lands on your clipboard", _imageIcon);
        }

        private void Island_DragOver(object sender, DragEventArgs e)
        {
            if (_currentTab == Tab.Launchpad && DroppedApp(e) is not null)
            {
                HideLaunchpadIntro(fade: false);

                e.Effects = DragDropEffects.Copy;
                e.Handled = true;

                OpenForDrop();
                HighlightSlot(SlotAt(e));
                return;
            }

            bool arriving = DroppedFiles(e)?.Any(file => !Pinboard.Current.Has(file)) == true;

            if (_currentTab == Tab.Clipboard && (arriving || WebImage.Carried(e.Data)))
            {
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

            ShowCaption(ShareImageButton);
        }

        private void OpenForDrop()
        {
            if (_expanded) return;

            _wasExpandedBeforeDrag = false;
            _expanded = true;
            UpdateContentVisibility();
            AnimateIslandSize(ExpandedWidth, ExpandedHeight);
            ShowIsland();
        }

        private void Island_DragLeave(object sender, DragEventArgs e)
        {
            EndDrag();
        }

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

                if (slot is null)
                {
                    EndDrag();
                    return;
                }

                _wasExpandedBeforeDrag = true;

                int index = Launchpad.Current.Slots.IndexOf(slot);
                if (index >= 0) Launchpad.Current.Place(index, app);
                return;
            }

            if (_currentTab == Tab.Clipboard && DroppedFiles(e) is { } files)
            {
                HighlightBoard(false);
                _wasExpandedBeforeDrag = true;

                Point at = e.GetPosition(ClipboardList);
                Pinboard.Current.Add(files, at.X, at.Y);
                return;
            }

            if (_currentTab == Tab.Clipboard && WebImage.Carried(e.Data))
            {
                HighlightBoard(false);
                _wasExpandedBeforeDrag = true;

                TakePicturedFileAsync(e.Data, e.GetPosition(ClipboardList));
                return;
            }

            if (DroppedImage(e) is not { } path)
            {
                if (WebImage.Carried(e.Data))
                {
                    _wasExpandedBeforeDrag = true;
                    SharePicturedFileAsync(e.Data);
                    return;
                }

                EndDrag();
                return;
            }

            _wasExpandedBeforeDrag = true;
            ShareImageAsync(path);
        }

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

        private static string? DroppedImage(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;
            return ImgBbUploader.LooksLikeImage(files[0]) ? files[0] : null;
        }

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

            ShowBanner(
                ClipboardSetText(result.Link) ? "Link copied" : "Image uploaded",
                result.Link,
                _imageIcon);
        }

        private void AskForKey() => AskToConfirm(
            "Add an ImgBB API key",
            "Open Settings",
            () => (Application.Current as App)?.ShowSettings(SettingsWindow.ApiKeysPage));

        private PinnedFile? _draggingFile;
        private Point _dragGrab;
        private Point _dragFrom;

        private int _dragZ;

        private void ClipboardFile_Down(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PinnedFile file) return;

            e.Handled = true;

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

            if (sender is DependencyObject element
                && ItemsControl.ContainerFromElement(ClipboardList, element) is UIElement container)
                Panel.SetZIndex(container, ++_dragZ);

            Mouse.Capture(ClipboardList);
        }

        private void ClipboardList_Move(object sender, MouseEventArgs e)
        {
            if (_draggingFile is null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ReleaseFileDrag();
                return;
            }

            if (!OnIsland(e))
            {
                DragFileOut(_draggingFile);
                return;
            }

            Point at = e.GetPosition(ClipboardList);
            Pinboard.Current.Move(_draggingFile, at.X - _dragGrab.X, at.Y - _dragGrab.Y);
        }

        private bool OnIsland(MouseEventArgs e)
        {
            Point here = e.GetPosition(this);
            Point corner = Island.TranslatePoint(new Point(0, 0), this);
            return new Rect(corner, Island.RenderSize).Contains(here);
        }

        private void DragFileOut(PinnedFile file)
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { file.Path });

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

            e.Handled = true;
            ReleaseFileDrag();
        }

        private void ClipboardList_LostCapture(object sender, MouseEventArgs e) => ReleaseFileDrag();

        private void ReleaseFileDrag()
        {
            var file = _draggingFile;
            if (file is null) return;

            _draggingFile = null;

            Mouse.Capture(null);
            file.IsDragging = false;

            if (file.X != _dragFrom.X || file.Y != _dragFrom.Y) Pinboard.Current.Save();
        }

        private void ClipboardRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if ((sender as FrameworkElement)?.DataContext is not PinnedFile file) return;

            Pinboard.Current.Remove(file);
            ForgetHoverName();

            bool stillPinned = Pinboard.Current.Files
                .Any(pinned => string.Equals(pinned.Path, file.Path, StringComparison.OrdinalIgnoreCase));

            if (!stillPinned) WebImage.Discard(file.Path);
        }

        private void ClipboardList_SizeChanged(object sender, SizeChangedEventArgs e) =>
            Pinboard.Current.Resize(ClipboardList.ActualWidth, ClipboardList.ActualHeight);

        private static readonly Brush BoardHotOutline =
            new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

        private void HighlightBoard(bool on) =>
            ClipboardBoard.BorderBrush = on ? BoardHotOutline : Brushes.Transparent;

        private static string[]? DroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

            string[] present = [.. files.Where(File.Exists)];
            return present.Length > 0 ? present : null;
        }

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

        private LaunchpadSlot? _dropSlot;

        private void HighlightSlot(LaunchpadSlot? slot)
        {
            if (ReferenceEquals(_dropSlot, slot)) return;

            if (_dropSlot is not null) _dropSlot.IsDropTarget = false;
            _dropSlot = slot;
            if (_dropSlot is not null) _dropSlot.IsDropTarget = true;
        }

        private void LaunchpadTile_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if ((sender as FrameworkElement)?.DataContext is not LaunchpadSlot slot) return;

            if (slot.IsEmpty)
            {
                PickAppFor(slot);
                return;
            }

            OpenFile(slot.Path, slot.Name, slot.Icon);
        }

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

        private void ShowLaunchpadIntroIfNew()
        {
            if (Settings.Current.LaunchpadIntroSeen) return;

            Settings.Current.Set(() => Settings.Current.LaunchpadIntroSeen = true);

            LaunchpadIntro.BeginAnimation(OpacityProperty, null);
            LaunchpadIntro.IsHitTestVisible = true;
            LaunchpadIntro.Visibility = Visibility.Visible;
        }

        private void LaunchpadIntro_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            HideLaunchpadIntro(fade: true);
        }

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

        private void LaunchpadRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if ((sender as FrameworkElement)?.DataContext is not LaunchpadSlot slot || slot.IsEmpty) return;

            int index = Launchpad.Current.Slots.IndexOf(slot);
            if (index >= 0) Launchpad.Current.Clear(index);

            ForgetHoverName();
        }

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
            SetForegroundWindow(hwnd);
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

        internal void AllowShutdown() => _quitting = true;

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

                TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, null);
                TrackInfoPanel.BeginAnimation(OpacityProperty, null);
                TrackInfoShift.X = dir * distance;
                TrackInfoPanel.Opacity = 0;

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

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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
