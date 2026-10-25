using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TopNotch
{
    public sealed record NotificationInfo(uint Id, string AppName, string Title, string Body, byte[]? IconPng);

    public sealed class NotificationService : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

        private UserNotificationListener? _listener;
        private DispatcherTimer? _poll;
        private bool _subscribed;
        private bool _seeded;
        private bool _polling;
        private HashSet<uint> _seen = new();

        public event Action<NotificationInfo>? NotificationRaised;

        public bool AccessDenied { get; private set; }

        public async Task InitAsync()
        {
            try
            {
                _listener = UserNotificationListener.Current;

                var status = await _listener.RequestAccessAsync();
                if (status != UserNotificationListenerAccessStatus.Allowed)
                {
                    AccessDenied = true;
                    return;
                }

                try
                {
                    _listener.NotificationChanged += OnNotificationChanged;
                    _subscribed = true;
                }
                catch
                {
                }

                await SeedAsync();

                if (!_subscribed) StartPolling();
            }
            catch
            {
                AccessDenied = true;
            }
        }

        private void StartPolling()
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
            {
                Interval = PollInterval
            };
            _poll.Tick += async (_, _) => await PollAsync();
            _poll.Start();
        }

        private async Task SeedAsync()
        {
            try
            {
                if (_listener == null) return;
                var all = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                _seen = all.Select(n => n.Id).ToHashSet();
                _seeded = true;
            }
            catch
            {
            }
        }

        private async Task PollAsync()
        {
            if (_polling || _listener == null) return;
            _polling = true;
            try
            {
                var all = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

                if (!_seeded)
                {
                    _seen = all.Select(n => n.Id).ToHashSet();
                    _seeded = true;
                    return;
                }

                foreach (var n in all)
                {
                    if (_seen.Contains(n.Id)) continue;

                    var info = await BuildInfoAsync(n);
                    if (info != null) NotificationRaised?.Invoke(info);
                }

                _seen = all.Select(n => n.Id).ToHashSet();
            }
            catch
            {
            }
            finally
            {
                _polling = false;
            }
        }

        private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (args.ChangeKind != UserNotificationChangedKind.Added) return;
            _ = HandleAddedAsync(args.UserNotificationId);
        }

        private async Task HandleAddedAsync(uint id)
        {
            try
            {
                if (_listener == null) return;

                var all = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var match = all.FirstOrDefault(n => n.Id == id);
                if (match == null) return;

                var info = await BuildInfoAsync(match);
                if (info != null) NotificationRaised?.Invoke(info);
            }
            catch
            {
            }
        }

        private static async Task<NotificationInfo?> BuildInfoAsync(UserNotification n)
        {
            try
            {
                string app = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }

                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return null;

                var texts = binding.GetTextElements();
                string title = texts.Count > 0 ? texts[0].Text ?? "" : "";
                string body = string.Join("\n", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return null;

                return new NotificationInfo(n.Id, app, title, body, await ReadIconAsync(n));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> ReadIconAsync(UserNotification n)
        {
            try
            {
                var logoRef = n.AppInfo?.DisplayInfo?.GetLogo(new Size(64, 64));
                if (logoRef != null)
                {
                    using var stream = await logoRef.OpenReadAsync();
                    if (stream is { Size: > 0 })
                    {
                        var size = (int)stream.Size;
                        var bytes = new byte[size];
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)size);
                        reader.ReadBytes(bytes);
                        return bytes;
                    }
                }
            }
            catch
            {
            }

            try
            {
                string? aumid = null;
                try { aumid = n.AppInfo?.AppUserModelId; } catch { }
                if (string.IsNullOrWhiteSpace(aumid)) return null;

                return await Task.Run(() => AppIconResolver.ResolvePng(aumid));
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _poll?.Stop();
            _poll = null;

            if (_subscribed && _listener != null)
            {
                try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
            }

            _subscribed = false;
            _listener = null;
        }
    }
}
