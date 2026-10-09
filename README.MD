# LastSeen2

A single-user video and channel bookmark app. The implementation roadmap and sequential agent handoffs are in [docs/plan.md](docs/plan.md).

## Current status

Phases 1–3 implement the protected MongoDB bookmark API and credential-free provider metadata. Automatic video saves refresh thumbnails without depending on provider availability. The original Blazor starter UI is retained until Phase 4.

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

Open <http://localhost:5033>. Run the server project, not the standalone client, to use the same-origin API. Stop any standalone client instance using that port first. Set the required MongoDB connection string and access key before starting; missing or invalid settings stop startup. MongoDB is needed for board operations. No Docker, Node.js, or optional WASM workload is required.

Publish and run the combined application:

```powershell
dotnet publish src/LastSeen2.Server -c Release -o artifacts/publish
Push-Location artifacts/publish
dotnet LastSeen2.Server.dll --urls http://localhost:5033
Pop-Location
```

The `dotnet` command runs until stopped; execute `Pop-Location` afterward. Publishing the server includes the client's assets. The client uses stable framework filenames (`WasmFingerprintAssets=false`, `BlazorFingerprintBlazorJs=false`) so the hosted SPA fallback works in both development and published output; the standalone HTML fingerprint placeholders have been removed. Use HTTPS for production to protect the shared key in transit. The host follows the [ASP.NET Core Blazor WebAssembly hosting model](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/?view=aspnetcore-10.0).

## Server configuration

Server `appsettings.json` contains empty credential placeholders and the default collection name. Standard ASP.NET Core configuration precedence applies; environment variables override JSON settings. Required settings are validated at startup with credential-free error messages.

| JSON setting | Environment variable | Default |
| --- | --- | --- |
| `ConnectionStrings:LastSeen2` | `ConnectionStrings__LastSeen2` | Empty placeholder |
| `MongoDb:Collection` | `MongoDb__Collection` | `lastseen2` |
| `Authentication:AccessKey` | `Authentication__AccessKey` | Empty placeholder |

Include the database name in the connection string path (for example, `mongodb://localhost:27017/my_database`). There is no separate database setting; the server reads it using the MongoDB driver and rejects a missing database name. `authSource` controls authentication and does not replace the data database name.

Set local/prod values through the environment rather than committing credentials. Server option types are `MongoDbOptions` and `AccessKeyOptions`. A database connection is established lazily on the first authorized board operation. A database outage returns 503; it does not prevent static assets or key checks from working.

## API contract

JSON uses camelCase property names, string enum names (`Frequent`, `Infrequent`, `Archived`; `Automatic`, `Manual`), GUID identifiers, and UTC `DateTimeOffset` timestamps. All routes below require `X-Api-Key`; key checking uses that same header and has no body. No session token is issued.

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

All bookmark handlers and metadata lookup are implemented. Metadata returns a nonfatal lookup result after authentication and URL validation. Unknown `/api` routes return a 404 problem response, never the frontend HTML; unknown paths within video/channel resources require a valid key first. Other extensionless routes fall back to the client shell.

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

The server enforces nonblank titles/names (up to 500 characters), absolute HTTP(S) URLs without embedded credentials (up to 4096 characters), known enum values, nonnegative incrementable revisions, item existence, complete reorder lists, and valid move indices. JSON constructor fields must be supplied, including nullable `manualThumbnailUrl`; malformed or missing fields return 400. Rejected mutations leave persisted bookmarks and revision unchanged. Kestrel limits request bodies to 1 MiB. Boards are limited to 10000 total bookmarks and 12 MiB of BSON, below MongoDB’s document limit.

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

Authentication uses a constant-time comparison of SHA-256 key digests. The first 30 invalid-key requests per process-wide one-minute window return 401; all subsequent protected requests return 429 with `Retry-After` until the window resets, before comparing keys. Successful authentication does not consume the budget. Static files remain public. This limiter resets on server restart and is not shared across multiple instances. API responses set `Cache-Control: no-store`. API error logs omit exception text and credentials.

## Credential-free metadata

`POST /api/metadata` accepts `{ "url": "https://youtu.be/dQw4w9WgXcQ" }` and returns `status`, nullable `title`, nullable `thumbnailUrl`, and nullable `message`. `Available` means at least one usable suggestion; it does not guarantee a thumbnail. `Unavailable` covers provider errors, missing metadata, unsafe redirects/DNS, malformed responses, and timeouts. `Unsupported` means the link is outside supported forms; no request is sent. Invalid URL syntax still returns 400. No provider credentials or configuration are required.

Supported links:

- YouTube `youtube.com/watch?v=<11-character-id>` (also `www` and `m`), `youtu.be/<id>` (also `www`), and YouTube `/shorts/<id>`. HTTP links are accepted and fetched through canonical HTTPS oEmbed; tracking/time parameters are discarded.
- Twitch `/videos/<numeric-id>` and `/<channel>/clip/<slug>` on `twitch.tv`, `www.twitch.tv`, or `m.twitch.tv`; `clips.twitch.tv/<slug>`. Public Open Graph/Twitter meta tags are best effort. Channel homepages are unsupported. Generic Twitch page titles and logo metadata are discarded.

Automatic-mode video adds and edits attempt lookup after validation, revision, and item checks. They keep the user-supplied title. A usable automatic thumbnail replaces the old one; failure or a missing thumbnail retains the old value only when the trimmed bookmark URL is exactly unchanged. Changing the URL clears stale automatic metadata even in manual mode. Manual mode does not fetch; `manualThumbnailUrl` is retained by resubmitting it and explicitly cleared with null. Final persistence still checks the revision, including when another writer saves during lookup. Channels never trigger lookup.

The dedicated client only fetches canonical provider URLs and up to three validated HTTPS redirects. It disables proxies, cookies, automatic redirects, and credentials. Each new connection resolves the provider hostname, rejects any private/local/special-use answer, and connects directly to a checked IP while preserving TLS hostname validation. Requests have a five-second total lookup deadline, three-second connection timeout, 32 KiB header limit, and 512 KiB decompressed body limit. Only JSON oEmbed and HTML meta suggestions are consumed; embed HTML is never returned. Thumbnail suggestions are limited to HTTPS provider image domains and are never fetched by the server. Arbitrary bookmarks and manual thumbnail URLs are never fetched.

The local fallback asset is `/images/video-placeholder.svg`. Phase 4/5 UI should use it when the selected thumbnail is missing or fails to load, render titles as text, and never send the app key to image hosts. No bookmark UI has been implemented yet.

Live checks on 2026-10-09: YouTube `dQw4w9WgXcQ` returned a title and thumbnail. Twitch VOD `2191576363` and clip `AwkwardHelplessSalamanderSwiftRage` returned generic shell titles/logo, so lookup correctly reported `Unavailable`. Availability varies with removal, region, bot restrictions, and provider markup; no authenticated Twitch API or JavaScript execution fallback is attempted. These observations are separate from deterministic fixture tests.

## Persistence and integration tests

MongoDB.Driver 3.12.0 is a server-only dependency. The configured collection contains a document with `_id: "board"`, `schemaVersion: 1`, an Int64 `revision`, and the ordered `videos` and grouped `channels` arrays. IDs and UTC timestamps are persisted as JSON-compatible strings. The first board operation initializes an empty revision-zero document; concurrent initialization tolerates duplicate-key races. Other documents in the collection are untouched.

Each save reads a snapshot, validates and builds the next snapshot, then replaces the board only when `_id` and the expected revision still match. A losing concurrent writer receives 409. Reads use the primary; writes require majority acknowledgement even if the URI specifies `w=0`. Database operations have bounded timeouts. A timeout may mean the write outcome is unknown: reload the board before retrying. The API never returns a successful snapshot after a failed/unacknowledged write.

The normal test run needs no database and explicitly skips the real-MongoDB test when `LASTSEEN2_TEST_MONGODB` is absent. To run all tests, provide a MongoDB server on which the test identity may create and drop databases:

```powershell
# A local, disposable test server; use your actual test server port.
$env:LASTSEEN2_TEST_MONGODB = "mongodb://127.0.0.1:27018/test_source"
dotnet test src/LastSeen2.slnx --no-restore
Remove-Item Env:LASTSEEN2_TEST_MONGODB
```

The integration test replaces the URI's data database with a unique `lastseen2_test_<guid>` name and uses `board_integration`, preserving the original authentication source. It never seeds, clears, or drops the application's database/collection. Cleanup only drops that generated test database. It exercises concurrent initialization, CRUD and ordering, rejected mutations, two competing web hosts, and persistence after disposing and recreating the hosts. Restricted production credentials generally cannot create these test databases; use a separate test server/identity.

## API examples

With the server running and the environment configured, these PowerShell examples use the key without putting it on the command line or in a URL:

```powershell
$headers = @{ 'X-Api-Key' = $env:Authentication__AccessKey }
$base = 'http://localhost:5033/api'
Invoke-WebRequest -Method Post -Uri "$base/auth/check" -Headers $headers
$board = Invoke-RestMethod -Uri "$base/board" -Headers $headers
$body = @{
    expectedRevision = $board.revision
    title = 'Example video'
    url = 'https://example.com/video'
    thumbnailMode = 'Automatic'
    manualThumbnailUrl = $null
} | ConvertTo-Json
$board = Invoke-RestMethod -Method Post -Uri "$base/videos" -Headers $headers -ContentType 'application/json' -Body $body
$id = $board.videos[0].id
$board = Invoke-RestMethod -Method Delete -Uri "$base/videos/${id}?expectedRevision=$($board.revision)" -Headers $headers
```

Use the revision in each returned snapshot for the next mutation. A 409 means reload and review the latest board before resubmitting. Automatic saves attempt provider thumbnail refresh; manual mode skips lookup.

## Agent workflow

Follow repository instructions and `docs/plan.md`. Preserve existing CRLF/LF endings, check `git diff --check`, and use `feature/<descriptive-name>` for new branches. Complete the phase handoff before the next agent begins.
