using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace TopNotch;

internal static class WebImage
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TopNotch");
        return client;
    }

    internal static bool Carried(IDataObject data) => Address(data) is not null;

    internal static async Task<string?> SaveAsync(IDataObject data)
    {
        if (Address(data) is not { } address) return null;

        try
        {
            using var response = await Http.GetAsync(address);
            if (!response.IsSuccessStatusCode) return null;

            string kind = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!kind.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;

            string folder = Path.Combine(Settings.Folder, "Dropped");
            Directory.CreateDirectory(folder);

            string path = Free(Path.Combine(folder, Named(data, address, kind)));
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                    or UnauthorizedAccessException or NotSupportedException
                                    or InvalidOperationException or ArgumentException
                                    or UriFormatException)
        {
            return null;
        }
    }

    internal static void Discard(string path)
    {
        try
        {
            string folder = Path.GetFullPath(Path.Combine(Settings.Folder, "Dropped"));
            string full = Path.GetFullPath(path);

            if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                    or NotSupportedException or System.Security.SecurityException)
        {
        }
    }

    private static string? Address(IDataObject data)
    {
        if (Picture(DragHtml(data)) is { } dragged) return dragged;

        foreach (string format in new[] { "text/x-moz-url", "UniformResourceLocatorW",
                                          "UniformResourceLocator", "text/uri-list", DataFormats.Text })
        {
            string text = (Text(data, format, wide: Wide(format)) ?? string.Empty)
                .Split('\r', '\n')[0].Trim();

            if (text.Length > 0 && LinkPicture(text) is { } link) return link;
        }

        return null;
    }

    private static string DragHtml(IDataObject data) =>
        Text(data, DataFormats.Html, wide: false)
        ?? Text(data, "text/html", wide: false)
        ?? string.Empty;

    private static string? Picture(string html)
    {
        var tag = Regex.Match(html, "<img[^>]*?\\ssrc\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
                              RegexOptions.IgnoreCase);

        if (!tag.Success) return null;

        string address = WebUtility.HtmlDecode(
            tag.Groups[2].Success ? tag.Groups[2].Value
            : tag.Groups[3].Success ? tag.Groups[3].Value
            : tag.Groups[4].Value);

        return Web(address);
    }

    private static string? Alt(IDataObject data)
    {
        var tag = Regex.Match(DragHtml(data), "<img[^>]*?\\salt\\s*=\\s*(\"([^\"]*)\"|'([^']*)')",
                              RegexOptions.IgnoreCase);

        if (!tag.Success) return null;

        return WebUtility.HtmlDecode(tag.Groups[2].Success ? tag.Groups[2].Value : tag.Groups[3].Value);
    }

    private static string Named(IDataObject data, string address, string kind)
    {
        string extension = Extension(kind);

        if (Alt(data) is { } alt && Clean(alt) is { } called)
        {
            if (called.Length > 60) called = called[..60].Trim();
            return Typed(called, extension);
        }

        string named = Clean(Path.GetFileName(new Uri(address).AbsolutePath)) ?? string.Empty;
        return Path.HasExtension(named) ? named : "Image" + extension;
    }

    private static string? Clean(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        name = name.Trim().TrimEnd('.');
        return name.Length > 0 ? name : null;
    }

    private static string Typed(string name, string extension) =>
        Path.HasExtension(name) ? name : name + extension;

    private static string Extension(string kind) => kind.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/avif" => ".avif",
        "image/svg+xml" => ".svg",
        _ => ".jpg",
    };

    private static string Free(string path)
    {
        if (!File.Exists(path)) return path;

        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int number = 2; ; number++)
        {
            string candidate = Path.Combine(folder, $"{name} ({number}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string? Web(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.AbsolutePath.Length > 0
            ? address
            : null;

    private static string? LinkPicture(string address) =>
        Web(address) is not null && ImgBbUploader.LooksLikeImage(new Uri(address).AbsolutePath)
            ? address
            : null;

    private static bool Wide(string format) =>
        format is "text/x-moz-url" or "UniformResourceLocatorW";

    private static string? Text(IDataObject data, string format, bool wide)
    {
        object? value;

        try
        {
            if (!data.GetDataPresent(format)) return null;
            value = data.GetData(format);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        Encoding encoding = wide ? Encoding.Unicode : Encoding.UTF8;

        return value switch
        {
            string text => text,
            byte[] bytes => encoding.GetString(bytes),
            MemoryStream stream => encoding.GetString(stream.ToArray()),
            _ => null,
        };
    }
}
