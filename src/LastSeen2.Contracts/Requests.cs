namespace LastSeen2.Contracts;

// Every mutation checks ExpectedRevision and returns the complete BoardSnapshot.
// Create and update share payloads; the route supplies the ID for updates.
public sealed record SaveVideoRequest(
    long ExpectedRevision, string Title, string Url,
    ThumbnailMode ThumbnailMode, string? ManualThumbnailUrl);

public sealed record SaveChannelRequest(
    long ExpectedRevision, string Name, string Url, ChannelGroup Group);

// DELETE sends expectedRevision as a query parameter, not a request body.
public sealed record DeleteBookmarkRequest(long ExpectedRevision);

// Contains every current video ID exactly once, in the desired order.
public sealed record ReorderVideosRequest(long ExpectedRevision, Guid[] VideoIds);

// DestinationIndex is zero-based in the destination AFTER removing the moved item.
// This handles both same-group reordering and cross-group moves atomically.
public sealed record MoveChannelRequest(
    long ExpectedRevision, ChannelGroup DestinationGroup, int DestinationIndex);

public sealed record MetadataRequest(string Url);
