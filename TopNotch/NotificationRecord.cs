using System.Globalization;
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
        /// <summary>
        /// Written the way the clock is written - the same choice, from the same setting - so the
        /// history never disagrees with the notch about what time it is.
        /// </summary>
        public string TimeText => Settings.Current.Use24HourClock
            ? Time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : Time.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }
}
