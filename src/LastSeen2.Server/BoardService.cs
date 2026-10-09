using LastSeen2.Contracts;

namespace LastSeen2.Server;

public sealed class BoardService(IBoardStore store, TimeProvider clock, IMetadataService metadata)
{
    public const int MaxItems = 10000;
    public Task<BoardSnapshot> ReadAsync(CancellationToken ct) => store.ReadAsync(ct);

    private Task<BoardSnapshot> Mutate(long revision, Func<BoardSnapshot, BoardSnapshot> change, CancellationToken ct) =>
        MutateAsync(revision, board => Task.FromResult(change(board)), ct);

    private async Task<BoardSnapshot> MutateAsync(long revision, Func<BoardSnapshot, Task<BoardSnapshot>> change, CancellationToken ct)
    {
        if (revision < 0 || revision == long.MaxValue) throw ApiFailure.Invalid("expectedRevision", "Revision must be nonnegative and incrementable");
        var current = await store.ReadAsync(ct);
        if (current.Revision != revision) throw ApiFailure.Conflict();
        var updated = (await change(current)) with { Revision = revision + 1 };
        if (updated.Videos.Length + AllChannels(updated).Count() > MaxItems)
            throw new ApiFailure(413, "board_too_large", "Board supports at most 10000 bookmarks");
        MongoBoardStore.Encode(updated); // Same BSON size guard for every store implementation.
        if (!await store.ReplaceAsync(revision, updated, ct)) throw ApiFailure.Conflict();
        return updated;
    }

    private static string Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 500)
            throw ApiFailure.Invalid(field, "Provide a nonblank value of at most 500 characters");
        return value.Trim();
    }

    public static string Url(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw ApiFailure.Invalid(field, "Provide an absolute HTTP(S) URL without credentials, at most 4096 characters");
        return value.Trim();
    }

    private static void Group(ChannelGroup group, string field)
    {
        if (!Enum.IsDefined(group)) throw ApiFailure.Invalid(field, "Unknown channel group");
    }

    public Task<BoardSnapshot> SaveVideo(Guid? id, SaveVideoRequest request, CancellationToken ct)
    {
        var title = Text(request.Title, "title");
        var url = Url(request.Url, "url");
        if (!Enum.IsDefined(request.ThumbnailMode)) throw ApiFailure.Invalid("thumbnailMode", "Unknown thumbnail mode");
        var manual = request.ManualThumbnailUrl is null ? null : Url(request.ManualThumbnailUrl, "manualThumbnailUrl");
        if (request.ThumbnailMode == ThumbnailMode.Manual && manual is null)
            throw ApiFailure.Invalid("manualThumbnailUrl", "Manual mode requires a thumbnail URL");
        return MutateAsync(request.ExpectedRevision, async board =>
        {
            var old = id is null ? null : board.Videos.FirstOrDefault(v => v.Id == id) ?? throw ApiFailure.Missing();
            var automatic = old?.Url == url ? old.AutomaticThumbnailUrl : null;
            if (request.ThumbnailMode == ThumbnailMode.Automatic)
            {
                var result = await metadata.LookupAsync(url, ct);
                automatic = result.ThumbnailUrl ?? automatic;
            }
            ct.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var video = new VideoBookmark(id ?? Guid.NewGuid(), title, url, request.ThumbnailMode,
                automatic, manual, old?.CreatedAt ?? now, now);
            return board with { Videos = new[] { video }.Concat(board.Videos.Where(v => v.Id != id)).ToArray() };
        }, ct);
    }

    public Task<BoardSnapshot> DeleteVideo(Guid id, long revision, CancellationToken ct) => Mutate(revision, board =>
    {
        if (!board.Videos.Any(v => v.Id == id)) throw ApiFailure.Missing();
        return board with { Videos = board.Videos.Where(v => v.Id != id).ToArray() };
    }, ct);

    public Task<BoardSnapshot> ReorderVideos(ReorderVideosRequest request, CancellationToken ct) => Mutate(request.ExpectedRevision, board =>
    {
        var ids = request.VideoIds;
        if (ids is null || ids.Length != board.Videos.Length || ids.Distinct().Count() != ids.Length
            || !ids.ToHashSet().SetEquals(board.Videos.Select(v => v.Id)))
            throw ApiFailure.Invalid("videoIds", "Supply every current video ID exactly once");
        var byId = board.Videos.ToDictionary(v => v.Id);
        return board with { Videos = ids.Select(id => byId[id]).ToArray() };
    }, ct);

    private static IEnumerable<ChannelBookmark> AllChannels(BoardSnapshot board) =>
        board.Channels.Frequent.Concat(board.Channels.Infrequent).Concat(board.Channels.Archived);

    private static BoardSnapshot WithChannels(BoardSnapshot board, IEnumerable<ChannelBookmark> channels)
    {
        var all = channels.ToArray();
        return board with { Channels = new(all.Where(c => c.Group == ChannelGroup.Frequent).ToArray(),
            all.Where(c => c.Group == ChannelGroup.Infrequent).ToArray(), all.Where(c => c.Group == ChannelGroup.Archived).ToArray()) };
    }

    public Task<BoardSnapshot> SaveChannel(Guid? id, SaveChannelRequest request, CancellationToken ct)
    {
        var name = Text(request.Name, "name");
        var url = Url(request.Url, "url");
        Group(request.Group, "group");
        return Mutate(request.ExpectedRevision, board =>
        {
            var all = AllChannels(board).ToList();
            var old = id is null ? null : all.Find(c => c.Id == id) ?? throw ApiFailure.Missing();
            var now = clock.GetUtcNow();
            var channel = new ChannelBookmark(id ?? Guid.NewGuid(), name, url, request.Group, old?.CreatedAt ?? now, now);
            if (old is not null && old.Group == channel.Group) all[all.IndexOf(old)] = channel;
            else { all.RemoveAll(c => c.Id == id); all.Insert(0, channel); }
            return WithChannels(board, all);
        }, ct);
    }

    public Task<BoardSnapshot> DeleteChannel(Guid id, long revision, CancellationToken ct) => Mutate(revision, board =>
    {
        var all = AllChannels(board).ToArray();
        if (!all.Any(c => c.Id == id)) throw ApiFailure.Missing();
        return WithChannels(board, all.Where(c => c.Id != id));
    }, ct);

    public Task<BoardSnapshot> MoveChannel(Guid id, MoveChannelRequest request, CancellationToken ct)
    {
        Group(request.DestinationGroup, "destinationGroup");
        return Mutate(request.ExpectedRevision, board =>
        {
            var all = AllChannels(board).ToList();
            var channel = all.Find(c => c.Id == id) ?? throw ApiFailure.Missing();
            all.Remove(channel);
            var destination = all.Where(c => c.Group == request.DestinationGroup).ToList();
            if (request.DestinationIndex < 0 || request.DestinationIndex > destination.Count)
                throw ApiFailure.Invalid("destinationIndex", "Index must be within the destination after removing the moved item");
            destination.Insert(request.DestinationIndex, channel with { Group = request.DestinationGroup, UpdatedAt = clock.GetUtcNow() });
            return WithChannels(board, all.Where(c => c.Group != request.DestinationGroup).Concat(destination));
        }, ct);
    }
}
