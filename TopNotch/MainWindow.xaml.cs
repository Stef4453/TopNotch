using System.Collections.ObjectModel;
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

        private DispatcherTimer? _topmostTimer;

        private bool _updatingFromSystem;

        // Swipe transition state (text only)
        private int _swipeDirection = 1;      // 1 = next (slide left), -1 = previous (slide right)
        private bool _isSwipeAnimating;
        private string? _pendingTitle;
        private string? _pendingArtist;

        // Tab switching state
        private bool _currentTabIsMedia = true;
        private bool _isTabAnimating;

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
            UpdateTabHighlight(showMedia: true);   // Media tab is active on startup

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
                _audio.Dispose();
                _privacy.Dispose();
                _notification.Dispose();
            };

            _privacy.StateChanged += (mic, cam) => Dispatcher.BeginInvoke(() => UpdatePrivacyDots(mic, cam));
            _privacy.Init();

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

        private void UpdateTabHighlight(bool showMedia)
        {
            var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
            MediaTab.Foreground = showMedia ? System.Windows.Media.Brushes.White : inactive;
            HistoryTab.Foreground = showMedia ? inactive : System.Windows.Media.Brushes.White;
        }

        private async void Tab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // don't let the click bubble to Island_Click and toggle expand

            bool showMedia = sender == MediaTab;
            if (showMedia == _currentTabIsMedia || _isTabAnimating) return;

            _currentTabIsMedia = showMedia;
            UpdateTabHighlight(showMedia);

            _isTabAnimating = true;

            // Slight, noticeable delay before actually switching
            await Task.Delay(110);

            if (showMedia)
            {
                string currentAudio = _audio.DefaultDeviceName ?? "Connected Device";
                string cleanAudio = currentAudio.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
                UpdateDeviceDisplay(cleanAudio, _audio.IsCurrentDeviceBluetooth);
            }

            if (!_expanded)
            {
                MediaContent.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
                HistoryContent.Visibility = showMedia ? Visibility.Collapsed : Visibility.Visible;
                TabContentShift.BeginAnimation(TranslateTransform.XProperty, null);
                TabContentHost.BeginAnimation(OpacityProperty, null);
                TabContentShift.X = 0;
                TabContentHost.Opacity = 1;
                _isTabAnimating = false;
                return;
            }


            int dir = showMedia ? -1 : 1;
            AnimateTabSwitch(showMedia, dir);
        }

        private void AnimateTabSwitch(bool showMedia, int dir)
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
                MediaContent.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
                HistoryContent.Visibility = showMedia ? Visibility.Collapsed : Visibility.Visible;

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
        private const double ExpandedWidth = 420;
        private const double ExpandedHeight = 120;
        private const double PeekWidth = 160;
        private const double PeekHeight = 32;

        // A notification banner is as long as the expanded island, but about half as tall.
        private const double NotificationWidth = ExpandedWidth;
        private const double NotificationHeight = ExpandedHeight / 2;

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

        private void ShowNotification(NotificationInfo info)
        {
            NotifTitle.Text = info.Title;
            NotifBody.Text = info.Body;

            if (info.IconPng is { Length: > 0 })
            {
                NotifIcon.Source = CreateBitmap(info.IconPng, 96);
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

        // ---- Overlay window styles ----

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
