using System.Globalization;
using System.Windows.Media.Imaging;

namespace TopNotch
{
    public sealed record NotificationRecord(
        string AppName,
        string Title,
        string Body,
        BitmapSource? Icon,
        DateTime Time)
    {
        public string TimeText => Settings.Current.Use24HourClock
            ? Time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : Time.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }
}
