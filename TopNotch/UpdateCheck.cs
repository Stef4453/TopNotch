using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TopNotch
{
    internal static class UpdateCheck
    {
        internal const string Repository = "Stef4453/TopNotch";

        internal static string Page => $"https://github.com/{Repository}";

        private const string Releases = $"https://api.github.com/repos/{Repository}/releases?per_page=30";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TopNotch");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        internal enum Status
        {
            UpToDate,
            Newer,
            NoReleases,
            Failed
        }

        internal sealed record Result(Status Status, string? Version = null, string? Url = null);

        internal static Result? Last { get; private set; }

        internal static string Current
        {
            get
            {
                string? informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                return (informational ?? "0.0").Split('+')[0];
            }
        }

        internal static bool CurrentIsPrerelease { get; private set; } = Prerelease(Current);

        internal static string CurrentLabel => Pretty(Current, CurrentIsPrerelease);

        internal static async Task<Result> RunAsync(bool includePrereleases) =>
            Last = await CheckAsync(includePrereleases);

        private static async Task<Result> CheckAsync(bool includePrereleases)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(Releases);

                if (response.StatusCode == HttpStatusCode.NotFound) return new Result(Status.NoReleases);
                if (!response.IsSuccessStatusCode) return new Result(Status.Failed);

                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind != JsonValueKind.Array) return new Result(Status.Failed);

                bool any = false;
                bool? currentIsPrerelease = null;
                Release? newest = null;

                foreach (JsonElement entry in document.RootElement.EnumerateArray())
                {
                    if (entry.TryGetProperty("draft", out JsonElement draft)
                        && draft.ValueKind == JsonValueKind.True) continue;

                    any = true;

                    string tag = entry.TryGetProperty("tag_name", out JsonElement name)
                        ? name.GetString() ?? string.Empty
                        : string.Empty;

                    if (!SemanticVersion.TryParse(tag, out SemanticVersion published)) continue;

                    bool prerelease = entry.TryGetProperty("prerelease", out JsonElement flag)
                        && flag.ValueKind == JsonValueKind.True;

                    if (SemanticVersion.TryParse(Current, out SemanticVersion running)
                        && published.CompareTo(running) == 0)
                        currentIsPrerelease = prerelease;

                    if (prerelease && !includePrereleases) continue;

                    string url = entry.TryGetProperty("html_url", out JsonElement page)
                        ? page.GetString() ?? Page
                        : Page;

                    if (newest is null || published.CompareTo(newest.Value.Version) > 0)
                        newest = new Release(published, Pretty(published.Text, prerelease), url);
                }

                if (currentIsPrerelease is bool known) CurrentIsPrerelease = known || Prerelease(Current);

                if (!any) return new Result(Status.NoReleases);
                if (newest is null) return new Result(Status.UpToDate);

                if (SemanticVersion.TryParse(Current, out SemanticVersion current)
                    && newest.Value.Version.CompareTo(current) > 0)
                    return new Result(Status.Newer, newest.Value.Label, newest.Value.Url);

                return new Result(Status.UpToDate);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                        or JsonException or InvalidOperationException)
            {
                return new Result(Status.Failed);
            }
        }

        private static bool Prerelease(string version) => version.Contains('-');

        private static string Pretty(string text, bool prerelease)
        {
            if (!prerelease) return text;

            int dash = text.IndexOf('-');
            if (dash < 0) return text + " Beta";

            string[] words = text[(dash + 1)..].Split('.');

            for (int i = 0; i < words.Length; i++) words[i] = Word(words[i]);

            return text[..dash] + " " + string.Join(' ', words);
        }

        private static string Word(string word) => word.ToLowerInvariant() switch
        {
            "rc" => "RC",
            "alpha" or "beta" or "pre" or "preview" =>
                char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant(),
            _ => word
        };

        private readonly record struct Release(SemanticVersion Version, string Label, string Url);

        private readonly struct SemanticVersion : IComparable<SemanticVersion>
        {
            private readonly string[]? _pre;

            private SemanticVersion(int major, int minor, int patch, string[]? pre, string text)
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                _pre = pre;
                Text = text;
            }

            public int Major { get; }

            public int Minor { get; }

            public int Patch { get; }

            public string Text { get; }

            public static bool TryParse(string? value, out SemanticVersion version)
            {
                version = default;

                if (string.IsNullOrWhiteSpace(value)) return false;

                string text = value.Trim();

                if (text.Length > 0 && text[0] is 'v' or 'V') text = text[1..];

                int plus = text.IndexOf('+');
                if (plus >= 0) text = text[..plus];

                string core = text;
                string[]? pre = null;

                int dash = text.IndexOf('-');
                if (dash >= 0)
                {
                    core = text[..dash];
                    string tail = text[(dash + 1)..];

                    if (tail.Length == 0) return false;

                    pre = tail.Split('.');

                    foreach (string part in pre)
                    {
                        if (part.Length == 0) return false;

                        foreach (char c in part)
                        {
                            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
                        }
                    }
                }

                string[] numbers = core.Split('.');
                if (numbers.Length > 3) return false;

                int[] parts = new int[3];

                for (int i = 0; i < numbers.Length; i++)
                {
                    if (!int.TryParse(numbers[i], NumberStyles.None,
                            CultureInfo.InvariantCulture, out parts[i])) return false;
                }

                version = new SemanticVersion(parts[0], parts[1], parts[2], pre, text);
                return true;
            }

            public int CompareTo(SemanticVersion other)
            {
                int order = Major.CompareTo(other.Major);
                if (order != 0) return order;

                order = Minor.CompareTo(other.Minor);
                if (order != 0) return order;

                order = Patch.CompareTo(other.Patch);
                if (order != 0) return order;

                if (_pre is null) return other._pre is null ? 0 : 1;
                if (other._pre is null) return -1;

                int shared = Math.Min(_pre.Length, other._pre.Length);

                for (int i = 0; i < shared; i++)
                {
                    string left = _pre[i];
                    string right = other._pre[i];

                    bool leftIsNumber = int.TryParse(left, NumberStyles.None,
                        CultureInfo.InvariantCulture, out int leftNumber);

                    bool rightIsNumber = int.TryParse(right, NumberStyles.None,
                        CultureInfo.InvariantCulture, out int rightNumber);

                    order = leftIsNumber && rightIsNumber
                        ? leftNumber.CompareTo(rightNumber)
                        : leftIsNumber ? -1
                        : rightIsNumber ? 1
                        : string.CompareOrdinal(left, right);

                    if (order != 0) return order;
                }

                return _pre.Length.CompareTo(other._pre.Length);
            }
        }
    }
}
