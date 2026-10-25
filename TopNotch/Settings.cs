using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopNotch
{
    internal enum IslandStyle
    {
        Notch,

        DynamicIsland
    }

    internal sealed class Settings
    {
        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private bool _use24HourClock;
        private string _imgBbApiKey = string.Empty;
        private IslandStyle _islandStyle = IslandStyle.Notch;
        private bool _islandOutline = true;
        private bool _launchpadIntroSeen;
        private bool _hideInFullscreen = true;
        private bool _checkForUpdatesOnLaunch = true;

        public static Settings Current { get; } = Load();

        public static event Action? Changed;

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TopNotch");

        public static string FilePath => Path.Combine(Folder, "settings.json");

        public bool Use24HourClock
        {
            get => _use24HourClock;
            set
            {
                if (_use24HourClock == value) return;
                _use24HourClock = value;
                Changed?.Invoke();
            }
        }

        public IslandStyle IslandStyle
        {
            get => _islandStyle;
            set
            {
                if (_islandStyle == value) return;
                _islandStyle = value;
                Changed?.Invoke();
            }
        }

        public bool IslandOutline
        {
            get => _islandOutline;
            set
            {
                if (_islandOutline == value) return;
                _islandOutline = value;
                Changed?.Invoke();
            }
        }

        public bool HideInFullscreen
        {
            get => _hideInFullscreen;
            set
            {
                if (_hideInFullscreen == value) return;
                _hideInFullscreen = value;
                Changed?.Invoke();
            }
        }

        public bool CheckForUpdatesOnLaunch
        {
            get => _checkForUpdatesOnLaunch;
            set
            {
                if (_checkForUpdatesOnLaunch == value) return;
                _checkForUpdatesOnLaunch = value;
                Changed?.Invoke();
            }
        }

        public bool LaunchpadIntroSeen
        {
            get => _launchpadIntroSeen;
            set
            {
                if (_launchpadIntroSeen == value) return;
                _launchpadIntroSeen = value;
                Changed?.Invoke();
            }
        }

        public string ImgBbApiKey
        {
            get => _imgBbApiKey;
            set
            {
                string trimmed = (value ?? string.Empty).Trim();
                if (_imgBbApiKey == trimmed) return;
                _imgBbApiKey = trimmed;
                Changed?.Invoke();
            }
        }

        [JsonIgnore]
        public bool HasImgBbApiKey => _imgBbApiKey.Length > 0;

        private static Settings Load()
        {
            bool found = File.Exists(FilePath);

            try
            {
                if (found)
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Format) ?? new Settings();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            var fresh = new Settings();

            if (!found) fresh.Save();

            return fresh;
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Format));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool Set(Action change)
        {
            change();
            return Save();
        }
    }
}
