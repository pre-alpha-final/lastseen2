using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LastSeen2.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace LastSeen2.Server;

public interface IMetadataService
{
    Task<MetadataResult> LookupAsync(string url, CancellationToken ct);
}

public sealed class MetadataService(ProviderHttpClient http) : IMetadataService
{
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, RegexTimeout);

    public async Task<MetadataResult> LookupAsync(string url, CancellationToken ct)
    {
        var input = new Uri(BoardService.Url(url, "url"));
        var target = Target(input);
        if (target is null) return new(MetadataStatus.Unsupported, null, null, "Use a title and optional manual thumbnail for this link.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LookupTimeout);
        try
        {
            var youtube = target.Host == "www.youtube.com";
            var body = await http.GetAsync(target, youtube ? "application/json" : "text/html", deadline.Token);
            var (title, thumbnail) = youtube ? ParseOEmbed(body) : ParseTwitch(body);
            title = CleanTitle(title);
            thumbnail = CleanThumbnail(thumbnail);
            return title is null && thumbnail is null ? Unavailable()
                : new(MetadataStatus.Available, title, thumbnail, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            or RegexMatchTimeoutException or SocketException or InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private static MetadataResult Unavailable() => new(MetadataStatus.Unavailable, null, null,
        "Preview unavailable. You can still save a title and optional manual thumbnail.");

    private static Uri? Target(Uri input)
    {
        if (!input.IsDefaultPort) return null;
        var host = input.Host;
        var path = input.AbsolutePath.TrimEnd('/');
        string? id = null;
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com")
        {
            if (path == "/watch")
            {
                var query = QueryHelpers.ParseQuery(input.Query);
                if (query.TryGetValue("v", out var values) && values.Count == 1) id = values[0];
            }
            else if (path.StartsWith("/shorts/", StringComparison.Ordinal)) id = path[8..];
        }
        else if (host is "youtu.be" or "www.youtu.be") id = path.TrimStart('/');
        if (id is not null && Matches(id, @"\A[A-Za-z0-9_-]{11}\z"))
            return new("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + id));
        if (host is "twitch.tv" or "www.twitch.tv" or "m.twitch.tv")
        {
            if (Matches(path, @"\A/videos/[0-9]{1,30}\z") || Matches(path, @"\A/[A-Za-z0-9_]{1,50}/clip/[A-Za-z0-9_-]{1,200}\z"))
                return new("https://www.twitch.tv" + path);
        }
        if (host == "clips.twitch.tv" && Matches(path, @"\A/[A-Za-z0-9_-]{1,200}\z"))
            return new("https://clips.twitch.tv" + path);
        return null;
    }

    private static (string?, string?) ParseOEmbed(string body)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        string? Value(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (Value("version") != "1.0" || Value("type") != "video") return (null, null);
        // We consume suggestions only; provider embed HTML is never returned or rendered.
        return (Value("title"), Value("thumbnail_url"));
    }

    private static (string?, string?) ParseTwitch(string body)
    {
        // Deliberately limited to public meta tags, with bounded input and regex work.
        // Ignore comments and raw-text elements so embedded scripts cannot masquerade as tags.
        body = Regex.Replace(body, @"<!--[\s\S]*?(?:-->|\z)|<(script|style|textarea|title)\b[^>]*>[\s\S]*?</\1\s*>",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in Regex.Matches(body, "<meta\\b(?:[^>\\\"']|\\\"[^\\\"]*\\\"|'[^']*')*>", RegexOptions.IgnoreCase, RegexTimeout))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attr in Regex.Matches(tag.Value, "([\\w:-]+)\\s*=\\s*(?:\\\"([^\\\"]*)\\\"|'([^']*)'|([^\\s>]+))", RegexOptions.CultureInvariant, RegexTimeout))
                attributes.TryAdd(attr.Groups[1].Value, WebUtility.HtmlDecode(attr.Groups[2].Success ? attr.Groups[2].Value : attr.Groups[3].Success ? attr.Groups[3].Value : attr.Groups[4].Value));
            if ((attributes.TryGetValue("property", out var key) || attributes.TryGetValue("name", out key))
                && attributes.TryGetValue("content", out var content)) values.TryAdd(key, content);
        }
        string? Value(string og, string twitter) => values.GetValueOrDefault(og) ?? values.GetValueOrDefault(twitter);
        var title = Value("og:title", "twitter:title");
        var image = Value("og:image", "twitter:image");
        if (title?.Trim() is "Twitch" or "VOD - Twitch" or "Clip - Twitch") title = null;
        if (Uri.TryCreate(image, UriKind.Absolute, out var imageUri)
            && imageUri.AbsolutePath.StartsWith("/ttv-static-metadata/", StringComparison.Ordinal)) image = null;
        return (title, image);
    }

    private static string? CleanTitle(string? title) => !string.IsNullOrWhiteSpace(title)
        && title.Trim().Length <= 500 && !title.Any(char.IsControl) ? title.Trim() : null;

    private static string? CleanThumbnail(string? value)
    {
        if (value is null || value.Length > 4096 || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Port != 443 || uri.UserInfo.Length != 0) return null;
        // Never fetch thumbnail URLs. Only return known provider image domains to the client.
        var host = uri.Host;
        return host == "i.ytimg.com" || host == "img.youtube.com" || host == "static-cdn.jtvnw.net"
            || host == "clips-media-assets2.twitch.tv" || host == "clips-media-assets.twitch.tv"
            || host.EndsWith(".twitchcdn.net", StringComparison.Ordinal) ? uri.AbsoluteUri : null;
    }
}
