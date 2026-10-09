using System.Text.Json;
using LastSeen2.Contracts;

namespace LastSeen2.Contracts.Tests;

public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BoardRoundTripPreservesOrderGroupsRevisionAndThumbnailOverrides()
    {
        var timestamp = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        var first = new VideoBookmark(Guid.NewGuid(), "First", "https://example.com/1",
            ThumbnailMode.Manual, "https://example.com/auto.jpg", "https://example.com/manual.jpg",
            timestamp, timestamp);
        var second = first with { Id = Guid.NewGuid(), Title = "Second" };
        var channel = new ChannelBookmark(Guid.NewGuid(), "Channel", "https://example.com/channel",
            ChannelGroup.Archived, timestamp, timestamp);
        var board = new BoardSnapshot(42, [first, second], new([], [], [channel]));

        var json = JsonSerializer.Serialize(board, JsonOptions);
        var restored = JsonSerializer.Deserialize<BoardSnapshot>(json, JsonOptions)!;

        Assert.Equal(42, restored.Revision);
        Assert.Equal(new[] { first, second }, restored.Videos);
        Assert.Equal(channel, Assert.Single(restored.Channels.Archived));
        Assert.Empty(restored.Channels.Frequent);
        Assert.Contains("\"thumbnailMode\":\"Manual\"", json);
        Assert.Contains("\"group\":\"Archived\"", json);
    }

    [Fact]
    public void MutationRoundTripPreservesRevisionAndMovePosition()
    {
        var request = new MoveChannelRequest(9, ChannelGroup.Infrequent, 0);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        Assert.Equal(request, JsonSerializer.Deserialize<MoveChannelRequest>(json, JsonOptions));
    }

    [Fact]
    public void ProblemDetailsCanBeReadWithoutAspNetDependency()
    {
        const string json = """
            {"type":"about:blank","title":"Validation failed","status":400,
             "code":"validation_failed","errors":{"url":["Use an HTTP(S) URL."]}}
            """;
        var error = JsonSerializer.Deserialize<ApiError>(json, JsonOptions)!;
        Assert.Equal(400, error.Status);
        Assert.Equal("validation_failed", error.Code);
        Assert.Equal("Use an HTTP(S) URL.", Assert.Single(error.Errors!["url"]));
    }
}
