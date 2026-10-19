using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace TopNotch;

// Uploads one picture to ImgBB and hands back the link to it.
//
// Where the shortener gets away with no account at all, this one cannot: ImgBB authenticates
// every upload with a key of the user's own, and their scheme is the key in the query string
// (https://api.imgbb.com/1/upload?key=...). Over HTTPS the request line is as private as the
// service makes it; putting the key in a header is not something their API offers. The key
// belongs to one free account and can be regenerated there whenever.
//
// The upload is a multipart form with the file under the field name "image", which is the way
// their documentation prefers, rather than the base64 body they also accept: it avoids inflating
// the picture by a third on the way out.
//
// As with the shortener, a dead network or a refused key is an ordinary outcome, not a bug: the
// caller gets a Result it can put in front of a person.
internal static class ImgBbUploader
{
    private const string Endpoint = "https://api.imgbb.com/1/upload?key=";

    // Their documented ceiling for a free account. Checked here so that a 200 MB screen recording
    // is refused instantly and in words, rather than after a long upload and an opaque error. If
    // they ever raise it, this only costs an upload that would have worked.
    private const long MaxBytes = 32L * 1024 * 1024;

    // One client for the life of the app, as with the shortener: a fresh one per drop would leave
    // a socket in TIME_WAIT behind every picture. The wait is longer than the shortener's because
    // an upload is a body going out, not a line coming back.
    private static readonly HttpClient Http = CreateClient();

    internal readonly record struct Result(bool Ok, string Link, string Error);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TopNotch");
        return client;
    }

    internal static async Task<Result> UploadAsync(string apiKey, string path)
    {
        byte[] bytes;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return new Result(false, string.Empty, "That file has gone missing");
            if (file.Length > MaxBytes) return new Result(false, string.Empty, "ImgBB takes images up to 32 MB");

            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Result(false, string.Empty, "Couldn't read that file");
        }

        try
        {
            using var form = new MultipartFormDataContent();
            var picture = new ByteArrayContent(bytes);
            picture.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeFor(path));
            form.Add(picture, "image", Path.GetFileName(path));

            using var response = await Http.PostAsync(Endpoint + Uri.EscapeDataString(apiKey), form);
            return Read(await response.Content.ReadAsStringAsync());
        }
        catch (TaskCanceledException)
        {
            return new Result(false, string.Empty, "ImgBB didn't answer");
        }
        catch (HttpRequestException)
        {
            return new Result(false, string.Empty, "Couldn't reach ImgBB");
        }
    }

    // A successful upload is {"success":true,"data":{...,"url":"https://i.ibb.co/.../name.png",...}}
    // and a failed one carries a written reason - "Invalid API key", "Image format not supported" -
    // which is always better than anything I would invent in its place, so it is passed through.
    private static Result Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("url", out var url) &&
                url.GetString() is { Length: > 0 } link)
            {
                return new Result(true, link, string.Empty);
            }

            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
            {
                return new Result(false, string.Empty, text);
            }
        }
        catch (JsonException)
        {
            // Not their JSON at all - a captive portal or a proxy error page, most likely.
        }

        return new Result(false, string.Empty, "ImgBB sent a reply we didn't understand");
    }

    // ImgBB works out the format from the bytes, so this only has to be close enough not to
    // confuse anything in between. Anything unrecognised goes as a plain byte stream, which is
    // what an unknown extension deserves.
    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".avif" => "image/avif",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Whether a dropped path is worth offering to upload. The list is the set of things ImgBB
    /// accepts, and it is also what decides whether a drag over the island shows as a drop at all:
    /// dragging a folder or a document across the notch should not light it up.
    /// </summary>
    internal static bool LooksLikeImage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".avif" or ".svg" => true,
        _ => false,
    };
}
