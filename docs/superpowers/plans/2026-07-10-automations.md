# Automations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Automations page in Tower where named smart-home automations (Tuya on/off, AC temp, Pi TV shutdown) are defined and triggered via Telegram `run <name>` (case-insensitive) or a Run button.

**Architecture:** One new `Automations` SQLite table storing an ordered JSON list of actions per automation. A scoped `AutomationService` executes actions through the existing `TuyaServiceClient` and `PiAgentClient`. A singleton Telegram handler registers `run`/`/run` on the existing `TelegramHub` command dispatcher (one-line hub relaxation to allow slash-less commands). A Blazor page provides CRUD + Run.

**Tech Stack:** .NET 10 Blazor interactive server, EF Core/SQLite, xunit.v3. Repo: `/home/atom/dev/Tower`, branch `main`.

## Global Constraints

- No new NuGet packages.
- Follow existing patterns: `CREATE TABLE IF NOT EXISTS` in `Program.cs` (EnsureCreated never alters existing schemas); Telegram handler shaped like `TodoTelegramHandler`; UI reuses global CSS classes (`page-title`, `atom-table`, `config-input`, `btn-primary`, `btn-secondary`, `btn-sm`, `text-muted`).
- `TuyaServiceClient` / `PiAgentClient` never throw — treat `false`/`null` returns as step failure (❌), never abort a run.
- **NOTE:** Task 1's five source changes may already exist in the working tree (drafted pre-plan). If a file exists, verify its content matches the plan exactly rather than re-creating it.
- Build command: `dotnet build /home/atom/dev/Tower/Tower.slnx` (or the csproj). Tests: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests`.

---

### Task 1: Backend — model, service, Telegram handler, wiring, unit test

**Files:**
- Create: `src/Tower.Core/Models/Automation.cs`
- Create: `src/Tower.Core/Automations/AutomationService.cs`
- Create: `src/Tower.Core/Automations/AutomationTelegramHandler.cs`
- Modify: `src/Tower.Core/Data/TowerDbContext.cs` (add DbSet after `Todos`, ~line 13)
- Modify: `src/Tower.Core/Telegram/TelegramHub.cs` (step-6 dispatch gate, ~line 469)
- Modify: `src/Tower/Program.cs` (using, DI, table SQL, handler Register)
- Test: `tests/Tower.Core.Tests/AutomationTests.cs`

**Interfaces:**
- Consumes: `TuyaServiceClient.SendCommandAsync(string, TuyaCommandRequest)`, `PiAgentClient.ShutdownAsync(string baseUrl)`, `TelegramHub.RegisterCommandHandler(string, Func<string,long,CancellationToken,Task>)`, `TowerConfig.Devices` (`DeviceConfig { Id, Name, Kind, BaseUrl }`), `TuyaDevice { DeviceId, Name, DeviceType, Room }`.
- Produces (Task 2 relies on): `record AutomationAction(string Kind, string Target, string TargetName, bool On, int? Temp)`; `AutomationService` members `ListAsync()`, `ListTuyaDevicesAsync()`, `PiDevices()`, `SaveAsync(Automation)`, `DeleteAsync(int)`, `RunAsync(Automation) → Task<string>`, static `ParseActions(string)` / `SerializeActions(List<AutomationAction>)`; model `Automation { Id, Name, ActionsJson }`.

- [ ] **Step 1: Write the failing test**

`tests/Tower.Core.Tests/AutomationTests.cs`:

```csharp
using Tower.Core.Automations;
using Xunit;
namespace Tower.Core.Tests;
public class AutomationTests {
    [Fact] public void Actions_round_trip_through_json() {
        var actions = new List<AutomationAction> {
            new("tuya", "dev1", "Baby Room Bulb", true, null),
            new("tuya", "dev2", "Baby Room AC", true, 27),
            new("pi", "atomtv", "AtomTV", false, null),
        };
        var json = AutomationService.SerializeActions(actions);
        Assert.Equal(actions, AutomationService.ParseActions(json));
    }
    [Fact] public void Malformed_json_parses_to_empty_list() {
        Assert.Empty(AutomationService.ParseActions("not json"));
        Assert.Empty(AutomationService.ParseActions(""));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests --filter AutomationTests 2>&1 | tail -5`
Expected: build FAILURE — `Tower.Core.Automations` / `AutomationService` do not exist (unless drafts are present; then tests pass and that's fine — verify draft content in Step 3 regardless).

- [ ] **Step 3: Create the model and service**

`src/Tower.Core/Models/Automation.cs`:

```csharp
namespace Tower.Core.Models;

public class Automation
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string ActionsJson { get; set; } = "[]";
}
```

`src/Tower.Core/Automations/AutomationService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Pi;
using Tower.Core.Tuya;

namespace Tower.Core.Automations;

/// <summary>
/// One step of an automation.
/// Kind: "tuya" (Target = TuyaDevice.DeviceId) or "pi" (Target = DeviceConfig.Id; only supports shutdown).
/// </summary>
public record AutomationAction(string Kind, string Target, string TargetName, bool On, int? Temp);

public class AutomationService(
    TowerDbContext db,
    TuyaServiceClient tuya,
    PiAgentClient pi,
    IOptions<TowerConfig> cfg)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<AutomationAction> ParseActions(string json)
    {
        try { return JsonSerializer.Deserialize<List<AutomationAction>>(json, Json) ?? []; }
        catch { return []; }
    }

    public static string SerializeActions(List<AutomationAction> actions)
        => JsonSerializer.Serialize(actions, Json);

    public Task<List<Automation>> ListAsync()
        => db.Automations.OrderBy(a => a.Name).ToListAsync();

    public Task<List<TuyaDevice>> ListTuyaDevicesAsync()
        => db.TuyaDevices.OrderBy(d => d.Room).ThenBy(d => d.Name).ToListAsync();

    public List<DeviceConfig> PiDevices()
        => cfg.Value.Devices.Where(d => d.Kind == "pi" && !string.IsNullOrWhiteSpace(d.BaseUrl)).ToList();

    public async Task SaveAsync(Automation a)
    {
        if (a.Id == 0) db.Automations.Add(a);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var a = await db.Automations.FindAsync(id);
        if (a is not null)
        {
            db.Automations.Remove(a);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Runs the automation whose name matches case-insensitively. Returns a human-readable summary.</summary>
    public async Task<string> RunByNameAsync(string name)
    {
        var all  = await ListAsync();
        var auto = all.FirstOrDefault(a =>
            string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (auto is null)
        {
            return all.Count == 0
                ? $"No automation named \"{name}\" — none are set up yet."
                : $"No automation named \"{name}\". Available: {string.Join(", ", all.Select(a => a.Name))}";
        }
        return await RunAsync(auto);
    }

    public async Task<string> RunAsync(Automation auto)
    {
        var actions = ParseActions(auto.ActionsJson);
        if (actions.Count == 0) return $"{auto.Name}: no actions configured.";

        var tuyaTypes = await db.TuyaDevices
            .ToDictionaryAsync(d => d.DeviceId, d => d.DeviceType);

        var results = new List<string>();
        foreach (var act in actions)
        {
            bool ok;
            string what;

            if (act.Kind == "pi")
            {
                var dev = cfg.Value.Devices.FirstOrDefault(d => d.Id == act.Target);
                ok   = dev?.BaseUrl is not null && await pi.ShutdownAsync(dev.BaseUrl);
                what = $"{act.TargetName} shutdown";
            }
            else if (tuyaTypes.TryGetValue(act.Target, out var type) && type == TuyaDeviceType.AcRemote)
            {
                ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                    Ac: new AcCommandPayload(Power: act.On, Temp: act.On ? act.Temp : null)));
                what = act.On
                    ? $"{act.TargetName} ON{(act.Temp is int t ? $" {t}°C" : "")}"
                    : $"{act.TargetName} OFF";
            }
            else
            {
                tuyaTypes.TryGetValue(act.Target, out var t2);
                // ponytail: power DPS by device type (bulbs=20, everything else=1); read live DPS if a device ever differs
                var key = t2 == TuyaDeviceType.Light ? "20" : "1";
                ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                    Dps: new Dictionary<string, object> { [key] = act.On }));
                what = $"{act.TargetName} {(act.On ? "ON" : "OFF")}";
            }

            results.Add($"{(ok ? "✅" : "❌")} {what}");
        }

        return $"{auto.Name}:\n{string.Join("\n", results)}";
    }
}
```

`src/Tower.Core/Automations/AutomationTelegramHandler.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Telegram;

namespace Tower.Core.Automations;

public sealed class AutomationTelegramHandler(
    TelegramHub hub,
    IServiceScopeFactory scopes)
{
    public void Register()
    {
        hub.RegisterCommandHandler("run", HandleRunAsync);   // plain "run Bedtime"
        hub.RegisterCommandHandler("/run", HandleRunAsync);  // "/run Bedtime"
    }

    private async Task HandleRunAsync(string text, long chatId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AutomationService>();

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            var names = (await svc.ListAsync()).Select(a => a.Name).ToList();
            await hub.SendAsync(TgAudience.Chat, chatId,
                names.Count == 0
                    ? "No automations set up yet. Create one on the Tower Automations page."
                    : $"Usage: run <name>\nAvailable: {string.Join(", ", names)}",
                null, ct);
            return;
        }

        var result = await svc.RunByNameAsync(parts[1]);
        await hub.SendAsync(TgAudience.Chat, chatId, result, null, ct);
    }
}
```

- [ ] **Step 4: Wire DbContext, TelegramHub, Program.cs**

`src/Tower.Core/Data/TowerDbContext.cs` — after the `Todos` DbSet line add:

```csharp
    public DbSet<Automation> Automations => Set<Automation>();
```

`src/Tower.Core/Telegram/TelegramHub.cs` — replace the step-6 gate:

```csharp
        // 6. Dispatch text commands to registered internal handlers (admin only)
        if (!u.IsCallback && u.Text.StartsWith('/') && _commandHandlers.Count > 0)
```

with:

```csharp
        // 6. Dispatch text commands to registered internal handlers (admin only).
        //    Commands may be registered with a slash ("/todo") or without ("run") —
        //    the first token of the message must match the registered form exactly.
        if (!u.IsCallback && u.Text.Length > 0 && _commandHandlers.Count > 0)
```

(The exact-token comparison below it is unchanged, so `/todo` still requires the slash.)

`src/Tower/Program.cs` — four edits:

1. Add `using Tower.Core.Automations;` with the other `Tower.Core.*` usings (before `using Tower.Core.Backup;`).
2. After the Todo services block (`builder.Services.AddHostedService<TodoReminderWorker>();`) add:

```csharp
// ── Automations ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<AutomationService>();
builder.Services.AddSingleton<AutomationTelegramHandler>();
```

3. In the DB-init scope, after the `Todos` `ExecuteSqlRaw` block add:

```csharp
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS Automations (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            ActionsJson TEXT NOT NULL DEFAULT '[]'
        );
    ");
```

4. After `app.Services.GetRequiredService<TodoTelegramHandler>().Register();` add:

```csharp
app.Services.GetRequiredService<AutomationTelegramHandler>().Register();
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj 2>&1 | tail -3`
Expected: Build succeeded, 0 errors.

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests --filter AutomationTests 2>&1 | tail -5`
Expected: PASS (2 tests). Also run the full suite once: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests 2>&1 | tail -5` — no regressions (TelegramHub change must not break `TelegramCallbackDispatchTests`).

- [ ] **Step 6: Commit**

```bash
cd /home/atom/dev/Tower
git add src/Tower.Core/Models/Automation.cs src/Tower.Core/Automations/ \
        src/Tower.Core/Data/TowerDbContext.cs src/Tower.Core/Telegram/TelegramHub.cs \
        src/Tower/Program.cs tests/Tower.Core.Tests/AutomationTests.cs
git commit -m "Add automation backend: model, service, Telegram run command"
```

---

### Task 2: UI — Automations page + nav link

**Files:**
- Create: `src/Tower/Components/Pages/Automations.razor`
- Create: `src/Tower/Components/Pages/Automations.razor.css`
- Modify: `src/Tower/Components/Layout/NavMenu.razor` (insert after the Tuya NavLink, before the Solar NavLink)

**Interfaces:**
- Consumes (from Task 1): `AutomationService` (`ListAsync`, `ListTuyaDevicesAsync`, `PiDevices`, `SaveAsync`, `DeleteAsync`, `RunAsync`, static `ParseActions`/`SerializeActions`), `AutomationAction(Kind, Target, TargetName, On, Temp)`, `Automation { Id, Name, ActionsJson }`. Also `TuyaDevice`/`TuyaDeviceType` (Tower.Core.Models) and `DeviceConfig` (namespace `Tower`, covered by _Imports).
- Produces: page at `/automations`; nav entry "Automations" under Smart Assistant.

- [ ] **Step 1: Create the page**

`src/Tower/Components/Pages/Automations.razor`:

```razor
@page "/automations"
@using Tower.Core.Automations
@using Tower.Core.Models
@inject AutomationService AutoSvc

<PageTitle>Tower — Automations</PageTitle>

<div class="auto-page">

    <div class="auto-header">
        <h1 class="page-title">Automations</h1>
        @if (_editor is null)
        {
            <button class="btn-primary btn-sm" @onclick="NewAutomation">New Automation</button>
        }
    </div>

    <div class="auto-hint">
        Trigger from Telegram: send <code>run &lt;name&gt;</code> to the Tower bot (case-insensitive), e.g. <code>run Bedtime</code>.
    </div>

    @if (_runResult is not null)
    {
        <div class="auto-result">@_runResult</div>
    }

    @if (_editor is not null)
    {
        <div class="auto-editor">
            <div class="auto-editor-name">
                <label>Name</label>
                <input class="config-input" type="text" placeholder="e.g. Bedtime" @bind="_editor.Name" />
            </div>

            <table class="atom-table">
                <thead><tr><th>Device</th><th>Action</th><th>Temp (AC)</th><th></th></tr></thead>
                <tbody>
                    @foreach (var row in _editor.Rows)
                    {
                        <tr>
                            <td>
                                <select class="config-input" value="@row.Key"
                                        @onchange="e => SetDevice(row, e.Value?.ToString() ?? string.Empty)">
                                    <option value="">— select device —</option>
                                    @foreach (var d in _tuyaDevices)
                                    {
                                        <option value="@($"tuya:{d.DeviceId}")">@DevLabel(d)</option>
                                    }
                                    @foreach (var p in _piDevices)
                                    {
                                        <option value="@($"pi:{p.Id}")">@p.Name (Pi)</option>
                                    }
                                </select>
                            </td>
                            <td>
                                @if (row.Kind == "pi")
                                {
                                    <span class="text-muted">Shutdown</span>
                                }
                                else if (row.Kind == "tuya")
                                {
                                    <select class="config-input" value="@(row.On ? "on" : "off")"
                                            @onchange='e => row.On = e.Value?.ToString() == "on"'>
                                        <option value="on">Turn ON</option>
                                        <option value="off">Turn OFF</option>
                                    </select>
                                }
                            </td>
                            <td>
                                @if (row.IsAc && row.On)
                                {
                                    <input class="config-input auto-temp" type="number" min="16" max="30" @bind="row.Temp" />
                                }
                            </td>
                            <td>
                                <button class="btn-secondary btn-sm" @onclick="() => _editor.Rows.Remove(row)">✕</button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>

            <div class="auto-editor-actions">
                <button class="btn-secondary btn-sm" @onclick="() => _editor.Rows.Add(new Row())">+ Add action</button>
                <span class="auto-spacer"></span>
                <button class="btn-primary btn-sm" @onclick="SaveAsync"
                        disabled="@(string.IsNullOrWhiteSpace(_editor.Name) || !_editor.Rows.Any(r => r.Kind != ""))">
                    Save
                </button>
                <button class="btn-secondary btn-sm" @onclick="() => _editor = null">Cancel</button>
            </div>
        </div>
    }

    @if (_automations.Count == 0 && _editor is null)
    {
        <div class="auto-empty">No automations yet. Click <strong>New Automation</strong> to create one.</div>
    }
    else if (_automations.Count > 0)
    {
        <table class="atom-table">
            <thead><tr><th>Name</th><th>Actions</th><th></th></tr></thead>
            <tbody>
                @foreach (var a in _automations)
                {
                    <tr>
                        <td class="auto-name">@a.Name</td>
                        <td class="text-muted">@Summary(a)</td>
                        <td>
                            <div class="auto-row-btns">
                                <button class="btn-primary btn-sm" disabled="@_running" @onclick="() => RunNowAsync(a)">
                                    @(_running ? "…" : "Run")
                                </button>
                                <button class="btn-secondary btn-sm" @onclick="() => Edit(a)">Edit</button>
                                <button class="btn-secondary btn-sm" @onclick="() => DeleteAsync(a)">Delete</button>
                            </div>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    }
</div>

@code {
    private List<Automation>   _automations = [];
    private List<TuyaDevice>   _tuyaDevices = [];
    private List<DeviceConfig> _piDevices   = [];
    private Editor?            _editor;
    private string?            _runResult;
    private bool               _running;

    protected override async Task OnInitializedAsync()
    {
        _tuyaDevices = await AutoSvc.ListTuyaDevicesAsync();
        _piDevices   = AutoSvc.PiDevices();
        await ReloadAsync();
    }

    private async Task ReloadAsync() => _automations = await AutoSvc.ListAsync();

    private static string DevLabel(TuyaDevice d)
        => string.IsNullOrEmpty(d.Room) ? d.Name : $"{d.Room} — {d.Name}";

    private static string Summary(Automation a)
    {
        var actions = AutomationService.ParseActions(a.ActionsJson);
        if (actions.Count == 0) return "no actions";
        return string.Join(", ", actions.Select(x =>
            x.Kind == "pi" ? $"{x.TargetName} shutdown"
            : x.On         ? $"{x.TargetName} on{(x.Temp is int t ? $" {t}°C" : "")}"
                           : $"{x.TargetName} off"));
    }

    private void NewAutomation()
    {
        _editor = new Editor { Rows = { new Row() } };
        _runResult = null;
    }

    private void Edit(Automation a)
    {
        var rows = AutomationService.ParseActions(a.ActionsJson).Select(x => new Row
        {
            Kind = x.Kind, Target = x.Target, TargetName = x.TargetName,
            On   = x.On,   Temp   = x.Temp ?? 24,
            IsAc = _tuyaDevices.Any(d => d.DeviceId == x.Target && d.DeviceType == TuyaDeviceType.AcRemote)
        }).ToList();
        if (rows.Count == 0) rows.Add(new Row());
        _editor = new Editor { Id = a.Id, Name = a.Name, Rows = rows };
        _runResult = null;
    }

    private void SetDevice(Row row, string key)
    {
        var parts = key.Split(':', 2);
        if (parts.Length < 2)
        {
            row.Kind = ""; row.Target = ""; row.TargetName = ""; row.IsAc = false;
            return;
        }
        row.Kind   = parts[0];
        row.Target = parts[1];
        if (row.Kind == "pi")
        {
            row.TargetName = _piDevices.FirstOrDefault(x => x.Id == row.Target)?.Name ?? row.Target;
            row.IsAc = false;
            row.On   = false; // Pi only supports shutdown
        }
        else
        {
            var d = _tuyaDevices.FirstOrDefault(x => x.DeviceId == row.Target);
            row.TargetName = d?.Name ?? row.Target;
            row.IsAc = d?.DeviceType == TuyaDeviceType.AcRemote;
        }
    }

    private async Task SaveAsync()
    {
        if (_editor is null) return;
        var actions = _editor.Rows
            .Where(r => r.Kind != "" && r.Target != "")
            .Select(r => new AutomationAction(r.Kind, r.Target, r.TargetName,
                r.Kind != "pi" && r.On,
                r.IsAc && r.On ? r.Temp : null))
            .ToList();

        var target = _editor.Id == 0 ? new Automation() : _automations.First(a => a.Id == _editor.Id);
        target.Name        = _editor.Name.Trim();
        target.ActionsJson = AutomationService.SerializeActions(actions);
        await AutoSvc.SaveAsync(target);
        _editor = null;
        await ReloadAsync();
    }

    private async Task DeleteAsync(Automation a)
    {
        await AutoSvc.DeleteAsync(a.Id);
        await ReloadAsync();
    }

    private async Task RunNowAsync(Automation a)
    {
        _running = true;
        _runResult = null;
        StateHasChanged();
        _runResult = await AutoSvc.RunAsync(a);
        _running = false;
    }

    private class Editor
    {
        public int       Id;
        public string    Name = "";
        public List<Row> Rows = [];
    }

    private class Row
    {
        public string Kind       = "";
        public string Target     = "";
        public string TargetName = "";
        public bool   On         = true;
        public int    Temp       = 24;
        public bool   IsAc;
        public string Key => Kind == "" ? "" : $"{Kind}:{Target}";
    }
}
```

`src/Tower/Components/Pages/Automations.razor.css`:

```css
.auto-page { max-width: 860px; }
.auto-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 0.6rem; }
.auto-hint { font-size: 0.8rem; color: var(--faint); margin-bottom: 1rem; }
.auto-hint code { color: var(--accent-fg); }
.auto-result {
    background: var(--accent-bg); border-left: 3px solid var(--accent);
    padding: 0.5rem 0.75rem; border-radius: var(--r-sm);
    font-size: 0.82rem; margin-bottom: 1rem; white-space: pre-line;
}
.auto-editor {
    background: var(--surface-1); border: 1px solid var(--border);
    border-radius: var(--r); padding: 1rem; margin-bottom: 1.25rem;
}
.auto-editor-name { display: flex; align-items: center; gap: 0.6rem; margin-bottom: 0.75rem; }
.auto-editor-name label { font-size: 0.8rem; color: var(--faint); }
.auto-editor-actions { display: flex; gap: 0.5rem; margin-top: 0.75rem; align-items: center; }
.auto-spacer { flex: 1; }
.auto-temp { width: 70px; }
.auto-name { font-weight: 600; }
.auto-row-btns { display: flex; gap: 0.4rem; justify-content: flex-end; }
.auto-empty { color: var(--faint); font-size: 0.85rem; padding: 1rem 0; }
```

- [ ] **Step 2: Add the nav link**

`src/Tower/Components/Layout/NavMenu.razor` — insert between the Tuya `</NavLink>` (line ~103) and the Solar `<NavLink ... href="/solar">`:

```razor
        <NavLink class="rail-link" href="/automations" Match="NavLinkMatch.Prefix">
            <span class="rail-icon">
                <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
                    <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"></polygon>
                </svg>
            </span>
            <span class="rail-label">Automations</span>
        </NavLink>
```

- [ ] **Step 3: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj 2>&1 | tail -3`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
cd /home/atom/dev/Tower
git add src/Tower/Components/Pages/Automations.razor src/Tower/Components/Pages/Automations.razor.css \
        src/Tower/Components/Layout/NavMenu.razor
git commit -m "Add Automations page and nav link"
```

---

### Task 3: Deploy and verify

**Files:** none (operations only).

**Interfaces:**
- Consumes: `deploy.sh` (stop → publish → start pattern), service `tower`, app at `http://localhost:8888`.

- [ ] **Step 1: Push and deploy**

```bash
cd /home/atom/dev/Tower
git push origin HEAD
bash deploy.sh
```

Expected: publish succeeds, `tower` service restarts.

- [ ] **Step 2: Verify service and page**

Run: `systemctl is-active tower && sleep 5 && curl -s -o /dev/null -w "%{http_code}" http://localhost:8888/automations`
Expected: `active` then `200`.

Run: `sqlite3 /home/atom/Tower/tower.db "SELECT name FROM sqlite_master WHERE name='Automations';"`
Expected: `Automations`.

Run: `journalctl -u tower -n 20 --no-pager | tail -10`
Expected: normal startup, no exceptions.

- [ ] **Step 3: Report**

Report deploy status. Telegram E2E (`run <name>`) requires John to create the Bedtime automation on `/automations` first — note this in the final summary rather than attempting it.
