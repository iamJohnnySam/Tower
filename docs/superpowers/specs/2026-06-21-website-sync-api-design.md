# Website Sync API — Design Spec

**Date:** 2026-06-21  
**Project:** Tower (`/home/atom/dev/Tower/`)  
**Scope:** Single new HTTP endpoint + CLAUDE.md workflow note

---

## Problem

After editing `iamJohnnySam.com` locally and pushing to GitHub, the live server (FTP host) must also be updated. Tower already owns `FtpSyncService`, which can scan for changed files and upload them, but there is no HTTP endpoint that triggers a full scan-and-sync cycle. Claude Code currently has no automated way to push changes to the live server after a commit.

---

## Solution

Add `POST /api/website/sync` to Tower's HTTP pipeline. When called, it:

1. Runs `FtpSyncService.ScanAsync()` — walks the local `public_html/` directory and the remote FTP server, returns a list of files that are new or changed locally.
2. Runs `FtpSyncService.SyncAsync(toUpload, [], ...)` — uploads those files. The delete list is always empty (upload-only) to avoid touching server-generated files or user uploads not tracked in git.
3. Returns JSON: `{ scanned, uploaded, failed }`.

No authentication is needed — Tower listens on `localhost:8888` only, consistent with the existing `/api/ftp-push` endpoint which also has no auth.

---

## Endpoint contract

```
POST http://localhost:8888/api/website/sync
Content-Type: (none required)

200 OK
{ "scanned": 142, "uploaded": 3, "failed": 0 }

500 Internal Server Error
{ "detail": "<error message>" }
```

`scanned` = total files found locally (after exclusions).  
`uploaded` = files successfully pushed to FTP.  
`failed` = files that failed to upload (errors logged by Tower).

---

## Implementation location

`src/Tower/Program.cs` — one `app.MapPost(...)` call added alongside the existing `/api/ftp-push` handler.

No new files, classes, or services required. `FtpSyncService` is already registered as scoped and does all the work.

---

## Claude workflow (CLAUDE.md note)

Added to `iamJohnnySam.com/CLAUDE.md`:

> After every code change: commit + push to GitHub, then run  
> `curl -s -X POST http://localhost:8888/api/website/sync`  
> to push the changes to the live server via FTP.

---

## What is NOT in scope

- Authentication / API keys (localhost only, consistent with existing pattern)
- Deleting remote-only files (safe default; avoids touching server-generated content)
- Progress streaming / WebSocket (fire-and-forget curl call is sufficient)
- Separate Tower UI page (existing Website tab in Blazor already covers manual sync)
