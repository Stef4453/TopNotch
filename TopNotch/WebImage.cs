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

/// <summary>
/// A picture dragged out of a browser, which is not a file until it is written down as one.
///
/// A browser hands a dragged picture over as a virtual file - its bytes, and a name, and no path
/// at all - which the shell can read and this cannot: a drop arrives as a WPF data object, and
/// asking it for the contents of that file comes back with a bad FORMATETC, because the format is
/// served one file at a time while the data object asks for every file at once.
///
/// What the browser does say, in the HTML it puts on the drag, is which picture was dragged and
/// where it was loaded from, and that is where the picture is got from instead. It is a download
/// where the file was already in hand, which is the price of the browser keeping it to itself.
///
/// What comes out of here is an ordinary file in %LOCALAPPDATA%\TopNotch\Dropped, named after the
/// picture the way the page named it. That folder is not a cache: a picture dragged out of a page
/// exists nowhere else on the disk, so the copy written here is the picture itself.
/// </summary>
internal static class WebImage
{
    // One client for the life of the app, as with the uploader and the shortener.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // A browser's user agent, near enough. Some picture hosts hand nothing to a client they do
        // not recognise, and this is a client they have not heard of either way.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TopNotch");
        return client;
    }

    /// <summary>
    /// Whether a drag is carrying a picture out of a page rather than a file off the disk. This is
    /// what decides whether a drag over the island reads as a drop at all, so it is answered from
    /// what the drag says about itself, and never from the network.
    /// </summary>
    internal static bool Carried(IDataObject data) => Address(data) is not null;

    /// <summary>
    /// Writes the picture a drag is carrying out as a file and says where it went, or null when
    /// there is no picture to be had - an ordinary outcome, being a page that would not give one up
    /// or a network that is not there, and not a thing being broken.
    /// </summary>
    internal static async Task<string?> SaveAsync(IDataObject data)
    {
        if (Address(data) is not { } address) return null;

        try
        {
            using var response = await Http.GetAsync(address);
            if (!response.IsSuccessStatusCode) return null;

            // It has to still be a picture when it arrives. The address came out of the drag's own
            // HTML, which is a claim about what was dragged rather than a promise about what will
            // be served, and a page that answers with itself is not one to write a file of.
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

    /// <summary>
    /// Throws away a picture that was taken off a page, if this is one. Anything else on the board
    /// is a file of the person's own - the board holds files, it does not own them - so anything
    /// that is not in the folder this class writes to is left exactly where it is.
    /// </summary>
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
            // A file that will not go is simply left behind: the pin is gone either way, and the
            // folder is the app's own to tidy up another day.
        }
    }

    // ---- Reading the drag ----

    // The address of the picture a drag is carrying, if it is carrying one.
    private static string? Address(IDataObject data)
    {
        // The drag's own HTML, and the picture in it: the ordinary way a picture comes out of a
        // page, where the address is the one the browser is showing, whether or not it ends in
        // anything a file would be named after.
        if (Picture(DragHtml(data)) is { } dragged) return dragged;

        // Failing that, a drag whose whole payload is an address, which is what dragging a link to
        // a picture looks like. There the address has to name a picture to count: the address of
        // the page a picture is on is not the picture, and refusing that is the point of this.
        foreach (string format in new[] { "text/x-moz-url", "UniformResourceLocatorW",
                                          "UniformResourceLocator", "text/uri-list", DataFormats.Text })
        {
            string text = (Text(data, format, wide: Wide(format)) ?? string.Empty)
                .Split('\r', '\n')[0].Trim();

            if (text.Length > 0 && LinkPicture(text) is { } link) return link;
        }

        return null;
    }

    // The HTML a drag is carrying, from either of the two formats that carry it.
    private static string DragHtml(IDataObject data) =>
        Text(data, DataFormats.Html, wide: false)
        ?? Text(data, "text/html", wide: false)
        ?? string.Empty;

    // The address of the picture in a piece of HTML. Roughly, because what arrives is a fragment a
    // browser wrote for a drag rather than a document to be understood: a tag, and one thing in it.
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

    // What the page called the picture.
    private static string? Alt(IDataObject data)
    {
        var tag = Regex.Match(DragHtml(data), "<img[^>]*?\\salt\\s*=\\s*(\"([^\"]*)\"|'([^']*)')",
                              RegexOptions.IgnoreCase);

        if (!tag.Success) return null;

        return WebUtility.HtmlDecode(tag.Groups[2].Success ? tag.Groups[2].Value : tag.Groups[3].Value);
    }

    // ---- Naming the file ----

    // What to write the picture as. The page's own name for it first, since that is the one a
    // person would recognise, then the name the address gives it, then the plainest name its type
    // allows.
    private static string Named(IDataObject data, string address, string kind)
    {
        string extension = Extension(kind);

        // Alt text is written to be read, so it names a picture better than an address usually does
        // - "Cat - Wikipedia" rather than a string of cache parameters.
        if (Alt(data) is { } alt && Clean(alt) is { } called)
        {
            if (called.Length > 60) called = called[..60].Trim();
            return Typed(called, extension);
        }

        string named = Clean(Path.GetFileName(new Uri(address).AbsolutePath)) ?? string.Empty;
        return Path.HasExtension(named) ? named : "Image" + extension;
    }

    // A name that can be a file name, or null when there is nothing of one left. Browsers name
    // pictures after things that were never meant to be names of files.
    private static string? Clean(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        name = name.Trim().TrimEnd('.');
        return name.Length > 0 ? name : null;
    }

    // With a type on the end, unless the name already carries one.
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

    // A name nothing is using yet. Nothing is overwritten: a second picture dragged out of the same
    // page gets a name of its own rather than replacing the first.
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

    // ---- Addresses and formats ----

    // An address of a page on the web, or null if it is not one.
    private static string? Web(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.AbsolutePath.Length > 0
            ? address
            : null;

    // The same, for an address that has to say it is a picture: a link to one names it after the
    // picture, and a link to a page does not.
    private static string? LinkPicture(string address) =>
        Web(address) is not null && ImgBbUploader.LooksLikeImage(new Uri(address).AbsolutePath)
            ? address
            : null;

    // Whether a format carries its text as UTF-16. The Windows ones do, and the ones written for
    // browsers say so in their names.
    private static bool Wide(string format) =>
        format is "text/x-moz-url" or "UniformResourceLocatorW";

    // The text a format carries, however the drag chose to write it: the same information arrives
    // as a string from one format and as bytes or a stream from another.
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
