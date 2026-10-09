using System.Net;
using System.Net.Http.Json;
using LastSeen2.Contracts;
using LastSeen2.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace LastSeen2.Server.Tests;

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LASTSEEN2_TEST_MONGODB")))
            Skip = "Set LASTSEEN2_TEST_MONGODB to run isolated real MongoDB integration tests.";
    }
}

public sealed class MongoIntegrationTests
{
    [MongoFact]
    public async Task RealMongoCrudOrderingConcurrencyAndHostRestart()
    {
        // Never use the source URI's data database or the app's configured collection.
        // Preserve authentication source when replacing the data database name.
        var source = new MongoUrl(Environment.GetEnvironmentVariable("LASTSEEN2_TEST_MONGODB")!);
        var databaseName = "lastseen2_test_" + Guid.NewGuid().ToString("N");
        var isolated = new MongoUrlBuilder(source.Url)
        {
            DatabaseName = databaseName,
            AuthenticationSource = source.AuthenticationSource ?? source.DatabaseName
        };
        var connection = isolated.ToString();
        using var root = new AppFactory();
        WebApplicationFactory<global::Program> CreateHost() => root.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<MongoDbOptions>(options =>
            {
                options.ConnectionString = connection;
                options.Collection = "board_integration";
            })));
        var settings = MongoClientSettings.FromConnectionString(connection);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        using var cleanup = new MongoClient(settings);
        var initialized = false;
        try
        {
            BoardSnapshot saved;
            Guid videoId;
            Guid channelId;
            using (var host = CreateHost())
            using (var otherHost = CreateHost())
            using (var client = ApiTests.Authorized(host))
            using (var other = ApiTests.Authorized(otherHost))
            {
                // Racing initial reads must produce one revision-zero document.
                var reads = await Task.WhenAll(client.GetAsync(ApiRoutes.Board), other.GetAsync(ApiRoutes.Board));
                foreach (var read in reads) Assert.Equal(0, (await ApiTests.Board(read)).Revision);
                initialized = true;
                var board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Videos, ApiTests.Video(0, "First")));
                videoId = board.Videos[0].Id;
                board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Videos, ApiTests.Video(1, "Second")));
                var secondId = board.Videos[0].Id;
                board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/videos/{videoId}", ApiTests.Video(2, "Edited")));
                Assert.Equal(videoId, board.Videos[0].Id);
                board = await ApiTests.Board(await client.PutAsJsonAsync(ApiRoutes.ReorderVideos, new ReorderVideosRequest(3, [secondId, videoId])));
                board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Channels, ApiTests.Channel(4, "First")));
                channelId = board.Channels.Frequent[0].Id;
                board = await ApiTests.Board(await client.PostAsJsonAsync(ApiRoutes.Channels, ApiTests.Channel(5, "Second")));
                var secondChannelId = board.Channels.Frequent[0].Id;
                board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/channels/{channelId}", ApiTests.Channel(6, "Edited")));
                Assert.Equal(channelId, board.Channels.Frequent[1].Id);
                board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/channels/{secondChannelId}/move", new MoveChannelRequest(7, ChannelGroup.Frequent, 1)));
                board = await ApiTests.Board(await client.PutAsJsonAsync($"/api/channels/{channelId}/move", new MoveChannelRequest(8, ChannelGroup.Archived, 0)));
                Assert.Equal(channelId, Assert.Single(board.Channels.Archived).Id);
                Assert.Equal(secondChannelId, Assert.Single(board.Channels.Frequent).Id);

                var beforeInvalid = await (await client.GetAsync(ApiRoutes.Board)).Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(ApiRoutes.ReorderVideos, new ReorderVideosRequest(9, [videoId, videoId]))).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/channels/{channelId}/move", new MoveChannelRequest(9, ChannelGroup.Frequent, 99))).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/videos/{videoId}?expectedRevision=8")).StatusCode);
                Assert.Equal(beforeInvalid, await (await client.GetAsync(ApiRoutes.Board)).Content.ReadAsStringAsync());

                // Different hosts race the same revision. Exactly one replacement may win.
                var writes = await Task.WhenAll(
                    client.PutAsJsonAsync($"/api/videos/{videoId}", ApiTests.Video(9, "Writer one")),
                    other.PutAsJsonAsync($"/api/videos/{videoId}", ApiTests.Video(9, "Writer two")));
                Assert.Single(writes, r => r.StatusCode == HttpStatusCode.OK);
                Assert.Single(writes, r => r.StatusCode == HttpStatusCode.Conflict);
                saved = await ApiTests.Board(await client.GetAsync(ApiRoutes.Board));
                Assert.Equal(10, saved.Revision);
            }
            // Discard both web hosts and all application services before re-reading MongoDB.
            using (var restarted = CreateHost())
            using (var client = ApiTests.Authorized(restarted))
            {
                var restored = await ApiTests.Board(await client.GetAsync(ApiRoutes.Board));
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(saved), System.Text.Json.JsonSerializer.Serialize(restored));
                restored = await ApiTests.Board(await client.DeleteAsync($"/api/videos/{videoId}?expectedRevision=10"));
                Assert.Single(restored.Videos);
                restored = await ApiTests.Board(await client.DeleteAsync($"/api/channels/{channelId}?expectedRevision=11"));
                Assert.Empty(restored.Channels.Archived);
                Assert.Equal(12, restored.Revision);
            }
            var documents = cleanup.GetDatabase(databaseName).GetCollection<BsonDocument>("board_integration");
            Assert.Equal(1, await documents.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            // Only the name generated above is eligible for cleanup. Never source.DatabaseName.
            if (!databaseName.StartsWith("lastseen2_test_", StringComparison.Ordinal)
                || databaseName == source.DatabaseName) throw new InvalidOperationException("Unsafe test cleanup target");
            try { await cleanup.DropDatabaseAsync(databaseName); }
            catch (MongoCommandException error) when (!initialized && error.Code == 13)
            {
                // Preserve the original initialization failure if credentials cannot create/drop test databases.
            }
        }
    }
}
