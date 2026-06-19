# Website Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/website` tab to Tower that scans differences between local `public_html` and an FTP server, then lets the user preview and selectively sync (upload + optional deletions).

**Architecture:** `FtpSyncService` in Tower.Core wraps FluentFTP with `ScanAsync()` and `SyncAsync()`. Static config (host, paths) comes from `appsettings.json` via a new `WebsiteConfig` nested in `TowerConfig` and bridged to `WebsiteOptions` in Program.cs. FTP credentials are stored via `SettingsService` (same pattern as Jellyfin/Dropbox). The Blazor page at `/website` drives a `Idle → Scanning → Preview → Syncing → Done` state machine with no background workers.

**Tech Stack:** .NET 10 Blazor Server, FluentFTP NuGet (≥50.0.0), xUnit v3, SettingsService (SQLite key/value).

## Global Constraints

- Target framework: `net10.0`; no `.Result` or `.Wait()` — all async
- FluentFTP added to `Tower.Core.csproj` only
- SettingsService keys: `website.ftp_user`, `website.ftp_pass`
- Static config under `Tower:Website` in `appsettings.json` (LocalPath, FtpHost, FtpRemotePath)
- `FtpSyncService` registered `AddScoped` (it injects scoped `SettingsService`)
- Follow existing Blazor patterns: `@inject`, `InvokeAsync(StateHasChanged)`, `IAsyncDisposable` only when needed
- CSS uses CSS custom properties: `var(--text)`, `var(--surface-1)`, `var(--border)`, etc. (defined in `app.css`)
- Shared CSS classes reused from `app.css`: `page-title`, `maint-log`, `atom-section-block`, `atom-block-title`, `config-form`, `config-row`, `config-label`, `config-control`, `config-input`, `config-input-wide`, `key-configured-row`, `key-input-row`, `configured-badge`, `val-green`, `val-amber`, `val-red`, `btn btn-primary`, `btn-secondary btn-sm`, `btn-link`, `save-confirm`

---

## File Map

| Action | File | Purpose |
|--------|------|---------|
| Modify | `src/Tower.Core/Tower.Core.csproj` | Add FluentFTP NuGet |
| Create | `src/Tower.Core/Website/WebsiteOptions.cs` | Static config POCO for FtpSyncService |
| Create | `src/Tower.Core/Website/FtpSyncService.cs` | FTP scan + sync logic |
| Modify | `src/Tower/TowerConfig.cs` | Add `WebsiteConfig` nested class + property |
| Modify | `src/Tower/appsettings.json` | Add `Tower.Website` config block |
| Modify | `src/Tower/Program.cs` | Register WebsiteOptions singleton + FtpSyncService scoped |
| Create | `src/Tower/Components/Pages/Website.razor` | Blazor page at `/website` |
| Create | `src/Tower/Components/Pages/Website.razor.css` | Scoped styles |
| Modify | `src/Tower/Components/Layout/NavMenu.razor` | Add Website nav entry |
| Create | `tests/Tower.Core.Tests/WebsiteSyncTests.cs` | Unit tests for Classify() |

---

### Task 1: FluentFTP + config scaffolding

**Files:**
- Modify: `src/Tower.Core/Tower.Core.csproj`
- Create: `src/Tower.Core/Website/WebsiteOptions.cs`
- Modify: `src/Tower/TowerConfig.cs`
- Modify: `src/Tower/appsettings.json`
- Modify: `src/Tower/Program.cs`

**Interfaces:**
- Produces: `WebsiteOptions` singleton registered in DI; `TowerConfig.Website` property; `FtpSyncService` can be added next task

- [ ] **Step 1: Add FluentFTP to Tower.Core.csproj**

Open `src/Tower.Core/Tower.Core.csproj` and add inside the first `<ItemGroup>` (with other PackageReferences):

```xml
<PackageReference Include="FluentFTP" Version="50.0.0" />
```

- [ ] **Step 2: Create WebsiteOptions.cs**

Create `src/Tower.Core/Website/WebsiteOptions.cs`:

```csharp
namespace Tower.Core.Website;

public class WebsiteOptions
{
    public string LocalPath { get; set; } = "";
    public string FtpHost { get; set; } = "";
    public string FtpRemotePath { get; set; } = "/public_html";
}
```

- [ ] **Step 3: Add WebsiteConfig to TowerConfig.cs**

In `src/Tower/TowerConfig.cs`, add after the last existing property in `TowerConfig`:

```csharp
    public WebsiteConfig Website { get; set; } = new();
```

And add this class at the bottom of the file (after `ProjectDef`):

```csharp
public class WebsiteConfig
{
    public string LocalPath { get; set; } = "/home/atom/dev/iamJohnnySam.com/public_html";
    public string FtpHost { get; set; } = "x11.x10hosting.com";
    public string FtpRemotePath { get; set; } = "/public_html";
}
```

- [ ] **Step 4: Add Website block to appsettings.json**

In `src/Tower/appsettings.json`, add inside the `"Tower"` object (after `"Projects": [...]`):

```json
    "Website": {
      "LocalPath": "/home/atom/dev/iamJohnnySam.com/public_html",
      "FtpHost": "x11.x10hosting.com",
      "FtpRemotePath": "/public_html"
    }
```

- [ ] **Step 5: Register in Program.cs**

In `src/Tower/Program.cs`, add this using at the top with the other Tower.Core usings:

```csharp
using Tower.Core.Website;
```

Then add this block after the `// ── PiHole ───` section:

```csharp
// ── Website ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new WebsiteOptions
{
    LocalPath     = towerCfg.Website.LocalPath,
    FtpHost       = towerCfg.Website.FtpHost,
    FtpRemotePath = towerCfg.Website.FtpRemotePath,
});
```

- [ ] **Step 6: Verify build**

```bash
cd /home/atom/dev/Tower && dotnet build src/Tower/Tower.csproj
```

Expected: Build succeeded, 0 errors. FluentFTP will be downloaded and cached.

- [ ] **Step 7: Commit**

```bash
git add src/Tower.Core/Tower.Core.csproj src/Tower.Core/Website/WebsiteOptions.cs \
        src/Tower/TowerConfig.cs src/Tower/appsettings.json src/Tower/Program.cs
git commit -m "Add FluentFTP dependency and Website config scaffolding"
```

---

### Task 2: FtpSyncService + tests

**Files:**
- Create: `src/Tower.Core/Website/FtpSyncService.cs`
- Create: `tests/Tower.Core.Tests/WebsiteSyncTests.cs`
- Modify: `src/Tower/Program.cs` (add `AddScoped<FtpSyncService>()`)

**Interfaces:**
- Consumes: `WebsiteOptions` (Task 1), `SettingsService` (existing), `ILogger<FtpSyncService>` (framework)
- Produces:
  - `record ScanResult(List<string> ToUpload, List<string> RemoteOnly, int UpToDate)`
  - `Task<(bool ok, string? error)> FtpSyncService.TestConnectionAsync()`
  - `Task<ScanResult> FtpSyncService.ScanAsync()`
  - `Task FtpSyncService.SyncAsync(IReadOnlyList<string> filesToUpload, IReadOnlyList<string> filesToDelete, IProgress<string> progress, CancellationToken ct = default)`
  - `static ScanResult FtpSyncService.Classify(Dictionary<string,(long,DateTime)> local, Dictionary<string,(long,DateTime)> remote)`

- [ ] **Step 1: Write failing tests**

Create `tests/Tower.Core.Tests/WebsiteSyncTests.cs`:

```csharp
using Tower.Core.Website;

namespace Tower.Core.Tests;

public class WebsiteSyncTests
{
    [Fact]
    public void Classify_NewLocalFile_AddedToUpload()
    {
        var local = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/index.php"] = (100, DateTime.UtcNow)
        };
        var remote = new Dictionary<string, (long size, DateTime mtime)>();

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/index.php", result.ToUpload[0]);
        Assert.Empty(result.RemoteOnly);
        Assert.Equal(0, result.UpToDate);
    }

    [Fact]
    public void Classify_RemoteOnlyFile_AddedToRemoteOnly()
    {
        var local = new Dictionary<string, (long size, DateTime mtime)>();
        var remote = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/old.php"] = (50, DateTime.UtcNow)
        };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Empty(result.ToUpload);
        Assert.Single(result.RemoteOnly);
        Assert.Equal("/old.php", result.RemoteOnly[0]);
        Assert.Equal(0, result.UpToDate);
    }

    [Fact]
    public void Classify_SameSizeAndRemoteNewer_CountedAsUpToDate()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/style.css"] = (200, now.AddHours(-1)) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/style.css"] = (200, now) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Empty(result.ToUpload);
        Assert.Empty(result.RemoteOnly);
        Assert.Equal(1, result.UpToDate);
    }

    [Fact]
    public void Classify_LocalNewer_AddedToUpload()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/index.php"] = (100, now) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/index.php"] = (100, now.AddHours(-1)) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/index.php", result.ToUpload[0]);
    }

    [Fact]
    public void Classify_DifferentSize_AddedToUpload()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/app.js"] = (300, now.AddHours(-1)) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/app.js"] = (200, now.AddHours(-2)) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/app.js", result.ToUpload[0]);
    }

    [Fact]
    public void Classify_MixedFiles_AllGroupsCorrect()
    {
        var now = DateTime.UtcNow;
        var local = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/index.php"]  = (100, now),                  // new
            ["/style.css"]  = (200, now.AddHours(-1)),     // up to date
            ["/updated.js"] = (300, now),                  // local newer
        };
        var remote = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/style.css"]  = (200, now),                  // matches
            ["/updated.js"] = (300, now.AddHours(-1)),     // remote older
            ["/old.html"]   = (50,  now.AddDays(-10)),     // remote only
        };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Equal(2, result.ToUpload.Count);
        Assert.Contains("/index.php", result.ToUpload);
        Assert.Contains("/updated.js", result.ToUpload);
        Assert.Single(result.RemoteOnly);
        Assert.Equal("/old.html", result.RemoteOnly[0]);
        Assert.Equal(1, result.UpToDate);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
cd /home/atom/dev/Tower && dotnet test tests/Tower.Core.Tests/ --filter "WebsiteSyncTests"
```

Expected: Build error — `FtpSyncService` and `ScanResult` do not exist yet.

- [ ] **Step 3: Create FtpSyncService.cs**

Create `src/Tower.Core/Website/FtpSyncService.cs`:

```csharp
using FluentFTP;
using Microsoft.Extensions.Logging;
using Tower.Core.Settings;

namespace Tower.Core.Website;

public record ScanResult(
    List<string> ToUpload,
    List<string> RemoteOnly,
    int UpToDate
);

public class FtpSyncService(WebsiteOptions opts, SettingsService settings, ILogger<FtpSyncService> logger)
{
    public async Task<(bool ok, string? error)> TestConnectionAsync()
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            return (false, "FTP credentials not configured.");
        try
        {
            using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
            await ftp.Connect();
            await ftp.Disconnect();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<ScanResult> ScanAsync()
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            throw new InvalidOperationException("FTP credentials not configured.");

        using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
        await ftp.Connect();

        var remoteItems = await ftp.GetListing(opts.FtpRemotePath, FtpListOption.Recursive);
        var remoteFiles = remoteItems
            .Where(i => i.Type == FtpObjectType.File)
            .ToDictionary(
                i => NormalizePath(i.FullName[opts.FtpRemotePath.TrimEnd('/').Length..]),
                i => (size: i.Size, mtime: i.Modified.ToUniversalTime()));

        await ftp.Disconnect();

        var localFiles = Directory
            .GetFiles(opts.LocalPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => NormalizePath(f[opts.LocalPath.TrimEnd('/').Length..]),
                f => { var fi = new FileInfo(f); return (size: fi.Length, mtime: fi.LastWriteTimeUtc); });

        return Classify(localFiles, remoteFiles);
    }

    public async Task SyncAsync(
        IReadOnlyList<string> filesToUpload,
        IReadOnlyList<string> filesToDelete,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            throw new InvalidOperationException("FTP credentials not configured.");

        using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
        await ftp.Connect();

        foreach (var rel in filesToUpload)
        {
            ct.ThrowIfCancellationRequested();
            var localFile  = Path.Combine(opts.LocalPath.TrimEnd('/'), rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var remotePath = opts.FtpRemotePath.TrimEnd('/') + rel;
            try
            {
                await ftp.UploadFile(localFile, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
                progress.Report($"↑ {rel}");
            }
            catch (Exception ex)
            {
                progress.Report($"✗ upload failed {rel}: {ex.Message}");
                logger.LogWarning(ex, "Failed to upload {Path}", rel);
            }
        }

        foreach (var rel in filesToDelete)
        {
            ct.ThrowIfCancellationRequested();
            var remotePath = opts.FtpRemotePath.TrimEnd('/') + rel;
            try
            {
                await ftp.DeleteFile(remotePath);
                progress.Report($"✗ deleted {rel}");
            }
            catch (Exception ex)
            {
                progress.Report($"✗ delete failed {rel}: {ex.Message}");
                logger.LogWarning(ex, "Failed to delete {Path}", rel);
            }
        }

        await ftp.Disconnect();
    }

    public static ScanResult Classify(
        Dictionary<string, (long size, DateTime mtime)> localFiles,
        Dictionary<string, (long size, DateTime mtime)> remoteFiles)
    {
        var toUpload   = new List<string>();
        var remoteOnly = new List<string>();
        var upToDate   = 0;

        foreach (var (path, local) in localFiles)
        {
            if (!remoteFiles.TryGetValue(path, out var remote))
                toUpload.Add(path);
            else if (local.size != remote.size || local.mtime > remote.mtime)
                toUpload.Add(path);
            else
                upToDate++;
        }

        foreach (var path in remoteFiles.Keys)
            if (!localFiles.ContainsKey(path))
                remoteOnly.Add(path);

        toUpload.Sort();
        remoteOnly.Sort();

        return new ScanResult(toUpload, remoteOnly, upToDate);
    }

    private (string? user, string? pass) GetCredentials() =>
        (settings.Get("website.ftp_user"), settings.Get("website.ftp_pass"));

    private static string NormalizePath(string path) =>
        "/" + path.TrimStart('/').Replace('\\', '/');
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
cd /home/atom/dev/Tower && dotnet test tests/Tower.Core.Tests/ --filter "WebsiteSyncTests"
```

Expected: All 6 tests pass.

- [ ] **Step 5: Register FtpSyncService in Program.cs**

In the `// ── Website ──` block added in Task 1, add after the `AddSingleton(new WebsiteOptions {...})` line:

```csharp
builder.Services.AddScoped<FtpSyncService>();
```

- [ ] **Step 6: Build check**

```bash
cd /home/atom/dev/Tower && dotnet build src/Tower/Tower.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Tower.Core/Website/FtpSyncService.cs \
        tests/Tower.Core.Tests/WebsiteSyncTests.cs \
        src/Tower/Program.cs
git commit -m "Add FtpSyncService with scan/sync/classify logic and unit tests"
```

---

### Task 3: Website.razor page + CSS + nav entry

**Files:**
- Create: `src/Tower/Components/Pages/Website.razor`
- Create: `src/Tower/Components/Pages/Website.razor.css`
- Modify: `src/Tower/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `FtpSyncService` (Task 2), `SettingsService` (existing)
- Produces: Blazor page at `/website` with credentials + sync UI

- [ ] **Step 1: Create Website.razor**

Create `src/Tower/Components/Pages/Website.razor`:

```razor
@page "/website"
@using Tower.Core.Settings
@using Tower.Core.Website
@inject SettingsService SettingsSvc
@inject FtpSyncService FtpSvc

<PageTitle>Tower — Website</PageTitle>

<div class="website-page">

    <h1 class="page-title">Website</h1>

    @* ── Credentials ─────────────────────────────────────────────────────────── *@
    <section class="web-section">
        <div class="atom-section-block">
            <div class="atom-block-title">FTP Credentials</div>
            <div class="config-form">

                @* Username *@
                <div class="config-row">
                    <label class="config-label">FTP Username</label>
                    <div class="config-control">
                        @if (SettingsSvc.IsConfigured("website.ftp_user") && !_userReplacing)
                        {
                            <div class="key-configured-row">
                                <span class="val-green configured-badge">&#10003; Configured</span>
                                <button class="btn-secondary btn-sm" @onclick="() => _userReplacing = true">Replace</button>
                            </div>
                        }
                        else
                        {
                            <div class="key-input-row">
                                <input type="text" class="config-input config-input-wide"
                                       placeholder="FTP username…"
                                       @bind="_userValue" @bind:event="oninput" />
                                <button class="btn btn-primary btn-sm"
                                        disabled="@string.IsNullOrWhiteSpace(_userValue)"
                                        @onclick="SaveUser">Save</button>
                                @if (SettingsSvc.IsConfigured("website.ftp_user"))
                                {
                                    <button class="btn-secondary btn-sm"
                                            @onclick="() => { _userReplacing = false; _userValue = null; }">Cancel</button>
                                }
                            </div>
                            @if (_userSaved) { <span class="save-confirm">Saved &#10003;</span> }
                        }
                    </div>
                </div>

                @* Password *@
                <div class="config-row">
                    <label class="config-label">FTP Password</label>
                    <div class="config-control">
                        @if (SettingsSvc.IsConfigured("website.ftp_pass") && !_passReplacing)
                        {
                            <div class="key-configured-row">
                                <span class="val-green configured-badge">&#10003; Configured</span>
                                <button class="btn-secondary btn-sm" @onclick="() => _passReplacing = true">Replace</button>
                            </div>
                        }
                        else
                        {
                            <div class="key-input-row">
                                <input type="password" class="config-input config-input-wide"
                                       placeholder="FTP password…"
                                       @bind="_passValue" @bind:event="oninput" />
                                <button class="btn btn-primary btn-sm"
                                        disabled="@string.IsNullOrWhiteSpace(_passValue)"
                                        @onclick="SavePass">Save</button>
                                @if (SettingsSvc.IsConfigured("website.ftp_pass"))
                                {
                                    <button class="btn-secondary btn-sm"
                                            @onclick="() => { _passReplacing = false; _passValue = null; }">Cancel</button>
                                }
                            </div>
                            @if (_passSaved) { <span class="save-confirm">Saved &#10003;</span> }
                        }
                    </div>
                </div>

                @* Test connection (only when both configured) *@
                @if (_credentialsConfigured)
                {
                    <div class="config-row">
                        <label class="config-label"></label>
                        <div class="config-control">
                            <button class="btn-secondary btn-sm" @onclick="TestConnectionAsync" disabled="@_testing">
                                @(_testing ? "Testing…" : "Test Connection")
                            </button>
                            @if (_testResult is not null)
                            {
                                <span class="@(_testOk ? "val-green" : "val-red") web-test-result">
                                    @(_testOk ? "✓ Connected" : $"✗ {_testResult}")
                                </span>
                            }
                        </div>
                    </div>
                }

            </div>
        </div>
    </section>

    @* ── Sync (only when credentials configured) ─────────────────────────────── *@
    @if (_credentialsConfigured)
    {
        <section class="web-section">
            <div class="atom-section-block">
                <div class="atom-block-title">Sync</div>

                @if (_syncError is not null)
                {
                    <div class="val-red web-error">✗ @_syncError</div>
                }

                @if (_state == SyncState.Idle)
                {
                    <button class="btn btn-primary" @onclick="ScanAsync">Scan</button>
                }

                else if (_state == SyncState.Scanning)
                {
                    <div class="web-notice">Scanning remote files…</div>
                }

                else if (_state == SyncState.Preview && _scanResult is not null)
                {
                    @* To upload *@
                    <div class="web-group">
                        <div class="web-group-header">
                            <span class="web-group-label">To upload</span>
                            <span class="web-group-count @(_scanResult.ToUpload.Count > 0 ? "val-green" : "")">
                                @_scanResult.ToUpload.Count
                            </span>
                        </div>
                        @if (_scanResult.ToUpload.Count > 0)
                        {
                            <button class="btn-link web-toggle" @onclick="() => _showUpload = !_showUpload">
                                @(_showUpload ? "Hide files" : "Show files")
                            </button>
                            @if (_showUpload)
                            {
                                <div class="web-file-list">
                                    @foreach (var f in _scanResult.ToUpload)
                                    {
                                        <div class="web-file-item">@f</div>
                                    }
                                </div>
                            }
                        }
                    </div>

                    @* Remote only *@
                    <div class="web-group">
                        <div class="web-group-header">
                            <span class="web-group-label">Remote only</span>
                            <span class="web-group-count @(_scanResult.RemoteOnly.Count > 0 ? "val-amber" : "")">
                                @_scanResult.RemoteOnly.Count
                            </span>
                        </div>
                        @if (_scanResult.RemoteOnly.Count > 0)
                        {
                            <div class="web-file-list">
                                @foreach (var f in _scanResult.RemoteOnly)
                                {
                                    <label class="web-file-item web-file-check">
                                        <input type="checkbox" @bind="_deleteChecked[f]" />
                                        <span>@f</span>
                                    </label>
                                }
                            </div>
                            <div class="web-delete-hint">Check files above to delete them during sync.</div>
                        }
                    </div>

                    @* Up to date *@
                    <div class="web-group">
                        <div class="web-group-header">
                            <span class="web-group-label">Up to date</span>
                            <span class="web-group-count">@_scanResult.UpToDate</span>
                        </div>
                    </div>

                    <div class="web-sync-actions">
                        <button class="btn btn-primary"
                                disabled="@(_scanResult.ToUpload.Count == 0 && !_deleteChecked.Any(kv => kv.Value))"
                                @onclick="DoSyncAsync">
                            Sync now
                        </button>
                        <button class="btn-secondary btn-sm" @onclick="Reset">Scan again</button>
                    </div>
                }

                else if (_state == SyncState.Syncing)
                {
                    <div class="web-notice">Syncing…</div>
                    <pre class="maint-log web-sync-log">@_syncLog</pre>
                }

                else if (_state == SyncState.Done)
                {
                    <pre class="maint-log web-sync-log">@_syncLog</pre>
                    <div class="web-sync-actions">
                        <button class="btn-secondary btn-sm" @onclick="Reset">Scan again</button>
                    </div>
                }

            </div>
        </section>
    }

</div>

@code {
    enum SyncState { Idle, Scanning, Preview, Syncing, Done }

    // Credentials
    private string?  _userValue;
    private string?  _passValue;
    private bool     _userReplacing;
    private bool     _passReplacing;
    private bool     _userSaved;
    private bool     _passSaved;
    private bool     _credentialsConfigured;

    // Test connection
    private bool     _testing;
    private string?  _testResult;
    private bool     _testOk;

    // Sync state
    private SyncState              _state       = SyncState.Idle;
    private ScanResult?            _scanResult;
    private Dictionary<string, bool> _deleteChecked = new();
    private bool                   _showUpload;
    private string                 _syncLog     = "";
    private string?                _syncError;

    protected override void OnInitialized() => RefreshConfigState();

    private void RefreshConfigState()
    {
        _credentialsConfigured = SettingsSvc.IsConfigured("website.ftp_user")
                              && SettingsSvc.IsConfigured("website.ftp_pass");
    }

    private void SaveUser()
    {
        if (string.IsNullOrWhiteSpace(_userValue)) return;
        SettingsSvc.Set("website.ftp_user", _userValue);
        _userValue = null;
        _userReplacing = false;
        _userSaved = true;
        RefreshConfigState();
    }

    private void SavePass()
    {
        if (string.IsNullOrWhiteSpace(_passValue)) return;
        SettingsSvc.Set("website.ftp_pass", _passValue);
        _passValue = null;
        _passReplacing = false;
        _passSaved = true;
        RefreshConfigState();
    }

    private async Task TestConnectionAsync()
    {
        _testing = true;
        _testResult = null;
        await InvokeAsync(StateHasChanged);

        var (ok, err) = await FtpSvc.TestConnectionAsync();
        _testOk     = ok;
        _testResult = ok ? "Connected" : err;
        _testing    = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ScanAsync()
    {
        _state     = SyncState.Scanning;
        _syncError = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            _scanResult   = await FtpSvc.ScanAsync();
            _deleteChecked = _scanResult.RemoteOnly.ToDictionary(f => f, _ => false);
            _showUpload   = false;
            _state        = SyncState.Preview;
        }
        catch (Exception ex)
        {
            _syncError = ex.Message;
            _state     = SyncState.Idle;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task DoSyncAsync()
    {
        var toDelete = _deleteChecked.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

        _state   = SyncState.Syncing;
        _syncLog = "";
        await InvokeAsync(StateHasChanged);

        var progress = new Progress<string>(line =>
        {
            _syncLog += line + "\n";
            InvokeAsync(StateHasChanged);
        });

        try
        {
            await FtpSvc.SyncAsync(_scanResult!.ToUpload, toDelete, progress);
        }
        catch (Exception ex)
        {
            _syncLog += $"✗ {ex.Message}\n";
        }

        _state = SyncState.Done;
        await InvokeAsync(StateHasChanged);
    }

    private void Reset()
    {
        _state        = SyncState.Idle;
        _scanResult   = null;
        _deleteChecked = new();
        _showUpload   = false;
        _syncLog      = "";
        _syncError    = null;
        StateHasChanged();
    }
}
```

- [ ] **Step 2: Create Website.razor.css**

Create `src/Tower/Components/Pages/Website.razor.css`:

```css
.website-page {
    display: flex;
    flex-direction: column;
    gap: 2rem;
    max-width: 900px;
}

.web-section {
    display: flex;
    flex-direction: column;
}

/* ── Sync groups ─────────────────────────────────────────────────────────── */

.web-group {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.75rem 0;
    border-bottom: 1px solid var(--border);
}

.web-group:last-of-type {
    border-bottom: none;
}

.web-group-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
}

.web-group-label {
    font-size: 0.82rem;
    font-weight: 600;
    color: var(--text);
    text-transform: uppercase;
    letter-spacing: 0.04em;
}

.web-group-count {
    font-size: 0.85rem;
    font-weight: 700;
    font-family: var(--mono);
    color: var(--muted);
}

.web-toggle {
    font-size: 0.78rem;
    color: var(--accent);
    background: none;
    border: none;
    padding: 0;
    cursor: pointer;
    align-self: flex-start;
}

.web-toggle:hover { text-decoration: underline; }

/* ── File list ───────────────────────────────────────────────────────────── */

.web-file-list {
    display: flex;
    flex-direction: column;
    gap: 2px;
    max-height: 260px;
    overflow-y: auto;
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: var(--r-sm);
    padding: 0.4rem 0.6rem;
}

.web-file-item {
    font-size: 0.78rem;
    font-family: var(--mono);
    color: var(--text);
    line-height: 1.6;
    white-space: nowrap;
}

.web-file-check {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    cursor: pointer;
}

.web-file-check:hover { color: var(--text-strong); }

.web-delete-hint {
    font-size: 0.75rem;
    color: var(--muted);
}

/* ── Actions + misc ──────────────────────────────────────────────────────── */

.web-sync-actions {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding-top: 0.75rem;
}

.web-notice {
    font-size: 0.82rem;
    color: var(--muted);
    padding: 0.5rem 0;
}

.web-sync-log {
    max-height: 320px;
    overflow-y: auto;
    margin-top: 0.5rem;
}

.web-error {
    font-size: 0.82rem;
    margin-bottom: 0.75rem;
}

.web-test-result {
    font-size: 0.82rem;
    margin-left: 0.75rem;
}
```

- [ ] **Step 3: Add Website entry to NavMenu.razor**

In `src/Tower/Components/Layout/NavMenu.razor`, add after the Tuya `</NavLink>` block (around line 87) and before the Jellyfin block:

```razor
        <NavLink class="rail-link" href="/website" Match="NavLinkMatch.Prefix">
            <span class="rail-icon">
                <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="12" r="10"></circle>
                    <line x1="2" y1="12" x2="22" y2="12"></line>
                    <path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z"></path>
                </svg>
            </span>
            <span class="rail-label">Website</span>
        </NavLink>
```

- [ ] **Step 4: Build and verify**

```bash
cd /home/atom/dev/Tower && dotnet build src/Tower/Tower.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run full test suite**

```bash
cd /home/atom/dev/Tower && dotnet test tests/Tower.Core.Tests/
```

Expected: All tests pass.

- [ ] **Step 6: Deploy and verify in browser**

```bash
bash /home/atom/dev/Tower/deploy.sh
```

Navigate to Tower's URL and confirm:
- "Website" nav item appears in the rail with a globe icon
- Navigating to `/website` shows the page title and FTP Credentials block
- Entering and saving FTP username reveals the Sync section
- Entering and saving FTP password reveals "Test Connection" button

- [ ] **Step 7: Commit**

```bash
git add src/Tower/Components/Pages/Website.razor \
        src/Tower/Components/Pages/Website.razor.css \
        src/Tower/Components/Layout/NavMenu.razor
git commit -m "Add Website tab with FTP credential entry, scan preview, and sync"
git push origin HEAD
```
