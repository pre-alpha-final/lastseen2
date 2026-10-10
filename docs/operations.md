# Operating LastSeen2

## Deploy and serve HTTPS

Publish the server using the commands in the [README](../README.MD). A framework-dependent deployment needs the .NET 10 ASP.NET Core Runtime on the host; building needs the SDK. Copy the **entire publish directory**, including `wwwroot`, to a versioned deployment directory. Run with that directory as the working directory under a dedicated service account and an OS service supervisor with restart-on-failure. Set `ASPNETCORE_ENVIRONMENT=Production` and `AllowedHosts` to the public hostname (for example `bookmarks.example.com`), and inject these settings into that service's environment:

- `ConnectionStrings__LastSeen2`: MongoDB URI including the data database; retain the correct `authSource` if authentication uses another database.
- `MongoDb__Collection`: `lastseen2`, or the existing collection name.
- `Authentication__AccessKey`: a long randomly generated shared key from a password manager.

Use the deployment platform's protected environment/secret configuration. Do not put values in shell arguments, source control, public files, screenshots, access logs, or support tickets. Restart the service after changing environment settings; an already-running Visual Studio or service does not inherit later user-environment changes. Keep production and test identities separate. Restrict MongoDB network access and grant the application only the access needed for its database; release tests require a different identity/server with database creation/deletion rights.

One concrete HTTPS arrangement is a TLS reverse proxy on the same machine, forwarding to a loopback-only Kestrel listener:

```powershell
# Required settings are already supplied by the service environment.
dotnet LastSeen2.Server.dll --urls http://127.0.0.1:5033
```

For example, inside an existing Nginx `http` configuration (replace the hostname and certificate paths):

```nginx
server {
    listen 80;
    server_name bookmarks.example.com;
    return 308 https://bookmarks.example.com$request_uri;
}
server {
    listen 443 ssl;
    server_name bookmarks.example.com;
    ssl_certificate /etc/ssl/bookmarks/fullchain.pem;
    ssl_certificate_key /etc/ssl/bookmarks/privkey.pem;
    add_header Strict-Transport-Security "max-age=31536000" always;
    client_max_body_size 1m;
    location / {
        proxy_pass http://127.0.0.1:5033;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_read_timeout 60s;
    }
}
```

Serve the app at the origin root, not a subpath. Forward `/api` and all assets to the same host; preserve `X-Api-Key`, avoid API caching, and never include that header in proxy logs. Keep the backend port inaccessible from outside. Install and renew a publicly trusted certificate through your hosting platform. If the proxy and app are on different machines, secure the backend hop too.

The application does not currently consume forwarded headers or perform HTTPS redirects. This example enforces TLS and HSTS at the edge; do not assume Kestrel redirects HTTP. If adding scheme/client-IP-dependent middleware later, configure Forwarded Headers with explicit trusted proxies first. The failed-key limiter is currently process-wide, not IP-based or distributed. See Microsoft's [reverse proxy hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/when-to-use-a-reverse-proxy?view=aspnetcore-10.0).

Before opening access, verify the public HTTPS origin: unlock, read the board, load bootstrap/runtime JavaScript and native WASM, load CSS/fonts, refresh, and Lock. Unknown `/api` routes must return Problem Details rather than HTML; `/appsettings.json` must return 404. A root HTML 200 alone is not a readiness check. `/api/auth/check` validates only the key; an authenticated `/api/board` also checks storage and can initialize/migrate data. Back up before that first read during an upgrade. Test a synthetic bookmark only in a disposable deployment, not by seeding production data.

## Rotate the shared key

1. Generate a replacement key and update the protected service environment.
2. Restart **all** app instances with the new value; mixed keys across instances cause intermittent authentication failures. No dual-key grace period exists.
3. Verify the old key gets 401 and the new key can read the board. Do not print either key during verification. After 30 failed attempts, wait for the minute-long rate-limit window to reset before checking again.
4. Unlock browser tabs with the new key. Existing tabs clear their session and displayed board on their next unauthorized API response. Rotation does not remotely erase an idle tab's already-rendered data; use Lock or close it on a shared device.

No database migration or bookmark changes are involved. A service restart preserves content and order.

## Back up and restore standalone MongoDB

Back up **the entire configured collection**, including `_id: "board"` and every item/version document. A metadata-only or filtered live-item dump is not sufficient. The storage protocol gives consistent application reads, but an ordinary multi-document dump during writes is not an application snapshot. Stop all app instances and other writers for the duration of the dump. MongoDB remains running. Do not use replica-set `--oplog` instructions for this standalone deployment.

Install compatible MongoDB Database Tools separately. Supply credentials through an access-restricted YAML config outside the repo/webroot, using the tool's `uri`/`password` fields; do not pass a credential-bearing URI on the command line. The source config's URI should identify the source server and authentication source. These illustrative PowerShell commands contain only placeholder paths and database names:

```powershell
# Stop all app writers first. Replace these non-secret paths/names.
$sourceDatabase = 'my_database'
$collection = 'lastseen2'
$backup = 'D:\private-backups\lastseen2-2026-10-10.archive.gz'
mongodump --config 'D:\private-config\mongo-source.yml' --db $sourceDatabase --collection $collection --archive=$backup --gzip
if ($LASTEXITCODE -ne 0) { throw 'Backup failed; do not proceed with upgrade.' }
Get-FileHash -LiteralPath $backup -Algorithm SHA256
```

Store the archive encrypted, restrict access, and retain the checksum, date, app version, MongoDB/tools versions, source database/collection and schema version alongside it (never credentials). Keep a pre-migration backup until the new release and a restore drill are accepted. Restart the app only after the dump finishes successfully.

For a restore drill or recovery, keep the target app stopped and restore into a **new empty database**, using a separate restricted target config and explicit namespace remapping. The target URI should specify the server and authentication source without pinning the source data database. Use a target identity authorized to create that database:

```powershell
$restoreDatabase = 'lastseen2_restore_20261010'
mongorestore --config 'D:\private-config\mongo-target.yml' --archive=$backup --gzip --nsInclude="$sourceDatabase.$collection" --nsFrom="$sourceDatabase.$collection" --nsTo="$restoreDatabase.$collection" --stopOnError
if ($LASTEXITCODE -ne 0) { throw 'Restore failed; keep the target app stopped.' }
```

Verify the restored collection with an isolated app instance pointed at the restored database: compare revision, item counts, every field, group and ordering with the expected pre-backup board, then restart that instance and confirm persistence. A legacy backup migrates on first authorized read when opened by this release; use the matching old binary if validating an actual schema rollback. Do not mix restored records with an existing live collection or restore only the manifest. This procedure deliberately avoids `--drop`; destructive in-place recovery needs its own explicit target review and backup.

Once validated, stop all production app instances, update their URI database path to the restored database while retaining the intended authentication source, and restart them together. Keep the previous database and backup for investigation until recovery is accepted. Schema rollback requires the matching older binary **and** a complete pre-migration restore; do not run an older writer against schema 3. See [storage invariants](storage.md) and MongoDB's [mongodump](https://www.mongodb.com/docs/database-tools/mongodump/) and [mongorestore](https://www.mongodb.com/docs/database-tools/mongorestore/) references.

## Diagnose and recover

| Symptom | Action |
| --- | --- |
| Startup rejects configuration | Check setting presence, URI database path and collection validity without printing secret values; restart the correct host. `authSource` is not the data database. |
| 401 / locked screen | Unlock with the current shared key. After rotation, stale tabs must unlock again. |
| 429 | Wait at least the returned `Retry-After`; repeated invalid attempts affect all protected requests on that process. |
| 409 / board changed | Reload and review the authoritative board before explicitly retrying. Never automatically replay a mutation. |
| 503 / storage unavailable | Check database process, network/DNS, authentication, disk and journal availability. Preserve the form draft; recover connectivity and use Reload and review or Retry loading board. |
| Lost response / uncertain save | The save may have committed. Reload and check whether the item/change exists before retrying, especially an add. |
| Missing referenced version / persistent storage failure | Stop writers, preserve the full collection and logs for investigation, and restore a verified full backup if needed. Do not manually delete staged versions or rebuild metadata from guessed current items. |
| Metadata unavailable | Keep a manual title, optionally use a manual image URL, and save. Twitch public pages often expose only generic shell metadata; no provider credentials or authenticated fallback exist. |
| Broken image | The local placeholder is expected; inspect/replace the saved URL if desired. The server never fetches arbitrary manual image URLs. |
| Blank frontend after deployment | Check WASM/JS MIME types and network failures, publish the server with all assets, and verify the proxy routes. Do not restore standalone fingerprint placeholders. |

A crash before metadata publication leaves the previous board authoritative; a crash afterward leaves the new board authoritative. Unreferenced versions are invisible and cleaned by later successful saves. Do not treat leftover versions as failed-save proof or manually prune them. Static assets and key checks may still work during a database outage. API logs intentionally omit raw exception text and credentials; use protected database/service diagnostics for the underlying cause.

## Release evidence and limits

Run the reproducible [release verification](../tests/browser/README.md#release-verification-phase-7) before shipping. It uses a private standalone MongoDB process, never the configured application collection. Evidence includes backend TRX results, published HTTP checks, browser screenshots and logs. Review screenshots against `docs/initial_idea.jpg`: two-thirds Saved Videos, one-third Channels, dark Sketchy styling, green/amber/red groups and stacked narrow screens.

Production deployment, public certificate/proxy provisioning and backup restoration against a real operator environment are operational steps, not performed by the test runner. Browser coverage uses Edge/Chromium with emulated touch; physical devices and other engines remain unverified. Provider fixtures keep release tests deterministic; the recorded live Twitch/YouTube observations are in the README and are not availability guarantees.
