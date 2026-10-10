# Phase 4 shell browser checks

`phase4.cjs` uses Playwright with installed Microsoft Edge. Run from the repository root. It intercepts board/image responses with synthetic fixtures and never mutates a database. Authentication calls reach the real server, including intentionally invalid keys; use a disposable test host, not an existing production instance. It regenerates the five committed `docs/screenshots/phase4-*.png` screenshots.

Install Playwright into ignored tooling output if it is not already available:

```powershell
npm install --prefix artifacts/browser-tools --no-save playwright
$env:NODE_PATH = Join-Path $pwd 'artifacts/browser-tools/node_modules'
```

Start the test host in one terminal (these settings apply only to that shell):

```powershell
$env:ConnectionStrings__LastSeen2 = 'mongodb://127.0.0.1:27021/lastseen2_browser_test'
$env:Authentication__AccessKey = 'phase4-disposable-test-key'
dotnet run --project src/LastSeen2.Server --no-launch-profile --urls http://127.0.0.1:5194
```

MongoDB need not be running for this fixture suite; it intercepts every board read. In a second terminal, from the repository root:

```powershell
$env:NODE_PATH = Join-Path $pwd 'artifacts/browser-tools/node_modules'
$env:PHASE4_TEST_URL = 'http://127.0.0.1:5194'
$env:PHASE4_TEST_KEY = 'phase4-disposable-test-key'
node tests/browser/phase4.cjs
```

Stop the disposable host afterward. To check published output, publish the server and run `dotnet LastSeen2.Server.dll --urls http://127.0.0.1:5194` from the publish directory with the same test environment, then run the same browser command. Phase 4 was verified with Playwright 1.62.1 and Edge on Windows. These tests supplement the solution tests; the real CRUD and ordering suites below cover the completed interface. Other browser engines remain unverified.


## Phase 5 CRUD checks

`phase5.cjs` runs real video/channel CRUD against the API and MongoDB. Use an **empty, disposable isolated database**, never the configured application database. The suite verifies revision zero/empty arrays before its first write and leaves synthetic bookmarks in that test database. Saves use unsupported example.com URLs; metadata/image failures and races are intercepted deterministically. Use the same Playwright/Edge setup above.

Start a local standalone test MongoDB on port 27025, bound to 127.0.0.1. No replica-set initialization is needed. Launch a disposable host with `ConnectionStrings__LastSeen2` pointing to a newly generated `lastseen2_test_<guid>` database on that server and `Authentication__AccessKey` set to a disposable test key. Run `dotnet run --project src/LastSeen2.Server --no-launch-profile --urls http://127.0.0.1:5195`.

In the test terminal, set `NODE_PATH` as above, `PHASE5_TEST_URL` to `http://127.0.0.1:5195`, `PHASE5_TEST_KEY` to the same disposable key, and `PHASE5_ISOLATED` to `yes`. Run `node tests/browser/phase5.cjs`.

For each repeat run, restart the host with a fresh generated database name. The script does not clear or drop databases. Stop your disposable processes afterward. Published checks use the same settings with the published server instead of `dotnet run`.

Coverage: persisted CRUD/reload and add/edit ordering; default/group changes; cancel/Escape and deletion confirmation; modal focus containment/restoration; keyboard save/delete; manual thumbnail preservation/clearing; real stale revisions and explicit reload/review; missing bookmarks; recoverable save errors; touched-title and changed-URL metadata races; partial/unavailable/network metadata; credential-bearing URL validation; broken previews; duplicate-submission prevention; 401 session clearing; key isolation; 320/390px overflow checks.

Screenshots: `docs/screenshots/phase5-board.png`, `phase5-video-dialog.png`, and `phase5-mobile-dialog.png`. Dialog scrolling keeps controls reachable at narrow heights. Ordering gestures are covered by Phase 6 below; other browser engines remain unverified.


## Phase 6 ordering checks

`phase6.cjs` uses the real revision-aware API and a fresh, empty, disposable standalone MongoDB database. It refuses to write unless `PHASE6_ISOLATED=yes` and the board is revision zero with empty lists. Never point it at application data. It does not clear or drop databases.

Use the Playwright/Edge setup above. Start a localhost-only standalone MongoDB (verification used port 27029), then run the server with `ConnectionStrings__LastSeen2` pointing to a new `lastseen2_test_<guid>` database and a disposable `Authentication__AccessKey`. To avoid an existing Debug host's output locks, Release can be used:

```powershell
dotnet build src/LastSeen2.slnx -c Release --no-restore
dotnet run --project src/LastSeen2.Server -c Release --no-build --no-launch-profile --urls http://127.0.0.1:5196
```

In the browser-test terminal:

```powershell
$env:NODE_PATH = Join-Path $pwd 'artifacts/browser-tools/node_modules'
$env:PHASE6_TEST_URL = 'http://127.0.0.1:5196'
# Set PHASE6_TEST_KEY to the disposable host's access key.
$env:PHASE6_ISOLATED = 'yes'
node tests/browser/phase6.cjs
```

Use a new database for every run; stop test hosts and MongoDB afterward. For published verification, publish the server and run its DLL from the publish directory with the same isolated settings. Phase 4 can share this host because its board reads are fixtures; Phase 5 requires its own fresh database.

Coverage: real pointer and Chromium-emulated touch drag gestures; keyboard up/down and native group selection; same-list ordering; moves into/out of empty groups and updated counts; saved reload order; video/channel edit-order rules after manual moves; safe link activation; duplicate-move suppression; real competing-writer conflict; rejected and committed-but-lost responses; explicit reload/review; unauthorized session clearing; late save after Lock; 390/320px overflow. The suite checks DOM order against persisted order and waits for the rendered revision, catching Blazor component-boundary corruption. Screenshots: `docs/screenshots/phase6-desktop.png` and `phase6-mobile.png`. Physical touchscreen devices and non-Chromium engines were not tested.


## Release verification (Phase 7)

`release.cjs` orchestrates restore, Release build, all real-MongoDB backend tests, production publish/HTTP checks, the Phase 4/5/6 Playwright suites, and additional real-process restart, database-outage/recovery and key-rotation browser checks. Install the .NET 10 SDK, Node.js, Microsoft Edge, and a standalone `mongod` executable. Playwright is the only Node dependency:

```powershell
npm install --prefix artifacts/browser-tools --no-save playwright@1.62.1
$env:NODE_PATH = Join-Path $pwd 'artifacts/browser-tools/node_modules'
# Set to your installed MongoDB executable; it need not be on PATH.
$env:LASTSEEN2_TEST_MONGOD = 'C:\Program Files\MongoDB\Server\8.2\bin\mongod.exe'
node tests/browser/release.cjs
```

Run from the repository root. The runner chooses free loopback ports, generates its own disposable keys and database names, and overrides the inherited application database/collection for its children. It starts its own MongoDB with a fresh data directory under `artifacts/release-<guid>` and never connects to the application's MongoDB. Backend tests drop only their generated test databases. Browser databases and logs remain in the isolated artifact directory for investigation; the runner stops only its own processes in `finally`. No existing server needs to be stopped; Release builds can coexist with an existing Debug host. Do not run simultaneous builds into the same Release output.

Evidence is written to the printed artifact directory: per-command logs, backend TRX results, full published output, screenshots and `passed.txt` only after success. Failures return a nonzero exit code with the failing step. Review the log named by the failure. A fresh invocation uses a new directory/database. There is no dependency on earlier ignored scripts or binaries. The screenshot suites accept `BROWSER_SCREENSHOT_DIR`; the runner sets it to its evidence directory, preserving the committed screenshots. Standalone phase scripts retain their historical screenshot location by default.

The release-only check compares the entire snapshot before/after a server process restart, kills the owned database during an ordering save, checks the actual 503 and explicit review UI, restarts MongoDB on the same files, and verifies every saved field/order is unchanged. It then rotates the key by restarting the host, verifies old-key rejection and browser session clearing, and unlocks with the new key. Phase 5 covers deterministic unavailable metadata and real stale writes; Phase 6 covers uncertain committed responses and pointer/emulated-touch/keyboard ordering. Production asset checks include JS, native WASM, CSS/fonts, local modules, dotted API 404s and nonpublic server configuration. The client scan checks known configured secret values without printing them; it supplements source review, not a general secret detector.

Scope: Windows Edge/Chromium was verified. Touch uses CDP emulation, not a physical device. The runner does not provision public HTTPS, deploy production, verify live provider availability, or perform an operator backup/restore drill. See [operations](../../docs/operations.md) for those procedures.
