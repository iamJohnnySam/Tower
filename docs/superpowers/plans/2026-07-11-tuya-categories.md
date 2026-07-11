# Tuya Categories & Full Controls — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Replace hardcoded per-`DeviceType` rendering on the Tuya page with a code-defined **category** system (each category declares capabilities as DPS codes; a device is assigned a category), giving the color bulb full brightness/warm-cool/color control, AC/IR remotes temp+mode+fan, and making new devices "pick a category."

**Architecture:** New `TuyaCategory` registry + `TuyaColor` HSV codec in `Tower.Core`; a nullable `Category` column on `TuyaDevice`; `TuyaDeviceService`/`AutomationService` resolve capabilities via the registry; `Tuya.razor` renders one capability-driven card and a category dropdown. No tinytuya service changes (`/command` already forwards arbitrary `dps` + the `ac` payload).

**Tech Stack:** .NET 10 Blazor interactive server, EF Core/SQLite, xunit.v3. Repo `/home/atom/dev/Tower`, branch `main`.

## Global Constraints

- No new NuGet packages. No changes to `tinytuya_service/`.
- Keep the `TuyaDeviceType` enum and column (legacy default mapping only; it no longer drives UI).
- Tuya v3.3+ colour DPS format = 12 hex chars `HHHHSSSSVVVV` (H 0–360, S 0–1000, V 0–1000).
- `TuyaServiceClient.SendCommandAsync` never throws; treat `false` as failure, don't abort a run.
- Build: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`. Tests: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests`.

---

### Task 1: Backend — category registry, color codec, model/service wiring, tests

**Files:**
- Create: `src/Tower.Core/Tuya/TuyaCategories.cs`, `src/Tower.Core/Tuya/TuyaColor.cs`
- Modify: `src/Tower.Core/Models/TuyaDevice.cs` (add `Category`)
- Modify: `src/Tower.Core/Tuya/TuyaModels.cs` (add `Category` to `TuyaDeviceView`)
- Modify: `src/Tower.Core/Tuya/TuyaDeviceService.cs` (populate/persist `Category`)
- Modify: `src/Tower.Core/Automations/AutomationService.cs` (resolve power-DPS/AC/eligibility via category)
- Modify: `src/Tower/Program.cs` (add `Category` column)
- Test: `tests/Tower.Core.Tests/TuyaCategoryTests.cs`

**Interfaces produced (Task 2 consumes):** `TuyaCategory` record with `Key,Name,PowerDps,BrightnessDps,ColorTempDps,ColorDps,WorkModeDps,Gangs,Ac,Energy,Sensors,Actuatable`; `TuyaCategories.All` (ordered) and `TuyaCategories.Resolve(string? category, TuyaDeviceType legacy) → TuyaCategory`; `TuyaColor.HsvHexFromRgb(string)`/`RgbHexFromHsvHex(string)`; `TuyaDeviceView.Category`; `EnergySpec(PowerDps,VoltageDps,CurrentDps)`; `SensorReadout(Label,Dps,Unit,Scale)`.

- [ ] **Step 1: Write the failing tests**

`tests/Tower.Core.Tests/TuyaCategoryTests.cs`:

```csharp
using Tower.Core.Models;
using Tower.Core.Tuya;
using Xunit;
namespace Tower.Core.Tests;

public class TuyaCategoryTests
{
    [Theory]
    [InlineData("#ff0000", "000003e803e8")]
    [InlineData("#00ff00", "007803e803e8")]
    [InlineData("#0000ff", "00f003e803e8")]
    public void Rgb_to_tuya_hsv_hex(string rgb, string expected)
        => Assert.Equal(expected, TuyaColor.HsvHexFromRgb(rgb));

    [Fact] public void Tuya_hsv_hex_back_to_rgb_primary()
        => Assert.Equal("#ff0000", TuyaColor.RgbHexFromHsvHex("000003e803e8"));

    [Fact] public void Color_round_trips_within_tolerance()
    {
        var hex = TuyaColor.HsvHexFromRgb("#3d7fb0");
        var rgb = TuyaColor.RgbHexFromHsvHex(hex);
        Assert.StartsWith("#", rgb);
        Assert.Equal(7, rgb.Length);
    }

    [Fact] public void Resolve_uses_explicit_category()
        => Assert.Equal("color_bulb", TuyaCategories.Resolve("color_bulb", TuyaDeviceType.Plug).Key);

    [Fact] public void Resolve_falls_back_to_legacy_type()
    {
        Assert.Equal("dimmable_light", TuyaCategories.Resolve(null, TuyaDeviceType.Light).Key);
        Assert.Equal("ac_remote",      TuyaCategories.Resolve(null, TuyaDeviceType.AcRemote).Key);
        Assert.Equal("switch_4",       TuyaCategories.Resolve(null, TuyaDeviceType.Switch4).Key);
        Assert.Equal("plug",           TuyaCategories.Resolve("nonsense", TuyaDeviceType.Plug).Key);
    }

    [Fact] public void Actuatable_excludes_readonly_sensors()
    {
        Assert.True(TuyaCategories.Resolve("color_bulb", TuyaDeviceType.Plug).Actuatable);
        Assert.True(TuyaCategories.Resolve("switch_2", TuyaDeviceType.Plug).Actuatable);
        Assert.False(TuyaCategories.Resolve("climate_sensor", TuyaDeviceType.Plug).Actuatable);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests --filter TuyaCategoryTests 2>&1 | tail -5`
Expected: build failure — `TuyaCategories`/`TuyaColor` do not exist.

- [ ] **Step 3: Create `TuyaColor.cs`**

```csharp
using System.Globalization;

namespace Tower.Core.Tuya;

/// <summary>Tuya v3.3+ colour DPS codec. Hex = HHHHSSSSVVVV (H 0-360, S 0-1000, V 0-1000).</summary>
public static class TuyaColor
{
    public static string HsvHexFromRgb(string rgbHex)
    {
        var (r, g, b) = ParseRgb(rgbHex);
        var (h, s, v) = RgbToHsv(r, g, b);
        return $"{(int)Math.Round(h):x4}{(int)Math.Round(s * 1000):x4}{(int)Math.Round(v * 1000):x4}";
    }

    public static string RgbHexFromHsvHex(string tuyaHex)
    {
        if (string.IsNullOrEmpty(tuyaHex) || tuyaHex.Length < 12) return "#ffffff";
        int h = int.Parse(tuyaHex.Substring(0, 4), NumberStyles.HexNumber);
        int s = int.Parse(tuyaHex.Substring(4, 4), NumberStyles.HexNumber);
        int v = int.Parse(tuyaHex.Substring(8, 4), NumberStyles.HexNumber);
        var (r, g, b) = HsvToRgb(h, s / 1000.0, v / 1000.0);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    static (int r, int g, int b) ParseRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (255, 255, 255);
        return (Convert.ToInt32(hex.Substring(0, 2), 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    static (double h, double s, double v) RgbToHsv(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min, h = 0;
        if (d != 0)
        {
            if (max == rf) h = 60 * (((gf - bf) / d) % 6);
            else if (max == gf) h = 60 * (((bf - rf) / d) + 2);
            else h = 60 * (((rf - gf) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
```

- [ ] **Step 4: Create `TuyaCategories.cs`**

```csharp
using Tower.Core.Models;

namespace Tower.Core.Tuya;

public record EnergySpec(string PowerDps, string VoltageDps, string CurrentDps);
public record SensorReadout(string Label, string Dps, string Unit, double Scale);

public record TuyaCategory(
    string Key,
    string Name,
    string? PowerDps = null,
    string? BrightnessDps = null,
    string? ColorTempDps = null,
    string? ColorDps = null,
    string? WorkModeDps = null,
    int Gangs = 0,
    bool Ac = false,
    EnergySpec? Energy = null,
    IReadOnlyList<SensorReadout>? Sensors = null,
    TuyaDeviceType Legacy = TuyaDeviceType.Plug)
{
    public bool Actuatable => PowerDps != null || Gangs > 0 || Ac;
}

public static class TuyaCategories
{
    public static readonly IReadOnlyList<TuyaCategory> All = new[]
    {
        new TuyaCategory("plug", "Plug", PowerDps: "1", Legacy: TuyaDeviceType.Plug),
        new TuyaCategory("energy_plug", "Energy Plug", PowerDps: "1",
            Energy: new EnergySpec("19", "20", "18"), Legacy: TuyaDeviceType.Plug),
        new TuyaCategory("dimmable_light", "Dimmable Light", PowerDps: "20", BrightnessDps: "22",
            Legacy: TuyaDeviceType.Light),
        new TuyaCategory("color_bulb", "Color Bulb", PowerDps: "20", BrightnessDps: "22",
            ColorTempDps: "23", ColorDps: "24", WorkModeDps: "21", Legacy: TuyaDeviceType.Light),
        new TuyaCategory("switch_1", "Switch (1-gang)", Gangs: 1, Legacy: TuyaDeviceType.Switch1),
        new TuyaCategory("switch_2", "Switch (2-gang)", Gangs: 2, Legacy: TuyaDeviceType.Switch2),
        new TuyaCategory("switch_4", "Switch (4-gang)", Gangs: 4, Legacy: TuyaDeviceType.Switch4),
        new TuyaCategory("ac_remote", "AC / IR Remote", Ac: true, Legacy: TuyaDeviceType.AcRemote),
        new TuyaCategory("presence_sensor", "Presence Sensor",
            Sensors: new[] { new SensorReadout("Presence", "1", "", 1), new SensorReadout("Illuminance", "11", "lux", 1) },
            Legacy: TuyaDeviceType.Sensor),
        new TuyaCategory("climate_sensor", "Climate Sensor",
            Sensors: new[] { new SensorReadout("Temp", "101", "°C", 0.1), new SensorReadout("Humidity", "102", "%", 1) },
            Legacy: TuyaDeviceType.Sensor),
    };

    static readonly Dictionary<string, TuyaCategory> ByKey = All.ToDictionary(c => c.Key);

    public static TuyaCategory Resolve(string? category, TuyaDeviceType legacy)
    {
        if (category is not null && ByKey.TryGetValue(category, out var c)) return c;
        return legacy switch
        {
            TuyaDeviceType.Light    => ByKey["dimmable_light"],
            TuyaDeviceType.AcRemote => ByKey["ac_remote"],
            TuyaDeviceType.Switch1  => ByKey["switch_1"],
            TuyaDeviceType.Switch2  => ByKey["switch_2"],
            TuyaDeviceType.Switch4  => ByKey["switch_4"],
            TuyaDeviceType.Sensor   => ByKey["presence_sensor"],
            _                       => ByKey["plug"],
        };
    }
}
```

- [ ] **Step 5: Add `Category` to model + view + service**

`src/Tower.Core/Models/TuyaDevice.cs` — add after `Room`:

```csharp
    public string?        Category   { get; set; }
```

`src/Tower.Core/Tuya/TuyaModels.cs` — add a `Category` param to `TuyaDeviceView` (after `Room`):

```csharp
public record TuyaDeviceView(
    int                              Id,
    string                           DeviceId,
    string                           Name,
    TuyaDeviceType                   DeviceType,
    string?                          Room,
    string?                          Category,
    int                              SortOrder,
    bool                             Reachable,
    Dictionary<string, JsonElement>  Dps);
```

`src/Tower.Core/Tuya/TuyaDeviceService.cs` — in `GetDeviceViewsAsync`, add `Category: d.Category,` after `Room: d.Room,`; in `SaveDeviceAsync`'s `else` block add `existing.Category = device.Category;`.

- [ ] **Step 6: Wire `AutomationService` to categories**

In `src/Tower.Core/Automations/AutomationService.cs`:

Replace `ListTuyaDevicesAsync`:

```csharp
    public async Task<List<TuyaDevice>> ListTuyaDevicesAsync()
    {
        var all = await db.TuyaDevices.OrderBy(d => d.Room).ThenBy(d => d.Name).ToListAsync();
        return all.Where(d => TuyaCategories.Resolve(d.Category, d.DeviceType).Actuatable).ToList();
    }
```

In `RunAsync`, replace the `tuyaTypes` load and the two Tuya branches:

```csharp
        var devs = await db.TuyaDevices.ToDictionaryAsync(d => d.DeviceId, d => d);
```

```csharp
            if (act.Kind == "pi")
            {
                var dev = cfg.Value.Devices.FirstOrDefault(d => d.Id == act.Target);
                ok   = dev?.BaseUrl is not null && await pi.ShutdownAsync(dev.BaseUrl);
                what = $"{act.TargetName} shutdown";
            }
            else
            {
                var cat = devs.TryGetValue(act.Target, out var td)
                    ? TuyaCategories.Resolve(td.Category, td.DeviceType) : null;
                if (cat?.Ac == true)
                {
                    ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                        Ac: new AcCommandPayload(Power: act.On, Temp: act.On && act.Temp is int tc ? Math.Clamp(tc, 16, 30) : null)));
                    what = act.On
                        ? $"{act.TargetName} ON{(act.Temp is int t ? $" {Math.Clamp(t, 16, 30)}°C" : "")}"
                        : $"{act.TargetName} OFF";
                }
                else
                {
                    var key = cat?.PowerDps ?? "1";
                    ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                        Dps: new Dictionary<string, object> { [key] = act.On }));
                    what = $"{act.TargetName} {(act.On ? "ON" : "OFF")}";
                }
            }
```

(Delete the now-unused `TuyaDeviceType`-based branches. `using Tower.Core.Tuya;` is already present.)

- [ ] **Step 7: Add `Category` column in `Program.cs`**

In the DB-init `using` scope (near the other `ExecuteSqlRaw` blocks), add:

```csharp
    try { db.Database.ExecuteSqlRaw("ALTER TABLE TuyaDevices ADD COLUMN Category TEXT"); }
    catch { /* column already exists */ }
```

- [ ] **Step 8: Build + run tests**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj 2>&1 | tail -3` → 0 errors.
Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Core.Tests 2>&1 | tail -6` → all pass (new TuyaCategoryTests + existing AutomationTests, no regressions).

- [ ] **Step 9: Commit**

```bash
cd /home/atom/dev/Tower
git add src/Tower.Core/Tuya/TuyaCategories.cs src/Tower.Core/Tuya/TuyaColor.cs \
        src/Tower.Core/Models/TuyaDevice.cs src/Tower.Core/Tuya/TuyaModels.cs \
        src/Tower.Core/Tuya/TuyaDeviceService.cs src/Tower.Core/Automations/AutomationService.cs \
        src/Tower/Program.cs tests/Tower.Core.Tests/TuyaCategoryTests.cs
git commit -m "Tuya: category registry + color codec, wire model/service/automations"
```

---

### Task 2: UI — capability-driven Tuya card + category dropdown

**Files:**
- Modify: `src/Tower/Components/Pages/Tuya.razor` (replace the per-`DeviceType` card region + assign-panel type select; add category select + capability helpers)
- Modify: `src/Tower/Components/Pages/Tuya.razor.css` (color/slider/readout styles)
- Modify: `src/Tower/Components/Pages/Automations.razor` (resolve `IsAc` via category)

**Interfaces consumed:** Task 1's `TuyaCategories.All`/`Resolve`, `TuyaCategory`, `TuyaColor`, `TuyaDeviceView.Category`.

**Razor quoting note (applies throughout this task):** a Razor attribute delimited by `"`
cannot contain nested unescaped `"`. Where a C# string literal is needed inside an attribute
value, either precompute a local above the markup (e.g. `var acOn = GetDpsBool(dev, "1");`) or
delimit that attribute with single quotes (`@onchange='...'`). Never emit `\"` in `.razor`.
The Step 7 build must pass with 0 errors — fix any quoting the fragments below missed.

- [ ] **Step 1: Read the current file**

Read `src/Tower/Components/Pages/Tuya.razor` fully. The device-card region is lines ~146–373 (the `@foreach (var group ...)` down to the closing before `<div class="tuya-updated">`), the assign-panel type `<select>` is ~99–108, and the `DeviceAssignment` class + `SendAsync`/`Get*` helpers are in `@code`. Preserve all scan/load/assign scaffolding; change only what the steps below specify.

- [ ] **Step 2: Replace the assign-panel device-type select with a category select**

Replace the `<td>` containing the `Enum.Parse<TuyaDeviceType>` select (~98–109) with:

```razor
                            <td>
                                <select class="config-input" value="@a.Category"
                                        @onchange="e => a.Category = e.Value?.ToString() ?? string.Empty">
                                    @foreach (var c in TuyaCategories.All)
                                    {
                                        <option value="@c.Key">@c.Name</option>
                                    }
                                </select>
                            </td>
```

In the `DeviceAssignment` class, replace `public TuyaDeviceType DeviceType ... = TuyaDeviceType.Plug;` with `public string Category { get; set; } = "plug";`. In `SaveAssignmentsAsync`, replace `DeviceType = a.DeviceType,` with `Category = a.Category,` (leave `DeviceType` to its enum default). Add `@using Tower.Core.Tuya` at the top if not present.

- [ ] **Step 3: Replace the device-card region with one capability-driven card**

Replace the entire `@foreach (var dev in group)` body (the whole per-`DeviceType` if/else chain) with:

```razor
                @foreach (var dev in group)
                {
                    var cat = TuyaCategories.Resolve(dev.Category, dev.DeviceType);
                    <div class="tuya-card @(dev.Reachable ? "" : "tuya-card-unreachable")">
                        <div class="tuya-card-top">
                            <div class="tuya-card-name">@dev.Name</div>
                            <select class="tuya-cat-select" value="@cat.Key"
                                    @onchange="e => SaveCategoryAsync(dev, e.Value?.ToString() ?? cat.Key)">
                                @foreach (var c in TuyaCategories.All)
                                {
                                    <option value="@c.Key">@c.Name</option>
                                }
                            </select>
                        </div>

                        @if (!dev.Reachable)
                        {
                            <div class="tuya-key-entry">
                                <input class="tuya-key-input" type="text" placeholder="Paste local key…"
                                       value="@PendingKey(dev.DeviceId)"
                                       @oninput="e => _pendingKeys[dev.DeviceId] = e.Value?.ToString() ?? string.Empty"
                                       disabled="@IsBusy(dev.DeviceId)" />
                                <button class="tuya-key-save" disabled="@(IsBusy(dev.DeviceId) || string.IsNullOrWhiteSpace(PendingKey(dev.DeviceId)))"
                                        @onclick="() => SaveKeyAsync(dev)">
                                    @(IsBusy(dev.DeviceId) ? "…" : "Save")
                                </button>
                            </div>
                        }
                        else
                        {
                            @* Power *@
                            @if (cat.PowerDps is not null)
                            {
                                <button class="tuya-toggle @(GetDpsBool(dev, cat.PowerDps) ? "tuya-toggle-on" : "tuya-toggle-off")"
                                        disabled="@IsBusy(dev.DeviceId)"
                                        @onclick="() => ToggleDpsAsync(dev, cat.PowerDps!)">
                                    @(GetDpsBool(dev, cat.PowerDps) ? "ON" : "OFF")
                                </button>
                            }
                            @* Gangs *@
                            @if (cat.Gangs > 0)
                            {
                                <div class="tuya-switch-gangs">
                                    @for (int g = 1; g <= cat.Gangs; g++)
                                    {
                                        var gang = g;
                                        var on = GetDpsBool(dev, gang.ToString());
                                        <div class="tuya-switch-gang">
                                            @if (cat.Gangs > 1) { <span class="tuya-gang-label">CH@gang</span> }
                                            <button class="tuya-toggle @(on ? "tuya-toggle-on" : "tuya-toggle-off")"
                                                    disabled="@IsBusy(dev.DeviceId)"
                                                    @onclick="() => ToggleDpsAsync(dev, gang.ToString())">
                                                @(on ? "ON" : "OFF")
                                            </button>
                                        </div>
                                    }
                                </div>
                            }
                            @* White brightness *@
                            @if (cat.BrightnessDps is not null && GetDpsBool(dev, cat.PowerDps ?? "20"))
                            {
                                <div class="tuya-slider-row">
                                    <span class="tuya-dim-label">Dim</span>
                                    <input type="range" min="10" max="1000" value="@GetDpsInt(dev, cat.BrightnessDps, 500)"
                                           disabled="@IsBusy(dev.DeviceId)"
                                           @onchange="e => SetWhiteAsync(dev, cat, cat.BrightnessDps!, int.Parse(e.Value!.ToString()!))" />
                                    <span class="tuya-dim-label">Bright</span>
                                </div>
                            }
                            @* Warm-cool *@
                            @if (cat.ColorTempDps is not null && GetDpsBool(dev, cat.PowerDps ?? "20"))
                            {
                                <div class="tuya-slider-row">
                                    <span class="tuya-dim-label">Warm</span>
                                    <input type="range" min="0" max="1000" value="@GetDpsInt(dev, cat.ColorTempDps, 500)"
                                           disabled="@IsBusy(dev.DeviceId)"
                                           @onchange="e => SetWhiteAsync(dev, cat, cat.ColorTempDps!, int.Parse(e.Value!.ToString()!))" />
                                    <span class="tuya-dim-label">Cool</span>
                                </div>
                            }
                            @* Color *@
                            @if (cat.ColorDps is not null && GetDpsBool(dev, cat.PowerDps ?? "20"))
                            {
                                <div class="tuya-slider-row">
                                    <span class="tuya-dim-label">Color</span>
                                    <input type="color" class="tuya-color" value="@GetColorHex(dev, cat.ColorDps)"
                                           disabled="@IsBusy(dev.DeviceId)"
                                           @onchange='e => SetColorAsync(dev, cat, e.Value?.ToString() ?? "#ffffff")' />
                                </div>
                            }
                            @* AC / IR *@
                            @if (cat.Ac)
                            {
                                var acOn = GetDpsBool(dev, "1");
                                <button class="tuya-toggle @(acOn ? "tuya-toggle-on" : "tuya-toggle-off")"
                                        disabled="@IsBusy(dev.DeviceId)"
                                        @onclick="() => ToggleAcPowerAsync(dev)">
                                    @(acOn ? "ON" : "OFF")
                                </button>
                                <div class="tuya-ac-temp-row">
                                    <button class="tuya-temp-btn" disabled="@IsBusy(dev.DeviceId)" @onclick="() => AdjustAcTempAsync(dev, -1)">−</button>
                                    <span class="tuya-temp-value">@GetDpsInt(dev, "3", 24)°C</span>
                                    <button class="tuya-temp-btn" disabled="@IsBusy(dev.DeviceId)" @onclick="() => AdjustAcTempAsync(dev, +1)">+</button>
                                </div>
                                <div class="tuya-ac-mode-row">
                                    @foreach (var mode in new[] { "cool", "heat", "fan", "dry", "auto" })
                                    {
                                        <button class="tuya-mode-btn @(GetAcMode(dev) == mode ? "tuya-mode-active" : "")"
                                                disabled="@IsBusy(dev.DeviceId)"
                                                @onclick="() => SetAcModeAsync(dev, mode)">
                                            @(char.ToUpper(mode[0]) + mode[1..])
                                        </button>
                                    }
                                </div>
                                <div class="tuya-ac-mode-row">
                                    @foreach (var fan in new[] { "low", "mid", "high", "auto" })
                                    {
                                        <button class="tuya-mode-btn" disabled="@IsBusy(dev.DeviceId)"
                                                @onclick="() => SetAcFanAsync(dev, fan)">@fan</button>
                                    }
                                </div>
                            }
                            @* Energy readout *@
                            @if (cat.Energy is not null)
                            {
                                <div class="tuya-readout">
                                    <span>@(GetDpsInt(dev, cat.Energy.PowerDps, 0) / 10.0) W</span>
                                    <span>@(GetDpsInt(dev, cat.Energy.VoltageDps, 0) / 10.0) V</span>
                                    <span>@GetDpsInt(dev, cat.Energy.CurrentDps, 0) mA</span>
                                </div>
                            }
                            @* Sensor readouts *@
                            @if (cat.Sensors is not null)
                            {
                                <div class="tuya-readout">
                                    @foreach (var s in cat.Sensors)
                                    {
                                        <span>@s.Label: @FormatSensor(dev, s)</span>
                                    }
                                </div>
                            }
                        }
                    </div>
                }
```

- [ ] **Step 4: Replace `@code` helpers**

Remove the now-unused type-specific helpers (`GetPower`, `GetBrightness`, `GetTemp`, `GetMode`, `TogglePowerAsync`, `SetBrightnessAsync`, `AdjustTempAsync`, `SetModeAsync`, `GetGangPower`, `ToggleGangAsync`, `GetPresence`). Keep `SendAsync`, `LoadAsync`, scan methods, key-entry helpers (`PendingKey`, `SaveKeyAsync`, `IsBusy`, `_busyDevices`, `_pendingKeys`). Add:

```csharp
    private static bool GetDpsBool(TuyaDeviceView dev, string key)
        => dev.Dps.TryGetValue(key, out var v)
           && (v.ValueKind == System.Text.Json.JsonValueKind.True
               || (v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is "1" or "true" or "on" or "presence"));

    private static int GetDpsInt(TuyaDeviceView dev, string key, int dflt)
        => dev.Dps.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : dflt;

    private static string GetDpsStr(TuyaDeviceView dev, string key)
        => dev.Dps.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string GetColorHex(TuyaDeviceView dev, string key)
    {
        var raw = GetDpsStr(dev, key);
        return raw.Length >= 12 ? TuyaColor.RgbHexFromHsvHex(raw) : "#ffffff";
    }

    private static string FormatSensor(TuyaDeviceView dev, SensorReadout s)
    {
        if (dev.Dps.TryGetValue(s.Dps, out var v))
        {
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number)
                return $"{v.GetInt32() * s.Scale:0.#} {s.Unit}".Trim();
            if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() ?? "-";
            if (v.ValueKind == System.Text.Json.JsonValueKind.True) return "yes";
            if (v.ValueKind == System.Text.Json.JsonValueKind.False) return "no";
        }
        return "-";
    }

    private async Task ToggleDpsAsync(TuyaDeviceView dev, string key)
    {
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Dps: new() { [key] = !GetDpsBool(dev, key) }));
        await LoadAsync();
    }

    private async Task SetWhiteAsync(TuyaDeviceView dev, TuyaCategory cat, string key, int value)
    {
        var dps = new Dictionary<string, object> { [key] = value };
        if (cat.WorkModeDps is not null) dps[cat.WorkModeDps] = "white";
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Dps: dps));
        await LoadAsync();
    }

    private async Task SetColorAsync(TuyaDeviceView dev, TuyaCategory cat, string rgbHex)
    {
        var dps = new Dictionary<string, object> { [cat.ColorDps!] = TuyaColor.HsvHexFromRgb(rgbHex) };
        if (cat.WorkModeDps is not null) dps[cat.WorkModeDps] = "colour";
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Dps: dps));
        await LoadAsync();
    }

    private async Task ToggleAcPowerAsync(TuyaDeviceView dev)
    {
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Ac: new AcCommandPayload(Power: !GetDpsBool(dev, "1"))));
        await LoadAsync();
    }

    private async Task AdjustAcTempAsync(TuyaDeviceView dev, int delta)
    {
        var next = Math.Clamp(GetDpsInt(dev, "3", 24) + delta, 16, 30);
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Ac: new AcCommandPayload(Temp: next)));
        await LoadAsync();
    }

    private string GetAcMode(TuyaDeviceView dev)
    {
        var raw = GetDpsStr(dev, "2");
        return raw switch { "cold" => "cool", "hot" => "heat", "wind" => "fan", "wet" => "dry", "" => "cool", _ => raw };
    }

    private async Task SetAcModeAsync(TuyaDeviceView dev, string mode)
    {
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Ac: new AcCommandPayload(Mode: mode)));
        await LoadAsync();
    }

    private async Task SetAcFanAsync(TuyaDeviceView dev, string fan)
    {
        await SendAsync(dev.DeviceId, new TuyaCommandRequest(Ac: new AcCommandPayload(Fan: fan)));
        await LoadAsync();
    }

    private async Task SaveCategoryAsync(TuyaDeviceView dev, string category)
    {
        await TuyaSvc.SaveDeviceAsync(new TuyaDevice
        {
            Id = dev.Id, DeviceId = dev.DeviceId, Name = dev.Name,
            DeviceType = dev.DeviceType, Room = dev.Room, Category = category, SortOrder = dev.SortOrder
        });
        await LoadAsync();
    }
```

Note: `SaveDeviceAsync` looks up by `DeviceId` and updates the existing row (its `else` branch now copies `Category`), so passing a populated `TuyaDevice` updates category in place. Confirm `@using Tower.Core.Models` and `@using Tower.Core.Tuya` are present.

- [ ] **Step 5: Add CSS**

Append to `src/Tower/Components/Pages/Tuya.razor.css`:

```css
.tuya-card-top { display:flex; align-items:center; justify-content:space-between; gap:0.5rem; margin-bottom:0.5rem; }
.tuya-cat-select { font-size:0.72rem; background:var(--surface-2); color:var(--text); border:1px solid var(--border); border-radius:var(--r-xs); padding:2px 4px; max-width:52%; }
.tuya-slider-row { display:flex; align-items:center; gap:0.5rem; margin-top:0.5rem; }
.tuya-slider-row input[type=range] { flex:1; }
.tuya-color { width:44px; height:28px; border:1px solid var(--border); border-radius:var(--r-xs); background:none; padding:0; }
.tuya-readout { display:flex; flex-wrap:wrap; gap:0.5rem; margin-top:0.5rem; font-size:0.78rem; color:var(--faint); }
```

- [ ] **Step 6: Fix `Automations.razor` `IsAc`**

In `src/Tower/Components/Pages/Automations.razor`, replace both `d.DeviceType == TuyaDeviceType.AcRemote` / `x.DeviceType == TuyaDeviceType.AcRemote` uses with the category check. Add `@using Tower.Core.Tuya`. Lines ~167 and ~194:

```csharp
            IsAc = _tuyaDevices.Any(d => d.DeviceId == x.Target && TuyaCategories.Resolve(d.Category, d.DeviceType).Ac)
```
```csharp
            row.IsAc = d is not null && TuyaCategories.Resolve(d.Category, d.DeviceType).Ac;
```

- [ ] **Step 7: Build**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj 2>&1 | tail -3` → 0 errors.

- [ ] **Step 8: Commit**

```bash
cd /home/atom/dev/Tower
git add src/Tower/Components/Pages/Tuya.razor src/Tower/Components/Pages/Tuya.razor.css \
        src/Tower/Components/Pages/Automations.razor
git commit -m "Tuya page: capability-driven cards, category dropdown, color/AC/energy controls"
```

---

### Task 3: Deploy and verify

**Files:** none (operations).

- [ ] **Step 1: Push + deploy**

```bash
cd /home/atom/dev/Tower && git push origin HEAD && bash deploy.sh
```

- [ ] **Step 2: Verify**

Run: `systemctl is-active tower && sleep 5 && curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8888/tuya` → `active` then `200`.
Run: `sqlite3 /home/atom/Tower/tower.db "PRAGMA table_info(TuyaDevices);" | grep -i category` → shows the `Category` column.
Run: `journalctl -u tower -n 20 --no-pager | tail -8` → clean startup, no exceptions.

- [ ] **Step 3: Assign the Smart Bulb + report**

Set the Smart Bulb's category to `color_bulb` in the DB so the color controls show immediately:
`sqlite3 /home/atom/Tower/tower.db "UPDATE TuyaDevices SET Category='color_bulb' WHERE DeviceId='bfe62ea75d77e2795a5z53';"`
Also set the "AC controller" temp/humidity sensor: `UPDATE TuyaDevices SET Category='climate_sensor' WHERE DeviceId='bf52d5589200370976dl5k';` and the two energy plugs (`bfa7e193b4f8cb0416yjss`, `bfb18911018d504ff2axux`) to `energy_plug`.
Report deploy status. Note: live color/AC verification is John's to do on `/tuya` (and AC remotes must be powered on first).
