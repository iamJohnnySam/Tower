# Website Tab Design
_Date: 2026-06-19_

## Goal

Add a "Website" tab to Tower for managing iamJohnnySam.com. The tab lets the user scan differences between the local `public_html` folder (source of truth) and the FTP server, then selectively sync files.

---

## Architecture

### New files

| File | Purpose |
|---|---|
| `src/Tower.Core/Website/WebsiteOptions.cs` | Config POCO: `LocalPath`, `FtpHost`, `FtpRemotePath` |
| `src/Tower.Core/Website/FtpSyncService.cs` | FluentFTP wrapper — `ScanAsync()` and `SyncAsync()` |
| `src/Tower/Components/Pages/Website.razor` | Blazor page at `/website` |
| `src/Tower/Components/Pages/Website.razor.css` | Scoped styles |

### Modified files

| File | Change |
|---|---|
| `src/Tower/Components/Layout/NavMenu.razor` | Add Website nav entry |
| `src/Tower/Program.cs` | Register `FtpSyncService`, bind `WebsiteOptions` |
| `src/Tower.Core/Tower.Core.csproj` | Add `FluentFTP` NuGet package |
| `src/Tower/appsettings.json` | Add `Tower.Website` config block |

---

## Configuration

`appsettings.json` additions under `Tower`:

```json
"Website": {
  "LocalPath": "/home/atom/dev/iamJohnnySam.com/public_html",
  "FtpHost": "x11.x10hosting.com",
  "FtpRemotePath": "/public_html"
}
```

FTP username and password are stored via `SettingsService` under keys `website.ftp_user` and `website.ftp_pass`. This keeps credentials out of config files and follows the existing Jellyfin/Dropbox pattern.

---

## FtpSyncService

### ScanResult model

```csharp
record ScanResult(
    List<string> ToUpload,      // new or modified locally (relative paths)
    List<string> RemoteOnly,    // exist on FTP but not locally
    int UpToDate                // count of files that match
);
```

File comparison uses **file size + last modified date** (FluentFTP's `GetChecksum` is optional/slow; size+mtime is sufficient for a personal site).

### ScanAsync()

1. Connect to FTP with stored credentials.
2. Recursively list all remote files under `FtpRemotePath` → dictionary of `relativePath → (size, mtime)`.
3. Recursively enumerate all local files under `LocalPath` → same shape.
4. Classify each file:
   - Local only or local newer/different size → `ToUpload`
   - Remote only → `RemoteOnly`
   - Match → `UpToDate`
5. Disconnect. Return `ScanResult`.

### SyncAsync(filesToDelete)

1. Connect to FTP.
2. Upload each file in `ToUpload` (create remote directories as needed).
3. Delete each path in `filesToDelete` from the remote.
4. Disconnect.
5. Yield progress events as files complete so the UI can stream the log.

Progress is reported via `IProgress<string>` (one line per file: `↑ path/to/file.php`, `✗ path/to/old.js`).

### Error handling

- Connection failures surface as an exception caught in the Razor page, shown as a red error message.
- Per-file upload errors are logged as a failed line in the progress stream; the sync continues for remaining files and reports a final count with error count.

---

## UI — Website.razor

### Credentials block

- Fields: FTP Host (pre-filled from `appsettings.json`), Remote Path, Username, Password (masked).
- Stored via `SettingsService`. Shows "✓ Configured" badge + Replace button once set, same as Settings page.
- "Test Connection" button calls `FtpSyncService.TestAsync()` → shows green "Connected" or red error inline.

### Sync block

Only shown once credentials are configured.

**State machine:**

```
Idle → Scanning → Preview → Syncing → Done
                          ↑
                    (Scan again)
```

- **Idle**: "Scan" button.
- **Scanning**: spinner + "Scanning…" label.
- **Preview**: three groups displayed:
  - **To upload** — file count header, collapsible list of relative paths.
  - **Remote only** — file count header, list with a checkbox per file (all unchecked by default). User checks any they want deleted.
  - **Up to date** — count only, no list.
  - "Sync now" button (disabled if `ToUpload` is empty and no deletions checked). "Scan again" link resets to Idle.
- **Syncing**: scrolling `<pre>` log that appends lines as `IProgress<string>` fires.
- **Done**: final summary line ("✓ 12 uploaded, 2 deleted" or "✓ 12 uploaded, 1 failed").

---

## Nav entry

Globe SVG icon, label "Website", route `/website`. Inserted after Tuya and before Jellyfin in the rail.

---

## NuGet dependency

`FluentFTP` (latest stable, currently ~50.0.x). Added to `Tower.Core.csproj`.

---

## Out of scope

- Two-way sync or pulling from FTP.
- File editing within Tower.
- Scheduling automatic syncs.
- SFTP support (x10hosting uses FTP).
