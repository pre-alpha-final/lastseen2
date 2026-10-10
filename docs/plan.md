# LastSeen2 — Sequential Implementation Plan

## Overview

This document is the implementation plan for LastSeen2. Each phase is run by a separate agent, in order.

Build a single-user bookmark app from `docs/initial_idea.txt` and `docs/initial_idea.jpg`. Keep the existing .NET 10 Blazor WebAssembly frontend and add an ASP.NET Core backend that serves both the API and frontend in production.

The finished app provides:

- Saved Videos occupying approximately two-thirds of the desktop layout.
- Channels occupying the remaining third, grouped into Frequent, Infrequent, and Archived.
- Add, edit, delete, and persistent drag-and-drop ordering.
- Automatic thumbnail lookup where available, with manual image URLs.
- A dark-only interface using [Bootswatch Sketchy](https://bootswatch.com/sketchy/), customized to match the reference.
- Shared-key authentication and MongoDB persistence.

## Decisions and Agent Rules

### Agreed architecture and behavior

- Preserve the existing frontend; add server and shared-contract projects to the solution.
- Use a same-origin API under `/api`. Keep MongoDB settings and the configured access key exclusively on the server.
- Store the entered key in `sessionStorage`; send it in an `X-Api-Key` header only to the app’s API. Provide a Lock action that clears it.
- Use standalone MongoDB with one collection and one document per video/channel (`kind` and item-owned versions/positions). Revision metadata holds ID/version references only. Publish complete saves through one atomic reference update; no transactions or replica-set/sharding requirements. The user decisions on 2026-10-10 supersede the original single-board-document design and the first transaction-based follow-up; see the standalone storage handoff below.
- Use revision checks on mutations. Reject stale writes with a conflict response and let the frontend reload without silently overwriting newer data.
- New videos and successfully edited videos move to the top. Manual reordering remains until a subsequent add or edit changes the affected item’s position.
- New channels enter the top of their selected group. Editing within the same group preserves order; changing groups places the channel at the destination’s top.
- Allow channel dragging within and between groups. Videos remain in Saved Videos.
- Manual thumbnails are image URLs, with no uploads. Manual overrides survive edits until explicitly cleared.
- Use credential-free metadata only. YouTube uses oEmbed; Twitch uses public page metadata when available. Twitch’s [official video API](https://dev.twitch.tv/docs/api/reference/#get-videos) requires authentication, so unavailable public previews must fall back gracefully.
- Other HTTP(S) bookmarks are supported with manually entered titles and optional thumbnail URLs.
- Duplicate URLs are allowed. Links open in a new tab.
- Mobile layouts stack Saved Videos above Channels.
- Exclude accounts, provider login, video playback, watched-state tracking, search, tags, import/export, and background synchronization from this implementation.

### Sequential execution contract

Each phase belongs to one agent and starts only after the preceding phase passes its acceptance checks.

Every agent must:

1. Read this plan, the idea files, repository instructions, and previous handoff notes.
2. Inspect the current implementation before changing it.
3. Complete only its phase and necessary fixes to earlier work.
4. Preserve each existing file’s line endings; detect CRLF/LF before editing.
5. Run relevant checks and `git diff --check`; inspect for line-ending-only changes.
6. Update its phase status and handoff notes in `docs/plan.md`.

Use `feature/<descriptive-name>` if creating branches.

Each handoff must record: status, delivered behavior, changed contracts/configuration, verification commands and results, known limitations, and instructions needed by the next agent. Do not mark blocked or unverified acceptance criteria as passed.

## Implementation Phases

### Phase 1 — Application foundation and contracts

**Goal:** Establish a runnable application structure and stable interfaces.

**Deliverables**

- Add an ASP.NET Core server and a shared contracts project while retaining the current Blazor client.
- Configure the server to host the published client and `/api` endpoints.
- Define video, channel, channel-group, board snapshot, mutation, and metadata-result contracts.
- Videos contain identifiers, title, URL, automatic/manual thumbnail values, and timestamps. Channels contain identifiers, name, URL, group, and timestamps. Array order determines display order.
- Define these API capabilities: authenticate/check key, retrieve board, create/update/delete videos and channels, reorder videos, move/reorder channels, and retrieve metadata.
- Mutations carry the expected board revision and return the updated snapshot. Use consistent validation and error responses.
- Add server configuration placeholders for MongoDB connection string, collection, and access key. Support normal environment overrides.
- Establish xUnit test projects and document build, run, and test commands.

**Acceptance**

- The solution builds.
- Running the server serves the existing client.
- Shared contracts contain no database credentials or server-only dependencies.
- Configuration guidance contains placeholders rather than real secrets.

**Handoff:** Project layout, API contract summary, configuration keys, and runnable commands.

### Phase 2 — Authentication, persistence, and bookmark API

**Goal:** Deliver a protected, persistent backend with complete ordering semantics.

**Deliverables**

- Implement shared-key validation for all content and metadata endpoints; leave static assets public.
- Reject missing/incorrect keys, avoid logging credentials, and rate-limit failed authentication attempts.
- Implement MongoDB board initialization and atomic revision-checked mutations. Read the database name from the connection string using the MongoDB driver; reject a missing database name. Use the configured collection, which defaults to `lastseen2`.
- Implement bookmark CRUD, video reorder, and channel move/reorder endpoints.
- Validate required names/titles, HTTP(S) URLs, item existence, group membership, and reorder payload integrity.
- Guard against oversized boards with explicit application item-count and aggregate BSON limits.
- Fail clearly for missing required configuration and report database failures without claiming successful saves.

**Acceptance**

- Unauthorized requests cannot read or modify bookmarks.
- CRUD and ordering survive server restarts.
- Video edits move to the top; channel edits preserve position unless changing groups.
- Cross-group moves update membership and order together.
- Stale revisions return conflicts; invalid mutations leave persisted data unchanged.
- Integration tests run against an isolated MongoDB test database.

**Handoff:** Working API examples, persistence behavior, and integration-test setup.

### Phase 3 — Credential-free thumbnail metadata

**Goal:** Add useful previews without making bookmark saving depend on external services.

**Deliverables**

- Implement YouTube oEmbed lookup, including common watch, short-link, and Shorts URL forms. Use the [oEmbed specification](https://oembed.com/) for response handling.
- Implement best-effort Twitch public-page metadata extraction for supported video/clip links.
- Restrict server-side metadata requests to supported provider hosts. Validate redirects and block private/local destinations; use bounded timeouts and response sizes.
- Return optional title and thumbnail suggestions plus a nonfatal lookup status. Never render provider HTML.
- Automatic-mode saves attempt a refresh on add/edit. Manual thumbnail mode takes precedence.
- Retain an existing automatic thumbnail on lookup failure when the bookmark URL is unchanged; clear stale automatic metadata when the URL changes.
- Unsupported sites and failed lookups use manual input and a local placeholder.

**Acceptance**

- Fixture-based tests cover successful, missing, malformed, and timed-out metadata responses.
- Private-network requests and unsafe redirects are rejected.
- Manual overrides remain intact.
- Provider failures never prevent saving otherwise valid bookmarks.
- Live provider checks are documented separately from deterministic tests.

**Handoff:** Supported URL forms, fallback behavior, and observed provider limitations.

### Phase 4 — Dark interface and unlocking flow

**Goal:** Establish the visual structure and authenticated client shell.

**Deliverables**

- Add pinned, locally served Sketchy assets and dark styling based on the mockup.
- Build the header, desktop two-column layout, mobile stack, video rows, channel groups, badges, and empty states.
- Use blue primary actions, green Frequent, amber Infrequent, and red Archived accents.
- Add an unlock screen, key validation, session restoration, and Lock action.
- Configure a dedicated API client to attach the key only to same-origin API requests.
- Clear authentication on unauthorized responses. Keep network failures distinguishable from incorrect credentials.
- Load real board data and provide loading, retry, and error states.

**Acceptance**

- Desktop and narrow mobile widths remain usable without horizontal overflow.
- Session reload stays unlocked; Lock clears the key.
- No key appears in URLs or requests to external thumbnail hosts.
- Keyboard focus, labels, contrast, and accessible names are present.
- Empty and populated layouts follow the reference’s visual hierarchy.

**Handoff:** Component structure, authentication flow, and screenshots at desktop/mobile sizes.

### Phase 5 — Complete bookmark editing

**Goal:** Make all bookmark management available through the interface.

**Deliverables**

- Add accessible add/edit dialogs for videos and channels.
- Video forms support title, URL, automatic/manual thumbnail mode, thumbnail preview, and metadata feedback.
- Metadata suggestions populate untouched fields without overwriting user-entered values.
- Channel forms support name, URL, and group; default new channels to Frequent.
- Add deletion confirmation, save progress, duplicate-submission prevention, and recoverable errors.
- Apply returned server snapshots after successful mutations; preserve form values after failures.
- Handle revision conflicts with an explicit reload/review action.
- Add provider icons, safe external links, and broken-image placeholders.

**Acceptance**

- All CRUD operations work end to end and persist after reload.
- Canceled dialogs and canceled deletions leave data unchanged.
- Ordering follows the agreed add/edit rules.
- Metadata lookup failures and broken images leave forms and lists usable.
- Keyboard users can complete all forms and confirmations.

**Handoff:** Completed user flows and browser-test coverage.

### Phase 6 — Persistent drag-and-drop ordering

**Goal:** Deliver reliable ordering across videos and channel groups.

**Deliverables**

- Integrate a pinned SortableJS module through Blazor JavaScript interop.
- Use explicit drag handles, stable item identifiers, and visible drop feedback.
- Support video reorder and channel reorder/moves, including empty groups.
- Submit changes through the existing revision-aware API.
- Restore or reload server state when persistence fails or a conflict occurs.
- Add keyboard-accessible move-up, move-down, and channel-group controls using the same backend operations.
- Ensure ordinary link activation and edit buttons do not initiate dragging.

**Acceptance**

- Reordering persists after reload.
- Moves into and out of empty groups work and counts stay correct.
- Failed saves do not leave a misleading local order.
- Pointer, touch, and keyboard paths are verified.
- Subsequent video edits and channel edits still obey their distinct ordering rules.

**Handoff:** Interop lifecycle details and interaction-test results.

### Phase 7 — Release verification and operating documentation

**Goal:** Produce a tested, deployable first version.

**Deliverables**

- Add focused Playwright browser coverage for unlocking, CRUD, metadata fallback, ordering, and reload persistence.
- Run backend tests against real MongoDB and the full frontend against the server.
- Review the completed interface against the mockup at desktop and mobile sizes; fix material usability issues.
- Document local startup, environment configuration, production publish/run, HTTPS hosting, key rotation, and MongoDB backup/restore.
- Verify published output serves both the client and API correctly.
- Document credential-free Twitch limitations, standalone per-item persistence/recovery, and migration/backup procedures.
- Update all phase statuses and record final verification evidence.

**Acceptance**

- Build, automated tests, production publish smoke test, and `git diff --check` pass.
- No secrets are committed or included in client assets.
- Bookmarks and ordering survive browser reload and server restart.
- Database outages, invalid keys, stale writes, and unavailable metadata show actionable errors without data loss.
- The README provides sufficient instructions to run and deploy the app.

## Completion Record

Each phase starts as **Not started**. Its owning agent updates the entry with:

- **Status:** Not started / In progress / Complete / Blocked
- **Delivered:** Implemented behavior and relevant contracts
- **Verification:** Commands, outcomes, and any checks not run
- **Handoff:** Configuration, limitations, and next-agent instructions

| Phase | Status | Delivered | Verification | Handoff |
| --- | --- | --- | --- | --- |
| 1 — Application foundation and contracts | Complete | Host, contracts, configuration scaffolding, test projects, README | Build, 25 tests, release publish and HTTP smoke checks passed | See Phase 1 handoff below |
| 2 — Authentication, persistence, and bookmark API | Complete | Protected CRUD, ordering, revision checks, MongoDB persistence and validation | 45 tests including isolated MongoDB; release publish, HTTP assets, process restart and outage checks passed | See Phase 2 handoff below |
| 3 — Credential-free thumbnail metadata | Complete | Protected YouTube/Twitch suggestions, restricted networking, nonfatal automatic thumbnail refresh and local placeholder | 102 tests including isolated MongoDB; release publish and HTTP smoke checks; live provider observations recorded | See Phase 3 handoff below |
| 4 — Dark interface and unlocking flow | Complete | Local dark Sketchy assets, responsive read-only board, unlock/session/Lock and recoverable loading errors | 102 tests including isolated Mongo; desktop/mobile browser checks; published assets and real API browser read passed | See Phase 4 handoff below |
| 5 — Complete bookmark editing | Complete | Accessible video/channel CRUD dialogs, metadata previews, confirmation, snapshot updates and explicit conflict review | 102 tests including isolated Mongo; real CRUD browser suite on development and published hosts; shell regression and published HTTP checks | See Phase 5 handoff below |
| 6 — Persistent drag-and-drop ordering | Not started | — | — | — |
| 7 — Release verification and operating documentation | Not started | — | — | — |

The app is complete only when all seven phases meet their acceptance criteria. Actual production deployment remains outside this plan.
### Phase 1 handoff — Complete

**Delivered:** Added `LastSeen2.Server` and dependency-free `LastSeen2.Contracts`, connected the existing client to Contracts, and added both xUnit projects to the solution. The server serves the existing Blazor UI and declares all planned API routes. The README documents request/response contracts, error semantics, environment variables, and build/run/test/publish commands.

**Contracts:** Board snapshots contain a revision, ordered videos, and three ordered channel arrays. Mutations carry the expected revision and will return an updated board. Channel moves use a destination group and zero-based index after removing the moved item. Delete revisions are query parameters. Enums serialize as strings; identifiers are GUIDs. `ApiError` reads Problem Details without referencing ASP.NET Core. Full endpoint and payload details are in the README and Contracts source.

**Configuration:** Server-only options bind `ConnectionStrings:LastSeen2`, `MongoDb:Collection`, and `Authentication:AccessKey`. The corresponding environment variables use double underscores. Credential placeholders are empty; the collection defaults to `lastseen2`. The database name is supplied in the connection string path; there is no separate database option. Phase 1 starts without a database or access key.

**Verification:**

- `dotnet build src/LastSeen2.slnx --nologo` passed with zero warnings/errors; subsequent test and publish runs rebuilt the final changes successfully.
- `dotnet test src/LastSeen2.slnx --no-restore --nologo` passed: 3 contract tests and 22 server tests, zero failures/skips.
- `dotnet publish src/LastSeen2.Server -c Release -o artifacts/phase1-publish --nologo` passed.
- Ran the published application in Production on `http://127.0.0.1:5187`. Root and client fallback returned HTML; JavaScript bootstrap/runtime files, native WASM, and CSS returned 200 with appropriate content types. Declared API returned 501 Problem Details; unknown API and server `appsettings.json` requests returned 404.
- The publish check caught unresolved standalone HTML fingerprint placeholders. The final hosted client uses stable framework filenames and no HTML placeholders. Development tests and production HTTP asset checks pass with this configuration.
- `git diff --check` passed. Existing edited files retain CRLF; new text files also use CRLF. No line-ending-only changes.

**Limitations and next agent:** Authentication, request validation, MongoDB operations, and metadata lookups are intentionally unimplemented. `FoundationApi` returns 501 for declared routes; it never claims a key is valid or data was saved. Phase 2 must replace the relevant stubs, protect all content/metadata routes, validate required configuration and requests, and implement atomic revision-checked persistence. Metadata remains a stub until Phase 3. Preserve the API boundary that prevents unknown `/api` routes from returning the frontend shell. Replace scaffold-specific 501 test expectations as real handlers arrive. Browser interaction/visual tests and live MongoDB/provider tests were not run in Phase 1; the original starter UI remains.

### Phase 2 handoff — Complete

**Delivered:** Replaced API scaffolds with `BookmarkApi`, shared-key protection before request binding, constant-time key digest comparison, failed-authentication throttling, no-store API responses, and safe Problem Details. Added `BoardService` for validated CRUD/reordering/moves and `MongoBoardStore` for lazy revision-zero initialization and atomic revision-filtered replacements. Video edits move to the top; same-group channel edits retain position, group changes go to the destination top, and move indices apply after removal. Manual thumbnail values are retained when resubmitted and cleared by explicit null. Metadata remains a protected, URL-validating 501 endpoint.

**Configuration and storage:** MongoDB.Driver 3.12.0 is server-only. Required configuration validates at startup without printing values. The driver reads the data database from `ConnectionStrings:LastSeen2`; collection remains `lastseen2` by default. One `_id: "board"` document contains `schemaVersion: 1`, an Int64 revision, ordered arrays, and JSON-compatible GUID/UTC timestamp strings. Primary reads, majority-acknowledged writes, and bounded timeouts prevent reported success on failed/unacknowledged writes. A write timeout can have an uncertain outcome: reload before retrying.

**Contract details:** Shared DTOs/routes are unchanged. Required JSON constructor parameters must be supplied, including nullable `manualThumbnailUrl`. Limits are 500 characters for names/titles, 4096 for credential-free HTTP(S) URLs, 10000 total bookmarks, and 12 MiB BSON per board. Rejected mutations do not alter persisted bookmarks or revision. Kestrel limits request bodies to 1 MiB; an oversized-body production probe encountered a connection reset before a structured response. Failed-key attempts use a bounded process-wide one-minute window: the first 30 return 401, then all protected requests return 429 with Retry-After until reset. Valid requests do not consume the failure budget. The limiter is not distributed and resets on restart.

**Verification (2026-10-09):**

- `dotnet test src/LastSeen2.slnx --no-restore --nologo` with `LASTSEEN2_TEST_MONGODB=mongodb://127.0.0.1:27018/test_source`: 45 passed (3 contract, 42 server), zero failures/skips; build had no warnings/errors. Without that variable: 44 passed and one real-MongoDB test explicitly skipped.
- Tests cover unauthorized access to every declared route, malformed/missing JSON, configuration failures, URL/enum/revision/reorder/move validation, CRUD ordering, thumbnail override retention/clearing, authentication throttle/reset, oversized-board 413, and safe storage failures.
- The real MongoDB 8.2.0 test used a fresh `lastseen2_test_<guid>` database and `board_integration` collection; it verified concurrent initialization, CRUD and order persistence, unchanged snapshots after invalid/stale writes, exactly one winner among two hosts writing the same revision, and host recreation. Cleanup dropped only its generated test database.
- The initial isolated test attempt with the remote identity was denied by database permissions. It did not use the configured application collection. Downloaded the official Windows MongoDB ZIP into ignored `artifacts/`, ran a temporary localhost-only instance on port 27018, and used it for successful integration checks. Remote application write permissions remain unverified.
- `dotnet publish src/LastSeen2.Server -c Release -o artifacts/phase2-publish --no-restore --nologo` passed. Production HTTP checks passed for root, client fallback, bootstrap/runtime JS, native WASM, CSS, unknown API routes (including dots), and absence of public server appsettings.
- A published-server process saved a manual-thumbnail bookmark in a separate local test database, was stopped, and a fresh process returned the exact persisted board and revision. An unreachable MongoDB endpoint returned sanitized 503 while key checks remained available.
- `git diff --check` and a tracked/untracked line-ending scan passed; changed and new source/docs retain CRLF, with no line-ending-only changes.

**Limitations and next agent:** Phase 3 is next; implement provider metadata and automatic-mode refresh without changing save ordering, manual overrides, or CAS behavior. No external metadata fetches exist yet. The starter UI is unchanged and browser interaction/visual checks await later phases. Production startup now needs `Authentication__AccessKey` (it was absent in this session's inherited environment); configure it privately. The README has working API examples and isolated integration-test setup. Temporary test binaries/data/logs under ignored `artifacts/` are not required in another checkout. No later phases were implemented.

### Phase 3 handoff — Complete

**Delivered:** `MetadataService` implements protected metadata suggestions using YouTube JSON oEmbed and best-effort Twitch Open Graph/Twitter meta tags. `ProviderHttpClient` restricts hosts and redirects, disables proxy/cookie/credential forwarding, checks every DNS answer for public addresses, and pins socket connections to a checked address to prevent DNS rebinding. Limits: five seconds per lookup, three seconds per connection, three redirects, 32 KiB headers, 512 KiB decompressed response. Failed/missing/malformed metadata returns a safe nonfatal status. Provider HTML is never returned or rendered, and thumbnail URLs are never fetched by the server.

**Save behavior:** Automatic-mode video adds/edits refresh after validating the input and checking the current revision/item. User titles and resubmitted manual overrides remain unchanged. Manual mode skips lookup. An absent/failed automatic thumbnail retains the previous value only for an exactly unchanged trimmed URL; changed URLs clear stale automatic metadata. Save ordering, timestamps, board-size validation, and final atomic revision checks remain intact. A writer racing a metadata lookup still causes 409. Caller cancellation propagates rather than saving an abandoned request. Added `/images/video-placeholder.svg` for the later UI's missing/broken-image fallback.

**Contracts/configuration:** No DTO, persistence schema, dependency, or configuration changes. Metadata now returns 200 `MetadataResult` instead of 501; invalid URL syntax remains 400 and authentication is still required. `Available` can contain only a title or only a thumbnail; clients must handle each nullable suggestion separately. Full supported URL forms, network limits, manual semantics, and fallback behavior are documented in README.

**Verification (2026-10-09):**

- `dotnet test src/LastSeen2.slnx --no-restore --nologo` with a temporary localhost-only MongoDB 8.2.0 on port 27019: **102 passed** (3 contract, 99 server), zero failures/skips. Build succeeded without warnings/errors. Integration used only its generated isolated database and cleaned it up. No application database was written. Without `LASTSEEN2_TEST_MONGODB`, the integration test remains explicitly skipped.
- Deterministic fixtures cover common YouTube/Twitch forms, title/thumbnail extraction, missing/malformed/wrong-type responses, provider HTTP errors, actual timeout/caller cancellation, response-size bounds with/without Content-Length, unsupported URLs, unsafe and bounded redirects, private/special IPv4/IPv6 and mixed DNS answers, checked-address socket connection, manual overrides/clearing, unchanged/changed URL fallback, provider-failure saves, and concurrent revision changes during lookup.
- `dotnet publish src/LastSeen2.Server -c Release -o artifacts/phase3-publish --no-restore --nologo` passed. Published Production HTTP smoke checks passed for root, client fallback, bootstrap/runtime JavaScript, native WASM, CSS, the new SVG, unknown API paths including dots, nonpublic server appsettings, and authorized metadata fallback. Temporary processes were stopped.
- Live checks used the actual restricted client independently of fixtures: YouTube `https://www.youtube.com/watch?v=dQw4w9WgXcQ` returned a video-specific title/thumbnail. Twitch `https://www.twitch.tv/videos/2191576363` and `https://clips.twitch.tv/AwkwardHelplessSalamanderSwiftRage` returned generic shell metadata, not a video preview; these are now filtered to `Unavailable`. This does not guarantee other or future Twitch links will expose previews.
- `git diff --check` and tracked/untracked changed-file line-ending checks passed. Existing and new text files use CRLF; no line-ending-only changes.

**Limitations and next agent:** Phase 4 is next. Keep the starter UI until that phase; consume nullable metadata suggestions without overwriting touched form fields in Phase 5. Use the local placeholder when metadata or images are unavailable, and never forward X-Api-Key to thumbnail hosts. Twitch parsing intentionally handles static public meta tags only; login shells, removed links, bot restrictions, client-rendered data, and provider changes may yield no preview. No provider credentials, arbitrary-site scraping, caching, background sync, or later-phase UI was added. Temporary probe/build/database files under ignored `artifacts/` are not prerequisites.

### Phase 4 handoff — Complete

**Delivered:** Replaced the starter home page with the dark, responsive read-only board and unlock flow. Locally served Sketchy 5.3.8 and Fontsource 5.2.6 Neucha/Cabin Sketch assets include licenses; the upstream remote-font import was removed. Saved Videos uses two-thirds of the desktop grid, Channels one-third; at 800px and below they stack. Ordered video rows, three named/color-coded channel groups, counts, empty states, loading, Refresh, and retry/error states consume the authoritative board. Missing/broken previews use the local placeholder; titles are text and links use new tabs with noopener/noreferrer. No misleading inert editing/drag controls were added.

**Components and authentication:** `Pages/Home.razor` owns unlock/restore/load/Lock view state. `Components/VideoRow.razor` handles preview selection/fallback; `Components/ChannelSection.razor` renders each ordered group. `Services/AccessSession.cs` stores the validated key in sessionStorage (`lastseen2.accessKey`); Lock and unauthorized responses remove it. `Services/BoardApiClient.cs` exposes key-check and board reads through a private HttpClient, adding X-Api-Key per request to fixed same-origin API routes. Browser redirects are rejected before a key could reach an external destination. No default headers or credentials are attached to other HTTP/image requests. Network/timeout, 401, 429, 503, and malformed JSON failures have safe messages. Non-auth failures preserve the saved session. Late board responses cannot redisplay content after Lock. Browser storage failures are reported rather than silently claiming a saved session.

**Contracts/configuration:** No backend, DTO, storage schema, or configuration changes. No production secrets or app data were used in fixtures/screenshots. Session storage must be enabled. CRUD remains Phase 5; ordering and keyboard move controls remain Phase 6.

**Verification (2026-10-09):**

- `dotnet build src/LastSeen2.slnx --no-restore --nologo`: passed, zero warnings/errors.
- `dotnet test src/LastSeen2.slnx --no-build --nologo`: 101 passed and one explicit Mongo skip without a test URI; then **102 passed**, no skips, with a temporary localhost-only MongoDB 8.2.0 on port 27021. Integration tests used their generated isolated database. The application collection was not accessed.
- `node tests/browser/phase4.cjs` (Playwright 1.62.1, headless Edge) passed against both development and published Production hosts. Real authentication endpoint plus synthetic intercepted board/image fixtures verify incorrect keys, network errors, throttling, blocked redirects, session reload/revalidation, Lock and late-response races, 401 removal, 503/network retry, empty/populated data, broken previews, escaped/long titles, keyboard submit/focus, safe links, and key isolation. No page exceptions or runtime CDN requests. Desktop 1536px and mobile 390px/320px checked; mobile stacks and has no horizontal overflow.
- `dotnet publish src/LastSeen2.Server -c Release -o artifacts/phase4-publish --no-restore --nologo`: passed. Production HTTP checks passed for root, client fallback, bootstrap/runtime JS, native WASM, CSS, Sketchy, both fonts, placeholder, unknown API routes including dots, and nonpublic appsettings. An additional browser pass without interception unlocked, loaded the actual empty Mongo board, and restored after reload against a newly generated localhost test database. Temporary host/Mongo processes were stopped.
- Screenshots inspected: [unlock](screenshots/phase4-unlock.png), [populated desktop](screenshots/phase4-populated-desktop.png), [populated mobile](screenshots/phase4-populated-mobile.png), [empty desktop](screenshots/phase4-empty-desktop.png), [empty mobile](screenshots/phase4-empty-mobile.png). Fixture artwork/content is synthetic. The reference's hierarchy, accents, and dark treatment are retained; editing controls are deferred.
- `git diff --check` and tracked/untracked text-file checks passed; existing and new text files retain CRLF with no line-ending-only changes.

**Next agent:** Implement Phase 5 only. Extend the dedicated API client with fixed same-origin mutation/metadata methods, retaining redirect rejection and unauthorized clearing. Apply full returned snapshots and preserve form values on failures; handle 409 explicitly. Honor explicit-null manual clearing and individually nullable metadata suggestions. Reuse the preview fallback and safe links; add provider icons in Phase 5. The Phase 4 browser script is a focused shell regression suite, not the complete Phase 7 CRUD/ordering suite. Browser automation was Edge/Chromium only; touch drag/drop is deferred to Phase 6. Test setup is documented in `tests/browser/README.md`; ignored probes/build output are not prerequisites.

**Phase 4 follow-up (2026-10-10):** Removed promotional slogans from the header, unlock screen, board heading, empty state, and footer at the user's request. Keep interface copy functional: labels, instructions, and status messages only. Updated browser selectors and regenerated screenshots. Solution build and Phase 4 browser checks passed; CRLF preserved and git diff --check passed. No API or configuration changes.

The same follow-up also removes the redundant unlock description and the main-page Bookmarks heading. Refresh remains right-aligned. Build, browser checks, and refreshed screenshots passed after this refinement.

Removed the page-title tagline as requested; the title is now LastSeen2 and the loading status uses neutral wording.


### Phase 5 handoff — Complete

**Delivered (2026-10-10):** Add/edit video and channel dialogs, thumbnail mode/manual URL/preview, explicit metadata lookup with partial suggestions and nonfatal failure feedback, provider icons, per-item editing/deletion, and deletion confirmation. New channels default to Frequent. Returned snapshots drive all lists/counts/order. Real CRUD confirms video add/edit-to-top, channel same-group order preservation and destination-top group changes. Existing safe new-tab links and local broken-image fallback remain.

**Interaction and recovery:** Native modal dialogs plus `wwwroot/dialog.js` provide focus containment, Escape cancellation, and focus restoration (Refresh fallback if the original item disappears). Saving disables controls and cancellation to prevent duplicate submissions or dismissal during an uncertain write. Form drafts survive failures. Conflicts, missing items, transport/read failures and server errors require an explicit Reload and review action; reload applies the latest snapshot and displays current saved values while retaining the draft. Missing items cannot be resubmitted. Uncertain creates instruct the user to check the refreshed board before adding again. Unauthorized metadata/mutation/read responses clear the session, board and dialog. Disposed dialogs ignore late responses.

**Metadata/manual behavior:** Look up metadata is an explicit form action; automatic saves still refresh through the existing backend. Suggestions never overwrite touched titles or manual URLs. URL changes invalidate in-flight suggestions and clear stale automatic previews. Title-only suggestions are supported. Existing manual URLs are resent even when inactive in Automatic mode; clearing sends explicit null. Manual mode requires an image URL. Metadata failures do not block valid saves. Provider text is rendered as text and external images never receive the app key.

**Contracts/configuration:** No backend, DTO, persistence-schema, dependency or environment-setting changes. `BoardApiClient` now exposes fixed-route CRUD/metadata calls, preserving per-request same-origin headers, redirect rejection and omitted browser credentials. `ApiClientException.RequiresReview` distinguishes errors requiring reload before resubmission. `BookmarkDialog` owns its opening revision and draft; parent snapshot updates do not silently advance that revision.

**Verification:**

- `dotnet build src/LastSeen2.slnx --no-restore --nologo`: passed, zero warnings/errors.
- `dotnet test src/LastSeen2.slnx --no-build --nologo`: **102 passed**, no skips, using a localhost-only MongoDB 8.2.0 on port 27025 and generated isolated test databases. Application data was not accessed.
- `node tests/browser/phase5.cjs`: passed against both development and published Production hosts with real isolated MongoDB CRUD. Covers persisted reload, ordering, cancellation, keyboard/focus behavior, manual preservation/clearing, real competing-writer conflicts and review, missing items, 503 recovery, metadata partial/failure/races, invalid URL credentials, broken images, duplicate submit suppression, 401 clearing, key isolation and mobile overflow. Metadata/images use deterministic fixtures; save operations use unsupported URLs to avoid live provider dependencies.
- `dotnet publish src/LastSeen2.Server -c Release -o artifacts/phase5-publish --no-restore --nologo`: passed. Published HTTP checks passed for root, client fallback, bootstrap/runtime JavaScript, native WASM, CSS, Sketchy, fonts, placeholder, new dialog module, unknown API routes (including dots), and nonpublic appsettings.
- `node tests/browser/phase4.cjs`: passed against published output. Original Phase 4 screenshots were retained; new Phase 5 screenshots show synthetic data only: [board](screenshots/phase5-board.png), [video dialog](screenshots/phase5-video-dialog.png), [mobile dialog](screenshots/phase5-mobile-dialog.png).
- `git diff --check` and changed tracked/untracked text checks passed; CRLF preserved without line-ending-only changes.

**Next agent and limitations:** Implement Phase 6 only. Add persistent pointer/touch/keyboard ordering through the same API client and authoritative snapshot flow. Preserve the distinct video/channel edit ordering rules, dialog focus behavior, draft preservation, revision checks, and explicit review after uncertain writes. See `tests/browser/README.md` for reproducible setup; temporary scripts/binaries under ignored artifacts are not prerequisites. Browser coverage is Edge/Chromium; other engines and ordering gestures remain for later phases. Live provider checks were not repeated because the metadata backend is unchanged. Process-restart persistence remains covered by the existing real Mongo integration test. Temporary verification processes were stopped.


### Item-document storage follow-up - Superseded by standalone implementation (2026-10-10)

**User decision:** One collection means one record per video/channel, not one record for all bookmarks. This supersedes schema-version-1 storage descriptions in the historical Phase 2-5 handoffs. Phases 6-7 remain not started.

**Delivered:** Schema version 2 stores flat video/channel documents keyed by existing GUID strings, distinguished by `kind`, with `position` per list/group. A small board metadata document holds only schema/kind/revision. The API still returns ordered snapshots. Writes update only affected items and revision in a single transaction; snapshot reads avoid mixed revisions. First read automatically migrates legacy data transactionally, preserving every field, timestamps, IDs, groups, positions and revision. ID collisions or mixed legacy/item data fail without overwriting records.

**Contracts/configuration:** No API or configuration-key changes. MongoDB now requires a replica set or sharded cluster for multi-document transactions; single-node replica sets work for development. Existing aggregate resource limits remain (10000 items, 12 MiB total item BSON). Stop old app instances and back up before deployment; schema rollback requires restoring the backup. README documents these requirements.

**Verification:** `dotnet test src/LastSeen2.slnx --no-restore --nologo` passed **103 tests** (3 contract, 100 server), no skips, using a disposable localhost-only MongoDB 8.2.0 single-node replica set on port 27027. Tests use generated isolated databases only. Coverage includes actual per-item document shapes, CRUD/order/group persistence, stale writes, competing hosts, host recreation, concurrent legacy migration, full legacy field preservation, unrelated records, and rollback after migration/save ID collisions. No application database was accessed or migrated. Final `git diff --check` and byte-level checks passed; all seven changed files preserve the original checkout CRLF, with no line-ending-only changes.

**Superseded:** The user rejected this topology requirement. Follow the standalone handoff below; do not apply the transaction-based guidance in this historical entry.


### Standalone item storage follow-up - Complete (2026-10-10)

**User decision:** Standalone MongoDB only. This supersedes the transaction topology and implementation in the immediately preceding handoff. Phases 6-7 remain not started.

**Delivered:** Each bookmark remains one document in the same collection, distinguished by `kind`. Schema version 3 holds immutable versions inside each item document. The board record contains only revision/generation and ID/version references. Saves stage changed versions, then use one atomic reference replacement to publish a consistent board. Reads verify the generation after fetching items and retry on races with commits or cleanup. Stale writers lose the compare-and-swap and return conflicts. No MongoDB transactions, explicit sessions, replica-set setup, or sharding are used. Writes force journaled acknowledgement; failed cleanup is nonfatal and retried after later saves.

**Migration/recovery:** Legacy single-board and flat-item layouts migrate without changing IDs, fields, timestamps, positions, groups or API revision. A staging failure leaves the previous references authoritative. Publication is the commit point; an uncertain response retains potentially committed data, and clients must reload. Crashes after publication but before pruning leave the completed save readable. Abandoned versions are invisible and removable after a later successful save. See `docs/storage.md` for the concurrency/pruning rules and README for deployment/backup guidance.

**Verification:** `dotnet build src/LastSeen2.slnx --no-restore --nologo` passed with zero warnings/errors. `dotnet test src/LastSeen2.slnx --no-restore --nologo` passed **106 tests** (3 contract, 103 server), no skips, against a disposable localhost-only standalone MongoDB 8.2.0 on port 27028. Five isolated real-database tests cover CRUD/order, competing hosts, consistent concurrent readers, actual version/reference documents, schema-1/schema-2 migration, unrelated IDs, partial staging failure, and simulated process-crash boundaries before publication and after publication before cleanup. A test asserts the server is standalone. No application database was accessed or migrated. Final `git diff --check` and tracked/untracked byte-level checks passed; existing files retain CRLF and the new storage document uses CRLF.

**Next agent:** Continue Phase 6 only. Keep the API unchanged and preserve the standalone-only storage protocol. Run real database/browser checks against standalone MongoDB. The earlier replica-set requirement is withdrawn. No production migration or deployment was performed.
