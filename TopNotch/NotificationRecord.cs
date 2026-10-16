using System.Windows.Media.Imaging;

namespace TopNotch
{
    /// <summary>
    /// One entry in the notification history tab. Immutable: rows are only ever added, never
    /// edited, so there's no need for change notification.
    /// </summary>
    public sealed record NotificationRecord(
        string AppName,
        string Title,
        string Body,
        BitmapSource? Icon,
        DateTime Time)
    {
        public string TimeText => Time.ToString("HH:mm");
    }
}
