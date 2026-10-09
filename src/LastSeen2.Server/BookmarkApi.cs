using LastSeen2.Contracts;

namespace LastSeen2.Server;

public static class BookmarkApi
{
    public static void MapBookmarkApi(this WebApplication app)
    {
        var api = app.MapGroup(ApiRoutes.Prefix).WithMetadata(new ProtectedApi());
        api.MapPost("/auth/check", () => Results.NoContent());
        api.MapGet("/board", (BoardService service, CancellationToken ct) => service.ReadAsync(ct));
        api.MapPost("/videos", (SaveVideoRequest request, BoardService service, CancellationToken ct) => service.SaveVideo(null, request, ct));
        api.MapPut("/videos/{id:guid}", (Guid id, SaveVideoRequest request, BoardService service, CancellationToken ct) => service.SaveVideo(id, request, ct));
        api.MapDelete("/videos/{id:guid}", (Guid id, long expectedRevision, BoardService service, CancellationToken ct) => service.DeleteVideo(id, expectedRevision, ct));
        api.MapPut("/videos/reorder", (ReorderVideosRequest request, BoardService service, CancellationToken ct) => service.ReorderVideos(request, ct));
        api.MapPost("/channels", (SaveChannelRequest request, BoardService service, CancellationToken ct) => service.SaveChannel(null, request, ct));
        api.MapPut("/channels/{id:guid}", (Guid id, SaveChannelRequest request, BoardService service, CancellationToken ct) => service.SaveChannel(id, request, ct));
        api.MapDelete("/channels/{id:guid}", (Guid id, long expectedRevision, BoardService service, CancellationToken ct) => service.DeleteChannel(id, expectedRevision, ct));
        api.MapPut("/channels/{id:guid}/move", (Guid id, MoveChannelRequest request, BoardService service, CancellationToken ct) => service.MoveChannel(id, request, ct));
        api.MapPost("/metadata", (MetadataRequest request) =>
        {
            BoardService.Url(request.Url, "url");
            return new ApiFailure(501, "not_implemented", "Metadata lookup is deferred to Phase 3").Result;
        });
    }
}
