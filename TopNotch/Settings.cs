using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopNotch
{
    /// <summary>
    /// The two shapes the widget comes in.
    /// </summary>
    internal enum IslandStyle
    {
        /// <summary>Attached to the top of the screen the way a laptop's camera housing is: square
        /// where it runs off the edge, a sliver of itself always showing.</summary>
        Notch,

        /// <summary>A pill that floats clear of the top edge and takes itself off the screen
        /// altogether when it has nothing to show.</summary>
        DynamicIsland
    }

    /// <summary>
    /// Everything the app remembers between runs, and the one place that knows where it is kept:
    /// %LOCALAPPDATA%\TopNotch\settings.json - plain, indented JSON, so it can be read or edited
    /// by hand without any tooling.
    ///
    /// Settings are announced rather than pushed. Putting a value in writes it to disk straight
    /// away (the settings window has no Save button, so every change is final) and then raises
    /// <see cref="Changed"/>, which is what lets the island redraw itself the moment a switch is
    /// flipped rather than at the next restart.
    /// </summary>
    internal sealed class Settings
    {
        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            // Readable in the file as well as here: "DynamicIsland" says more to whoever opens it
            // than a 1 does, and a word that is wrong is a word that can be put right.
            Converters = { new JsonStringEnumConverter() }
        };

        private bool _use24HourClock;
        private string _imgBbApiKey = string.Empty;
        private IslandStyle _islandStyle = IslandStyle.Notch;
        private bool _islandOutline = true;
        private bool _launchpadIntroSeen;

        public static Settings Current { get; } = Load();

        /// <summary>Raised after a value changed, on the thread that changed it.</summary>
        public static event Action? Changed;

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TopNotch");

        public static string FilePath => Path.Combine(Folder, "settings.json");

        /// <summary>
        /// Whether the clock is written the way most of the world writes it (21:41) rather than
        /// with a designator (9:41 PM). Twelve-hour by default, as the island has always drawn it.
        /// </summary>
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

        /// <summary>
        /// Which shape the widget is. The notch is what it has always been; the Dynamic Island is
        /// the one that comes and goes.
        /// </summary>
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

        /// <summary>
        /// Whether the widget is drawn with a hairline around it. On by default: black against a
        /// dark wallpaper has no edge of its own, and without one the island reads as a hole in the
        /// screen rather than as something lying on top of it.
        /// </summary>
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

        /// <summary>
        /// Whether the Launchpad has introduced itself yet. Its tiles give no clue on their own
        /// that they can be dragged onto, clicked, or emptied, so the tab explains itself the
        /// first time it is opened and never again - a page of instructions in front of the apps
        /// is only helpful to somebody who has not read it. Kept in the same file as everything
        /// else, which is what makes it once rather than once a session.
        /// </summary>
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

        /// <summary>
        /// The ImgBB API key the Share Image tile uploads with, or empty when it has not been set
        /// - which is the signal for that tile to ask for one rather than attempt an upload.
        ///
        /// It is stored in the same plain file as everything else, because the file is the only
        /// thing this app remembers between runs and a secret in a second, obfuscated place would
        /// be a worse lie than a plain one. Anyone who can read %LOCALAPPDATA% can read this; the
        /// key is scoped to one free ImgBB account and can be regenerated there at any time.
        /// </summary>
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

        /// <summary>True once an API key has been configured, which is all the island needs to
        /// know before it offers to upload. Derived, so it is kept out of the file.</summary>
        [JsonIgnore]
        public bool HasImgBbApiKey => _imgBbApiKey.Length > 0;

        // A settings file that is missing, unreadable or corrupt is treated as no settings file:
        // starting from the defaults beats refusing to start.
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

            // An install with no settings file gets one on the way past rather than at the first
            // switch to be flipped: what the app remembers is worth being able to find, read and
            // edit from the moment it is installed, and an empty folder tells you nothing.
            //
            // A file that is there but would not read is left exactly as it is. It is still the
            // user's - possibly mid-edit - and the app runs on the defaults either way, so
            // overwriting something they can still repair would be a worse answer than carrying
            // on without it.
            if (!found) fresh.Save();

            return fresh;
        }

        /// <summary>Writes the file, creating %LOCALAPPDATA%\TopNotch on the way. False if it could
        /// not be written - quietly, since a settings file that will not save is not something the
        /// user can do anything about, and the change is still live in memory either way.</summary>
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

        /// <summary>Applies a change and puts it on disk in one step.</summary>
        public bool Set(Action change)
        {
            change();
            return Save();
        }
    }
}
