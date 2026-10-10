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
            var stored = await documents.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            Assert.Equal(3, stored.Count); // Two remaining bookmarks plus revision metadata.
            var metadata = Assert.Single(stored, d => d["kind"] == "board");
            Assert.Equal(1, metadata["schemaVersion"].AsInt32);
            Assert.Equal(12, metadata["revision"].ToInt64());
            Assert.False(metadata.Contains("videos"));
            Assert.False(metadata.Contains("channels"));
            Assert.Single(stored, d => d["kind"] == "video");
            Assert.Single(stored, d => d["kind"] == "channel");
            Assert.All(stored.Where(d => d["kind"] != "board"), d =>
            {
                Assert.True(Guid.TryParse(d["_id"].AsString, out _));
                Assert.Equal(1, d["schemaVersion"].AsInt32);
                var version = Assert.Single(d["versions"].AsBsonDocument);
                Assert.Equal(0, version.Value["value"]["position"].AsInt32);
                Assert.False(d.Contains("id"));
            });
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

    [MongoFact]
    public async Task PartialStagingFailurePreservesBoardAndUnrelatedDocuments()
    {
        using var test = new IsolatedStore();
        try
        {
            using var store = test.Open();
            await store.ReadAsync(default);
            var video = TestVideo("Keep");
            var original = new BoardSnapshot(1, [video], new([], [], []));
            Assert.True(await store.ReplaceAsync(0, original, default));
            var unrelated = new BsonDocument { { "_id", Guid.NewGuid().ToString() }, { "note", "Leave intact" } };
            await test.Collection.InsertOneAsync(unrelated);
            var collision = video with { Id = Guid.Parse(unrelated["_id"].AsString) };
            var invalid = original with { Revision = 2, Videos = [video with { Title = "Must roll back" }, collision] };
            await Assert.ThrowsAnyAsync<MongoException>(() => store.ReplaceAsync(1, invalid, default));
            Assert.Equal(Serialize(original), Serialize(await store.ReadAsync(default)));
            Assert.Equal(unrelated, await test.Collection.Find(new BsonDocument("_id", unrelated["_id"])).SingleAsync());
            using var restarted = test.Open();
            Assert.Equal(Serialize(original), Serialize(await restarted.ReadAsync(default)));
            Assert.False(await restarted.ReplaceAsync(0, original, default));
            Assert.True(await restarted.ReplaceAsync(1, original with { Revision = 2, Videos = [] }, default));
            Assert.Empty((await store.ReadAsync(default)).Videos);
            Assert.Equal(2, await test.Collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(unrelated, await test.Collection.Find(new BsonDocument("_id", unrelated["_id"])).SingleAsync());
        }
        finally { await test.CleanupAsync(); }
    }

    private sealed class IsolatedStore : IDisposable
    {
        private readonly MongoUrl source = new(Environment.GetEnvironmentVariable("LASTSEEN2_TEST_MONGODB")!);
        public string DatabaseName { get; } = "lastseen2_test_" + Guid.NewGuid().ToString("N");
        public MongoClient Client { get; }
        public IMongoCollection<BsonDocument> Collection { get; }
        private readonly Microsoft.Extensions.Options.IOptions<MongoDbOptions> options;

        public IsolatedStore()
        {
            var connection = new MongoUrlBuilder(source.Url)
            {
                DatabaseName = DatabaseName, AuthenticationSource = source.AuthenticationSource ?? source.DatabaseName
            }.ToString();
            Client = new MongoClient(connection);
            Collection = Client.GetDatabase(DatabaseName).GetCollection<BsonDocument>("board_integration");
            options = Microsoft.Extensions.Options.Options.Create(new MongoDbOptions
            {
                ConnectionString = connection, Collection = "board_integration"
            });
        }

        public MongoBoardStore Open() => new(options);
        public async Task CleanupAsync()
        {
            if (!DatabaseName.StartsWith("lastseen2_test_", StringComparison.Ordinal) || DatabaseName == source.DatabaseName)
                throw new InvalidOperationException("Unsafe test cleanup target");
            await Client.DropDatabaseAsync(DatabaseName);
        }
        public void Dispose() => Client.Dispose();
    }

    private static VideoBookmark TestVideo(string title) => new(Guid.NewGuid(), title, "https://example.com/video",
        ThumbnailMode.Manual, null, "https://example.com/image", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static string Serialize(BoardSnapshot board) => System.Text.Json.JsonSerializer.Serialize(board);

    [MongoFact]
    public async Task StandaloneReadsIgnoreInterruptedStagingAndRecoverCommittedSaveBeforeCleanup()
    {
        using var test = new IsolatedStore();
        try
        {
            var hello = await test.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
            Assert.False(hello.Contains("setName")); // This test must exercise a real standalone mongod.
            Assert.NotEqual("isdbgrid", hello.GetValue("msg", "").AsString);
            var before = new BoardSnapshot(1, [TestVideo("Keep"), TestVideo("Delete")], new([], [], []));
            using (var store = test.Open())
            {
                await store.ReadAsync(default);
                Assert.True(await store.ReplaceAsync(0, before, default));
            }
            var metadata = await test.Collection.Find(new BsonDocument("_id", "board")).SingleAsync();
            var after = before with { Revision = 2, Videos = [before.Videos[0] with { Title = "Updated" }, TestVideo("New")] };
            var token = Guid.NewGuid().ToString("N");
            var generation = metadata["generation"].ToInt64() + 1;
            var references = new BsonArray();
            // Persist item versions as if the process died after staging but before publishing.
            foreach (var value in MongoBoardStore.EncodeItems(after))
            {
                await test.Collection.UpdateOneAsync(new BsonDocument("_id", value["_id"]),
                    Builders<BsonDocument>.Update.SetOnInsert("kind", value["kind"]).SetOnInsert("schemaVersion", 1)
                        .Set("versions." + token, new BsonDocument { { "generation", generation }, { "value", value } }),
                    new UpdateOptions { IsUpsert = true });
                references.Add(new BsonDocument { { "id", value["_id"] }, { "version", token } });
            }
            using (var restarted = test.Open())
                Assert.Equal(Serialize(before), Serialize(await restarted.ReadAsync(default)));
            // Publishing is one atomic document replacement. Simulate a crash before cleanup or response.
            metadata["revision"] = 2L;
            metadata["generation"] = generation;
            metadata["items"] = references;
            await test.Collection.ReplaceOneAsync(new BsonDocument("_id", "board"), metadata);
            using (var restarted = test.Open())
            {
                Assert.Equal(Serialize(after), Serialize(await restarted.ReadAsync(default)));
                Assert.False(await restarted.ReplaceAsync(1, before, default));
                Assert.True(await restarted.ReplaceAsync(2, after with { Revision = 3 }, default));
            }
            var documents = await test.Collection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
            Assert.Equal(3, documents.Count);
            Assert.All(documents.Where(d => d["kind"] == "video"), d => Assert.Single(d["versions"].AsBsonDocument));
            // A missing committed version is an error, never a successful partial board.
            await test.Collection.DeleteOneAsync(new BsonDocument("_id", after.Videos[0].Id.ToString()));
            using var broken = test.Open();
            await Assert.ThrowsAsync<ApiFailure>(() => broken.ReadAsync(default));
        }
        finally { await test.CleanupAsync(); }
    }

    [MongoFact]
    public async Task ConcurrentReadersAndWritersKeepCompleteSnapshotsOnStandalone()
    {
        using var test = new IsolatedStore();
        try
        {
            using var first = test.Open();
            using var second = test.Open();
            using var reader = test.Open();
            await first.ReadAsync(default);
            var board = new BoardSnapshot(1, Enumerable.Range(0, 8).Select(_ => TestVideo("1-a")).ToArray(), new([], [], []));
            Assert.True(await first.ReplaceAsync(0, board, default));
            for (var revision = 2; revision <= 12; revision++)
            {
                var a = board with { Revision = revision, Videos = board.Videos.Reverse().Select(v => v with { Title = revision + "-a" }).ToArray() };
                var b = a with { Videos = a.Videos.Select(v => v with { Title = revision + "-b" }).ToArray() };
                var writes = Task.WhenAll(first.ReplaceAsync(revision - 1, a, default), second.ReplaceAsync(revision - 1, b, default));
                var reads = Enumerable.Range(0, 6).Select(async _ =>
                {
                    var snapshot = await reader.ReadAsync(default);
                    Assert.Equal(8, snapshot.Videos.Length);
                    Assert.All(snapshot.Videos, v => Assert.Equal(snapshot.Videos[0].Title, v.Title));
                    Assert.StartsWith(snapshot.Revision + "-", snapshot.Videos[0].Title);
                }).ToArray();
                Assert.Single(await writes, won => won);
                await Task.WhenAll(reads);
                board = await reader.ReadAsync(default);
                Assert.Equal(revision, board.Revision);
            }
        }
        finally { await test.CleanupAsync(); }
    }

    [MongoFact]
    public async Task UnsupportedStorageIsRejectedWithoutRewritingDocuments()
    {
        using var test = new IsolatedStore();
        try
        {
            using var store = test.Open();
            // Unsupported versions and a version-1 record without the initial manifest shape.
            foreach (var schema in new[] { 2, 3, 1 })
            {
                var document = new BsonDocument
                {
                    { "_id", "board" }, { "kind", "board" }, { "schemaVersion", schema }, { "revision", 7L }
                };
                await test.Collection.InsertOneAsync(document);
                await Assert.ThrowsAsync<ApiFailure>(() => store.ReadAsync(default));
                Assert.Equal(document, await test.Collection.Find(new BsonDocument("_id", "board")).SingleAsync());
                Assert.Equal(1, await test.Collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                await test.Collection.DeleteOneAsync(new BsonDocument("_id", "board"));
            }
        }
        finally { await test.CleanupAsync(); }
    }

}
