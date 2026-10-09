using System.Net;
using System.Net.Http.Json;
using LastSeen2.Contracts;
using LastSeen2.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace LastSeen2.Server.Tests;

public sealed class MemoryBoardStore : IBoardStore
{
    private readonly object gate = new();
    public BoardSnapshot Board { get; set; } = new(0, [], new([], [], []));
    public bool RejectReplacement { get; set; }
    public Task<BoardSnapshot> ReadAsync(CancellationToken ct) { lock (gate) return Task.FromResult(Board); }
    public Task<bool> ReplaceAsync(long revision, BoardSnapshot board, CancellationToken ct)
    {
        lock (gate)
        {
            if (RejectReplacement || Board.Revision != revision) return Task.FromResult(false);
            Board = board;
            return Task.FromResult(true);
        }
    }
}

public sealed class ApiTests
{
    internal static HttpClient Authorized(WebApplicationFactory<global::Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiRoutes.AccessKeyHeader, "test-only-not-a-real-key");
        return client;
    }

    internal static WebApplicationFactory<global::Program> WithStore(AppFactory factory, IBoardStore store) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBoardStore>();
            services.AddSingleton(store);
        }));

    internal static async Task<BoardSnapshot> Board(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardSnapshot>())!;
    }

    internal static SaveVideoRequest Video(long revision, string title = "Video") =>
        new(revision, title, "https://example.com/video", ThumbnailMode.Automatic, null);
    internal static SaveChannelRequest Channel(long revision, string name, ChannelGroup group = ChannelGroup.Frequent) =>
        new(revision, name, "https://example.com/channel", group);

    [Fact]
    public async Task VideoCrudOrderingManualOverridesAndRevisions()
    {
        using var root = new AppFactory();
        using var factory = WithStore(root, new MemoryBoardStore());
        using var client = Authorized(factory);
        var board = await Board(await client.GetAsync(ApiRoutes.Board));
        Assert.Equal(0, board.Revision);
        board = await Board(await client.PostAsJsonAsync(ApiRoutes.Videos, Video(0, "One") with
            { ThumbnailMode = ThumbnailMode.Manual, ManualThumbnailUrl = "https://example.com/image.png" }));
        var first = board.Videos[0];
        board = await Board(await client.PostAsJsonAsync(ApiRoutes.Videos, Video(1, "Two")));
        var second = board.Videos[0];
        Assert.Equal(new[] { second.Id, first.Id }, board.Videos.Select(v => v.Id));
        board = await Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", Video(2, "Edited") with
            { ManualThumbnailUrl = first.ManualThumbnailUrl }));
        Assert.Equal(first.Id, board.Videos[0].Id);
        Assert.Equal(first.CreatedAt, board.Videos[0].CreatedAt);
        Assert.Equal(TimeSpan.Zero, board.Videos[0].UpdatedAt.Offset);
        Assert.Equal(first.ManualThumbnailUrl, board.Videos[0].ManualThumbnailUrl);
        board = await Board(await client.PutAsJsonAsync(ApiRoutes.ReorderVideos, new ReorderVideosRequest(3, [second.Id, first.Id])));
        Assert.Equal(second.Id, board.Videos[0].Id);
        board = await Board(await client.PutAsJsonAsync($"/api/videos/{first.Id}", Video(4)));
        Assert.Null(board.Videos[0].ManualThumbnailUrl);
        board = await Board(await client.DeleteAsync($"/api/videos/{first.Id}?expectedRevision=5"));
        Assert.Equal(second.Id, Assert.Single(board.Videos).Id);
        Assert.Equal(6, board.Revision);
    }

    [Fact]
    public async Task ChannelCrudAndMovesUsePostRemovalIndices()
    {
        using var root = new AppFactory();
        using var factory = WithStore(root, new MemoryBoardStore());
        using var client = Authorized(factory);
        var board = await Board(await client.PostAsJsonAsync(ApiRoutes.Channels, Channel(0, "One")));
        var first = board.Channels.Frequent[0];
        board = await Board(await client.PostAsJsonAsync(ApiRoutes.Channels, Channel(1, "Two")));
        var second = board.Channels.Frequent[0];
        board = await Board(await client.PutAsJsonAsync($"/api/channels/{first.Id}", Channel(2, "Edited")));
        Assert.Equal(new[] { second.Id, first.Id }, board.Channels.Frequent.Select(c => c.Id));
        board = await Board(await client.PutAsJsonAsync($"/api/channels/{second.Id}/move", new MoveChannelRequest(3, ChannelGroup.Frequent, 1)));
        Assert.Equal(new[] { first.Id, second.Id }, board.Channels.Frequent.Select(c => c.Id));
        board = await Board(await client.PutAsJsonAsync($"/api/channels/{first.Id}/move", new MoveChannelRequest(4, ChannelGroup.Archived, 0)));
        Assert.Equal(ChannelGroup.Archived, Assert.Single(board.Channels.Archived).Group);
        Assert.Equal(second.Id, Assert.Single(board.Channels.Frequent).Id);
        board = await Board(await client.PutAsJsonAsync($"/api/channels/{second.Id}", Channel(5, "Two", ChannelGroup.Archived)));
        Assert.Equal(new[] { second.Id, first.Id }, board.Channels.Archived.Select(c => c.Id));
        Assert.Empty(board.Channels.Frequent);
        board = await Board(await client.DeleteAsync($"/api/channels/{second.Id}?expectedRevision=6"));
        Assert.Equal(first.Id, Assert.Single(board.Channels.Archived).Id);
    }

    [Theory]
    [InlineData("", "https://example.com", "Automatic", null)]
    [InlineData("x", "javascript:alert(1)", "Automatic", null)]
    [InlineData("x", "https://user:password@example.com", "Automatic", null)]
    [InlineData("x", "relative/path", "Automatic", null)]
    [InlineData("x", "https://example.com", "Manual", null)]
    [InlineData("x", "https://example.com", "Automatic", "file:///image")]
    [InlineData("x", "https://example.com", "Unknown", null)]
    public async Task InvalidVideoDoesNotChangeBoard(string title, string url, string mode, string? manual)
    {
        using var root = new AppFactory();
        var store = new MemoryBoardStore();
        using var factory = WithStore(root, store);
        using var client = Authorized(factory);
        var response = await client.PostAsJsonAsync(ApiRoutes.Videos, new { expectedRevision = 0, title, url, thumbnailMode = mode, manualThumbnailUrl = manual });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Equal(0, store.Board.Revision);
        Assert.Empty(store.Board.Videos);
    }

    [Fact]
    public async Task InvalidReordersMovesMissingItemsAndStaleWritesLeaveSnapshotUnchanged()
    {
        using var root = new AppFactory();
        var store = new MemoryBoardStore();
        using var factory = WithStore(root, store);
        using var client = Authorized(factory);
        var board = await Board(await client.PostAsJsonAsync(ApiRoutes.Videos, Video(0)));
        var id = board.Videos[0].Id;
        foreach (var ids in new Guid[]?[] { null, [], [id, id], [Guid.NewGuid()] })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(ApiRoutes.ReorderVideos, new { expectedRevision = 1, videoIds = ids })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(ApiRoutes.Videos, Video(0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/videos/{Guid.NewGuid()}?expectedRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/videos/{id}?expectedRevision=-1")).StatusCode);
        board = await Board(await client.PostAsJsonAsync(ApiRoutes.Channels, Channel(1, "One")));
        var channelId = board.Channels.Frequent[0].Id;
        foreach (var index in new[] { -1, 1 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/channels/{channelId}/move", new MoveChannelRequest(2, ChannelGroup.Archived, index))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(ApiRoutes.Channels, Channel(2, "x", (ChannelGroup)42))).StatusCode);
        Assert.Equal(2, store.Board.Revision);
        Assert.Equal(channelId, Assert.Single(store.Board.Channels.Frequent).Id);
        store.RejectReplacement = true;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(ApiRoutes.Videos, Video(2))).StatusCode);
        Assert.Equal(2, store.Board.Revision);
    }

    [Fact]
    public async Task BadBodiesAndQueriesReturnSafeValidationProblems()
    {
        using var root = new AppFactory();
        using var factory = WithStore(root, new MemoryBoardStore());
        using var client = Authorized(factory);
        foreach (var body in new[] { "{", "null", "{}", "{\"expectedRevision\":\"wrong\"}" })
        {
            var response = await client.PostAsync(ApiRoutes.Videos, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        var delete = await client.DeleteAsync($"/api/videos/{Guid.NewGuid()}?expectedRevision=wrong");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task AuthenticationFailuresBlockFurtherGuessesUntilWindowResets()
    {
        using var root = new AppFactory();
        var clock = new TestClock();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiRoutes.AccessKeyHeader, "incorrect");
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(ApiRoutes.CheckKey, null)).StatusCode);
        var limited = await client.PostAsync(ApiRoutes.CheckKey, null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/app.css")).StatusCode);
        using var valid = Authorized(factory);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await valid.PostAsync(ApiRoutes.CheckKey, null)).StatusCode);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal(HttpStatusCode.NoContent, (await valid.PostAsync(ApiRoutes.CheckKey, null)).StatusCode);
        var metadata = await valid.PostAsJsonAsync(ApiRoutes.Metadata, new MetadataRequest("https://youtube.com/watch?v=test"));
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal(MetadataStatus.Unsupported, (await metadata.Content.ReadFromJsonAsync<MetadataResult>())!.Status);
    }

    [Fact]
    public async Task StorageFailuresDoNotLeakExceptionDetails()
    {
        using var root = new AppFactory();
        using var factory = WithStore(root, new UnavailableStore());
        using var client = Authorized(factory);
        var response = await client.GetAsync(ApiRoutes.Board);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("storage_unavailable", body);
        Assert.DoesNotContain("sensitive", body);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class UnavailableStore : IBoardStore
    {
        public Task<BoardSnapshot> ReadAsync(CancellationToken ct) => throw new MongoException("sensitive connection information");
        public Task<bool> ReplaceAsync(long revision, BoardSnapshot board, CancellationToken ct) => throw new NotImplementedException();
    }

    [Theory]
    [InlineData("ConnectionStrings:LastSeen2", "")]
    [InlineData("ConnectionStrings:LastSeen2", "mongodb://localhost:27017/?authSource=admin")]
    [InlineData("ConnectionStrings:LastSeen2", "not-a-uri")]
    [InlineData("MongoDb:Collection", "")]
    [InlineData("Authentication:AccessKey", "")]
    public void InvalidConfigurationStopsStartupWithoutLeakingValues(string key, string value)
    {
        using var root = new AppFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.DoesNotContain("not-a-uri", exception.Message);
    }

    [Fact]
    public async Task OversizedBoardIsRejectedBeforeReplacement()
    {
        var video = new VideoBookmark(Guid.NewGuid(), "x", "https://example.com", ThumbnailMode.Automatic,
            null, new string('x', 4096), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var store = new MemoryBoardStore { Board = new(0, Enumerable.Range(0, 3000)
            .Select(_ => video with { Id = Guid.NewGuid() }).ToArray(), new([], [], [])) };
        using var root = new AppFactory();
        using var factory = WithStore(root, store);
        using var client = Authorized(factory);
        var original = store.Board;
        var response = await client.PostAsJsonAsync(ApiRoutes.Videos, Video(0));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("board_too_large", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.Same(original, store.Board);
    }
}
