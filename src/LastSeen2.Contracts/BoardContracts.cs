using System.Text.Json.Serialization;

namespace LastSeen2.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelGroup>))]
public enum ChannelGroup { Frequent, Infrequent, Archived }

[JsonConverter(typeof(JsonStringEnumConverter<ThumbnailMode>))]
public enum ThumbnailMode { Automatic, Manual }

public sealed record VideoBookmark(
    Guid Id, string Title, string Url, ThumbnailMode ThumbnailMode,
    string? AutomaticThumbnailUrl, string? ManualThumbnailUrl,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChannelBookmark(
    Guid Id, string Name, string Url, ChannelGroup Group,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// Each array is in display order. A channel belongs only to its matching group.
public sealed record ChannelGroups(
    ChannelBookmark[] Frequent, ChannelBookmark[] Infrequent, ChannelBookmark[] Archived);

public sealed record BoardSnapshot(long Revision, VideoBookmark[] Videos, ChannelGroups Channels);
