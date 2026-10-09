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
    public const int MaxDocumentBytes = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly FilterDefinition<BsonDocument> BoardId = Builders<BsonDocument>.Filter.Eq("_id", "board");
    private readonly IMongoCollection<BsonDocument> collection;
    private readonly MongoClient client;

    public MongoBoardStore(IOptions<MongoDbOptions> options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(10);
        settings.ReadPreference = ReadPreference.Primary;
        // Require acknowledgement even if the supplied URI requests w=0.
        settings.WriteConcern = WriteConcern.WMajority;
        client = new MongoClient(settings);
        collection = client.GetDatabase(new MongoUrl(options.Value.ConnectionString).DatabaseName)
            .GetCollection<BsonDocument>(options.Value.Collection);
    }

    public void Dispose() => client.Dispose();

    public static BsonDocument Encode(BoardSnapshot board)
    {
        var document = BsonDocument.Parse(JsonSerializer.Serialize(board, Json));
        document["_id"] = "board";
        document["schemaVersion"] = 1;
        document["revision"] = board.Revision;
        if (document.ToBson().Length > MaxDocumentBytes)
            throw new ApiFailure(413, "board_too_large", "Board exceeds the 12 MiB storage limit; remove items or shorten fields");
        return document;
    }

    public async Task<BoardSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = timeout.Token;
        var document = await collection.Find(BoardId).FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            var empty = Encode(new BoardSnapshot(0, [], new([], [], [])));
            try { await collection.InsertOneAsync(empty, cancellationToken: cancellationToken); }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            document = await collection.Find(BoardId).FirstAsync(cancellationToken);
        }
        if (document["schemaVersion"].AsInt32 != 1)
            throw new ApiFailure(503, "storage_unavailable", "Unsupported board storage version");
        return JsonSerializer.Deserialize<BoardSnapshot>(document.ToJson(new JsonWriterSettings
            { OutputMode = JsonOutputMode.RelaxedExtendedJson }), Json)!;
    }

    public async Task<bool> ReplaceAsync(long expectedRevision, BoardSnapshot board, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = timeout.Token;
        var document = Encode(board);
        var filter = BoardId & Builders<BsonDocument>.Filter.Eq("revision", expectedRevision);
        var result = await collection.ReplaceOneAsync(filter, document, cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }
}
