# LastSeen2

A single-user video and channel bookmark app. The implementation roadmap and sequential agent handoffs are in [docs/plan.md](docs/plan.md).

## Current status

Phase 1 supplies the application host, shared contracts, configuration scaffolding, and xUnit projects. The original Blazor starter UI is intentionally retained. Authentication, MongoDB operations, input validation, and metadata lookup are not implemented yet. Declared API endpoints return `501 application/problem+json`; they neither authenticate nor save data.

## Projects

- `src/LastSeen2`: existing .NET 10 Blazor WebAssembly client.
- `src/LastSeen2.Server`: ASP.NET Core host for the client and same-origin `/api` endpoints.
- `src/LastSeen2.Contracts`: dependency-free DTOs and route constants shared by client/server.
- `tests/LastSeen2.Contracts.Tests`: JSON compatibility tests.
- `tests/LastSeen2.Server.Tests`: in-process hosting, API boundary, and configuration tests.

The client references Contracts only. MongoDB and access-key configuration live only in Server; never move these settings into the client's `wwwroot` or Contracts.

## Build, run, and test

Install the .NET 10 SDK. From the repository root, run:

```powershell
dotnet restore src/LastSeen2.slnx
dotnet build src/LastSeen2.slnx --no-restore
dotnet test src/LastSeen2.slnx --no-build
dotnet run --project src/LastSeen2.Server --launch-profile http
```

Open <http://localhost:5033>. Run the server project, not the standalone client, to use the same-origin API. Stop any standalone client instance using that port first. Phase 1 runs without MongoDB or an access key; configuration validation and real dependencies arrive in Phase 2. No Docker, Node.js, or optional WASM workload is needed for this phase.

Publish and run the combined application:

```powershell
dotnet publish src/LastSeen2.Server -c Release -o artifacts/publish
Push-Location artifacts/publish
dotnet LastSeen2.Server.dll --urls http://localhost:5033
Pop-Location
```

The `dotnet` command runs until stopped; execute `Pop-Location` afterward. Publishing the server includes the client's assets. The client uses stable framework filenames (`WasmFingerprintAssets=false`, `BlazorFingerprintBlazorJs=false`) so the hosted SPA fallback works in both development and published output; the standalone HTML fingerprint placeholders have been removed. Use HTTPS for production when key authentication is implemented. The host follows the [ASP.NET Core Blazor WebAssembly hosting model](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/?view=aspnetcore-10.0).

## Server configuration

Server `appsettings.json` contains empty credential placeholders and the default collection name. Standard ASP.NET Core configuration precedence applies; environment variables override JSON settings. Required-value startup validation belongs to Phase 2.

| JSON setting | Environment variable | Default |
| --- | --- | --- |
| `ConnectionStrings:LastSeen2` | `ConnectionStrings__LastSeen2` | Empty placeholder |
| `MongoDb:Collection` | `MongoDb__Collection` | `lastseen2` |
| `Authentication:AccessKey` | `Authentication__AccessKey` | Empty placeholder |

Include the database name in the connection string path (for example, `mongodb://localhost:27017/my_database`). There is no separate database setting; Phase 2 will read it from the MongoDB connection string and reject a missing database name. `authSource` controls authentication and does not replace the data database name.

Set local/prod values through the environment rather than committing credentials. Server option types are `MongoDbOptions` and `AccessKeyOptions`. Phase 1 binds these settings but does not connect to MongoDB or accept a key.

## API contract for subsequent phases

JSON uses camelCase property names, string enum names (`Frequent`, `Infrequent`, `Archived`; `Automatic`, `Manual`), GUID identifiers, and UTC `DateTimeOffset` timestamps. Once Phase 2 is implemented, all routes below require `X-Api-Key`; key checking uses that same header and has no body. No session token is issued.

| Method | Route | Request | Intended success response |
| --- | --- | --- | --- |
| POST | `/api/auth/check` | Header only | 204 |
| GET | `/api/board` | None | 200 `BoardSnapshot` |
| POST | `/api/videos` | `SaveVideoRequest` | 200 `BoardSnapshot` |
| PUT | `/api/videos/{id}` | `SaveVideoRequest` | 200 `BoardSnapshot` |
| DELETE | `/api/videos/{id}?expectedRevision={revision}` | Revision query | 200 `BoardSnapshot` |
| PUT | `/api/videos/reorder` | `ReorderVideosRequest` | 200 `BoardSnapshot` |
| POST | `/api/channels` | `SaveChannelRequest` | 200 `BoardSnapshot` |
| PUT | `/api/channels/{id}` | `SaveChannelRequest` | 200 `BoardSnapshot` |
| DELETE | `/api/channels/{id}?expectedRevision={revision}` | Revision query | 200 `BoardSnapshot` |
| PUT | `/api/channels/{id}/move` | `MoveChannelRequest` | 200 `BoardSnapshot` |
| POST | `/api/metadata` | `MetadataRequest` | 200 `MetadataResult` |

These are contracts, not completed handlers: every declared route currently returns 501. Unknown `/api` routes return a 404 problem response, never the frontend HTML. Other extensionless routes fall back to the client shell.

### Board and mutation semantics

- `BoardSnapshot` contains `revision`, ordered `videos`, and `channels` with ordered `frequent`, `infrequent`, and `archived` arrays. A channel's group must match its containing array.
- Initial revision is zero. Every successful mutation checks `expectedRevision`, atomically increments it, and returns the authoritative snapshot. Reject stale writes with 409; never silently overwrite them.
- IDs and timestamps are server-owned. Save payloads contain only editable data and the expected revision. Delete requests bind the revision query parameter to `DeleteBookmarkRequest`.
- Video creation/update moves the video to the top. Saving a channel in its existing group preserves order; changing group places it at the destination's top.
- `ReorderVideosRequest.videoIds` must contain all current video IDs exactly once.
- `MoveChannelRequest` identifies the channel in the route and sends `destinationGroup` and a zero-based `destinationIndex`, measured after removing the moved channel. This handles same-group reordering and cross-group moves; index zero works for empty groups.
- `SaveVideoRequest` explicitly includes `thumbnailMode` and nullable `manualThumbnailUrl`. Automatic thumbnail values come from the server. A null manual URL explicitly clears the override; clients resend an existing manual URL to retain it across edits. Manual mode requires a valid image URL. An automatic-mode save may retain an inactive manual override for later reuse.
- Metadata returns `Available`, `Unavailable`, or `Unsupported` with optional title, thumbnail URL, and message. Provider lookup failure is a nonfatal result, not a failed bookmark mutation.

### Validation and errors

Use RFC Problem Details (`application/problem+json`), readable as the shared `ApiError` DTO. `code` is a stable machine identifier; validation errors also contain an `errors` map from JSON field names to message arrays. Never include credentials, connection strings, or raw exception details.

Phase 2 must enforce required nonblank titles/names, absolute HTTP(S) URLs, known enum values, nonnegative revisions, valid IDs/group membership, complete reorder lists, and valid move indices. Rejected mutations leave persisted data unchanged. Required configuration validation and request validation are intentionally deferred to that phase.

| Status | Code | Meaning |
| --- | --- | --- |
| 400 | `validation_failed` | Invalid payload or field values |
| 401 | `unauthorized` | Missing or incorrect access key |
| 404 | `not_found` | Missing item or unknown API route |
| 409 | `revision_conflict` | Stale board revision |
| 413 | `board_too_large` | Board would exceed the supported storage size |
| 429 | `rate_limited` | Authentication attempt limit reached |
| 500 | `internal_error` | Unexpected server failure |
| 503 | `storage_unavailable` | Database unavailable |
| 501 | `not_implemented` | Phase 1 route scaffold |

Phase 1 implements the 404/501 API responses; later handlers must implement the remaining cases consistently. Framework-generated errors use ASP.NET Core Problem Details.

## Agent workflow

Follow repository instructions and `docs/plan.md`. Preserve existing CRLF/LF endings, check `git diff --check`, and use `feature/<descriptive-name>` for new branches. Complete the phase handoff before the next agent begins.
