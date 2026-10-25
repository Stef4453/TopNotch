using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace TopNotch;

internal static class UrlShortener
{
    private const string Endpoint = "https://tinyurl.com/api-create.php?url=";

    private static readonly HttpClient Http = CreateClient();

    internal readonly record struct Result(bool Ok, string Link, string Error);

    internal static bool IsOwnLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("tinyurl.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("www.tinyurl.com", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TopNotch");
        return client;
    }

    internal static async Task<Result> ShortenAsync(string longUrl)
    {
        try
        {
            using var response = await Http.GetAsync(Endpoint + Uri.EscapeDataString(longUrl));

            if (!response.IsSuccessStatusCode)
            {
                return new Result(false, string.Empty,
                    $"TinyURL wouldn't shorten that link (HTTP {(int)response.StatusCode})");
            }

            string reply = (await response.Content.ReadAsStringAsync()).Trim();

            if (reply.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return new Result(true, reply, string.Empty);
            }

            return new Result(false, string.Empty, reply.Length > 0 ? reply : "TinyURL sent an empty reply");
        }
        catch (TaskCanceledException)
        {
            return new Result(false, string.Empty, "TinyURL didn't respond");
        }
        catch (HttpRequestException)
        {
            return new Result(false, string.Empty, "Couldn't reach TinyURL");
        }
    }
}
