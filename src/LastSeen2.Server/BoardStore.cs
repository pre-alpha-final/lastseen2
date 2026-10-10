using System.Text.Json;
using LastSeen2.Contracts;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace LastSeen2.Server;

public interface IBoardStore
{
    Task<BoardSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(long expectedRevision, BoardSnapshot board, CancellationToken cancellationToken);
}

public sealed class MongoBoardStore : IBoardStore, IDisposable
{
    public const int MaxBoardBytes = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly FilterDefinitionBuilder<BsonDocument> Filter = Builders<BsonDocument>.Filter;
    private static readonly FilterDefinition<BsonDocument> BoardId = Filter.Eq("_id", "board");
    private static readonly FilterDefinition<BsonDocument> Items = Filter.In("kind", new[] { "video", "channel" });
    private readonly IMongoCollection<BsonDocument> collection;
    private readonly MongoClient client;

    public MongoBoardStore(IOptions<MongoDbOptions> options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(10);
        settings.ReadPreference = ReadPreference.Primary;
        settings.ReadConcern = ReadConcern.Local;
        // Standalone MongoDB: acknowledged, journaled writes; no sessions or transactions.
        settings.WriteConcern = new WriteConcern(1, journal: true);
        settings.RetryWrites = false;
        client = new MongoClient(settings);
        collection = client.GetDatabase(new MongoUrl(options.Value.ConnectionString).DatabaseName)
            .GetCollection<BsonDocument>(options.Value.Collection);
    }

    public void Dispose() => client.Dispose();

    private static BsonDocument Metadata(long revision, long generation, BsonArray references) => new()
    {
        { "_id", "board" }, { "kind", "board" }, { "schemaVersion", 3 },
        { "revision", revision }, { "generation", generation }, { "items", references }
    };

    // Positions belong to the item; the board metadata contains references, never bookmark data.
    public static List<BsonDocument> EncodeItems(BoardSnapshot board)
    {
        var documents = new List<BsonDocument>();
        void Add<T>(IEnumerable<T> items, string kind)
        {
            var position = 0;
            foreach (var item in items)
            {
                var document = BsonDocument.Parse(JsonSerializer.Serialize(item, Json));
                document.InsertAt(0, new BsonElement("_id", document["id"]));
                document.Remove("id");
                document["kind"] = kind;
                document["position"] = position++;
                documents.Add(document);
            }
        }
        Add(board.Videos, "video");
        Add(board.Channels.Frequent, "channel");
        Add(board.Channels.Infrequent, "channel");
        Add(board.Channels.Archived, "channel");
        if (documents.Sum(document => (long)document.ToBson().Length) > MaxBoardBytes)
            throw new ApiFailure(413, "board_too_large", "Board exceeds the 12 MiB storage limit; remove items or shorten fields");
        return documents;
    }

    private static T Decode<T>(BsonDocument document) => JsonSerializer.Deserialize<T>(
        document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }), Json)!;

    private static BoardSnapshot Snapshot(long revision, IEnumerable<BsonDocument> values)
    {
        var ordered = values.OrderBy(d => d["position"].AsInt32).ThenBy(d => d["_id"].AsString).ToArray();
        foreach (var value in ordered) value["id"] = value["_id"];
        var videos = ordered.Where(d => d["kind"] == "video").Select(Decode<VideoBookmark>).ToArray();
        var channels = ordered.Where(d => d["kind"] == "channel").Select(Decode<ChannelBookmark>).ToArray();
        return new(revision, videos, new(
            channels.Where(c => c.Group == ChannelGroup.Frequent).ToArray(),
            channels.Where(c => c.Group == ChannelGroup.Infrequent).ToArray(),
            channels.Where(c => c.Group == ChannelGroup.Archived).ToArray()));
    }

    private static ApiFailure Unavailable() => new(503, "storage_unavailable", "Storage unavailable; reload the board before retrying a save");

    private static FilterDefinition<BsonDocument> Match(BsonDocument metadata) => BoardId
        & Filter.Eq("schemaVersion", metadata["schemaVersion"])
        & Filter.Eq("revision", metadata["revision"])
        & (metadata.TryGetValue("generation", out var generation)
            ? Filter.Eq("generation", generation) : Filter.Exists("generation", false));

    private async Task EnsureMetadataAsync(CancellationToken ct)
    {
        if (await collection.Find(BoardId).AnyAsync(ct)) return;
        try { await collection.InsertOneAsync(Metadata(0, 0, []), cancellationToken: ct); }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    private async Task<(BsonDocument Metadata, BoardSnapshot Board)> LoadAsync(CancellationToken ct)
    {
        await EnsureMetadataAsync(ct);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = await collection.Find(BoardId).FirstAsync(ct);
            var schema = metadata["schemaVersion"].AsInt32;
            if (schema is 1 or 2)
            {
                BoardSnapshot legacy;
                if (schema == 1) legacy = Decode<BoardSnapshot>(metadata);
                else
                {
                    var flat = await collection.Find(Items).ToListAsync(ct);
                    if (!await collection.Find(Match(metadata)).AnyAsync(ct)) continue;
                    legacy = Snapshot(metadata["revision"].ToInt64(), flat);
                }
                // Competing migrations stage distinct versions and CAS the same old metadata.
                await SaveAsync(metadata, legacy, migrating: true, ct);
                continue;
            }
            if (schema != 3) throw Unavailable();
            var references = metadata["items"].AsBsonArray;
            var documents = await collection.Find(Filter.In("_id", references.Select(r => r["id"]))).ToListAsync(ct);
            var byId = documents.ToDictionary(d => d["_id"]);
            var values = new List<BsonDocument>();
            foreach (var reference in references)
            {
                if (byId.TryGetValue(reference["id"], out var document)
                    && document.GetValue("versions", new BsonDocument()).AsBsonDocument
                        .TryGetValue(reference["version"].AsString, out var version))
                    values.Add(version["value"].AsBsonDocument.DeepClone().AsBsonDocument);
            }
            // Old versions can be pruned while a reader is in flight. Retry against the new
            // manifest rather than mixing revisions or treating a collected version as a deletion.
            if (!await collection.Find(Match(metadata)).AnyAsync(ct)) continue;
            if (values.Count != references.Count) throw Unavailable();
            return (metadata, Snapshot(metadata["revision"].ToInt64(), values));
        }
    }

    public async Task<BoardSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return (await LoadAsync(timeout.Token)).Board;
    }

    public async Task<bool> ReplaceAsync(long expectedRevision, BoardSnapshot board, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var current = await LoadAsync(timeout.Token);
        if (current.Board.Revision != expectedRevision) return false;
        return await SaveAsync(current.Metadata, board, migrating: false, timeout.Token);
    }

    private async Task<bool> SaveAsync(BsonDocument before, BoardSnapshot board, bool migrating, CancellationToken ct)
    {
        var values = EncodeItems(board);
        var generation = checked(before.GetValue("generation", 0).ToInt64() + 1);
        var token = Guid.NewGuid().ToString("N");
        var existing = (await collection.Find(Items).ToListAsync(ct)).ToDictionary(d => d["_id"]);
        if (migrating && before["schemaVersion"] == 1 && existing.Values.Any(d => d["schemaVersion"] != 3))
            throw Unavailable();
        var previous = migrating ? new Dictionary<BsonValue, BsonValue>()
            : before["items"].AsBsonArray.ToDictionary(r => r["id"], r => r["version"]);
        var references = new BsonArray();
        var stagedIds = new List<BsonValue>();
        var writes = new List<WriteModel<BsonDocument>>();
        var commitAttempted = false;
        try
        {
            foreach (var value in values)
            {
                var id = value["_id"];
                if (previous.TryGetValue(id, out var previousToken)
                    && existing.TryGetValue(id, out var document)
                    && document["versions"].AsBsonDocument.TryGetValue(previousToken.AsString, out var version)
                    && version["value"].Equals(value))
                {
                    references.Add(new BsonDocument { { "id", id }, { "version", previousToken } });
                    continue;
                }
                stagedIds.Add(id);
                var filter = Filter.Eq("_id", id) & Filter.Eq("kind", value["kind"])
                    & Filter.In("schemaVersion", migrating ? new[] { 2, 3 } : new[] { 3 });
                var update = Builders<BsonDocument>.Update
                    .SetOnInsert("_id", id).SetOnInsert("kind", value["kind"]).SetOnInsert("schemaVersion", 3)
                    .Set("versions." + token, new BsonDocument { { "generation", generation }, { "value", value } });
                // Different writers only add immutable, uniquely named versions in each item.
                // Upsert colliding with an unrelated ID fails without overwriting that record.
                writes.Add(new UpdateOneModel<BsonDocument>(filter, update) { IsUpsert = true });
                references.Add(new BsonDocument { { "id", id }, { "version", token } });
            }
            while (writes.Count > 0)
            {
                try
                {
                    await collection.BulkWriteAsync(writes, cancellationToken: ct);
                    break;
                }
                catch (MongoBulkWriteException<BsonDocument> error) when (error.WriteErrors.Count > 0 && error.WriteConcernError is null
                    && error.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
                {
                    // Two writers can upsert the same new ID simultaneously. Retry only when
                    // the winning insert is an app item of the expected kind/schema.
                    foreach (var failure in error.WriteErrors)
                    {
                        var write = (UpdateOneModel<BsonDocument>)writes[failure.Index];
                        if (!await collection.Find(write.Filter).AnyAsync(ct)) throw;
                    }
                }
            }
            var after = Metadata(board.Revision, generation, references);
            commitAttempted = true;
            var result = await collection.ReplaceOneAsync(Match(before), after, cancellationToken: ct);
            if (result.MatchedCount == 0)
            {
                // An acknowledged CAS loss cannot become a late successful commit.
                await DiscardAsync(stagedIds, token, ct);
                return false;
            }
            // The atomic manifest replacement is the commit point. Cleanup is optional maintenance.
            await TryPruneAsync(after, ct);
            return true;
        }
        catch
        {
            // A timed-out commit may have succeeded. Never discard potentially committed versions.
            if (!commitAttempted)
            {
                try { await DiscardAsync(stagedIds, token, ct); }
                catch (Exception error) when (error is MongoException or OperationCanceledException or TimeoutException) { }
            }
            throw;
        }
    }

    private async Task DiscardAsync(IEnumerable<BsonValue> ids, string token, CancellationToken ct)
    {
        var filter = Filter.In("_id", ids) & Items;
        await collection.UpdateManyAsync(filter, Builders<BsonDocument>.Update.Unset("versions." + token), cancellationToken: ct);
        await collection.DeleteManyAsync(filter & Filter.Eq("schemaVersion", 3)
            & Filter.Eq("versions", new BsonDocument()), ct);
    }

    private async Task TryPruneAsync(BsonDocument committed, CancellationToken ct)
    {
        try
        {
            var keep = committed["items"].AsBsonArray.ToDictionary(r => r["id"], r => r["version"].AsString);
            var generation = committed["generation"].ToInt64();
            var writes = new List<WriteModel<BsonDocument>>();
            foreach (var document in await collection.Find(Items).ToListAsync(ct))
            {
                var id = document["_id"];
                var updates = new List<UpdateDefinition<BsonDocument>>();
                foreach (var version in document.GetValue("versions", new BsonDocument()).AsBsonDocument)
                {
                    if (keep.GetValueOrDefault(id) == version.Name || version.Value["generation"].ToInt64() > generation) continue;
                    updates.Add(Builders<BsonDocument>.Update.Unset("versions." + version.Name));
                }
                // Upgrade flat schema-2 items without replacing concurrent writers' versions.
                if (document["schemaVersion"] == 2)
                {
                    var update = Builders<BsonDocument>.Update.Set("schemaVersion", 3);
                    foreach (var field in document.Names.Where(n => n is not ("_id" or "kind" or "schemaVersion" or "versions")))
                        update = update.Unset(field);
                    updates.Add(update);
                }
                if (updates.Count > 0) writes.Add(new UpdateOneModel<BsonDocument>(Filter.Eq("_id", id) & Items,
                    Builders<BsonDocument>.Update.Combine(updates)));
            }
            if (writes.Count > 0) await collection.BulkWriteAsync(writes, cancellationToken: ct);
            await collection.DeleteManyAsync(Items & Filter.Eq("schemaVersion", 3)
                & Filter.Eq("versions", new BsonDocument()), ct);
        }
        catch (Exception error) when (error is MongoException or OperationCanceledException or TimeoutException)
        {
            // Unreferenced versions are invisible and can be collected after the next save.
        }
    }
}
