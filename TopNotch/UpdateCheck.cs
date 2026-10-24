using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TopNotch
{
    /// <summary>
    /// Where TopNotch comes from, and whether there is a newer one there.
    ///
    /// A release is a tag on GitHub: pushing one starts the workflow in .github/workflows, which
    /// builds the app and publishes it with that tag's version baked into the executable. So "the
    /// newest version" is simply the newest published release, and a running copy compares its own
    /// version against that. Nothing here installs anything - an update is a place to go and get it
    /// from, which is the only honest thing an unsigned app can offer.
    /// </summary>
    internal static class UpdateCheck
    {
        internal const string Repository = "Stef4453/TopNotch";

        /// <summary>The project's own page, which is where the address of a release is relative to.</summary>
        internal static string Page => $"https://github.com/{Repository}";

        private const string Latest = $"https://api.github.com/repos/{Repository}/releases/latest";

        // The same shape as the uploader's client: one connection pool, a timeout short enough that
        // a failed check never feels like a hang, and the user agent GitHub insists on - a request
        // without one is refused outright.
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TopNotch");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>What a check came to. <see cref="Newer"/> is the only one with somewhere to go.</summary>
        internal enum Status
        {
            UpToDate,
            Newer,
            NoReleases,
            Failed
        }

        /// <summary>The answer: what was found, and - if it is newer - where it was found.</summary>
        internal sealed record Result(Status Status, string? Version = null, string? Url = null);

        /// <summary>
        /// The answer to the last check, whoever asked for it. Kept so that the About page can show
        /// what the start-up check found: the banner that announced a new version may be long gone by
        /// the time somebody opens Settings, and "not checked yet" would then be the one thing on the
        /// page that is not true.
        /// </summary>
        internal static Result? Last { get; private set; }

        /// <summary>
        /// The version of the copy that is running. Taken from the informational version rather than
        /// the assembly version because that is the one written from the project's Version, without
        /// the fourth, always-zero part. Anything .NET appends for a build made from a repository -
        /// a plus sign and the commit - is cut off: it is not part of the version, and it would make
        /// two builds of 1.0.0 compare as different.
        /// </summary>
        internal static string Current
        {
            get
            {
                string? informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                return (informational ?? "0.0.0").Split('+')[0];
            }
        }

        /// <summary>
        /// Asks GitHub for the newest release and compares it with this copy. Never throws: an update
        /// check that took the app down would be worse than no update check at all.
        /// </summary>
        internal static async Task<Result> RunAsync() => Last = await CheckAsync();

        private static async Task<Result> CheckAsync()
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(Latest);

                // A repository with no releases answers 404, which is not a failure: it is an app
                // that has not been released yet, and saying so is the truthful answer.
                if (response.StatusCode == HttpStatusCode.NotFound) return new Result(Status.NoReleases);
                if (!response.IsSuccessStatusCode) return new Result(Status.Failed);

                using JsonDocument release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                JsonElement root = release.RootElement;

                string tag = root.TryGetProperty("tag_name", out JsonElement name)
                    ? name.GetString() ?? string.Empty : string.Empty;

                string url = root.TryGetProperty("html_url", out JsonElement page)
                    ? page.GetString() ?? Page : Page;

                if (!Ahead(tag, out Version? available)) return new Result(Status.UpToDate);

                return new Result(Status.Newer, available!.ToString(3), url);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                        or JsonException or InvalidOperationException)
            {
                return new Result(Status.Failed);
            }
        }

        // Whether a tag names a version newer than this copy's. Tags are written v1.2.3 by
        // convention but nothing enforces it, so the v is optional; anything unreadable is not an
        // update, because offering a version that cannot be compared would be worse than silence.
        private static bool Ahead(string tag, out Version? available)
        {
            available = null;

            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out Version? published)) return false;
            if (!Version.TryParse(Current, out Version? running)) return false;

            available = published;
            return Normal(published) > Normal(running);
        }

        // Versions with different numbers of parts do not compare the way a person would read them:
        // 1.2 against 1.2.0 is shorter, and would compare as older rather than equal. The parts a
        // version does not have are zeroes.
        private static Version Normal(Version version) => new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }
}
