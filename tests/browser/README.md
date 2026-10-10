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

Stop the disposable host afterward. To check published output, publish the server and run `dotnet LastSeen2.Server.dll --urls http://127.0.0.1:5194` from the publish directory with the same test environment, then run the same browser command. Phase 4 was verified with Playwright 1.62.1 and Edge on Windows. These tests supplement the solution tests; complete CRUD/ordering and cross-browser coverage belongs to later phases.


## Phase 5 CRUD checks

`phase5.cjs` runs real video/channel CRUD against the API and MongoDB. Use an **empty, disposable isolated database**, never the configured application database. The suite verifies revision zero/empty arrays before its first write and leaves synthetic bookmarks in that test database. Saves use unsupported example.com URLs; metadata/image failures and races are intercepted deterministically. Use the same Playwright/Edge setup above.

Start a local standalone test MongoDB on port 27025, bound to 127.0.0.1. No replica-set initialization is needed. Launch a disposable host with `ConnectionStrings__LastSeen2` pointing to a newly generated `lastseen2_test_<guid>` database on that server and `Authentication__AccessKey` set to a disposable test key. Run `dotnet run --project src/LastSeen2.Server --no-launch-profile --urls http://127.0.0.1:5195`.

In the test terminal, set `NODE_PATH` as above, `PHASE5_TEST_URL` to `http://127.0.0.1:5195`, `PHASE5_TEST_KEY` to the same disposable key, and `PHASE5_ISOLATED` to `yes`. Run `node tests/browser/phase5.cjs`.

For each repeat run, restart the host with a fresh generated database name. The script does not clear or drop databases. Stop your disposable processes afterward. Published checks use the same settings with the published server instead of `dotnet run`.

Coverage: persisted CRUD/reload and add/edit ordering; default/group changes; cancel/Escape and deletion confirmation; modal focus containment/restoration; keyboard save/delete; manual thumbnail preservation/clearing; real stale revisions and explicit reload/review; missing bookmarks; recoverable save errors; touched-title and changed-URL metadata races; partial/unavailable/network metadata; credential-bearing URL validation; broken previews; duplicate-submission prevention; 401 session clearing; key isolation; 320/390px overflow checks.

Screenshots: `docs/screenshots/phase5-board.png`, `phase5-video-dialog.png`, and `phase5-mobile-dialog.png`. Dialog scrolling keeps controls reachable at narrow heights. Phase 6 ordering gestures and broader cross-browser coverage remain deferred.
