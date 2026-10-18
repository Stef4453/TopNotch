using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace TopNotch;

// Shortens a URL through TinyURL, which needs no account and no API key: the whole protocol is
// one GET with the long URL in the query string and the short URL back as plain text.
//
// Alternatives, in case this one ever stops answering. All are keyless and HTTPS; all but
// spoo.me reply with plain text, so only the request line and the reply check would change:
//
//   da.gd      POST https://da.gd/shorten                  body: url=<encoded>
//   ulvis.net  GET  https://ulvis.net/api.php?url=<encoded>
//   is.gd      GET  https://is.gd/create.php?format=simple&url=<encoded>
//   spoo.me    POST https://spoo.me/                       body: url=<encoded>   (JSON reply)
//
// 1pt.co was the original choice. Its backend certificate expired in August 2026 and its own
// frontend asks for it over hard-coded HTTPS, which takes the whole service down: minting fails
// and every link stalls on "Redirecting...".
//
// One caveat of the free tier: for destinations it deems worth monetising, TinyURL does not
// answer with a direct redirect but with a 302 to its own redirect.viglink.com wrapper, which
// then forwards to the destination. GitHub links go straight through; shop and vendor pages
// tend to be wrapped. Every other service on the list above redirects plainly, so moving to one
// of them is a one-constant change.
//
// A dead network is an ordinary outcome here, not a bug: the caller gets a Result it can show
// rather than an exception it has to remember to catch.
internal static class UrlShortener
{
    private const string Endpoint = "https://tinyurl.com/api-create.php?url=";

    // One client for the life of the app. Making a new one per click would leave a socket in
    // TIME_WAIT behind every press.
    private static readonly HttpClient Http = CreateClient();

    internal readonly record struct Result(bool Ok, string Link, string Error);

    // TinyURL refuses to shorten its own links - it answers 400 with a body of just "Error" -
    // and a second pass would be pointless anyway. The caller can say so without a round trip.
    // This is easy to hit in practice: the first press leaves a TinyURL link on the clipboard,
    // so a second press is shortening our own output.
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
                // A 400 means they will not shorten that particular URL - a domain they have
                // blocked, say. Their body is only the word "Error", so it is worth spelling out.
                return new Result(false, string.Empty,
                    $"TinyURL wouldn't shorten that link (HTTP {(int)response.StatusCode})");
            }

            string reply = (await response.Content.ReadAsStringAsync()).Trim();

            // Success is a bare short URL. Anything else - "Error: Invalid URL", a rate-limit
            // notice - is passed through as it stands, since their wording beats a guess of mine.
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
