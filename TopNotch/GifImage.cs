using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TopNotch;

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
        if (_decoder.Frames.Count == 1) return;

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Advance();
        if (IsVisible) ScheduleNext();
    }

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
        catch { }

        _timer.Interval = hundredths > 0
            ? TimeSpan.FromMilliseconds(hundredths * 10.0)
            : TimeSpan.FromMilliseconds(100);
        _timer.Start();
    }
}
