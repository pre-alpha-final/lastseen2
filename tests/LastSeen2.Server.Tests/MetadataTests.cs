using System.Net;
using System.Net.Http.Json;
using System.Text;
using LastSeen2.Contracts;
using LastSeen2.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LastSeen2.Server.Tests;

public sealed class MetadataTests
{
    private const string VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string Thumbnail = "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg";
    private const string Json = """{"version":"1.0","type":"video","title":"Fixture title","thumbnail_url":"https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg","html":"<script>never return me</script>"}""";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.False(request.Headers.Contains(ApiRoutes.AccessKeyHeader));
            Assert.Null(request.Headers.Authorization);
            Requests.Add(request.RequestUri!);
            return send(request, ct);
        }
    }

    private static HttpResponseMessage Response(string body, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Theory]
    [InlineData(VideoUrl)]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=1")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=ignored")]
    [InlineData("http://youtube.com/shorts/dQw4w9WgXcQ")]
    public async Task YouTubeFormsUseCanonicalOEmbedAndReturnOnlySuggestions(string url)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Json)));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var result = await new MetadataService(http).LookupAsync(url, default);
        Assert.Equal(new MetadataResult(MetadataStatus.Available, "Fixture title", Thumbnail, null), result);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("www.youtube.com", uri.Host);
        Assert.Equal("/oembed", uri.AbsolutePath);
        Assert.Contains(Uri.EscapeDataString(VideoUrl), uri.Query);
        Assert.DoesNotContain("ignored", uri.Query);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/videos/123456")]
    [InlineData("https://twitch.tv/channel/clip/Example-Slug_123?tracking=ignored")]
    [InlineData("https://clips.twitch.tv/Example-Slug_123")]
    public async Task TwitchReadsEncodedMetaAttributesWithoutExecutingHtml(string url)
    {
        const string html = """
            <!-- <meta property="og:title" content="wrong"> -->
            <script>var x = '<meta property="og:title" content="wrong">';</script>
            <META content='Fixture &amp; &quot;clip&quot;' PROPERTY='og:title'>
            <meta content="https://static-cdn.jtvnw.net/image.jpg?a=1&amp;b=2" property="og:image">
            """;
        using var handler = new Handler((_, _) => Task.FromResult(Response(html, "text/html")));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var result = await new MetadataService(http).LookupAsync(url, default);
        Assert.Equal(MetadataStatus.Available, result.Status);
        Assert.Equal("Fixture & \"clip\"", result.Title);
        Assert.Equal("https://static-cdn.jtvnw.net/image.jpg?a=1&b=2", result.ThumbnailUrl);
        Assert.Empty(Assert.Single(handler.Requests).Query);
    }

    [Theory]
    [InlineData("https://127.0.0.1/video")]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com:444/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ/extra")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&v=dQw4w9WgXcQ")]
    [InlineData("https://www.twitch.tv/channel")]
    public async Task UnsupportedLinksNeverFetch(string url)
    {
        using var handler = new Handler((_, _) => throw new Exception("Must not fetch"));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Unsupported, (await new MetadataService(http).LookupAsync(url, default)).Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"1.0\",\"type\":\"video\"}")]
    [InlineData("{\"version\":\"2.0\",\"type\":\"video\",\"title\":\"Invalid version\"}")]
    [InlineData("{\"version\":\"1.0\",\"type\":\"video\",\"title\":42,\"thumbnail_url\":\"javascript:alert(1)\"}")]
    public async Task MissingMalformedOrUnsafeMetadataIsNonfatal(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var result = await new MetadataService(http).LookupAsync(VideoUrl, default);
        Assert.Equal(MetadataStatus.Unavailable, result.Status);
        Assert.Null(result.Title);
        Assert.Null(result.ThumbnailUrl);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProviderErrorsAreNonfatal(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Unavailable, (await new MetadataService(http).LookupAsync(VideoUrl, default)).Status);
    }

    [Fact]
    public async Task DeadlineIsNonfatalButCallerCancellationPropagates()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(Json); });
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var service = new MetadataService(http);
        Assert.Equal(MetadataStatus.Unavailable, (await service.LookupAsync(VideoUrl, default)).Status);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LookupAsync(VideoUrl, caller.Token));
    }

    [Theory]
    [InlineData("https://127.0.0.1/private")]
    [InlineData("https://localhost/private")]
    [InlineData("https://www.youtube.com.evil.example/")]
    [InlineData("http://www.youtube.com/oembed")]
    [InlineData("https://www.youtube.com:444/oembed")]
    [InlineData("https://user:secret@www.youtube.com/oembed")]
    [InlineData("https://www.twitch.tv/videos/123")]
    public async Task UnsafeRedirectsAreRejectedBeforeSecondRequest(string location)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri(location) } }));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Unavailable, (await new MetadataService(http).LookupAsync(VideoUrl, default)).Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RelativeRedirectsWorkAndLoopsAreBounded()
    {
        var count = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++count == 2 ? Response(Json)
            : new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/oembed?redirected=1", UriKind.Relative) } }));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Available, (await new MetadataService(http).LookupAsync(VideoUrl, default)).Status);
        Assert.Equal(2, count);
        Assert.Equal(MetadataStatus.Unavailable, (await new MetadataService(http).LookupAsync(VideoUrl, default)).Status);
        Assert.Equal(6, count);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("64:ff9b::7f00:1")]
    public async Task PrivateOrSpecialDnsResultsNeverConnectEvenInMixedAnswers(string address)
    {
        var connected = false;
        await Assert.ThrowsAsync<HttpRequestException>(async () => await PublicNetwork.ConnectAsync(
            new("www.youtube.com", 443), default,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address) }),
            (_, _, _) => { connected = true; return Task.FromResult<Stream>(new MemoryStream()); }));
        Assert.False(connected);
    }

    [Fact]
    public async Task ConnectionsUseTheCheckedAddressWithNoSecondResolution()
    {
        var resolutions = 0;
        var address = IPAddress.Parse("2606:4700:4700::1111");
        using var stream = await PublicNetwork.ConnectAsync(new("www.youtube.com", 443), default,
            (_, _) => { resolutions++; return Task.FromResult(new[] { address }); },
            (ip, port, _) => { Assert.Equal(address, ip); Assert.Equal(443, port); return Task.FromResult<Stream>(new MemoryStream()); });
        Assert.Equal(1, resolutions);
        using var handler = ProviderHttpClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBodiesAreRejectedWithAndWithoutContentLength(bool length)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response(new string('x', ProviderHttpClient.MaxBytes + 1));
            if (!length)
            {
                response.Content = new StreamContent(new UnseekableStream(new byte[ProviderHttpClient.MaxBytes + 1]));
                response.Content.Headers.ContentType = new("application/json");
                response.Content.Headers.ContentLength = null;
            }
            return Task.FromResult(response);
        });
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Unavailable, (await new MetadataService(http).LookupAsync(VideoUrl, default)).Status);
    }

    [Fact]
    public async Task WrongContentTypeAndMissingTwitchMetaFallBack()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("<html><body>Sign in</body></html>", "text/html")));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var service = new MetadataService(http);
        Assert.Equal(MetadataStatus.Unavailable, (await service.LookupAsync(VideoUrl, default)).Status);
        Assert.Equal(MetadataStatus.Unavailable, (await service.LookupAsync("https://www.twitch.tv/videos/123", default)).Status);
    }

    [Fact]
    public async Task TwitchGenericShellIsUnavailable()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("""
            <meta property="og:title" content="VOD - Twitch">
            <meta property="og:image" content="https://static-cdn.jtvnw.net/ttv-static-metadata/twitch_logo3.jpg">
            """, "text/html")));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        Assert.Equal(MetadataStatus.Unavailable, (await new MetadataService(http).LookupAsync("https://www.twitch.tv/videos/123", default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureAndTimeoutStillSave(bool timeout)
    {
        using var handler = new Handler((_, _) => throw (timeout ? new OperationCanceledException() : new HttpRequestException("fixture failure")));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var store = new MemoryBoardStore();
        var service = new BoardService(store, TimeProvider.System, new MetadataService(http));
        var board = await service.SaveVideo(null, new(0, "Title", VideoUrl, ThumbnailMode.Automatic, null), default);
        Assert.Equal(1, board.Revision);
        Assert.Null(Assert.Single(board.Videos).AutomaticThumbnailUrl);
    }

    [Fact]
    public async Task ApiRefreshPreservesManualValuesAndFallsBackWithoutChangingTitles()
    {
        var body = Json;
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        using var root = new AppFactory();
        using var factory = ApiTests.WithStore(root, new MemoryBoardStore()).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataService>();
            services.AddSingleton<IMetadataService>(new MetadataService(http));
        }));
        using var client = ApiTests.Authorized(factory);
        var suggestion = await client.PostAsJsonAsync(ApiRoutes.Metadata, new MetadataRequest(VideoUrl));
        Assert.Equal(MetadataStatus.Available, (await suggestion.Content.ReadFromJsonAsync<MetadataResult>())!.Status);
        var request = new SaveVideoRequest(0, "User title", VideoUrl, ThumbnailMode.Automatic, "https://example.com/manual.jpg");
        var board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Videos, request));
        var first = board.Videos[0];
        Assert.Equal(Thumbnail, first.AutomaticThumbnailUrl);
        Assert.Equal("User title", first.Title);
        body = "broken JSON";
        board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Videos, request with { ExpectedRevision = 1 }));
        Assert.Null(board.Videos[0].AutomaticThumbnailUrl);
        board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", request with { ExpectedRevision = 2 }));
        Assert.Equal(first.Id, board.Videos[0].Id);
        Assert.Equal(Thumbnail, board.Videos[0].AutomaticThumbnailUrl);
        Assert.Equal(first.CreatedAt, board.Videos[0].CreatedAt);
        var count = handler.Requests.Count;
        board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", request with { ExpectedRevision = 3, ThumbnailMode = ThumbnailMode.Manual }));
        Assert.Equal(count, handler.Requests.Count);
        Assert.Equal(request.ManualThumbnailUrl, board.Videos[0].ManualThumbnailUrl);
        board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", request with { ExpectedRevision = 4, Url = "https://youtu.be/aaaaaaaaaaa", ManualThumbnailUrl = null }));
        Assert.Null(board.Videos[0].AutomaticThumbnailUrl);
        Assert.Null(board.Videos[0].ManualThumbnailUrl);
        body = Json;
        board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", request with { ExpectedRevision = 5 }));
        Assert.Equal(Thumbnail, board.Videos[0].AutomaticThumbnailUrl);
    }

    [Fact]
    public async Task ClearingItemUrlClearsAutomaticThumbnailWithoutFetching()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Json)));
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var service = new BoardService(new MemoryBoardStore(), TimeProvider.System, new MetadataService(http));
        var request = new SaveVideoRequest(0, "Title", VideoUrl, ThumbnailMode.Automatic, "https://example.com/manual.jpg");
        var board = await service.SaveVideo(null, request, default);
        Assert.Equal(Thumbnail, board.Videos[0].AutomaticThumbnailUrl);
        board = await service.SaveVideo(board.Videos[0].Id, request with { ExpectedRevision = 1, Url = "" }, default);
        Assert.Null(board.Videos[0].AutomaticThumbnailUrl);
        Assert.Equal(request.ManualThumbnailUrl, board.Videos[0].ManualThumbnailUrl);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StaleOrMissingWritesDoNotFetchAndLookupRaceStillChecksRevision()
    {
        var store = new MemoryBoardStore();
        using var handler = new Handler((_, _) =>
        {
            store.Board = store.Board with { Revision = 1 };
            return Task.FromResult(Response(Json));
        });
        using var http = new ProviderHttpClient(new HttpClient(handler));
        var service = new BoardService(store, TimeProvider.System, new MetadataService(http));
        var request = new SaveVideoRequest(2, "Title", VideoUrl, ThumbnailMode.Automatic, null);
        await Assert.ThrowsAsync<ApiFailure>(() => service.SaveVideo(null, request, default));
        await Assert.ThrowsAsync<ApiFailure>(() => service.SaveVideo(Guid.NewGuid(), request with { ExpectedRevision = 0 }, default));
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<ApiFailure>(() => service.SaveVideo(null, request with { ExpectedRevision = 0 }, default));
        Assert.Single(handler.Requests);
        Assert.Empty(store.Board.Videos);
        Assert.Equal(1, store.Board.Revision);
    }
}
