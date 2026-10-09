# LastSeen2 Agent Instructions

## Start here

- Read `docs/plan.md`, including the completion record and phase handoffs, before implementation. It is the implementation roadmap; `README.MD` describes current setup and API contracts.
- Read `docs/initial_idea.txt` and inspect `docs/initial_idea.jpg` for product intent and visual direction. Later user decisions recorded here and in the plan supersede the original idea where they differ.
- Inspect the current code and `git status` before editing. Preserve existing user changes.
- Work on the phase requested by the user. Phases run sequentially, each in a separate agent run; finish the phase's acceptance checks and handoff before the next phase. Do not automatically implement later phases.
- Update the plan's phase status and handoff with delivered behavior, contract/config changes, commands and results, limitations, and next-agent instructions. Keep README instructions accurate. Update this file when its architectural or operational guidance changes.

## Branches

Do not use Codex-specific names when creating branches. Use `feature/feature-name`.

## Line endings

Preserve each file's existing line endings exactly.

- Before editing a text file, detect whether it uses CRLF or LF.
- When replacing or inserting lines, use the same line ending style as the file.
- Do not normalize line endings unless explicitly asked.
- Avoid edits that create mixed CRLF/LF files.
- After editing, run `git diff --check` and ensure the diff does not contain line-ending-only changes.
- Existing source/docs currently use CRLF. Use CRLF for new text files unless the surrounding directory establishes another convention. Check untracked files too; plain `git diff --check` does not cover them.

## Current implementation baseline

At the end of Phase 3 on 2026-10-09:

- Plan creation and Phases 1–3 are complete. Phase 4 (dark interface and unlocking flow) is next; Phases 4–7 remain unimplemented.
- The original Blazor starter UI remains. No bookmark management interface exists yet.
- `BookmarkApi` implements protected authentication checks, board reads, CRUD, and ordering. Metadata is protected and returns credential-free YouTube/Twitch suggestions with nonfatal fallback.
- Unknown `/api` routes return 404 Problem Details, including paths containing dots. Keep them out of the SPA HTML fallback.
- Startup requires a valid `ConnectionStrings:LastSeen2` URI with a database name, a valid collection, and `Authentication:AccessKey`. Board operations connect lazily; outages return safe 503 responses.
- The completed Phase 3 suite has 102 passing tests (3 contract, 99 server), including a real isolated MongoDB integration test. Without `LASTSEEN2_TEST_MONGODB`, that one test is explicitly skipped. Release publish, production HTTP assets, process restart persistence, and real database-outage checks passed. Rerun relevant checks after changes.

## Project structure and local startup

- `src/LastSeen2.slnx`: solution, including tests located outside `src`.
- `src/LastSeen2`: .NET 10 Blazor WebAssembly frontend.
- `src/LastSeen2.Server`: ASP.NET Core server and production host.
- `src/LastSeen2.Contracts`: dependency-free shared DTOs and API route constants.
- `tests/LastSeen2.Contracts.Tests`: JSON contract compatibility tests.
- `tests/LastSeen2.Server.Tests`: xUnit hosting/API/configuration tests using `WebApplicationFactory`.

Select **LastSeen2.Server** as the Visual Studio startup project. It runs both frontend and API; multiple startup projects are unnecessary. The HTTP launch profile uses `http://localhost:5033`. Do not start the standalone client on the same port at the same time.

The initial environment was Windows/PowerShell with .NET SDK 10.0.401; projects target .NET 10. Use paths relative to the repository rather than hardcoding the original checkout location. Phase 1 needs no Node.js, Docker, or optional WASM workload.

From the repository root:

```powershell
dotnet restore src/LastSeen2.slnx
dotnet build src/LastSeen2.slnx --no-restore
dotnet test src/LastSeen2.slnx --no-build
dotnet run --project src/LastSeen2.Server --launch-profile http
```

For testing while rebuilding changes, use `dotnet test src/LastSeen2.slnx --no-restore` after restoring any changed dependencies.

```powershell
dotnet publish src/LastSeen2.Server -c Release -o artifacts/publish
Push-Location artifacts/publish
dotnet LastSeen2.Server.dll --urls http://localhost:5033
# Stop the running application before executing the next command.
Pop-Location
```

Publish the server, not just the client. `artifacts/` is ignored and suitable for temporary probes/build output; do not depend on its contents being present in another checkout.

## Hosting details to preserve

- The server uses `UseBlazorFrameworkFiles`, `UseStaticFiles`, and `MapFallbackToFile("index.html")`.
- Client `WasmFingerprintAssets` and `BlazorFingerprintBlazorJs` are false. `index.html` uses `_framework/blazor.webassembly.js`, without standalone import-map/preload/fingerprint placeholders.
- This configuration fixed a real development-versus-publish failure: the published host initially served unresolved HTML fingerprint placeholders. Do not restore those placeholders or switch static asset strategies without verifying both development and published output.
- Hosting verification must include the root, a client fallback route, bootstrap/runtime JavaScript, native WASM, CSS, unknown API routes, and absence of publicly served server configuration. An HTML 200 alone does not prove the client can boot.
- Server tests use `WebApplicationFactory<global::Program>` to avoid resolving the client's `LastSeen2.Program`. They explicitly set the server content root because the solution and tests have different parent directories.
- Replace scaffold-specific 501 expectations when real handlers arrive; keep contract, hosting, and API-boundary coverage.

## MongoDB and secrets — final user decisions

| Setting | Environment variable | Meaning |
| --- | --- | --- |
| `ConnectionStrings:LastSeen2` | `ConnectionStrings__LastSeen2` | MongoDB URI, including the database name |
| `MongoDb:Collection` | `MongoDb__Collection` | Collection; default is `lastseen2` |
| `Authentication:AccessKey` | `Authentication__AccessKey` | Shared application access key |

- The connection string name must remain LastSeen2-specific because the user sets environment variables locally alongside other applications.
- There is **no separate database setting**. Do not reintroduce `MongoDb:Database`, `MongoDb__Database`, or a hardcoded database name. The server obtains the database name from the URI using the MongoDB driver and rejects a missing name.
- `authSource` is the authentication database, not a substitute for the URI's data database name.
- The collection is **`lastseen2`**, not the earlier planned `content`.
- `Program.cs` maps `GetConnectionString("LastSeen2")` into `MongoDbOptions.ConnectionString`. The old `MongoDb__ConnectionString` name is obsolete.
- Keep credentials in the environment, with empty placeholders in committed appsettings. Never put them in the client, shared contracts, documentation, command-line arguments, logs, or test fixtures. Do not reproduce credentials from prior conversation history.
- On 2026-10-09, `ConnectionStrings__LastSeen2` existed in the local process and Windows user environment. A read-only probe successfully pinged the remote database and read from collection `lastseen2`; no document contents were displayed and no writes occurred. This did not test write permissions, database creation rights, or application persistence.
- The server now references MongoDB.Driver 3.12.0. The original read-only probe remains an ignored temporary project and is not a test prerequisite.
- Windows environment changes may require restarting Visual Studio or another already-running host. Check whether settings exist without printing their values. Recheck current availability rather than assuming future agent environments inherit them.
- Use an explicitly isolated test database/collection for write-based integration tests. Never drop, clear, or seed the user's configured application collection as test setup/cleanup. The read-only connectivity verification is not authorization for destructive database operations.

## Product and architecture decisions

- Preserve Blazor WebAssembly plus an ASP.NET Core same-origin `/api` backend. Keep server dependencies and secrets out of Contracts and the client.
- Single-user access uses `X-Api-Key` on app API requests. The frontend will store the entered key in `sessionStorage` and offer Lock. No provider login or multi-user accounts.
- Use one MongoDB collection containing a single versioned board document. Store ordered video and channel arrays; use atomic revision-checked mutations to prevent silent lost updates. Initial revision is zero; stale writes return 409.
- Shared contracts use camelCase JSON, string enum names, GUID IDs, and UTC timestamps. Successful mutations return the full authoritative board snapshot. See README and Contracts for routes, payloads, and Problem Details conventions.
- Video additions and successful edits move to the top. Channel additions go to the top of the selected group; edits in the same group preserve order; group changes go to the destination's top.
- Support video reordering and channel reordering/moves between Frequent, Infrequent, and Archived. Channel move indices are measured after removing the moved item. Video reorder payloads contain all current IDs exactly once.
- Metadata must be credential-free: YouTube oEmbed and best-effort public Twitch metadata. Manual thumbnails are image URLs, not uploads. Provider lookup failures must not block an otherwise valid bookmark save. Preserve manual overrides until explicitly cleared. Keep server fetches restricted and protected against private-network access.
- Dark-only Bootswatch Sketchy UI follows the reference image: Saved Videos at roughly two-thirds width, Channels at one-third, stacked on mobile. Default new channels to Frequent. Include touch/keyboard alternatives to drag-and-drop.
- Duplicate URLs are allowed; external links open in a new tab. Search, tags, watched-state tracking, uploads, embedded playback, import/export, provider credentials, and background synchronization are outside the current plan.

## Phase 4 entry point

Follow Phase 4 in `docs/plan.md` to build the dark interface and unlocking flow. Read the Phase 3 handoff and README metadata section first. The metadata result can be `Available` with only one suggestion. Use `/images/video-placeholder.svg` for absent or broken previews; render provider titles as text and never send the app key to thumbnail hosts. Automatic saves already refresh thumbnails with nonfatal fallback, and manual mode skips lookup. Preserve explicit-null manual clearing, ordering, and atomic revision checks. Never fetch arbitrary bookmark or manual-thumbnail URLs from the server.

`ProviderHttpClient` uses a dedicated client with no proxy, cookies, credentials, or automatic redirects. Keep its host restrictions, checked-IP socket connections, time/size limits, and fixture-based security coverage. Twitch public metadata may be unavailable; generic shell titles/logo are intentionally rejected.

## Backend implementation notes

- `ApiBoundaryMiddleware` protects API handlers before body binding and emits credential-free Problem Details. `AccessKeyGuard` uses a process-wide failed-attempt window: after 30 failures, all protected requests receive 429 until the minute resets; successful requests do not consume the budget. Static files remain public. The limiter is in memory and per process, not distributed.
- `BoardService` validates and builds immutable snapshots; `MongoBoardStore` stores a schema-version-1 BSON document with `_id: "board"`, Int64 revision, ordered arrays, and JSON-compatible string GUIDs/UTC timestamps. Do not change this format without a migration plan.
- First read initializes revision zero safely under concurrency. Replacements match `_id` plus expected revision, use primary reads and acknowledged majority writes, and do not upsert mutations. Any uncertain storage result requires a reload before retrying.
- Limits: 500-character titles/names, 4096-character credential-free HTTP(S) URLs, 10000 bookmarks, 12 MiB BSON board, 1 MiB Kestrel request bodies. Oversized boards return 413 without saving; transport-level oversized bodies may be rejected before a structured response.
- Required JSON constructor fields must be present. A nullable `manualThumbnailUrl` still must be supplied; null explicitly clears it. Full contract shape remains unchanged.
- Set `LASTSEEN2_TEST_MONGODB` only to a test-capable server/identity. Integration tests generate a unique `lastseen2_test_<guid>` database, preserve the URI authentication source, use `board_integration`, and drop only that generated database. Do not weaken isolation to accommodate production permissions.
- The current remote credentials could not access an isolated database. Phase 2 integration verification used a temporary localhost-only MongoDB 8.2.0 process instead. Do not assume the remote application's write permissions were verified.
