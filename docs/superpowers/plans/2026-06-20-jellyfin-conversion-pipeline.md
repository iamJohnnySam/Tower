# Jellyfin Conversion Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When Jellyfin transcodes the same video 3+ times, Tower sends an interactive Telegram alert; the user marks it for conversion; Tower auto-converts it to H.264/AAC MKV during low-CPU windows; the converted file is staged for approval before replacing the original.

**Architecture:** A new `ConversionJob` DB table tracks state (Pending → Queued → Converting → AwaitingApproval → Approved/Rejected/Failed/Ignored). `ConversionService` owns the queue, ffmpeg execution, and approval logic. `ConversionScheduler` fires during low-CPU windows or sustained idle. `TelegramHub` gains a prefix-based callback dispatcher. `JellyfinWorker`'s alert is upgraded to send inline keyboards.

**Tech Stack:** .NET 10, EF Core (SQLite), xUnit v3, System.Diagnostics.Process for ffmpeg, Telegram Bot API inline keyboards (already integrated).

## Global Constraints

- Target framework: net10.0
- ffmpeg binary: `/usr/bin/ffmpeg`
- Conversion output: H.264 (libx264, CRF 20, preset medium) + AAC 192k + subtitle copy, MKV container
- Test staging folder: `/molecule/Media/ConversionTest` (configurable via `appsettings.json` `Tower:ConversionTestPath`)
- All DB tests use SQLite in-memory: `"DataSource=:memory:"` with `OpenConnection()` + `EnsureCreated()`
- Telegram callback data ≤ 64 bytes (all formats here are well under)
- One ffmpeg job at a time (no parallel conversions)
- Timeout per conversion job: 4 hours (14,400,000 ms)

---

### Task 1: ConversionJob model, ConversionOptions, and DbContext

**Files:**
- Create: `src/Tower.Core/Models/ConversionJob.cs`
- Modify: `src/Tower.Core/Data/TowerDbContext.cs`
- Modify: `src/Tower/TowerConfig.cs`
- Test: `tests/Tower.Core.Tests/ConversionJobTests.cs`

**Interfaces:**
- Produces: `ConversionStatus` enum, `ConversionJob` model, `TowerDbContext.ConversionJobs` DbSet, `TowerConfig.ConversionTestPath`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Tower.Core.Tests/ConversionJobTests.cs
using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;

namespace Tower.Core.Tests;

public class ConversionJobTests
{
    static TowerDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void Can_persist_and_read_conversion_job()
    {
        using var db = NewDb();
        var job = new ConversionJob
        {
            MediaId         = "abc-123",
            MediaName       = "The Dark Knight",
            OriginalPath    = "/molecule/Media/Movies/The Dark Knight (2008)/tdknight.mkv",
            Status          = ConversionStatus.Queued,
            TranscodeReasons = "VideoCodecNotSupported",
            CreatedAt       = new DateTime(2026, 6, 20, 3, 0, 0),
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();

        var loaded = db.ConversionJobs.Single(j => j.MediaId == "abc-123");
        Assert.Equal("The Dark Knight", loaded.MediaName);
        Assert.Equal(ConversionStatus.Queued, loaded.Status);
        Assert.Equal("/molecule/Media/Movies/The Dark Knight (2008)/tdknight.mkv", loaded.OriginalPath);
    }

    [Fact]
    public void MediaId_unique_index_prevents_duplicates()
    {
        using var db = NewDb();
        db.ConversionJobs.Add(new ConversionJob { MediaId = "dupe", MediaName = "A", Status = ConversionStatus.Pending, CreatedAt = DateTime.Now });
        db.SaveChanges();
        db.ConversionJobs.Add(new ConversionJob { MediaId = "dupe", MediaName = "B", Status = ConversionStatus.Pending, CreatedAt = DateTime.Now });
        Assert.ThrowsAny<Exception>(() => db.SaveChanges());
    }

    [Fact]
    public void All_status_values_defined()
    {
        var values = Enum.GetNames<ConversionStatus>();
        Assert.Contains("Pending", values);
        Assert.Contains("Queued", values);
        Assert.Contains("Converting", values);
        Assert.Contains("AwaitingApproval", values);
        Assert.Contains("Approved", values);
        Assert.Contains("Rejected", values);
        Assert.Contains("Failed", values);
        Assert.Contains("Ignored", values);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
cd /home/atom/dev/Tower
dotnet test tests/Tower.Core.Tests/ --filter "ConversionJobTests" 2>&1 | tail -10
```
Expected: build errors — `ConversionJob`, `ConversionStatus`, `ConversionJobs` not found.

- [ ] **Step 3: Create ConversionJob model**

```csharp
// src/Tower.Core/Models/ConversionJob.cs
namespace Tower.Core.Models;

public enum ConversionStatus
{
    Pending,          // alert sent to Telegram, awaiting user response
    Queued,           // user tapped "Convert", waiting for scheduler
    Converting,       // ffmpeg running
    AwaitingApproval, // ffmpeg done, test file ready
    Approved,         // user approved, original replaced
    Rejected,         // user rejected, test file deleted
    Failed,           // ffmpeg failed
    Ignored,          // user tapped "Ignore", no further alerts
}

public class ConversionJob
{
    public int Id { get; set; }
    public string MediaId { get; set; } = "";
    public string MediaName { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string? TestPath { get; set; }
    public ConversionStatus Status { get; set; }
    public string? TranscodeReasons { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int? AlertMessageId { get; set; }
}
```

- [ ] **Step 4: Add ConversionJobs to TowerDbContext**

In `src/Tower.Core/Data/TowerDbContext.cs`, add after the existing DbSets and extend `OnModelCreating`:

```csharp
public DbSet<ConversionJob> ConversionJobs => Set<ConversionJob>();
```

In `OnModelCreating`, add after the last `b.Entity<>()` block:

```csharp
b.Entity<ConversionJob>().HasIndex(x => x.MediaId).IsUnique();
```

The full updated file:
```csharp
using Microsoft.EntityFrameworkCore;
using Tower.Core.Models;
namespace Tower.Core.Data;
public class TowerDbContext(DbContextOptions<TowerDbContext> options) : DbContext(options) {
    public DbSet<PlayHistory> PlayHistory => Set<PlayHistory>();
    public DbSet<CpuProfileSlot> CpuProfile => Set<CpuProfileSlot>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<TelegramSubscriber> Subscribers => Set<TelegramSubscriber>();
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
    public DbSet<ProjectConfig> Projects => Set<ProjectConfig>();
    public DbSet<TuyaDevice>    TuyaDevices  => Set<TuyaDevice>();
    public DbSet<ConversionJob> ConversionJobs => Set<ConversionJob>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<CpuProfileSlot>().HasKey(x => x.Slot);
        b.Entity<CpuProfileSlot>().Property(x => x.Slot).ValueGeneratedNever();
        b.Entity<Setting>().HasKey(x => x.Key);
        b.Entity<Setting>().Property(x => x.Key).ValueGeneratedNever();
        b.Entity<TelegramSubscriber>().HasKey(x => x.ChatId);
        b.Entity<TelegramSubscriber>().Property(x => x.ChatId).ValueGeneratedNever();
        b.Entity<PlayHistory>().HasIndex(x => x.StartedAt);
        b.Entity<PlayHistory>().HasIndex(x => x.MediaName);
        b.Entity<TelegramMessage>().HasIndex(x => x.ChatId);
        b.Entity<ConversionJob>().HasIndex(x => x.MediaId).IsUnique();
    }
}
```

- [ ] **Step 5: Add ConversionTestPath to TowerConfig**

In `src/Tower/TowerConfig.cs`, add one property to `TowerConfig`:

```csharp
public string ConversionTestPath { get; set; } = "/molecule/Media/ConversionTest";
```

- [ ] **Step 6: Add to appsettings.json**

In `src/Tower/appsettings.json`, inside the `"Tower"` object add:

```json
"ConversionTestPath": "/molecule/Media/ConversionTest",
```

- [ ] **Step 7: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionJobTests" 2>&1 | tail -10
```
Expected: 3 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Tower.Core/Models/ConversionJob.cs src/Tower.Core/Data/TowerDbContext.cs src/Tower/TowerConfig.cs src/Tower/appsettings.json tests/Tower.Core.Tests/ConversionJobTests.cs
git commit -m "Add ConversionJob model, DbSet, and ConversionTestPath config"
```

---

### Task 2: JellyfinClient.GetItemPathAsync

**Files:**
- Modify: `src/Tower.Core/Jellyfin/JellyfinClient.cs`
- Test: `tests/Tower.Core.Tests/JellyfinParseTests.cs` (add cases)

**Interfaces:**
- Consumes: existing `JellyfinClient(HttpClient http)`
- Produces: `JellyfinClient.ParseItemPath(string json) → string?` (static, testable), `JellyfinClient.GetItemPathAsync(string baseUrl, string apiKey, string mediaId) → Task<string?>`

- [ ] **Step 1: Write the failing test**

Add to `tests/Tower.Core.Tests/JellyfinParseTests.cs`:

```csharp
[Fact]
public void ParseItemPath_returns_path_from_json()
{
    var json = """{"Id":"abc","Name":"The Dark Knight","Path":"/molecule/Media/Movies/The Dark Knight/tdknight.mkv"}""";
    var path = JellyfinClient.ParseItemPath(json);
    Assert.Equal("/molecule/Media/Movies/The Dark Knight/tdknight.mkv", path);
}

[Fact]
public void ParseItemPath_returns_null_for_missing_path()
{
    var json = """{"Id":"abc","Name":"The Dark Knight"}""";
    Assert.Null(JellyfinClient.ParseItemPath(json));
}

[Fact]
public void ParseItemPath_returns_null_for_malformed_json()
{
    Assert.Null(JellyfinClient.ParseItemPath("not json"));
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ParseItemPath" 2>&1 | tail -10
```
Expected: compile error — `ParseItemPath` not defined.

- [ ] **Step 3: Add ParseItemPath and GetItemPathAsync to JellyfinClient**

In `src/Tower.Core/Jellyfin/JellyfinClient.cs`, add after the existing `ParseSessions` method and before `SessionsAsync`:

```csharp
public static string? ParseItemPath(string json)
{
    try
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return node?["Path"]?.ToString();
    }
    catch { return null; }
}

public async Task<string?> GetItemPathAsync(string baseUrl, string apiKey, string mediaId)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var json = await http.GetStringAsync(
            $"{baseUrl}/Items/{mediaId}?api_key={apiKey}&Fields=Path", cts.Token);
        return ParseItemPath(json);
    }
    catch { return null; }
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ParseItemPath" 2>&1 | tail -10
```
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Jellyfin/JellyfinClient.cs tests/Tower.Core.Tests/JellyfinParseTests.cs
git commit -m "Add JellyfinClient.GetItemPathAsync for file path resolution"
```

---

### Task 3: TelegramHub callback dispatcher

**Files:**
- Modify: `src/Tower.Core/Telegram/TelegramHub.cs`
- Test: `tests/Tower.Core.Tests/TelegramCallbackDispatchTests.cs`

**Interfaces:**
- Produces: `TelegramHub.RegisterCallbackHandler(string prefix, Func<string, long, string, CancellationToken, Task> handler)` — registers a handler invoked when a callback's `Data` starts with `prefix`. First matching prefix wins. Handler signature: `(callbackData, chatId, callbackId, ct)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Tower.Core.Tests/TelegramCallbackDispatchTests.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tower.Core.Data;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Tests;

public class TelegramCallbackDispatchTests
{
    static TelegramHub BuildHub()
    {
        // Minimal DI for TelegramHub: needs IServiceScopeFactory, TelegramApi, LiveState, ILogger
        var services = new ServiceCollection();
        var dbOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite("DataSource=:memory:").Options;
        services.AddSingleton(dbOpts);
        services.AddScoped<TowerDbContext>(sp =>
        {
            var db = new TowerDbContext(sp.GetRequiredService<DbContextOptions<TowerDbContext>>());
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            return db;
        });
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        return new TelegramHub(
            sp.GetRequiredService<TelegramApi>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<LiveState>(),
            NullLogger<TelegramHub>.Instance);
    }

    [Fact]
    public async Task Registered_handler_is_invoked_for_matching_prefix()
    {
        var hub = BuildHub();
        string? captured = null;
        hub.RegisterCallbackHandler("conv:convert:", (data, chatId, cbId, ct) =>
        {
            captured = data;
            return Task.CompletedTask;
        });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Equal("conv:convert:42", captured);
    }

    [Fact]
    public async Task Non_matching_callback_does_not_invoke_handler()
    {
        var hub = BuildHub();
        bool invoked = false;
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked = true; return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "other:data", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.False(invoked);
    }

    [Fact]
    public async Task First_matching_prefix_wins_when_multiple_registered()
    {
        var hub = BuildHub();
        var invoked = new List<string>();
        hub.RegisterCallbackHandler("conv:", (_, _, _, _) => { invoked.Add("short"); return Task.CompletedTask; });
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked.Add("long"); return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Single(invoked);
        Assert.Equal("short", invoked[0]); // first registered wins
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "TelegramCallbackDispatchTests" 2>&1 | tail -10
```
Expected: compile error — `RegisterCallbackHandler` not defined.

- [ ] **Step 3: Add callback dispatcher to TelegramHub**

In `src/Tower.Core/Telegram/TelegramHub.cs`, add the handler registry field and registration method after the `_clients` dictionary field:

```csharp
// ── Callback dispatcher ───────────────────────────────────────────────────

private readonly List<(string Prefix, Func<string, long, string, CancellationToken, Task> Handler)> _callbackHandlers = new();

/// <summary>
/// Registers a handler invoked when an inbound callback's Data starts with <paramref name="prefix"/>.
/// The first registered matching prefix wins. Handler receives (callbackData, chatId, callbackId, ct).
/// </summary>
public void RegisterCallbackHandler(string prefix, Func<string, long, string, CancellationToken, Task> handler)
{
    _callbackHandlers.Add((prefix, handler));
}
```

In `HandleIncomingAsync`, add callback dispatch **after** the broadcast call (after `BroadcastUpdate(u);`):

```csharp
// 5. Dispatch to registered internal callback handlers
if (u.IsCallback && _callbackHandlers.Count > 0)
{
    foreach (var (prefix, handler) in _callbackHandlers)
    {
        if (u.CallbackData.StartsWith(prefix, StringComparison.Ordinal))
        {
            try { await handler(u.CallbackData, u.ChatId, u.CallbackId, ct); }
            catch (Exception ex) { logger.LogError(ex, "Callback handler failed for prefix {Prefix}", prefix); }
            break;
        }
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "TelegramCallbackDispatchTests" 2>&1 | tail -10
```
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Telegram/TelegramHub.cs tests/Tower.Core.Tests/TelegramCallbackDispatchTests.cs
git commit -m "Add callback prefix dispatcher to TelegramHub"
```

---

### Task 4: ConversionService — queue management and callback wiring

**Files:**
- Create: `src/Tower.Core/Conversion/ConversionService.cs`
- Test: `tests/Tower.Core.Tests/ConversionServiceTests.cs`

**Interfaces:**
- Consumes: `TowerDbContext.ConversionJobs`, `TelegramHub.RegisterCallbackHandler`, `TelegramHub.EditAsync`, `TelegramHub.AnswerCallbackAsync`, `TelegramHub.SendKeyboardAsync`, `JellyfinClient.GetItemPathAsync`, `SubscriberService.GetAdmin()`, `SettingsService.Get("jellyfin.api_key")`, `JellyfinOptions.JellyfinUrl`, `ConversionStatus` enum
- Produces:
  - `ConversionService(IServiceScopeFactory, TelegramHub, JellyfinOptions, IHttpClientFactory, string conversionTestPath)`
  - `Task SendAlertAsync(SessionInfo se, int transcodeCount, CancellationToken ct)` — resolves file path, creates Pending job, sends inline keyboard
  - `void RegisterCallbacks(TelegramHub hub)` — wires four prefixes
  - `bool IsConverting` — true while ffmpeg runs
  - `Task<bool> RunNextJobAsync(CancellationToken ct)` — picks oldest Queued job, runs ffmpeg (implemented in Task 5)

- [ ] **Step 1: Write failing tests for queue management**

```csharp
// tests/Tower.Core.Tests/ConversionServiceTests.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Tests;

public class ConversionServiceTests
{
    static (TowerDbContext db, IServiceScopeFactory scopes) BuildDb()
    {
        var services = new ServiceCollection();
        var dbOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite("DataSource=:memory:").Options;
        services.AddSingleton(dbOpts);
        services.AddScoped<TowerDbContext>(sp =>
        {
            var db = new TowerDbContext(sp.GetRequiredService<DbContextOptions<TowerDbContext>>());
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            return db;
        });
        services.AddScoped<SettingsService>();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        return (db, scopes);
    }

    static TelegramHub BuildHub(IServiceScopeFactory scopes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        return new TelegramHub(
            sp.GetRequiredService<TelegramApi>(),
            scopes,
            sp.GetRequiredService<LiveState>(),
            NullLogger<TelegramHub>.Instance);
    }

    [Fact]
    public async Task JobExistsForMedia_returns_true_when_pending_job_exists()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        db.ConversionJobs.Add(new ConversionJob
        {
            MediaId = "media-abc", MediaName = "Test", OriginalPath = "/tmp/test.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now
        });
        db.SaveChanges();

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        Assert.True(await svc.JobExistsForMediaAsync("media-abc"));
        Assert.False(await svc.JobExistsForMediaAsync("other-media"));
    }

    [Fact]
    public async Task HandleConvertCallback_updates_job_to_queued()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "media-xyz", MediaName = "Film", OriginalPath = "/tmp/film.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now, AlertMessageId = 99
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        // Invoke internal callback handler directly
        await svc.HandleConvertCallbackAsync($"conv:convert:{jobId}", 100L, "cb1", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Queued, updated.Status);
    }

    [Fact]
    public async Task HandleIgnoreCallback_updates_job_to_ignored()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "media-ign", MediaName = "Film2", OriginalPath = "/tmp/film2.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now, AlertMessageId = 88
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        await svc.HandleIgnoreCallbackAsync($"conv:ignore:{jobId}", 100L, "cb2", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Ignored, updated.Status);
    }
}

// Minimal IHttpClientFactory for tests (no real HTTP)
file class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionServiceTests" 2>&1 | tail -10
```
Expected: compile error — `ConversionService` not found.

- [ ] **Step 3: Create ConversionService with queue management**

```csharp
// src/Tower.Core/Conversion/ConversionService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.Telegram;

namespace Tower.Core.Conversion;

public class ConversionService(
    IServiceScopeFactory scopes,
    TelegramHub telegram,
    JellyfinOptions jellyfinOpts,
    IHttpClientFactory httpFactory,
    string conversionTestPath)
{
    private int _converting = 0;
    public bool IsConverting => _converting == 1;

    // ── Public query ──────────────────────────────────────────────────────────

    public async Task<bool> JobExistsForMediaAsync(string mediaId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        return await db.ConversionJobs.AnyAsync(j => j.MediaId == mediaId);
    }

    // ── Alert sender (called by JellyfinWorker) ───────────────────────────────

    /// <summary>
    /// Resolves the file path from Jellyfin, creates a Pending job, and sends
    /// an inline keyboard alert to the admin. No-ops if a job already exists.
    /// Falls back to plain text alert if the file path cannot be resolved.
    /// </summary>
    public async Task SendAlertAsync(
        string mediaId, string mediaName, string mediaLabel,
        string transcodeReasons, int transcodeCount,
        CancellationToken ct)
    {
        // Resolve admin chat
        long adminChatId;
        string apiKey;
        using (var scope = scopes.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var subscribers = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            var adminId = subscribers.GetAdmin();
            if (adminId is null) return;
            adminChatId = adminId.Value;
            apiKey = settings.Get("jellyfin.api_key") ?? "";
        }

        // Resolve file path
        string? filePath = null;
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(mediaId))
        {
            var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)));
            filePath = await client.GetItemPathAsync(jellyfinOpts.JellyfinUrl, apiKey, mediaId);
        }

        if (filePath is null)
        {
            // Fallback: plain text (no conversion option)
            var fallback = $"🔁 Repeatedly transcoded ({transcodeCount}×)\n{mediaLabel}\nReason: {transcodeReasons}\n\n(File path unresolvable — cannot offer conversion)";
            await telegram.SendAsync(TgAudience.Chat, adminChatId, fallback, null, ct);
            return;
        }

        // Create Pending job
        int jobId;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = new ConversionJob
            {
                MediaId          = mediaId,
                MediaName        = mediaName,
                OriginalPath     = filePath,
                Status           = ConversionStatus.Pending,
                TranscodeReasons = transcodeReasons,
                CreatedAt        = DateTime.Now,
            };
            db.ConversionJobs.Add(job);
            await db.SaveChangesAsync(ct);
            jobId = job.Id;
        }

        // Send inline keyboard
        var text = $"🔁 Repeatedly transcoded ({transcodeCount}×)\n{mediaLabel}\nReason: {transcodeReasons}\n\nWhat would you like to do?";
        var buttons = new List<IReadOnlyList<(string, string)>>
        {
            new List<(string, string)>
            {
                ("✅ Mark for conversion", $"conv:convert:{jobId}"),
                ("🚫 Ignore", $"conv:ignore:{jobId}"),
            }
        };

        var result = await telegram.SendKeyboardAsync(adminChatId, text, buttons, null, ct);

        // Store message_id so we can edit the message after user responds
        if (result.Ok && result.MessageId > 0)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is not null)
            {
                job.AlertMessageId = result.MessageId;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // ── Callback handlers (called by dispatcher) ──────────────────────────────

    public async Task HandleConvertCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data["conv:convert:".Length..], out int jobId)) return;

        int? alertMsgId = null;
        string mediaName = "";
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            job.Status = ConversionStatus.Queued;
            alertMsgId = job.AlertMessageId;
            mediaName = job.MediaName;
            await db.SaveChangesAsync(ct);
        }

        if (alertMsgId.HasValue)
            await telegram.EditAsync(chatId, alertMsgId.Value, $"✅ Queued for conversion — {mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public async Task HandleIgnoreCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data["conv:ignore:".Length..], out int jobId)) return;

        int? alertMsgId = null;
        string mediaName = "";
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            job.Status = ConversionStatus.Ignored;
            alertMsgId = job.AlertMessageId;
            mediaName = job.MediaName;
            await db.SaveChangesAsync(ct);
        }

        if (alertMsgId.HasValue)
            await telegram.EditAsync(chatId, alertMsgId.Value, $"🚫 Ignored — {mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public async Task HandleApproveCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:approve:{jobId}:{approvalMsgId}"
        var parts = data["conv:approve:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int approvalMsgId)) return;
        await ApproveAsync(jobId, chatId, approvalMsgId, callbackId, ct);
    }

    public async Task HandleRejectCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:reject:{jobId}:{approvalMsgId}"
        var parts = data["conv:reject:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int approvalMsgId)) return;
        await RejectAsync(jobId, chatId, approvalMsgId, callbackId, ct);
    }

    // ── Register all four prefixes into TelegramHub ───────────────────────────

    public void RegisterCallbacks(TelegramHub hub)
    {
        hub.RegisterCallbackHandler("conv:convert:", HandleConvertCallbackAsync);
        hub.RegisterCallbackHandler("conv:ignore:",  HandleIgnoreCallbackAsync);
        hub.RegisterCallbackHandler("conv:approve:", HandleApproveCallbackAsync);
        hub.RegisterCallbackHandler("conv:reject:",  HandleRejectCallbackAsync);
    }

    // ── ffmpeg execution (implemented in Task 5) ──────────────────────────────

    public Task<bool> RunNextJobAsync(CancellationToken ct) => Task.FromResult(false); // stub

    // ── Approval / rejection (implemented in Task 5) ──────────────────────────

    private Task ApproveAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct) => Task.CompletedTask; // stub
    private Task RejectAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct) => Task.CompletedTask; // stub
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionServiceTests" 2>&1 | tail -10
```
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Conversion/ConversionService.cs tests/Tower.Core.Tests/ConversionServiceTests.cs
git commit -m "Add ConversionService with queue management and callback wiring"
```

---

### Task 5: ConversionService — ffmpeg execution, approve, and reject

**Files:**
- Modify: `src/Tower.Core/Conversion/ConversionService.cs`
- Test: `tests/Tower.Core.Tests/ConversionServiceTests.cs` (add cases)

**Interfaces:**
- Consumes: `ConversionJob.TestPath`, `ConversionJob.Status`, `ConversionJob.OriginalPath`, all from Task 1
- Produces:
  - `Task<bool> RunNextJobAsync(CancellationToken ct)` — returns true if a job was started, false if none queued or already converting
  - `Task ApproveAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)`
  - `Task RejectAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)`

- [ ] **Step 1: Write failing tests for approve and reject**

Add to `tests/Tower.Core.Tests/ConversionServiceTests.cs`:

```csharp
[Fact]
public async Task ApproveAsync_moves_test_file_to_original_path()
{
    var (_, scopes) = BuildDb();
    var tmpDir = Path.GetTempPath();
    var testFile = Path.Combine(tmpDir, $"conv_test_{Guid.NewGuid()}.mkv");
    var origFile = Path.Combine(tmpDir, $"conv_orig_{Guid.NewGuid()}.mkv");
    await File.WriteAllTextAsync(testFile, "fake-video-data");

    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
    var job = new ConversionJob
    {
        MediaId = "m1", MediaName = "Film", OriginalPath = origFile,
        TestPath = testFile, Status = ConversionStatus.AwaitingApproval,
        CreatedAt = DateTime.Now
    };
    db.ConversionJobs.Add(job);
    db.SaveChanges();
    int jobId = job.Id;

    var hub = BuildHub(scopes);
    var svc = new ConversionService(scopes, hub,
        new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
        new TestHttpClientFactory(), tmpDir);

    await svc.HandleApproveCallbackAsync($"conv:approve:{jobId}:777", 100L, "cb3", CancellationToken.None);

    using var scope2 = scopes.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
    var updated = db2.ConversionJobs.Find(jobId)!;
    Assert.Equal(ConversionStatus.Approved, updated.Status);
    Assert.True(File.Exists(origFile));
    Assert.False(File.Exists(testFile));

    // cleanup
    File.Delete(origFile);
}

[Fact]
public async Task RejectAsync_deletes_test_file()
{
    var (_, scopes) = BuildDb();
    var tmpDir = Path.GetTempPath();
    var testFile = Path.Combine(tmpDir, $"conv_reject_{Guid.NewGuid()}.mkv");
    await File.WriteAllTextAsync(testFile, "fake-video-data");

    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
    var job = new ConversionJob
    {
        MediaId = "m2", MediaName = "Film2", OriginalPath = "/tmp/orig.mkv",
        TestPath = testFile, Status = ConversionStatus.AwaitingApproval,
        CreatedAt = DateTime.Now
    };
    db.ConversionJobs.Add(job);
    db.SaveChanges();
    int jobId = job.Id;

    var hub = BuildHub(scopes);
    var svc = new ConversionService(scopes, hub,
        new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
        new TestHttpClientFactory(), tmpDir);

    await svc.HandleRejectCallbackAsync($"conv:reject:{jobId}:888", 100L, "cb4", CancellationToken.None);

    using var scope2 = scopes.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
    var updated = db2.ConversionJobs.Find(jobId)!;
    Assert.Equal(ConversionStatus.Rejected, updated.Status);
    Assert.False(File.Exists(testFile));
}

[Fact]
public async Task RunNextJobAsync_returns_false_when_no_queued_jobs()
{
    var (_, scopes) = BuildDb();
    var hub = BuildHub(scopes);
    var svc = new ConversionService(scopes, hub,
        new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
        new TestHttpClientFactory(), "/tmp/conv-test");

    var result = await svc.RunNextJobAsync(CancellationToken.None);
    Assert.False(result);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionServiceTests.ApproveAsync|RejectAsync|RunNextJobAsync" 2>&1 | tail -10
```
Expected: 3 tests fail (stubs return early without doing anything).

- [ ] **Step 3: Implement ApproveAsync, RejectAsync, and RunNextJobAsync**

Replace the three stub methods in `src/Tower.Core/Conversion/ConversionService.cs`:

```csharp
// ── Approval / rejection ──────────────────────────────────────────────────

private async Task ApproveAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)
{
    string? testPath = null, originalPath = null, mediaName = null;
    using (var scope = scopes.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = await db.ConversionJobs.FindAsync(jobId);
        if (job is null) return;
        testPath     = job.TestPath;
        originalPath = job.OriginalPath;
        mediaName    = job.MediaName;
        job.Status   = ConversionStatus.Approved;
        job.CompletedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    try
    {
        if (testPath is not null && originalPath is not null && File.Exists(testPath))
            File.Move(testPath, originalPath, overwrite: true);
    }
    catch (Exception ex)
    {
        await telegram.EditAsync(chatId, approvalMsgId, $"❌ Approve failed — {mediaName}\n{ex.Message}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
        return;
    }

    await telegram.EditAsync(chatId, approvalMsgId, $"✅ Approved — original replaced\n{mediaName}", null, null, ct);
    await telegram.AnswerCallbackAsync(callbackId, null, ct);
}

private async Task RejectAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)
{
    string? testPath = null, mediaName = null;
    using (var scope = scopes.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = await db.ConversionJobs.FindAsync(jobId);
        if (job is null) return;
        testPath     = job.TestPath;
        mediaName    = job.MediaName;
        job.Status   = ConversionStatus.Rejected;
        job.CompletedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    try
    {
        if (testPath is not null && File.Exists(testPath))
            File.Delete(testPath);
    }
    catch { /* best effort */ }

    await telegram.EditAsync(chatId, approvalMsgId, $"❌ Rejected — test file deleted\n{mediaName}", null, null, ct);
    await telegram.AnswerCallbackAsync(callbackId, null, ct);
}

// ── ffmpeg execution ──────────────────────────────────────────────────────

public async Task<bool> RunNextJobAsync(CancellationToken ct)
{
    // Only one job at a time
    if (System.Threading.Interlocked.CompareExchange(ref _converting, 1, 0) != 0) return false;

    try
    {
        // Pick oldest queued job
        ConversionJob? job;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            job = await db.ConversionJobs
                .Where(j => j.Status == ConversionStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (job is null) return false;

            // Verify source file exists
            if (!File.Exists(job.OriginalPath))
            {
                job.Status = ConversionStatus.Failed;
                job.ErrorMessage = "Source file not found";
                job.CompletedAt = DateTime.Now;
                await db.SaveChangesAsync(ct);
                return false;
            }

            // Build output path: {testDir}/{id}_{nameWithoutExt}.mkv
            Directory.CreateDirectory(conversionTestPath);
            var nameNoExt = Path.GetFileNameWithoutExtension(job.OriginalPath);
            var testFileName = $"{job.Id}_{nameNoExt}.mkv";
            job.TestPath   = Path.Combine(conversionTestPath, testFileName);
            job.Status     = ConversionStatus.Converting;
            job.StartedAt  = DateTime.Now;
            await db.SaveChangesAsync(ct);
        }

        // Run ffmpeg in Task.Run to avoid blocking the scheduler thread
        await Task.Run(async () =>
        {
            string? stderr = null;
            int exitCode = -1;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/ffmpeg",
                    $"-i \"{job.OriginalPath}\" -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k -c:s copy -map 0 -y \"{job.TestPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                };
                using var proc = System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start ffmpeg");

                var stderrTask = proc.StandardError.ReadToEndAsync();
                bool finished = proc.WaitForExit(14_400_000); // 4 hours
                stderr = await stderrTask;
                exitCode = finished ? proc.ExitCode : -1;
                if (!finished) try { proc.Kill(entireProcessTree: true); } catch { }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                exitCode = -1;
            }

            if (exitCode == 0)
            {
                await MarkAwaitingApprovalAsync(job, ct);
            }
            else
            {
                var snippet = stderr is null ? "unknown error"
                    : stderr.Length <= 500 ? stderr
                    : "…" + stderr[^500..];
                await MarkFailedAsync(job, snippet, ct);
            }
        }, ct);

        return true;
    }
    finally
    {
        System.Threading.Interlocked.Exchange(ref _converting, 0);
    }
}

private async Task MarkAwaitingApprovalAsync(ConversionJob job, CancellationToken ct)
{
    long adminChatId = 0;
    using (var scope = scopes.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var loaded = await db.ConversionJobs.FindAsync(job.Id);
        if (loaded is null) return;
        loaded.Status = ConversionStatus.AwaitingApproval;
        loaded.CompletedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
        adminChatId = subs.GetAdmin() ?? 0;
    }
    if (adminChatId == 0) return;

    var text = $"✅ Conversion complete\n{job.MediaName}\nTest file ready.\n\nAdd ConversionTest/ as a Jellyfin library to verify playback, then:";
    var buttons = new List<IReadOnlyList<(string, string)>>();
    // We don't know the approval message_id yet — we embed it in callback data after send
    // Use a placeholder send first, then edit with correct msgId embedded in buttons
    var sent = await telegram.SendKeyboardAsync(adminChatId, text,
        new List<IReadOnlyList<(string, string)>>
        {
            new List<(string, string)>
            {
                ($"✅ Approve — replace original", $"conv:approve:{job.Id}:0"),
                ($"❌ Reject — delete test file", $"conv:reject:{job.Id}:0"),
            }
        }, null, ct);

    if (sent.Ok && sent.MessageId > 0)
    {
        // Re-send with correct message_id embedded in callback data
        await telegram.EditAsync(adminChatId, sent.MessageId, text,
            new List<IReadOnlyList<(string, string)>>
            {
                new List<(string, string)>
                {
                    ($"✅ Approve — replace original", $"conv:approve:{job.Id}:{sent.MessageId}"),
                    ($"❌ Reject — delete test file",  $"conv:reject:{job.Id}:{sent.MessageId}"),
                }
            }, null, ct);
    }
}

private async Task MarkFailedAsync(ConversionJob job, string error, CancellationToken ct)
{
    long adminChatId = 0;
    using (var scope = scopes.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var loaded = await db.ConversionJobs.FindAsync(job.Id);
        if (loaded is null) return;
        loaded.Status = ConversionStatus.Failed;
        loaded.ErrorMessage = error;
        loaded.CompletedAt  = DateTime.Now;
        await db.SaveChangesAsync(ct);

        var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
        adminChatId = subs.GetAdmin() ?? 0;
    }
    if (adminChatId == 0) return;

    var snippet = error.Length > 300 ? error[..300] + "…" : error;
    await telegram.SendAsync(TgAudience.Chat, adminChatId,
        $"❌ Conversion failed — {job.MediaName}\n{snippet}", null, ct);
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionServiceTests" 2>&1 | tail -10
```
Expected: all 6 ConversionServiceTests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Conversion/ConversionService.cs tests/Tower.Core.Tests/ConversionServiceTests.cs
git commit -m "Implement ConversionService ffmpeg execution, approve, and reject"
```

---

### Task 6: JellyfinWorker alert upgrade

**Files:**
- Modify: `src/Tower.Core/Workers/JellyfinWorker.cs`

**Interfaces:**
- Consumes: `ConversionService.JobExistsForMediaAsync(string mediaId)`, `ConversionService.SendAlertAsync(...)`, all from Task 4
- Produces: `JellyfinWorker` with `ConversionService` injected; `AlertIfProblematicAsync` sends inline keyboard via `ConversionService.SendAlertAsync` instead of plain text

- [ ] **Step 1: Update JellyfinWorker**

Replace the full `src/Tower.Core/Workers/JellyfinWorker.cs` with:

```csharp
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Workers;

public class JellyfinWorker(
    LiveState state,
    IHttpClientFactory httpFactory,
    IServiceScopeFactory scopes,
    JellyfinOptions opts,
    TelegramHub telegram,
    ConversionService conversion) : BackgroundService
{
    private readonly Dictionary<string, string> _prevPlaying = new();
    private readonly HashSet<string> _alertedMedia = new();
    private readonly HashSet<string> _alertedRepeat = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (count, cpu) = FfmpegStats.Collect();

                string apiKey;
                using (var scope = scopes.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    apiKey = settings.Get("jellyfin.api_key") ?? "";
                }

                List<SessionInfo> sessions = new();
                string err = "";
                bool configured = !string.IsNullOrEmpty(apiKey);

                if (configured)
                {
                    var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)));
                    var fetched = await client.SessionsAsync(opts.JellyfinUrl, apiKey);
                    if (fetched is null)
                    {
                        err = "Jellyfin unreachable";
                    }
                    else
                    {
                        sessions = fetched;

                        var newPrev = new Dictionary<string, string>();
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

                        foreach (var se in sessions)
                        {
                            if (!se.Playing) continue;
                            var itemId = string.IsNullOrEmpty(se.MediaId) ? se.Media : se.MediaId;
                            newPrev[se.SessionId] = itemId;

                            bool isNew = !_prevPlaying.TryGetValue(se.SessionId, out var prev) || prev != itemId;
                            if (isNew)
                            {
                                db.PlayHistory.Add(MapPlay(se));
                                await AlertIfProblematicAsync(se, ct);
                            }
                        }

                        if (db.ChangeTracker.HasChanges())
                            await db.SaveChangesAsync(ct);

                        _prevPlaying.Clear();
                        foreach (var kv in newPrev)
                            _prevPlaying[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    _prevPlaying.Clear();
                }

                state.PushFfmpegHistory(count, cpu);
                var (countHist, cpuHist) = state.SnapshotFfmpegHistory();
                state.SetJellyfin(new JellyfinSnapshot(
                    Sessions: sessions,
                    FfmpegCount: count,
                    FfmpegCpu: cpu,
                    FfmpegCountHistory: countHist,
                    FfmpegCpuHistory: cpuHist,
                    Error: err,
                    ApiConfigured: configured,
                    Updated: DateTime.Now));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[jellyfin] {ex.Message}");
                var (countHist, cpuHist) = state.SnapshotFfmpegHistory();
                state.SetJellyfin(new JellyfinSnapshot(
                    Sessions: Array.Empty<SessionInfo>(),
                    FfmpegCount: 0, FfmpegCpu: 0,
                    FfmpegCountHistory: countHist, FfmpegCpuHistory: cpuHist,
                    Error: $"worker error: {ex.Message}",
                    ApiConfigured: state.Jellyfin.ApiConfigured,
                    Updated: DateTime.Now));
            }

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AlertIfProblematicAsync(SessionInfo se, CancellationToken ct)
    {
        if (!se.Method.Equals("Transcode", StringComparison.OrdinalIgnoreCase)) return;

        var mediaId = string.IsNullOrEmpty(se.MediaId) ? se.Media : se.MediaId;

        // Skip if already in conversion queue (any status)
        if (await conversion.JobExistsForMediaAsync(mediaId)) return;

        // Count previous transcode plays
        int prevTranscodes;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            prevTranscodes = string.IsNullOrEmpty(se.MediaId)
                ? db.PlayHistory.Count(p => p.MediaName == se.Media && p.PlayMethod == "Transcode")
                : db.PlayHistory.Count(p => p.MediaId == mediaId && p.PlayMethod == "Transcode");
        }

        // Send interactive alert on 3rd+ transcode
        if (prevTranscodes >= 2 && _alertedRepeat.Add(mediaId))
        {
            var reasons = string.Join(", ", se.TranscodeReasons.DefaultIfEmpty("Unknown"));
            await conversion.SendAlertAsync(
                mediaId:          mediaId,
                mediaName:        string.IsNullOrEmpty(se.SeriesName) ? se.Media : se.SeriesName,
                mediaLabel:       BuildTitle(se),
                transcodeReasons: reasons,
                transcodeCount:   prevTranscodes + 1,
                ct:               ct);
            return;
        }

        // First-play alert for HEVC 10-bit (plain text — not a conversion candidate yet)
        bool isHevc10bit = se.VideoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                           && se.VideoBitDepth == 10;
        if (isHevc10bit && _alertedMedia.Add(mediaId))
        {
            var msg = $"⚠️ HEVC 10-bit transcode\n{BuildTitle(se)}\n\nThis file requires live CPU transcoding.";
            await telegram.SendAsync(TgAudience.Admin, 0, msg, null, ct);
        }
    }

    private static string BuildTitle(SessionInfo se) =>
        string.IsNullOrEmpty(se.SeriesName)
            ? se.Media
            : $"{se.SeriesName} S{se.SeasonNumber:D2}E{se.EpisodeNumber:D2} — {se.Media}";

    private static PlayHistory MapPlay(SessionInfo s) => new()
    {
        StartedAt        = DateTime.Now,
        SessionKey       = s.SessionId,
        MediaId          = s.MediaId,
        MediaName        = string.IsNullOrEmpty(s.Media) ? "Unknown" : s.Media,
        MediaType        = s.MediaType,
        SeriesName       = s.SeriesName,
        SeasonNumber     = s.SeasonNumber,
        EpisodeNumber    = s.EpisodeNumber,
        UserName         = s.User,
        PlayMethod       = s.Method,
        TranscodeReasons = string.Join(",", s.TranscodeReasons),
        VideoCodec       = s.VideoCodec,
        AudioCodec       = s.AudioCodec,
        Container        = s.Container,
        ClientName       = s.Client,
        DeviceName       = s.Device,
    };
}
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build src/Tower.Core/Tower.Core.csproj 2>&1 | tail -10
```
Expected: Build succeeded.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test tests/Tower.Core.Tests/ 2>&1 | tail -10
```
Expected: all existing tests still pass.

- [ ] **Step 4: Commit**

```bash
git add src/Tower.Core/Workers/JellyfinWorker.cs
git commit -m "Upgrade JellyfinWorker: inline keyboard alert with conversion option"
```

---

### Task 7: ConversionScheduler

**Files:**
- Create: `src/Tower.Core/Workers/ConversionScheduler.cs`
- Test: `tests/Tower.Core.Tests/ConversionSchedulerTests.cs`

**Interfaces:**
- Consumes: `ConversionService.IsConverting`, `ConversionService.RunNextJobAsync`, `CpuProfile.BestWindow`, `CpuProfileRecorder.LoadFromDb`, `LiveState.Stats.CpuPct`
- Produces: `ConversionScheduler(ConversionService, IServiceScopeFactory, LiveState)` BackgroundService

- [ ] **Step 1: Write failing tests**

```csharp
// tests/Tower.Core.Tests/ConversionSchedulerTests.cs
using Tower.Core.Workers;

namespace Tower.Core.Tests;

public class ConversionSchedulerTests
{
    [Theory]
    [InlineData(0, false)]   // not in window, cpu high
    [InlineData(14, true)]   // 14 consecutive idle ticks = opportunistic
    [InlineData(15, true)]   // 15 idle ticks = opportunistic
    public void ShouldFire_opportunistic_when_idle_ticks_reach_15(int idleTicks, bool expected)
    {
        Assert.Equal(expected, ConversionScheduler.IsOpportunistic(idleTicks));
    }

    [Fact]
    public void ShouldFire_window_when_in_target_hour_and_first_10_min()
    {
        Assert.True(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 5));
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 10));
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 4, currentMinute: 0));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionSchedulerTests" 2>&1 | tail -10
```
Expected: compile error — `ConversionScheduler` not found.

- [ ] **Step 3: Create ConversionScheduler**

```csharp
// src/Tower.Core/Workers/ConversionScheduler.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Maintenance;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class ConversionScheduler(
    ConversionService conversion,
    IServiceScopeFactory scopes,
    LiveState state) : BackgroundService
{
    private int _idleTicks = 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60_000, ct);

                // Track consecutive idle ticks
                if (state.Stats.CpuPct < 30.0)
                    _idleTicks++;
                else
                    _idleTicks = 0;

                if (conversion.IsConverting) continue;

                var now = DateTime.Now;
                bool shouldFire = false;

                // Window condition: lowest-CPU hour from weekly profile
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                    var (cpu, samples) = CpuProfileRecorder.LoadFromDb(db);
                    int window = CpuProfile.BestWindow(cpu, samples);
                    shouldFire = IsInWindow(window, now.Hour, now.Minute);
                }

                // Opportunistic: 15 consecutive minutes of low CPU
                if (!shouldFire)
                    shouldFire = IsOpportunistic(_idleTicks);

                if (shouldFire)
                    _ = Task.Run(() => conversion.RunNextJobAsync(ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[ConversionScheduler] {ex.Message}");
            }
        }
    }

    public static bool IsInWindow(int targetHour, int currentHour, int currentMinute) =>
        currentHour == targetHour && currentMinute < 10;

    public static bool IsOpportunistic(int idleTicks) => idleTicks >= 15;
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Tower.Core.Tests/ --filter "ConversionSchedulerTests" 2>&1 | tail -10
```
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Workers/ConversionScheduler.cs tests/Tower.Core.Tests/ConversionSchedulerTests.cs
git commit -m "Add ConversionScheduler with window and opportunistic CPU triggers"
```

---

### Task 8: Program.cs wiring

**Files:**
- Modify: `src/Tower/Program.cs`

**Interfaces:**
- Consumes: `ConversionService(IServiceScopeFactory, TelegramHub, JellyfinOptions, IHttpClientFactory, string)`, `ConversionScheduler(ConversionService, IServiceScopeFactory, LiveState)`, `TowerConfig.ConversionTestPath`, `ConversionService.RegisterCallbacks(TelegramHub)`
- Produces: all services registered; `JellyfinWorker` injected with `ConversionService`; callbacks wired on startup

- [ ] **Step 1: Update Program.cs**

Add `using Tower.Core.Conversion;` to the usings block at the top.

In the `// ── Jellyfin ──` section, replace:

```csharp
// ── Jellyfin ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new JellyfinOptions { JellyfinUrl = towerCfg.JellyfinUrl });
builder.Services.AddHttpClient<JellyfinClient>();
builder.Services.AddScoped<JellyfinStats>();
```

with:

```csharp
// ── Jellyfin ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new JellyfinOptions { JellyfinUrl = towerCfg.JellyfinUrl });
builder.Services.AddHttpClient<JellyfinClient>();
builder.Services.AddScoped<JellyfinStats>();
builder.Services.AddSingleton(sp => new ConversionService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TelegramHub>(),
    sp.GetRequiredService<JellyfinOptions>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    towerCfg.ConversionTestPath));
```

In the `// ── Background workers ──` section, add after `AddHostedService<MaintenanceScheduler>()`:

```csharp
builder.Services.AddHostedService<ConversionScheduler>();
```

After `db.Database.EnsureCreated();` in the startup block, add:

```csharp
// Wire Telegram callbacks for conversion pipeline
var convSvc = app.Services.GetRequiredService<ConversionService>();
var telegramHub = app.Services.GetRequiredService<TelegramHub>();
convSvc.RegisterCallbacks(telegramHub);
```

- [ ] **Step 2: Verify it builds**

```bash
dotnet build src/Tower/Tower.csproj 2>&1 | tail -15
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test tests/Tower.Core.Tests/ 2>&1 | tail -10
```
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Tower/Program.cs
git commit -m "Wire ConversionService and ConversionScheduler into Program.cs"
```

---

### Task 9: Jellyfin page — Conversion Queue UI

**Files:**
- Modify: `src/Tower/Components/Pages/Jellyfin.razor`
- Modify: `src/Tower.Core/Jellyfin/JellyfinStats.cs`

**Interfaces:**
- Consumes: `TowerDbContext.ConversionJobs`, `ConversionJob`, `ConversionStatus`
- Produces: `JellyfinStats.ConversionQueue()` returning `List<ConversionJob>` (newest first); Jellyfin page shows conversion queue section with status badges, Re-queue button for Failed, Re-enable button for Ignored

- [ ] **Step 1: Add ConversionQueue query to JellyfinStats**

In `src/Tower.Core/Jellyfin/JellyfinStats.cs`, add:

```csharp
using Tower.Core.Models;
```

And add the method after `Recent`:

```csharp
public List<ConversionJob> ConversionQueue() =>
    db.ConversionJobs
      .OrderByDescending(j => j.CreatedAt)
      .ToList();

public void RequeueJob(int jobId)
{
    var job = db.ConversionJobs.Find(jobId);
    if (job is null) return;
    job.Status       = ConversionStatus.Queued;
    job.StartedAt    = null;
    job.CompletedAt  = null;
    job.ErrorMessage = null;
    job.TestPath     = null;
    db.SaveChanges();
}

public void DeleteJob(int jobId)
{
    var job = db.ConversionJobs.Find(jobId);
    if (job is null) return;
    db.ConversionJobs.Remove(job);
    db.SaveChanges();
}
```

- [ ] **Step 2: Add Conversion Queue section to Jellyfin.razor**

In `src/Tower/Components/Pages/Jellyfin.razor`, add after the `</section>` closing tag of "Watch Analytics" (before the `</div>` that closes `jellyfin-page`):

```razor
    @* ── 4. Conversion Queue ─────────────────────────────────────────────────── *@
    <section class="jf-section">
        <h2 class="section-title">
            Conversion Queue
            <button class="btn-secondary btn-sm jf-refresh-btn" @onclick="RefreshConversionQueue">Refresh</button>
        </h2>

        @if (_convQueueLoading)
        {
            <div class="jf-notice jf-notice-info">Loading…</div>
        }
        else if (_convQueue.Count == 0)
        {
            <div class="jf-notice jf-notice-idle">No conversion jobs yet. Jobs appear here when you mark a file for conversion via Telegram.</div>
        }
        else
        {
            <table class="atom-table">
                <thead>
                    <tr><th>Media</th><th>Status</th><th>Created</th><th>Completed</th><th>Actions</th></tr>
                </thead>
                <tbody>
                    @foreach (var job in _convQueue)
                    {
                        <tr>
                            <td>@job.MediaName</td>
                            <td><span class="@ConvStatusClass(job.Status)">@job.Status</span></td>
                            <td class="jf-ts">@job.CreatedAt.ToLocalTime().ToString("MM/dd HH:mm")</td>
                            <td class="jf-ts">@(job.CompletedAt.HasValue ? job.CompletedAt.Value.ToLocalTime().ToString("MM/dd HH:mm") : "—")</td>
                            <td>
                                @if (job.Status == Tower.Core.Models.ConversionStatus.Failed)
                                {
                                    <button class="btn-secondary btn-sm" @onclick="() => RequeueJob(job.Id)">Re-queue</button>
                                }
                                @if (job.Status == Tower.Core.Models.ConversionStatus.Ignored)
                                {
                                    <button class="btn-secondary btn-sm" @onclick="() => ReenableJob(job.Id)">Re-enable alerts</button>
                                }
                            </td>
                        </tr>
                        @if (job.Status == Tower.Core.Models.ConversionStatus.Failed && !string.IsNullOrEmpty(job.ErrorMessage))
                        {
                            <tr>
                                <td colspan="5" class="jf-error-detail">@job.ErrorMessage</td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        }
    </section>
```

- [ ] **Step 3: Add code-behind fields and methods to the @code block**

In the `@code { ... }` block, add the following fields alongside the existing analytics fields:

```csharp
    private bool _convQueueLoading = true;
    private List<Tower.Core.Models.ConversionJob> _convQueue = new();
```

Add these methods inside the `@code` block:

```csharp
    private async Task LoadConversionQueueAsync()
    {
        _convQueueLoading = true;
        _convQueue = await Task.Run(() => JellyfinStatsSvc.ConversionQueue());
        _convQueueLoading = false;
    }

    private async Task RefreshConversionQueue()
    {
        await LoadConversionQueueAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task RequeueJob(int jobId)
    {
        await Task.Run(() => JellyfinStatsSvc.RequeueJob(jobId));
        await RefreshConversionQueue();
    }

    private async Task ReenableJob(int jobId)
    {
        await Task.Run(() => JellyfinStatsSvc.DeleteJob(jobId));
        await RefreshConversionQueue();
    }

    static string ConvStatusClass(Tower.Core.Models.ConversionStatus s) => s switch
    {
        Tower.Core.Models.ConversionStatus.Queued           => "val-amber",
        Tower.Core.Models.ConversionStatus.Converting       => "val-amber",
        Tower.Core.Models.ConversionStatus.AwaitingApproval => "jf-method-badge val-green",
        Tower.Core.Models.ConversionStatus.Approved         => "val-green",
        Tower.Core.Models.ConversionStatus.Failed           => "val-red",
        Tower.Core.Models.ConversionStatus.Pending          => "val-amber",
        _                                                    => "val-grey",
    };
```

Also add `await LoadConversionQueueAsync();` inside `OnInitializedAsync` after `await LoadAnalyticsAsync();`.

- [ ] **Step 4: Build to verify no compile errors**

```bash
dotnet build src/Tower/Tower.csproj 2>&1 | tail -15
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Tower.Core/Jellyfin/JellyfinStats.cs src/Tower/Components/Pages/Jellyfin.razor
git commit -m "Add Conversion Queue section to Jellyfin page"
```

---

### Task 10: Deploy and smoke test

**Files:**
- No new files — deploy and verify

- [ ] **Step 1: Run full test suite one final time**

```bash
dotnet test tests/Tower.Core.Tests/ 2>&1 | tail -20
```
Expected: all tests pass, 0 failures.

- [ ] **Step 2: Deploy**

```bash
bash /home/atom/dev/Tower/deploy.sh
```
Expected: build + service restart succeeds.

- [ ] **Step 3: Push to remote**

```bash
git push origin HEAD
```

- [ ] **Step 4: Verify Jellyfin page loads**

Navigate to Tower's Jellyfin page (`http://localhost:8888/jellyfin`). Confirm:
- Existing sections (Now Playing, Transcode Monitor, Watch Analytics) render normally
- New "Conversion Queue" section appears with "No conversion jobs yet" message

- [ ] **Step 5: Verify DB migration**

The new `ConversionJobs` table is created by `EnsureCreated()` on first run (no migration needed — Tower uses code-first without explicit migrations). Confirm by checking Tower logs on startup for any DB errors.

- [ ] **Step 6: Add ConversionTest folder as Jellyfin library (manual)**

In Jellyfin admin → Libraries → Add Library:
- Content type: Movies (or Mixed)  
- Folder: `/molecule/Media/ConversionTest`
- Library name: "Conversion Test"

This only needs to be done once. Converted files placed here will appear in Jellyfin for playback verification before approval.
