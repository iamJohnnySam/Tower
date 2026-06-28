# Website Sync API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `POST /api/website/sync` to Tower so Claude can trigger a full FTP scan-and-upload after each commit.

**Architecture:** One new `app.MapPost(...)` call in `Program.cs`, modelled on the existing `/api/ftp-push` handler. Calls the already-registered `FtpSyncService.ScanAsync()` then `SyncAsync()`. No new files, no new services.

**Tech Stack:** .NET 10 Minimal API, `FtpSyncService` (FluentFTP), `curl` for manual smoke-test.

## Global Constraints

- Upload-only: delete list passed to `SyncAsync` must always be `[]`
- No authentication required (localhost:8888 only, consistent with `/api/ftp-push`)
- Do not change `FtpSyncService` or `WebsiteOptions`

---

### Task 1: Add `/api/website/sync` endpoint

**Files:**
- Modify: `src/Tower/Program.cs` (after line ~244, after the `/api/ftp-push` block)

**Interfaces:**
- Consumes: `FtpSyncService.ScanAsync() → ScanResult` (fields: `ToUpload: List<FileCompareResult>`, `UpToDate: int`)
- Consumes: `FtpSyncService.SyncAsync(IReadOnlyList<string>, IReadOnlyList<string>, IProgress<string>, CancellationToken) → (int uploaded, int deleted, int failed)`
- Produces: `POST /api/website/sync → 200 { scanned, uploaded, failed }` or `500 { detail }`

- [ ] **Step 1: Add the endpoint block in `Program.cs`**

  Insert immediately after the closing `});` of the `/api/ftp-push` block (around line 244):

  ```csharp
  // ── Full scan-and-sync (triggered by Claude after each git push) ─────────────
  app.MapPost("/api/website/sync", async (
      FtpSyncService ftpSvc,
      CancellationToken ct) =>
  {
      try
      {
          var scan     = await ftpSvc.ScanAsync();
          var toUpload = scan.ToUpload.Select(f => f.Path).ToList();
          var (uploaded, _, failed) = await ftpSvc.SyncAsync(toUpload, [], new Progress<string>(), ct);
          return Results.Ok(new { scanned = scan.ToUpload.Count + scan.UpToDate, uploaded, failed });
      }
      catch (Exception ex)
      {
          return Results.Problem(ex.Message);
      }
  });
  ```

- [ ] **Step 2: Build to verify no compile errors**

  ```bash
  cd /home/atom/dev/Tower
  dotnet build src/Tower/Tower.csproj -c Release --no-restore 2>&1 | tail -5
  ```

  Expected: `Build succeeded.`

- [ ] **Step 3: Run the existing test suite to confirm nothing broke**

  ```bash
  cd /home/atom/dev/Tower
  dotnet test tests/Tower.Core.Tests/ --no-build 2>&1 | tail -8
  ```

  Expected: all tests pass (no failures).

- [ ] **Step 4: Deploy Tower and smoke-test the endpoint**

  ```bash
  bash /home/atom/dev/Tower/deploy.sh
  ```

  Then call the new endpoint:

  ```bash
  curl -s -X POST http://localhost:8888/api/website/sync
  ```

  Expected: JSON like `{"scanned":142,"uploaded":0,"failed":0}` (zero uploads if local and server are already in sync).

  If FTP credentials are not configured: `{"detail":"FTP credentials not configured."}` with HTTP 500 — that is correct behaviour and means the endpoint is wired up; configure credentials in Tower's Website settings tab.

- [ ] **Step 5: Commit**

  ```bash
  cd /home/atom/dev/Tower
  git add src/Tower/Program.cs
  git commit -m "feat: add POST /api/website/sync endpoint for full FTP scan-and-upload"
  git push origin HEAD
  ```

---

### Task 2: Add sync step to iamJohnnySam.com CLAUDE.md

**Files:**
- Modify: `/home/atom/dev/iamJohnnySam.com/CLAUDE.md`

**Interfaces:** none (documentation only)

- [ ] **Step 1: Append the deploy workflow section to CLAUDE.md**

  Add the following block at the end of `/home/atom/dev/iamJohnnySam.com/CLAUDE.md`:

  ```markdown
  ## Deploy Workflow — After Every Change

  After every code change to this project:

  1. Commit and push to GitHub (already required by global CLAUDE.md).
  2. Trigger Tower to sync changes to the live server via FTP:
     ```bash
     curl -s -X POST http://localhost:8888/api/website/sync
     ```
  3. Verify the response shows `"failed": 0`. If not, check Tower logs or the Website tab in Tower's Blazor UI at http://localhost:8888.
  ```

- [ ] **Step 2: Commit**

  ```bash
  cd /home/atom/dev/iamJohnnySam.com
  git add CLAUDE.md
  git commit -m "docs: add deploy workflow — commit+push then Tower FTP sync"
  git push origin HEAD
  ```
