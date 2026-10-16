using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace TopNotch
{
    public partial class MainWindow : Window
    {
        private bool _expanded;
        private bool _hovered;

        private readonly MediaService _media = new();

        private readonly AudioService _audio = new();

        private bool _updatingFromSystem;

        // Swipe transition state (text only)
        private int _swipeDirection = 1;      // 1 = next (slide left), -1 = previous (slide right)
        private bool _isSwipeAnimating;
        private string? _pendingTitle;
        private string? _pendingArtist;

        // Tab switching state
        private bool _currentTabIsMedia = true;
        private bool _isTabAnimating;

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
                _audio.Dispose();
            };

            _audio.DeviceChanged += (deviceName, isBluetooth) => Dispatcher.BeginInvoke(() =>
            {
                string clean = deviceName.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
                UpdateDeviceDisplay(clean, isBluetooth);
            });

            _media.PlayingChanged += playing => Dispatcher.BeginInvoke(() =>
            {
                PlayPause.Source = playing ? _pauseIcon : _playIcon;
            });

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

        // Tab switching

        private void UpdateTabHighlight(bool showMedia)
        {
            var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
            MediaTab.Foreground = showMedia ? System.Windows.Media.Brushes.White : inactive;
            DeviceTab.Foreground = showMedia ? inactive : System.Windows.Media.Brushes.White;
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

            if (!showMedia)
            {
                string currentAudio = _audio.DefaultDeviceName ?? "Connected Device";
                string cleanAudio = currentAudio.Replace("Headphones (", "").Replace("Speakers (", "").Replace(")", "").Trim();
                UpdateDeviceDisplay(cleanAudio, _audio.IsCurrentDeviceBluetooth);
            }

            if (!_expanded)
            {
                MediaContent.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
                DevicesContent.Visibility = showMedia ? Visibility.Collapsed : Visibility.Visible;
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
                DevicesContent.Visibility = showMedia ? Visibility.Collapsed : Visibility.Visible;

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

        private void Zone_MouseEnter(object sender, MouseEventArgs e)
        {
            _hovered = true;
            UpdateContentVisibility();
            MoveIsland(HoverY);
        }

        private void Zone_MouseLeave(object sender, MouseEventArgs e)
        {
            _hovered = false;
            UpdateContentVisibility();
            MoveIsland(RestY);
        }

        private void MoveIsland(double targetY)
        {
            if (!_expanded)
            {
                var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                };
                IslandShift.BeginAnimation(TranslateTransform.YProperty, anim);
            }
        }

        private void UpdateContentVisibility()
        {
            // Expanded shows the tabbed UI; retracted+hovered shows the peek panel; otherwise nothing.
            ExpandedContent.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            PeekContent.Visibility = (!_expanded && _hovered) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Island_Click(object sender, MouseButtonEventArgs e)
        {
            _expanded = !_expanded;
            UpdateContentVisibility();

            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            var duration = TimeSpan.FromMilliseconds(350);

            Island.BeginAnimation(WidthProperty,
                new DoubleAnimation(_expanded ? 420 : 160, duration) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty,
                new DoubleAnimation(_expanded ? 120 : 32, duration) { EasingFunction = ease });
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
                    _swipeDirection = 1; // reset to default for system-level changes

                    // If another track change arrived mid-animation, chain it
                    if (_pendingTitle != null || _pendingArtist != null)
                        AnimateTrackSwipe();
                };

                TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, slideIn);
                TrackInfoPanel.BeginAnimation(OpacityProperty, fadeIn);
            };

            TrackInfoShift.BeginAnimation(TranslateTransform.XProperty, slideOut);
            TrackInfoPanel.BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}
