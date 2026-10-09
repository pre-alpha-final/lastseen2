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
