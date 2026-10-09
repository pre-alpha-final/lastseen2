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
- Use one MongoDB collection. For this small personal app, store one versioned board document containing videos and grouped channels. Atomic document updates keep ordering and group moves consistent.
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
- Guard against oversized board documents with a clear error before MongoDB’s document limit is reached.
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
- Document credential-free Twitch limitations and the single-document storage boundary.
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
| 3 — Credential-free thumbnail metadata | Not started | — | — | — |
| 4 — Dark interface and unlocking flow | Not started | — | — | — |
| 5 — Complete bookmark editing | Not started | — | — | — |
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
