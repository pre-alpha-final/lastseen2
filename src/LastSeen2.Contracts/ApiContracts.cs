using System.Text.Json.Serialization;

namespace LastSeen2.Contracts;

public static class ApiRoutes
{
    public const string Prefix = "/api";
    public const string AccessKeyHeader = "X-Api-Key";
    public const string CheckKey = Prefix + "/auth/check";
    public const string Board = Prefix + "/board";
    public const string Videos = Prefix + "/videos";
    public const string Video = Videos + "/{id:guid}";
    public const string ReorderVideos = Videos + "/reorder";
    public const string Channels = Prefix + "/channels";
    public const string Channel = Channels + "/{id:guid}";
    public const string MoveChannel = Channel + "/move";
    public const string Metadata = Prefix + "/metadata";
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataStatus>))]
public enum MetadataStatus { Available, Unavailable, Unsupported }

public sealed record MetadataResult(
    MetadataStatus Status, string? Title, string? ThumbnailUrl, string? Message);

// RFC Problem Details shape, independent of ASP.NET Core. Errors is present for
// validation failures; Code is a stable machine-readable error identifier.
public sealed record ApiError(
    string Type, string Title, int Status, string Code,
    string? Detail = null, string? Instance = null,
    Dictionary<string, string[]>? Errors = null);
