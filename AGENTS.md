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

At the end of the initial session on 2026-10-09:

- Plan creation and Phase 1 are complete. Phase 2 (authentication, persistence, and bookmark API) is next; Phases 2–7 remain unimplemented.
- The original Blazor starter UI remains. No bookmark management interface exists yet.
- Server hosts the client and declares the planned API routes. `FoundationApi` deliberately returns 501 Problem Details for every declared route; it does not authenticate, persist, or fetch metadata.
- Unknown `/api` routes return 404 Problem Details, including paths containing dots. Keep them out of the SPA HTML fallback.
- Configuration binds successfully, but required-value validation and MongoDB application integration belong to Phase 2. A successful connectivity probe does not mean the app's persistence is implemented.
- The completed Phase 1 suite has 25 tests (3 contract, 22 server). Server tests passed again after the configuration changes. Release publish and production HTTP asset checks also passed. These are historical results; rerun relevant checks after changes.

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
- There is **no separate database setting**. Do not reintroduce `MongoDb:Database`, `MongoDb__Database`, or a hardcoded database name. Phase 2 must obtain the database name from the URI using the MongoDB driver and reject a missing name.
- `authSource` is the authentication database, not a substitute for the URI's data database name.
- The collection is **`lastseen2`**, not the earlier planned `content`.
- `Program.cs` maps `GetConnectionString("LastSeen2")` into `MongoDbOptions.ConnectionString`. The old `MongoDb__ConnectionString` name is obsolete.
- Keep credentials in the environment, with empty placeholders in committed appsettings. Never put them in the client, shared contracts, documentation, command-line arguments, logs, or test fixtures. Do not reproduce credentials from prior conversation history.
- On 2026-10-09, `ConnectionStrings__LastSeen2` existed in the local process and Windows user environment. A read-only probe successfully pinged the remote database and read from collection `lastseen2`; no document contents were displayed and no writes occurred. This did not test write permissions, database creation rights, or application persistence.
- The probe used MongoDB.Driver 3.12.0 in an ignored temporary project. The application itself has no MongoDB driver reference yet; add the required dependency in Phase 2. Do not treat the temporary probe as app code or a required test prerequisite.
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

## Phase 2 entry point

Follow the full Phase 2 acceptance criteria in `docs/plan.md`. Replace the API scaffolds with shared-key protection, required configuration validation, request validation, and atomic MongoDB persistence. Protect metadata routes too, even while their implementation remains deferred to Phase 3. Preserve agreed ordering, revision conflicts, board-size safeguards, and safe error responses. Update tests and the phase handoff with actual evidence, and clearly record anything not verified.
