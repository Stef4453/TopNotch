using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace TopNotch;

public class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public event Action<string, string>? TrackChanged;
    public event Action<byte[]?>? ArtChanged;
    public event Action<bool>? PlayingChanged;
    public event Action? TimelineChanged;

    public bool CanSeek =>
    _session?.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled ?? false;

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (s, e) => Attach(_manager.GetCurrentSession());
        Attach(_manager.GetCurrentSession());
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }

        _session = session;
        if (_session == null) return;

        _session.MediaPropertiesChanged += OnMediaChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;

        OnMediaChanged(_session, null);
        OnPlaybackChanged(_session, null);
    }

    private async void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object? e)
    {
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            TrackChanged?.Invoke(props.Title, props.Artist);

            byte[]? art = null;
            if (props.Thumbnail != null)
            {
                using var winStream = await props.Thumbnail.OpenReadAsync();
                using var stream = winStream.AsStreamForRead();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                art = ms.ToArray();
            }
            ArtChanged?.Invoke(art);
        }
        catch { }
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object? e)
    {
        var info = s.GetPlaybackInfo();
        PlayingChanged?.Invoke(info.PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, object? e)
        => TimelineChanged?.Invoke();

    public double PositionOffsetSeconds { get; set; } = 0.4;

    public (double position, double length) GetTimeline()
    {
        if (_session == null) return (0, 0);

        var t = _session.GetTimelineProperties();
        var playing = _session.GetPlaybackInfo().PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var pos = t.Position;
        if (playing)
        {
            pos += DateTimeOffset.Now - t.LastUpdatedTime;
            pos += TimeSpan.FromSeconds(PositionOffsetSeconds);
        }

        var length = (t.EndTime - t.StartTime).TotalSeconds;
        return (Math.Clamp(pos.TotalSeconds, 0, length), length);
    }

    public void PlayPause() => _session?.TryTogglePlayPauseAsync();
    public void Next() => _session?.TrySkipNextAsync();
    public void Previous() => _session?.TrySkipPreviousAsync();
    public async Task<bool> SeekAsync(double seconds)
    {
        if (_session == null) return false;
        try
        {
            var start = _session.GetTimelineProperties().StartTime;
            var target = start + TimeSpan.FromSeconds(seconds);
            bool ok = await _session.TryChangePlaybackPositionAsync(target.Ticks);
            System.Diagnostics.Debug.WriteLine($"Seek to {seconds:F1}s -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }
}