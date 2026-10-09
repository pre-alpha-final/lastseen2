using LastSeen2.Contracts;

namespace LastSeen2.Server;

public static class FoundationApi
{
    public static void MapFoundationApi(this WebApplication app)
    {
        // Deliberately no fake authentication or persistence. Phase 2 replaces these
        // endpoints with protected handlers; Phase 3 implements metadata lookup.
        app.MapPost(ApiRoutes.CheckKey, NotImplemented);
        app.MapGet(ApiRoutes.Board, NotImplemented);
        app.MapPost(ApiRoutes.Videos, NotImplemented);
        app.MapPut(ApiRoutes.Video, NotImplemented);
        app.MapDelete(ApiRoutes.Video, NotImplemented);
        app.MapPut(ApiRoutes.ReorderVideos, NotImplemented);
        app.MapPost(ApiRoutes.Channels, NotImplemented);
        app.MapPut(ApiRoutes.Channel, NotImplemented);
        app.MapDelete(ApiRoutes.Channel, NotImplemented);
        app.MapPut(ApiRoutes.MoveChannel, NotImplemented);
        app.MapPost(ApiRoutes.Metadata, NotImplemented);
    }

    private static IResult NotImplemented() => Results.Problem(
        statusCode: StatusCodes.Status501NotImplemented,
        title: "This capability is not implemented yet",
        detail: "Phase 1 provides the application host and API contracts only.",
        extensions: new Dictionary<string, object?> { ["code"] = "not_implemented" });
}
