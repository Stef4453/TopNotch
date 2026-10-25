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

        private const string Latest = $"https://api.github.com/repos/{Repository}/releases/latest";

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

        internal static async Task<Result> RunAsync() => Last = await CheckAsync();

        private static async Task<Result> CheckAsync()
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(Latest);

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

        private static bool Ahead(string tag, out Version? available)
        {
            available = null;

            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out Version? published)) return false;
            if (!Version.TryParse(Current, out Version? running)) return false;

            available = published;
            return Normal(published) > Normal(running);
        }

        private static Version Normal(Version version) => new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }
}
