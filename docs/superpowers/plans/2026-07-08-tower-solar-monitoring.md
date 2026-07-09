# Tower Solar Monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move solar performance monitoring out of FinanceTracker into Tower as a `/gmail` connector page and a `/solar` dashboard fed by SolaxCloud report emails + the SolaxCloud real-time API.

**Architecture:** Two new SQLite tables in `tower.db` (`SolarSnapshot` from API polls, `SolarReport` from parsed emails). Pure parser/client units + two `BackgroundService` workers, all in `Tower.Core`, following existing Tower patterns (`DropboxTokenService` for OAuth, `DiskUsageWorker` for workers, `Sparkline`/`Home.razor` for SVG charts). Gmail is read via its REST API over `HttpClient` using a `gmail.readonly` OAuth grant.

**Tech Stack:** .NET 10, Blazor interactive server, EF Core + SQLite, xUnit (`Tower.Core.Tests`), `HttpClient` (no Google SDK).

## Global Constraints

- .NET 10 / C# — match existing `Tower.Core` and `Tower` project style (file-scoped namespaces, primary constructors where the codebase uses them).
- No new NuGet dependencies. Gmail + SolaX are plain `HttpClient` REST/JSON (`System.Text.Json`).
- New tables created via `CREATE TABLE IF NOT EXISTS` in `src/Tower/Program.cs` (EF `EnsureCreated` does not alter existing schemas). Models in `src/Tower.Core/Models/`, `DbSet`s in `TowerDbContext`.
- Config/tokens live in the `Settings` table via `SettingsService` (keys: `gmail.client_id`, `gmail.client_secret`, `gmail.refresh_token`, `gmail.label_id`, `solax.token_id`, `solax.sn`, `solar.mail.last_run`, `solar.mail.last_count`, `solar.mail.last_error`).
- Gmail OAuth scope is exactly `https://www.googleapis.com/auth/gmail.readonly`. Redirect URI `http://localhost:8888/gmail/callback` (mirrors Dropbox; paste-the-code fallback for remote browsers).
- SolaX poll frequency ≤ 10/min and ≤ 10 000/day → fixed 5-minute interval. Email poll 30-minute interval. Both workers idle (no error) until their config keys are set.
- Numbers parsed with `CultureInfo.InvariantCulture`; strip `,` before `decimal.Parse`.
- Commit after every task with the repo's convention, ending the message with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- Tower work happens on branch `solar-monitoring` (already created). FinanceTracker work on its own branch `remove-gmail-solar`.

---

## File Structure

**FinanceTracker** (`/home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/`)
- Delete: `Services/GmailSolarService.cs`, `Controllers/GmailController.cs`, `Models/SolarDailyLog.cs`
- Modify: `Program.cs` (DI + backfill), `Data/ApplicationDbContext.cs` (DbSet + relationship), `ApplyMigrationHelper.cs`, `Models/AppSetting.cs`, `Components/Pages/Settings.razor`, `Components/Pages/Solar.razor`

**Tower.Core** (`/home/atom/dev/Tower/src/Tower.Core/`)
- Create: `Models/SolarSnapshot.cs`, `Models/SolarReport.cs`, `Solar/SolarReportParser.cs`, `Solar/SolaxClient.cs`, `Gmail/GmailTokenService.cs`, `Gmail/GmailReader.cs`, `Workers/SolaxPollWorker.cs`, `Workers/SolarMailWorker.cs`
- Modify: `Data/TowerDbContext.cs`

**Tower** (`/home/atom/dev/Tower/src/Tower/`)
- Create: `Components/Pages/Gmail.razor`, `Components/Pages/Solar.razor`
- Modify: `Program.cs` (tables, DI, callback endpoint), `Components/Layout/NavMenu.razor`

**Tests** (`/home/atom/dev/Tower/tests/Tower.Core.Tests/`)
- Create: `SolarReportParserTests.cs`, `SolaxClientTests.cs`

---

## Task 1: Remove Gmail import from FinanceTracker

**Files:**
- Delete: `Services/GmailSolarService.cs`, `Controllers/GmailController.cs`, `Models/SolarDailyLog.cs`
- Modify: `Program.cs`, `Data/ApplicationDbContext.cs`, `ApplyMigrationHelper.cs`, `Models/AppSetting.cs`, `Components/Pages/Settings.razor`, `Components/Pages/Solar.razor`

**Interfaces:**
- Consumes: nothing.
- Produces: FinanceTracker builds clean with no Gmail/SolarDailyLog references. (Independent of all Tower tasks.)

Base dir: `/home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/`. Work on branch `remove-gmail-solar`.

- [ ] **Step 1: Branch**

```bash
cd /home/atom/dev/FinanceTracker && git checkout -b remove-gmail-solar
```

- [ ] **Step 2: Delete the three files**

```bash
cd /home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker
rm Services/GmailSolarService.cs Controllers/GmailController.cs Models/SolarDailyLog.cs
```

- [ ] **Step 3: Remove all references**

Remove, using grep to find each site:
```bash
grep -rn "GmailSolarService\|IGmailSolarService\|SolarDailyLog\|GmailAccessToken\|GmailRefreshToken\|GmailTokenExpiry\|GmailEnabled\|GmailController" --include=*.cs --include=*.razor .
```
- `Program.cs`: delete the `IGmailSolarService` DI line and any `BackfillSolarDailyLog*`/Gmail startup block.
- `Data/ApplicationDbContext.cs`: delete `public DbSet<SolarDailyLog> SolarDailyLogs => ...;` and its `OnModelCreating` relationship block.
- `ApplyMigrationHelper.cs`: delete the `EnsureSolarDailyLogsTable` call/method and any `SolarDailyLog` column migration lines.
- `Models/AppSetting.cs`: delete the four `Gmail*` properties.
- `Components/Pages/Settings.razor`: delete the Gmail enable/connect/disconnect UI block and its `@code` handlers + `IGmailSolarService` injection.
- `Components/Pages/Solar.razor`: delete any "Import from Gmail" button/section + handler + `IGmailSolarService` injection.

Leave the physical `SolarDailyLogs` table in the live DB (harmless). Keep `SolarEarning`, rates, tariffs, tax integration untouched.

- [ ] **Step 4: Build**

Run: `dotnet build /home/atom/dev/FinanceTracker/FinanceTracker.slnx`
Expected: Build succeeded, 0 errors. (Fix any missed reference the compiler flags.)

- [ ] **Step 5: Smoke-check the pages still render**

Run: `dotnet run --project /home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/FinanceTracker.csproj &` then `curl -sSf -o /dev/null -w "%{http_code}\n" http://localhost:5500/` (expect `200`); stop the process. (Auth may redirect to login — a `200`/`302` both prove it started without a DI crash.)

- [ ] **Step 6: Commit**

```bash
cd /home/atom/dev/FinanceTracker && git add -A && git commit -m "Remove Gmail solar-report import (moved to Tower)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Tower solar data model

**Files:**
- Create: `src/Tower.Core/Models/SolarSnapshot.cs`, `src/Tower.Core/Models/SolarReport.cs`
- Modify: `src/Tower.Core/Data/TowerDbContext.cs`, `src/Tower/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `class SolarSnapshot { int Id; DateTime CapturedAt; string? UploadTime; double AcPower; double YieldToday; double YieldTotal; double FeedInPower; double FeedInEnergy; double ConsumeEnergy; double Soc; double BatPower; double PowerDc1; string? InverterStatus; }`
  - `enum SolarReportType { Daily = 0, Weekly = 1, Monthly = 2 }`
  - `class SolarReport { int Id; SolarReportType ReportType; DateTime PeriodStart; DateTime PeriodEnd; double PeriodYieldKWh; double TotalYieldKWh; decimal PeriodEarningsLkr; decimal TotalEarningsLkr; double Co2SavedTons; int AlarmQuantity; string GmailMessageId; DateTime ImportedAt; }`

Base dir: `/home/atom/dev/Tower`. Branch `solar-monitoring`.

- [ ] **Step 1: Create `SolarSnapshot.cs`**

```csharp
namespace Tower.Core.Models;

public class SolarSnapshot
{
    public int Id { get; set; }
    public DateTime CapturedAt { get; set; }      // UTC, when Tower polled
    public string? UploadTime { get; set; }        // inverter's own uploadTime
    public double AcPower { get; set; }            // W
    public double YieldToday { get; set; }         // kWh
    public double YieldTotal { get; set; }         // kWh
    public double FeedInPower { get; set; }        // W (+export / -import)
    public double FeedInEnergy { get; set; }       // kWh
    public double ConsumeEnergy { get; set; }      // kWh
    public double Soc { get; set; }                // % battery
    public double BatPower { get; set; }           // W
    public double PowerDc1 { get; set; }           // W
    public string? InverterStatus { get; set; }
}
```

- [ ] **Step 2: Create `SolarReport.cs`**

```csharp
namespace Tower.Core.Models;

public enum SolarReportType { Daily = 0, Weekly = 1, Monthly = 2 }

public class SolarReport
{
    public int Id { get; set; }
    public SolarReportType ReportType { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public double PeriodYieldKWh { get; set; }
    public double TotalYieldKWh { get; set; }
    public decimal PeriodEarningsLkr { get; set; }
    public decimal TotalEarningsLkr { get; set; }
    public double Co2SavedTons { get; set; }
    public int AlarmQuantity { get; set; }
    public string GmailMessageId { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}
```

- [ ] **Step 3: Register DbSets + indexes in `TowerDbContext.cs`**

Add to the `DbSet` block:
```csharp
    public DbSet<SolarSnapshot> SolarSnapshots => Set<SolarSnapshot>();
    public DbSet<SolarReport> SolarReports => Set<SolarReport>();
```
Add inside `OnModelCreating`:
```csharp
        b.Entity<SolarSnapshot>().HasIndex(x => x.CapturedAt);
        b.Entity<SolarReport>().HasIndex(x => x.GmailMessageId).IsUnique();
        b.Entity<SolarReport>().HasIndex(x => new { x.ReportType, x.PeriodEnd });
```

- [ ] **Step 4: Create the tables in `Program.cs`**

In the `using (var scope = ...)` DB-init block, after the `Secrets` `ExecuteSqlRaw`, add:
```csharp
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS SolarSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CapturedAt TEXT NOT NULL,
            UploadTime TEXT,
            AcPower REAL NOT NULL DEFAULT 0,
            YieldToday REAL NOT NULL DEFAULT 0,
            YieldTotal REAL NOT NULL DEFAULT 0,
            FeedInPower REAL NOT NULL DEFAULT 0,
            FeedInEnergy REAL NOT NULL DEFAULT 0,
            ConsumeEnergy REAL NOT NULL DEFAULT 0,
            Soc REAL NOT NULL DEFAULT 0,
            BatPower REAL NOT NULL DEFAULT 0,
            PowerDc1 REAL NOT NULL DEFAULT 0,
            InverterStatus TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_SolarSnapshots_CapturedAt ON SolarSnapshots (CapturedAt);
        CREATE TABLE IF NOT EXISTS SolarReports (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ReportType INTEGER NOT NULL,
            PeriodStart TEXT NOT NULL,
            PeriodEnd TEXT NOT NULL,
            PeriodYieldKWh REAL NOT NULL DEFAULT 0,
            TotalYieldKWh REAL NOT NULL DEFAULT 0,
            PeriodEarningsLkr TEXT NOT NULL DEFAULT '0',
            TotalEarningsLkr TEXT NOT NULL DEFAULT '0',
            Co2SavedTons REAL NOT NULL DEFAULT 0,
            AlarmQuantity INTEGER NOT NULL DEFAULT 0,
            GmailMessageId TEXT NOT NULL,
            ImportedAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_SolarReports_GmailMessageId ON SolarReports (GmailMessageId);
    ");
```
(`decimal` maps to SQLite `TEXT` in EF Core — matches the `PeriodEarningsLkr TEXT` columns.)

- [ ] **Step 5: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add SolarSnapshot + SolarReport model and tables

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: SolarReportParser (TDD with real samples)

**Files:**
- Create: `src/Tower.Core/Solar/SolarReportParser.cs`
- Test: `tests/Tower.Core.Tests/SolarReportParserTests.cs`

**Interfaces:**
- Consumes: `SolarReport`, `SolarReportType` (Task 2).
- Produces: `static class SolarReportParser { static SolarReport? Parse(string subject, string body); }` — returns `null` when the type or date can't be found. `GmailMessageId`/`ImportedAt` are NOT set here (the worker sets them).

- [ ] **Step 1: Write the failing tests** (uses the three real 2026-07-08 sample bodies)

```csharp
using Tower.Core.Models;
using Tower.Core.Solar;
using Xunit;

namespace Tower.Core.Tests;

public class SolarReportParserTests
{
    private const string Daily = @"Plant Daily Report
    Daily yield 31.70kWh
    Monthly yield 154.30kWh
    Total yield 10.52MWh
    Daily earnings LKR 857.66
    Total earnings LKR 290735.60
    CO₂ Saved 7.46t
    Alarm quantity 0
    Date 2026/07/05";

    private const string Weekly = @"Plant Weekly Report
    Weekly yield 198.80kWh
    Total yield 10.52MWh
    Weekly earnings LKR 5378.48
    Total earnings LKR 290735.60
    CO₂ Saved 7.46t
    Alarm quantity 0
    Date 2026/07/05";

    private const string Monthly = @"Plant Monthly Report
    Monthly yield 859.45kWh
    Total yield 9.61MWh
    Monthly earnings LKR 23318.79
    Total earnings LKR 266021.39
    CO₂ Saved 6.82t
    Alarm quantity 0
    Date 2026/05/01 - 2026/05/31";

    [Fact]
    public void Parses_daily_report()
    {
        var r = SolarReportParser.Parse("Plant Daily Report", Daily)!;
        Assert.Equal(SolarReportType.Daily, r.ReportType);
        Assert.Equal(31.70, r.PeriodYieldKWh, 2);
        Assert.Equal(10520.0, r.TotalYieldKWh, 1);        // 10.52 MWh -> kWh
        Assert.Equal(857.66m, r.PeriodEarningsLkr);
        Assert.Equal(290735.60m, r.TotalEarningsLkr);
        Assert.Equal(7.46, r.Co2SavedTons, 2);
        Assert.Equal(0, r.AlarmQuantity);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodStart);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodEnd);
    }

    [Fact]
    public void Parses_weekly_report()
    {
        var r = SolarReportParser.Parse("Plant Weekly Report", Weekly)!;
        Assert.Equal(SolarReportType.Weekly, r.ReportType);
        Assert.Equal(198.80, r.PeriodYieldKWh, 2);
        Assert.Equal(5378.48m, r.PeriodEarningsLkr);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodEnd);
    }

    [Fact]
    public void Parses_monthly_report_with_date_range()
    {
        var r = SolarReportParser.Parse("Plant Monthly Report", Monthly)!;
        Assert.Equal(SolarReportType.Monthly, r.ReportType);
        Assert.Equal(859.45, r.PeriodYieldKWh, 2);
        Assert.Equal(9610.0, r.TotalYieldKWh, 1);
        Assert.Equal(23318.79m, r.PeriodEarningsLkr);
        Assert.Equal(new DateTime(2026, 5, 1), r.PeriodStart);
        Assert.Equal(new DateTime(2026, 5, 31), r.PeriodEnd);
    }

    [Fact]
    public void Returns_null_for_unrecognized_body()
    {
        Assert.Null(SolarReportParser.Parse("Newsletter", "nothing solar here"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj --filter SolarReportParserTests`
Expected: FAIL — `SolarReportParser` does not exist.

- [ ] **Step 3: Implement `SolarReportParser.cs`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using Tower.Core.Models;

namespace Tower.Core.Solar;

public static class SolarReportParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SolarReport? Parse(string subject, string body)
    {
        var text = (subject ?? "") + "\n" + (body ?? "");

        var typeMatch = Regex.Match(text, @"Plant\s+(Daily|Weekly|Monthly)\s+Report", RegexOptions.IgnoreCase);
        if (!typeMatch.Success) return null;
        var type = typeMatch.Groups[1].Value.ToLowerInvariant() switch
        {
            "daily" => SolarReportType.Daily,
            "weekly" => SolarReportType.Weekly,
            _ => SolarReportType.Monthly
        };

        var dateMatch = Regex.Match(text, @"Date\s+(\d{4}/\d{2}/\d{2})(?:\s*-\s*(\d{4}/\d{2}/\d{2}))?");
        if (!dateMatch.Success) return null;
        var end = ParseDate(dateMatch.Groups[1].Value);
        var start = dateMatch.Groups[2].Success ? end : end;
        if (dateMatch.Groups[2].Success)
        {
            start = ParseDate(dateMatch.Groups[1].Value);
            end = ParseDate(dateMatch.Groups[2].Value);
        }

        return new SolarReport
        {
            ReportType = type,
            PeriodStart = start,
            PeriodEnd = end,
            PeriodYieldKWh = YieldKWh(text, @"(?:Daily|Weekly|Monthly)\s+yield"),
            TotalYieldKWh = YieldKWh(text, @"Total\s+yield"),
            PeriodEarningsLkr = Money(text, @"(?:Daily|Weekly|Monthly)\s+earnings"),
            TotalEarningsLkr = Money(text, @"Total\s+earnings"),
            Co2SavedTons = Num(text, @"CO₂?\s*Saved\s+([\d.,]+)\s*t"),
            AlarmQuantity = (int)Num(text, @"Alarm quantity\s+(\d+)"),
        };
    }

    private static DateTime ParseDate(string s) =>
        DateTime.ParseExact(s, "yyyy/MM/dd", Inv);

    // "<label> 31.70kWh" or "10.52MWh" -> kWh
    private static double YieldKWh(string text, string labelPattern)
    {
        var m = Regex.Match(text, labelPattern + @"\s+([\d.,]+)\s*(kWh|MWh)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value.Replace(",", ""), Inv);
        return m.Groups[2].Value.Equals("MWh", StringComparison.OrdinalIgnoreCase) ? v * 1000.0 : v;
    }

    private static decimal Money(string text, string labelPattern)
    {
        var m = Regex.Match(text, labelPattern + @"\s+LKR\s+([\d.,]+)", RegexOptions.IgnoreCase);
        return m.Success ? decimal.Parse(m.Groups[1].Value.Replace(",", ""), Inv) : 0m;
    }

    private static double Num(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value.Replace(",", ""), Inv) : 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj --filter SolarReportParserTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add SolarReportParser with tests from real report samples

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: SolaxClient (real-time API)

**Files:**
- Create: `src/Tower.Core/Solar/SolaxClient.cs`
- Test: `tests/Tower.Core.Tests/SolaxClientTests.cs`

**Interfaces:**
- Consumes: `SolarSnapshot` (Task 2).
- Produces:
  - `static SolarSnapshot? SolaxClient.ParseRealtime(string json)` — pure, testable; returns `null` if `success != true`.
  - `class SolaxClient(HttpClient http)` with `Task<SolarSnapshot?> GetRealtimeAsync(string tokenId, string sn, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test** (canned response shape per SolaxCloud API V6.1)

```csharp
using Tower.Core.Solar;
using Xunit;

namespace Tower.Core.Tests;

public class SolaxClientTests
{
    private const string Json = @"{
      ""success"": true, ""exception"": ""Query success!"",
      ""result"": {
        ""inverterSN"": ""ABC"", ""sn"": ""SN123"",
        ""acpower"": 2450.0, ""yieldtoday"": 12.3, ""yieldtotal"": 10520.4,
        ""feedinpower"": 1800.0, ""feedinenergy"": 5000.1, ""consumeenergy"": 3000.2,
        ""soc"": 87.0, ""batPower"": -500.0, ""powerdc1"": 1300.0,
        ""inverterStatus"": ""102"", ""uploadTime"": ""2026-07-08 10:15:00""
      }
    }";

    [Fact]
    public void Parses_realtime_result()
    {
        var s = SolaxClient.ParseRealtime(Json)!;
        Assert.Equal(2450.0, s.AcPower);
        Assert.Equal(12.3, s.YieldToday);
        Assert.Equal(10520.4, s.YieldTotal);
        Assert.Equal(1800.0, s.FeedInPower);
        Assert.Equal(87.0, s.Soc);
        Assert.Equal(-500.0, s.BatPower);
        Assert.Equal("102", s.InverterStatus);
        Assert.Equal("2026-07-08 10:15:00", s.UploadTime);
    }

    [Fact]
    public void Returns_null_when_not_success()
    {
        Assert.Null(SolaxClient.ParseRealtime(@"{""success"":false,""exception"":""error""}"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj --filter SolaxClientTests`
Expected: FAIL — `SolaxClient` does not exist.

- [ ] **Step 3: Implement `SolaxClient.cs`**

```csharp
using System.Text.Json;
using Tower.Core.Models;

namespace Tower.Core.Solar;

public class SolaxClient(HttpClient http)
{
    private const string BaseUrl =
        "https://www.solaxcloud.com/proxyApp/proxy/api/getRealtimeInfo.do";

    public async Task<SolarSnapshot?> GetRealtimeAsync(string tokenId, string sn, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?tokenId={Uri.EscapeDataString(tokenId)}&sn={Uri.EscapeDataString(sn)}";
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return ParseRealtime(await resp.Content.ReadAsStringAsync(ct));
    }

    public static SolarSnapshot? ParseRealtime(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
        if (!root.TryGetProperty("result", out var r)) return null;

        return new SolarSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            UploadTime = Str(r, "uploadTime"),
            AcPower = Dbl(r, "acpower"),
            YieldToday = Dbl(r, "yieldtoday"),
            YieldTotal = Dbl(r, "yieldtotal"),
            FeedInPower = Dbl(r, "feedinpower"),
            FeedInEnergy = Dbl(r, "feedinenergy"),
            ConsumeEnergy = Dbl(r, "consumeenergy"),
            Soc = Dbl(r, "soc"),
            BatPower = Dbl(r, "batPower"),
            PowerDc1 = Dbl(r, "powerdc1"),
            InverterStatus = Str(r, "inverterStatus"),
        };
    }

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ToString() : null;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj --filter SolaxClientTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add SolaxClient realtime API + parse tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: GmailTokenService (OAuth, clone of DropboxTokenService)

**Files:**
- Create: `src/Tower.Core/Gmail/GmailTokenService.cs`

**Interfaces:**
- Consumes: `SettingsService` (existing), `IServiceScopeFactory`, `IHttpClientFactory`.
- Produces: `class GmailTokenService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory)` with:
  - `const string RedirectUri = "http://localhost:8888/gmail/callback";`
  - `string BuildAuthUrl(string clientId)`
  - `Task<string?> GetAccessTokenAsync(CancellationToken ct = default)`
  - `Task<(bool Ok, string? Error)> ExchangeCodeAsync(string code, CancellationToken ct = default)`
  - `void Disconnect()`
  - `bool IsConnected` (has a stored refresh token)

- [ ] **Step 1: Implement `GmailTokenService.cs`** (structure mirrors `Backup/DropboxTokenService.cs`)

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Gmail;

/// <summary>
/// Google OAuth for gmail.readonly. Stores client_id/client_secret/refresh_token in the
/// Settings table (keys gmail.*). Caches the short-lived access token in memory.
/// Redirect is http://localhost:8888/gmail/callback (Google allows http for loopback).
/// For a remote browser the callback won't load, but the ?code= is in the URL bar —
/// paste it into the /gmail page (same fallback as Dropbox).
/// </summary>
public class GmailTokenService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory)
{
    public const string RedirectUri = "http://localhost:8888/gmail/callback";
    public const string Scope = "https://www.googleapis.com/auth/gmail.readonly";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected
    {
        get
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            return !string.IsNullOrEmpty(settings.Get("gmail.refresh_token"));
        }
    }

    public string BuildAuthUrl(string clientId) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString(Scope)}" +
        "&access_type=offline&prompt=consent" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var refreshToken = settings.Get("gmail.refresh_token");
            var clientId = settings.Get("gmail.client_id");
            var clientSecret = settings.Get("gmail.client_secret");
            if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                return null;

            using var http = httpFactory.CreateClient(nameof(GmailTokenService));
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });
            var resp = await http.PostAsync(TokenEndpoint, form, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _cachedToken = json.RootElement.GetProperty("access_token").GetString();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
            return _cachedToken;
        }
        finally { _lock.Release(); }
    }

    public async Task<(bool Ok, string? Error)> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var clientId = settings.Get("gmail.client_id");
        var clientSecret = settings.Get("gmail.client_secret");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return (false, "Client ID or Secret not configured");

        using var http = httpFactory.CreateClient(nameof(GmailTokenService));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code.Trim(),
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = RedirectUri,
        });
        var resp = await http.PostAsync(TokenEndpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"Token exchange failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");

        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("refresh_token", out var rt))
            return (false, "No refresh_token returned (revoke access and retry with prompt=consent)");
        settings.Set("gmail.refresh_token", rt.GetString());
        _cachedToken = json.RootElement.GetProperty("access_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
        return (true, null);
    }

    public void Disconnect()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<SettingsService>().Set("gmail.refresh_token", null);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add GmailTokenService (gmail.readonly OAuth)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: GmailReader (Gmail REST wrapper)

**Files:**
- Create: `src/Tower.Core/Gmail/GmailReader.cs`

**Interfaces:**
- Consumes: `GmailTokenService.GetAccessTokenAsync` (Task 5).
- Produces: `class GmailReader(HttpClient http, GmailTokenService tokens)` with:
  - `Task<List<(string Id, string Name)>> ListLabelsAsync(CancellationToken ct = default)`
  - `Task<List<string>> ListMessageIdsAsync(string labelId, DateTime? after, CancellationToken ct = default)`
  - `Task<(string Subject, string Body)?> GetMessageAsync(string id, CancellationToken ct = default)`

- [ ] **Step 1: Implement `GmailReader.cs`**

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tower.Core.Gmail;

public class GmailReader(HttpClient http, GmailTokenService tokens)
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";

    private async Task<HttpRequestMessage> AuthGet(string url, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct)
            ?? throw new InvalidOperationException("Gmail not connected");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<List<(string Id, string Name)>> ListLabelsAsync(CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/labels", ct);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("labels", out var labels))
            foreach (var l in labels.EnumerateArray())
                list.Add((l.GetProperty("id").GetString()!, l.GetProperty("name").GetString()!));
        return list;
    }

    public async Task<List<string>> ListMessageIdsAsync(string labelId, DateTime? after, CancellationToken ct = default)
    {
        var ids = new List<string>();
        string? pageToken = null;
        var q = after is { } a ? $"&q=after:{a:yyyy/MM/dd}" : "";
        do
        {
            var url = $"{Api}/messages?labelIds={Uri.EscapeDataString(labelId)}&maxResults=100{q}" +
                      (pageToken != null ? $"&pageToken={pageToken}" : "");
            using var req = await AuthGet(url, ct);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("messages", out var msgs))
                foreach (var m in msgs.EnumerateArray())
                    ids.Add(m.GetProperty("id").GetString()!);
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() : null;
        } while (pageToken != null);
        return ids;
    }

    public async Task<(string Subject, string Body)?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=full", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var payload = doc.RootElement.GetProperty("payload");

        string subject = "";
        if (payload.TryGetProperty("headers", out var headers))
            foreach (var h in headers.EnumerateArray())
                if (h.GetProperty("name").GetString()!.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                    subject = h.GetProperty("value").GetString() ?? "";

        var body = ExtractText(payload);
        return (subject, body);
    }

    // Recursively find the first text/plain part; fall back to any body data.
    private static string ExtractText(JsonElement part)
    {
        if (part.TryGetProperty("mimeType", out var mt) && mt.GetString() == "text/plain")
        {
            var d = Decode(part);
            if (d.Length > 0) return d;
        }
        if (part.TryGetProperty("parts", out var parts))
            foreach (var child in parts.EnumerateArray())
            {
                var found = ExtractText(child);
                if (!string.IsNullOrEmpty(found)) return found;
            }
        return Decode(part);
    }

    private static string Decode(JsonElement part)
    {
        if (!part.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("data", out var data)) return "";
        var b64 = data.GetString();
        if (string.IsNullOrEmpty(b64)) return "";
        var bytes = Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight((b64.Length + 3) / 4 * 4, '='));
        return Encoding.UTF8.GetString(bytes);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add GmailReader (labels/messages REST wrapper)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: SolaxPollWorker

**Files:**
- Create: `src/Tower.Core/Workers/SolaxPollWorker.cs`

**Interfaces:**
- Consumes: `SolaxClient` (Task 4), `SettingsService`, `TowerDbContext`, `SolarSnapshot` (Task 2) — all resolved from a per-loop scope (a `BackgroundService` is a singleton, so it must NOT capture the transient typed `SolaxClient`/`TowerDbContext` via the constructor).
- Produces: `class SolaxPollWorker` (a `BackgroundService`). No public surface beyond registration.

- [ ] **Step 1: Implement `SolaxPollWorker.cs`** (pattern from `DiskUsageWorker`; resolve scoped services inside the loop)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Settings;
using Tower.Core.Solar;

namespace Tower.Core.Workers;

public class SolaxPollWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private const int IntervalMs = 300_000; // 5 minutes (288/day, under 10k/day limit)

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var tokenId = settings.Get("solax.token_id");
                var sn = settings.Get("solax.sn");

                if (!string.IsNullOrWhiteSpace(tokenId) && !string.IsNullOrWhiteSpace(sn))
                {
                    var client = scope.ServiceProvider.GetRequiredService<SolaxClient>();
                    var snap = await client.GetRealtimeAsync(tokenId!, sn!, stoppingToken);
                    if (snap != null)
                    {
                        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                        // Dedup: skip if inverter's uploadTime unchanged from the latest row.
                        var lastUpload = db.SolarSnapshots
                            .OrderByDescending(x => x.CapturedAt)
                            .Select(x => x.UploadTime)
                            .FirstOrDefault();
                        if (snap.UploadTime == null || snap.UploadTime != lastUpload)
                        {
                            db.SolarSnapshots.Add(snap);
                            db.SaveChanges();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[SolaxPollWorker] {ex.Message}");
            }
            await Task.Delay(IntervalMs, stoppingToken);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add SolaxPollWorker (5-min realtime polling)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: SolarMailWorker

**Files:**
- Create: `src/Tower.Core/Workers/SolarMailWorker.cs`

**Interfaces:**
- Consumes: `GmailTokenService` (Task 5), `GmailReader` (Task 6), `SolarReportParser` (Task 3), `SettingsService`, `TowerDbContext` — all resolved from a per-call scope (singleton worker must not capture the transient `GmailReader`/`TowerDbContext`).
- Produces: `class SolarMailWorker` (`BackgroundService`) with `public async Task<int> RunOnceAsync(CancellationToken ct = default)` — returns count imported (the `/gmail` page's "Import now" calls this).

- [ ] **Step 1: Implement `SolarMailWorker.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Settings;
using Tower.Core.Solar;

namespace Tower.Core.Workers;

public class SolarMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private const int IntervalMs = 1_800_000; // 30 minutes

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[SolarMailWorker] {ex.Message}"); }
            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var tokens = scope.ServiceProvider.GetRequiredService<GmailTokenService>();
        var reader = scope.ServiceProvider.GetRequiredService<GmailReader>();
        var labelId = settings.Get("gmail.label_id");
        if (!tokens.IsConnected || string.IsNullOrWhiteSpace(labelId)) return 0;

        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var known = db.SolarReports.Select(r => r.GmailMessageId).ToHashSet();

        // Bound the query with a lookback window to keep pages small.
        var after = DateTime.UtcNow.AddDays(-45);
        var ids = await reader.ListMessageIdsAsync(labelId!, after, ct);

        int imported = 0;
        string? lastError = null;
        foreach (var id in ids)
        {
            if (known.Contains(id)) continue;
            try
            {
                var msg = await reader.GetMessageAsync(id, ct);
                if (msg == null) continue;
                var report = SolarReportParser.Parse(msg.Value.Subject, msg.Value.Body);
                if (report == null) continue;
                report.GmailMessageId = id;
                report.ImportedAt = DateTime.UtcNow;
                db.SolarReports.Add(report);
                db.SaveChanges();
                imported++;
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("solar.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("solar.mail.last_count", imported.ToString());
        settings.Set("solar.mail.last_error", lastError);
        return imported;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add SolarMailWorker (30-min email import, RunOnceAsync)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 9: Wire DI + OAuth callback in Program.cs

**Files:**
- Modify: `src/Tower/Program.cs`

**Interfaces:**
- Consumes: all services from Tasks 4–8.
- Produces: registered services + workers + `GET /gmail/callback` endpoint.

- [ ] **Step 1: Register services + workers**

In the service-registration section (near the other `AddHttpClient`/`AddHostedService` blocks) add:
```csharp
// ── Solar (SolaX API + Gmail report import) ──────────────────────────────────
builder.Services.AddHttpClient<Tower.Core.Solar.SolaxClient>();          // typed; page + worker resolve from scope
builder.Services.AddSingleton<Tower.Core.Gmail.GmailTokenService>();
builder.Services.AddHttpClient(nameof(Tower.Core.Gmail.GmailTokenService)); // named client used inside GmailTokenService
builder.Services.AddHttpClient<Tower.Core.Gmail.GmailReader>();          // typed; page + worker resolve from scope
builder.Services.AddSingleton<Tower.Core.Workers.SolarMailWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tower.Core.Workers.SolarMailWorker>());
builder.Services.AddHostedService<Tower.Core.Workers.SolaxPollWorker>();
```
Notes: `SolarMailWorker` is registered as a singleton first so the `/gmail` page can inject the same instance for "Import now", then handed to the hosted-service collection (same shape as the MediaBox scheduler). Both workers resolve `SolaxClient`/`GmailReader`/`TowerDbContext` from a per-loop scope (Tasks 7 & 8), so no transient `HttpClient` is captured by a singleton. The `/gmail` and `/solar` Blazor pages inject `SolaxClient`/`GmailReader` directly — safe because component injection is already scoped.

- [ ] **Step 2: Add the OAuth callback endpoint**

After the `/dropbox/callback` `MapGet`, add:
```csharp
// ── Gmail OAuth callback ──────────────────────────────────────────────────────
app.MapGet("/gmail/callback", async (
    string? code, string? error,
    Tower.Core.Gmail.GmailTokenService tokenSvc) =>
{
    if (!string.IsNullOrEmpty(error))
        return Results.Redirect("/gmail?error=" + Uri.EscapeDataString(error));
    if (string.IsNullOrEmpty(code))
        return Results.Redirect("/gmail?error=no_code");
    var (ok, err) = await tokenSvc.ExchangeCodeAsync(code);
    return ok
        ? Results.Redirect("/gmail?connected=1")
        : Results.Redirect("/gmail?error=" + Uri.EscapeDataString(err ?? "unknown"));
});
```

- [ ] **Step 3: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj`
Expected: PASS (all existing + 6 new).

- [ ] **Step 5: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Wire solar services, workers, and Gmail OAuth callback

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 10: /gmail connector page

**Files:**
- Create: `src/Tower/Components/Pages/Gmail.razor`
- Modify: `src/Tower/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `GmailTokenService`, `GmailReader`, `SolarMailWorker`, `SettingsService`, `NavigationManager`.
- Produces: a page at `/gmail`.

**Reference before writing:** open `src/Tower/Components/Pages/Settings.razor` and copy its Dropbox connect/disconnect + paste-code block styling and `@code` structure; reuse the same CSS classes as other pages (look at `Secrets.razor` for card/button classes).

- [ ] **Step 1: Create `Gmail.razor`**

```razor
@page "/gmail"
@rendermode InteractiveServer
@using Microsoft.AspNetCore.WebUtilities
@using Tower.Core.Gmail
@using Tower.Core.Settings
@using Tower.Core.Workers
@inject GmailTokenService Tokens
@inject GmailReader Reader
@inject SolarMailWorker Mail
@inject SettingsService Settings
@inject NavigationManager Nav
@inject IServiceScopeFactory Scopes

<PageTitle>Gmail — Tower</PageTitle>
<h1>Gmail Connector</h1>

@if (_flash != null) { <div class="alert">@_flash</div> }

<section class="card">
    <h2>Connection</h2>
    @if (_connected)
    {
        <p>✅ Connected (gmail.readonly).</p>
        <button class="btn" @onclick="Disconnect">Disconnect</button>
    }
    else
    {
        <p>Not connected.</p>
        <label>Client ID <input @bind="_clientId" /></label>
        <label>Client Secret <input @bind="_clientSecret" type="password" /></label>
        <button class="btn" @onclick="SaveCredsAndStart">Connect</button>
        <details>
            <summary>Remote browser? Paste the code</summary>
            <p>After approving, if the localhost page fails to load, copy the <code>code</code>
               value from the browser URL bar and paste it here.</p>
            <input @bind="_pasteCode" placeholder="4/0A..." />
            <button class="btn" @onclick="ExchangePasted">Submit code</button>
        </details>
    }
</section>

@if (_connected)
{
    <section class="card">
        <h2>Labels</h2>
        <button class="btn" @onclick="LoadLabels">Refresh</button>
        <ul>@foreach (var l in _labels) { <li>@l.Name <small>(@l.Id)</small></li> }</ul>
    </section>

    <section class="card">
        <h2>Import log</h2>
        <p>Last run: @(_lastRun ?? "never") · Imported: @_lastCount · Error: @(_lastError ?? "none")</p>
        <button class="btn" @onclick="ImportNow" disabled="@_importing">
            @(_importing ? "Importing…" : "Import now")</button>
    </section>
}

@code {
    private bool _connected;
    private string _clientId = "", _clientSecret = "", _pasteCode = "";
    private string? _flash, _lastRun, _lastError;
    private int _lastCount;
    private bool _importing;
    private List<(string Id, string Name)> _labels = new();

    protected override async Task OnInitializedAsync()
    {
        var uri = Nav.ToAbsoluteUri(Nav.Uri);
        var q = QueryHelpers.ParseQuery(uri.Query);
        if (q.ContainsKey("connected")) _flash = "Gmail connected.";
        if (q.TryGetValue("error", out var e)) _flash = "Error: " + e;

        _connected = Tokens.IsConnected;
        _clientId = Settings.Get("gmail.client_id") ?? "";
        _lastRun = Settings.Get("solar.mail.last_run");
        _lastError = Settings.Get("solar.mail.last_error");
        int.TryParse(Settings.Get("solar.mail.last_count"), out _lastCount);
        if (_connected) await LoadLabels();
    }

    private void SaveCredsAndStart()
    {
        Settings.Set("gmail.client_id", _clientId.Trim());
        Settings.Set("gmail.client_secret", _clientSecret.Trim());
        Nav.NavigateTo(Tokens.BuildAuthUrl(_clientId.Trim()), forceLoad: true);
    }

    private async Task ExchangePasted()
    {
        var (ok, err) = await Tokens.ExchangeCodeAsync(_pasteCode);
        _flash = ok ? "Gmail connected." : "Error: " + err;
        _connected = Tokens.IsConnected;
        if (_connected) await LoadLabels();
    }

    private async Task LoadLabels()
    {
        try { _labels = await Reader.ListLabelsAsync(); }
        catch (Exception ex) { _flash = "Label load failed: " + ex.Message; }
    }

    private void Disconnect() { Tokens.Disconnect(); _connected = false; _labels.Clear(); }

    private async Task ImportNow()
    {
        _importing = true;
        try
        {
            _lastCount = await Mail.RunOnceAsync();
            _lastRun = Settings.Get("solar.mail.last_run");
            _lastError = Settings.Get("solar.mail.last_error");
            _flash = $"Imported {_lastCount} report(s).";
        }
        finally { _importing = false; }
    }
}
```

- [ ] **Step 2: Add nav link**

In `NavMenu.razor`, following an existing `<NavLink>` entry, add:
```razor
<div class="nav-item"><NavLink class="nav-link" href="gmail"><span>📧 Gmail</span></NavLink></div>
```
(Match the exact markup/classes of the neighboring nav entries.)

- [ ] **Step 3: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add /gmail connector page + nav link

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 11: /solar dashboard page

**Files:**
- Create: `src/Tower/Components/Pages/Solar.razor`
- Modify: `src/Tower/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `TowerDbContext`, `SettingsService`, `SolaxClient`, `GmailReader` (label picker), `SolarSnapshot`/`SolarReport` (Task 2).
- Produces: a page at `/solar`.

**Reference before writing:** open `src/Tower/Components/Pages/Home.razor` and `Jellyfin.razor` for the live-tile + hand-rolled SVG chart patterns and CSS classes, and reuse `Components/Shared/Sparkline.razor` for small trend lines. Match those classes rather than inventing new ones.

- [ ] **Step 1: Create `Solar.razor`** (data layer complete; markup follows Home.razor's tile/card classes)

```razor
@page "/solar"
@rendermode InteractiveServer
@using Microsoft.EntityFrameworkCore
@using Tower.Core.Data
@using Tower.Core.Models
@using Tower.Core.Settings
@using Tower.Core.Solar
@using Tower.Core.Gmail
@inject IDbContextFactory<TowerDbContext> DbFactory
@inject SettingsService Settings
@inject SolaxClient Solax
@inject GmailReader Reader

<PageTitle>Solar — Tower</PageTitle>
<h1>Solar</h1>

@if (_latest is { } s)
{
    <div class="tiles">
        <div class="tile"><span class="tile-label">AC Power</span><span class="tile-value">@s.AcPower.ToString("0") W</span></div>
        <div class="tile"><span class="tile-label">Battery</span><span class="tile-value">@s.Soc.ToString("0")%</span></div>
        <div class="tile"><span class="tile-label">Today</span><span class="tile-value">@s.YieldToday.ToString("0.0") kWh</span></div>
        <div class="tile"><span class="tile-label">Grid</span><span class="tile-value">@s.FeedInPower.ToString("0") W</span></div>
        <div class="tile"><span class="tile-label">Total</span><span class="tile-value">@s.YieldTotal.ToString("0") kWh</span></div>
        <div class="tile"><span class="tile-label">Status</span><span class="tile-value">@s.InverterStatus</span></div>
        <div class="tile @(_alarms > 0 ? "tile-alarm" : "")"><span class="tile-label">Alarms</span><span class="tile-value">@_alarms</span></div>
    </div>
}
else { <p>No live data yet. Configure SolaX below.</p> }

<section class="card">
    <h2>Power today</h2>
    @* SVG line chart: _todayPower (W) over today's snapshots. Follow the polyline
       approach in Home.razor's net-worth chart; x = index, y scaled to max power.
       Reuse <Sparkline Data="_todayPower" Width="600" Height="120" /> as the minimal
       version if a full axed chart is more than needed. *@
    <Sparkline Data="_todayPower" Width="640" Height="140" />
</section>

<section class="card">
    <h2>Battery SOC today</h2>
    <Sparkline Data="_todaySoc" Width="640" Height="140" Color="#3fb950" />
</section>

<section class="card">
    <h2>Daily yield (30d)</h2>
    @* Bar chart from _daily. Follow Home.razor's institution bar-chart markup. *@
    @foreach (var d in _daily)
    {
        <div class="bar-row"><span class="bar-label">@d.PeriodEnd.ToString("MM-dd")</span>
            <span class="bar" style="width:@(BarPct(d.PeriodYieldKWh, _dailyMax))%"></span>
            <span class="bar-val">@d.PeriodYieldKWh.ToString("0.0")</span></div>
    }
</section>

<section class="card">
    <h2>Reports</h2>
    <div class="tabs">
        <button class="@Tab(SolarReportType.Daily)" @onclick="() => SetTab(SolarReportType.Daily)">Daily</button>
        <button class="@Tab(SolarReportType.Weekly)" @onclick="() => SetTab(SolarReportType.Weekly)">Weekly</button>
        <button class="@Tab(SolarReportType.Monthly)" @onclick="() => SetTab(SolarReportType.Monthly)">Monthly</button>
    </div>
    <table class="data-table">
        <thead><tr><th>Period</th><th>Yield kWh</th><th>Earnings LKR</th><th>CO₂ t</th><th>Alarms</th></tr></thead>
        <tbody>
        @foreach (var r in _reports.Where(r => r.ReportType == _tab).OrderByDescending(r => r.PeriodEnd))
        {
            <tr><td>@r.PeriodStart.ToString("yyyy-MM-dd")@(r.PeriodStart != r.PeriodEnd ? " – " + r.PeriodEnd.ToString("MM-dd") : "")</td>
                <td>@r.PeriodYieldKWh.ToString("0.0")</td><td>@r.PeriodEarningsLkr.ToString("N2")</td>
                <td>@r.Co2SavedTons.ToString("0.0")</td><td>@r.AlarmQuantity</td></tr>
        }
        </tbody>
    </table>
</section>

<section class="card">
    <h2>Configuration</h2>
    <label>SolaX Token ID <input @bind="_tokenId" /></label>
    <label>Inverter SN <input @bind="_sn" /></label>
    <button class="btn" @onclick="SaveSolax">Save</button>
    <button class="btn" @onclick="TestSolax">Test</button>
    @if (_testMsg != null) { <span>@_testMsg</span> }

    <hr />
    <label>Gmail label for reports
        <select @bind="_labelId">
            <option value="">— none —</option>
            @foreach (var l in _labels) { <option value="@l.Id">@l.Name</option> }
        </select>
    </label>
    <button class="btn" @onclick="SaveLabel">Save label</button>
</section>

@code {
    private SolarSnapshot? _latest;
    private int _alarms;
    private double[] _todayPower = [], _todaySoc = [];
    private List<SolarReport> _daily = new(), _reports = new();
    private double _dailyMax = 1;
    private SolarReportType _tab = SolarReportType.Daily;
    private string _tokenId = "", _sn = "", _labelId = "", _testMsg = null!;
    private List<(string Id, string Name)> _labels = new();

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _latest = await db.SolarSnapshots.OrderByDescending(x => x.CapturedAt).FirstOrDefaultAsync();

        var since = DateTime.UtcNow.Date;
        var today = await db.SolarSnapshots.Where(x => x.CapturedAt >= since)
            .OrderBy(x => x.CapturedAt).ToListAsync();
        _todayPower = today.Select(x => x.AcPower).ToArray();
        _todaySoc = today.Select(x => x.Soc).ToArray();

        _reports = await db.SolarReports.ToListAsync();
        _daily = _reports.Where(r => r.ReportType == SolarReportType.Daily)
            .OrderByDescending(r => r.PeriodEnd).Take(30).OrderBy(r => r.PeriodEnd).ToList();
        _dailyMax = _daily.Count > 0 ? Math.Max(1, _daily.Max(d => d.PeriodYieldKWh)) : 1;
        _alarms = _reports.OrderByDescending(r => r.PeriodEnd).FirstOrDefault()?.AlarmQuantity ?? 0;

        _tokenId = Settings.Get("solax.token_id") ?? "";
        _sn = Settings.Get("solax.sn") ?? "";
        _labelId = Settings.Get("gmail.label_id") ?? "";
        try { _labels = await Reader.ListLabelsAsync(); } catch { /* gmail not connected yet */ }
    }

    private double BarPct(double v, double max) => Math.Round(v / max * 100, 1);
    private string Tab(SolarReportType t) => t == _tab ? "tab active" : "tab";
    private void SetTab(SolarReportType t) => _tab = t;
    private void SaveSolax() { Settings.Set("solax.token_id", _tokenId.Trim()); Settings.Set("solax.sn", _sn.Trim()); }
    private void SaveLabel() => Settings.Set("gmail.label_id", _labelId);

    private async Task TestSolax()
    {
        var snap = await Solax.GetRealtimeAsync(_tokenId.Trim(), _sn.Trim());
        _testMsg = snap != null ? $"OK — {snap.AcPower:0} W now" : "No data / invalid token or SN";
    }
}
```

- [ ] **Step 2: Add any missing CSS** to `src/Tower/wwwroot/app.css` (or the existing site CSS) for `.tiles/.tile/.tile-label/.tile-value/.tile-alarm/.bar-row/.bar/.bar-label/.bar-val/.tabs/.tab` **only if** equivalents don't already exist — check first with `grep -n "tile-value\|bar-row\|\.tab " src/Tower/wwwroot/*.css`. Reuse existing classes where present.

- [ ] **Step 3: Add nav link**

In `NavMenu.razor` add, matching neighbor markup:
```razor
<div class="nav-item"><NavLink class="nav-link" href="solar"><span>☀️ Solar</span></NavLink></div>
```

- [ ] **Step 4: Build + full test run**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj && dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests/Tower.Core.Tests.csproj`
Expected: Build succeeded; all tests PASS.

- [ ] **Step 5: Deploy + smoke test**

Run: `bash /home/atom/dev/Tower/deploy.sh` then `curl -sSf -o /dev/null -w "%{http_code}\n" http://localhost:8888/solar` (expect `200`) and `.../gmail` (expect `200`).

- [ ] **Step 6: Commit**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add /solar dashboard page + nav link

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Post-implementation (manual, by John)

1. On `/gmail`: enter the Google client id/secret (reuse FinanceTracker's), Connect, approve `gmail.readonly`, confirm the `Solar` label appears.
2. On `/solar`: enter SolaX `tokenId` + inverter `sn`, Test, Save; pick the `Solar` label; Save.
3. "Import now" on `/gmail` to backfill existing reports. Live tiles populate within ~5 min.
4. Store the SolaX token + Gmail client secret in the `/secrets` vault for the record.

## Notes for the implementer

- The FinanceTracker task (Task 1) is fully independent — can run in parallel with Tower tasks.
- Tower tasks 2→9 are mostly sequential (each builds on the prior model/service). Tasks 3 and 4 (parser, client) are independent of each other. Tasks 10 and 11 depend on everything before them.
- SolaX field names come from the API V6.1 docs; `SolaxClient` tolerates missing fields (0/null). Confirm against the first live response and adjust `Dbl`/`Str` field names if SolaX returns different casing.
