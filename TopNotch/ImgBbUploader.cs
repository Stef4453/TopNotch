using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace TopNotch;

internal static class ImgBbUploader
{
    private const string Endpoint = "https://api.imgbb.com/1/upload?key=";

    private const long MaxBytes = 32L * 1024 * 1024;

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
        }

        return new Result(false, string.Empty, "ImgBB sent a reply we didn't understand");
    }

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

    internal static bool LooksLikeImage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".avif" or ".svg" => true,
        _ => false,
    };
}
