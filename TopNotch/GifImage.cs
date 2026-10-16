using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TopNotch;

// Minimal animated-GIF Image. WPF's Image only ever draws the first frame of a GIF,
// so this walks the decoder's frames on a DispatcherTimer, honouring each frame's delay.
public class GifImage : Image
{
    private GifBitmapDecoder? _decoder;
    private DispatcherTimer? _timer;
    private int _frame;

    public GifImage()
    {
        IsVisibleChanged += OnVisibilityChanged;
    }

    public static readonly DependencyProperty SourceUriProperty =
        DependencyProperty.Register(
            nameof(SourceUri), typeof(string), typeof(GifImage),
            new PropertyMetadata(null, OnSourceUriChanged));

    // A resource path such as "/Sprites/music_waveform.gif" (a leading "/" packs against
    // the application). Absolute "pack://..." URIs are accepted too.
    public string? SourceUri
    {
        get => (string?)GetValue(SourceUriProperty);
        set => SetValue(SourceUriProperty, value);
    }

    private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GifImage)d).Load((string?)e.NewValue);

    private void Load(string? path)
    {
        _timer?.Stop();
        _timer = null;
        _decoder = null;
        _frame = 0;

        if (string.IsNullOrEmpty(path))
        {
            Source = null;
            return;
        }

        Uri uri;
        try
        {
            uri = path.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(path, UriKind.Absolute)
                : new Uri($"pack://application:,,,{path}", UriKind.Absolute);
        }
        catch
        {
            Source = null;
            return;
        }

        try
        {
            _decoder = new GifBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch
        {
            Source = null;
            return;
        }

        if (_decoder.Frames.Count == 0)
        {
            Source = null;
            return;
        }

        Source = _decoder.Frames[0];
        if (_decoder.Frames.Count == 1) return;   // static image, nothing to animate

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Advance();
        if (IsVisible) ScheduleNext();            // only animate while actually on screen
    }

    // Stop animating when hidden (collapsed panel / retracted island) and resume when shown.
    private void OnVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_timer == null || _decoder == null) return;

        if (e.NewValue is true) ScheduleNext();
        else _timer.Stop();
    }

    private void Advance()
    {
        if (_decoder == null) return;
        _frame = (_frame + 1) % _decoder.Frames.Count;
        Source = _decoder.Frames[_frame];
        ScheduleNext();
    }

    private void ScheduleNext()
    {
        if (_timer == null || _decoder == null) return;

        // GIF frame delay lives in hundredths of a second under /grctlext/Delay.
        int hundredths = 0;
        try
        {
            if (_decoder.Frames[_frame].Metadata is BitmapMetadata meta)
            {
                object? raw = meta.GetQuery("/grctlext/Delay");
                hundredths = raw switch
                {
                    ushort u => u,
                    int i => i,
                    _ => 0
                };
            }
        }
        catch { /* frame without timing metadata: use the default below */ }

        _timer.Interval = hundredths > 0
            ? TimeSpan.FromMilliseconds(hundredths * 10.0)
            : TimeSpan.FromMilliseconds(100);
        _timer.Start();
    }
}
