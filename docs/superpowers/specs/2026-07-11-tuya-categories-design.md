# Tuya Device Categories & Full Controls — Design Spec (2026-07-11)

## Goal

Replace the Tuya page's hardcoded per-`DeviceType` rendering with a **category** system:
each category declares its capabilities (as DPS codes) in code; a device is assigned a
category from a dropdown. The page renders controls from the category's capabilities and
reads current values from live DPS. This gives the Bedroom color bulb full brightness /
warm-cool / color control (reading current state), AC/IR remotes temperature+mode+fan, and
makes adding a new device just "pick a category".

Confirmed by live device probe (2026-07-11): color bulb exposes power(20)/mode(21)/
brightness(22)/colortemp(23)/HSV-color(24); energy plugs expose watts/volts/amps; switches
expose per-gang booleans; presence + climate sensors expose read-only values. AC/IR remotes
+ gateway were offline and are built from the standard mapping (flagged verify-on-device).

## Categories (code-defined registry — `Tower.Core/Tuya/TuyaCategories.cs`)

`record TuyaCategory(string Key, string Name, string? PowerDps, string? BrightnessDps,
string? ColorTempDps, string? ColorDps, string? WorkModeDps, int Gangs, bool Ac,
EnergySpec? Energy, IReadOnlyList<SensorReadout>? Sensors, TuyaDeviceType Legacy)`

- `EnergySpec(string PowerDps, string VoltageDps, string CurrentDps)` — read-only.
- `SensorReadout(string Label, string Dps, string Unit, double Scale)` — read-only.
- `Actuatable => PowerDps != null || Gangs > 0 || Ac` (drives automation eligibility).

Registry (static `Dictionary<string,TuyaCategory>`, insertion-ordered for the dropdown):

| Key | Name | Capabilities |
|---|---|---|
| `plug` | Plug | Power `1` |
| `energy_plug` | Energy Plug | Power `1`; Energy power `19`/10 W, voltage `20`/10 V, current `18` mA |
| `dimmable_light` | Dimmable Light | Power `20`, Brightness `22` (10–1000) |
| `color_bulb` | Color Bulb | Power `20`, Brightness `22`, ColorTemp `23` (0–1000), Color `24` (HSV), WorkMode `21` |
| `switch_1` | Switch (1-gang) | Gangs 1 (dps `1`) |
| `switch_2` | Switch (2-gang) | Gangs 2 (dps `1`,`2`) |
| `switch_4` | Switch (4-gang) | Gangs 4 (dps `1`..`4`) |
| `ac_remote` | AC / IR Remote | Ac (power/temp/mode/fan via existing `/command` `ac` payload) |
| `presence_sensor` | Presence Sensor | Sensors: presence `1` (str), illuminance `11` |
| `climate_sensor` | Climate Sensor | Sensors: temp `101`/10 °C, humidity `102` %RH |

`Legacy` maps each category to the nearest old `TuyaDeviceType` for back-compat default
resolution. `Resolve(device)` = registry[device.Category] if set, else derive from
`device.DeviceType` (Plug→plug, Light→dimmable_light, AcRemote→ac_remote, Switch1/2/4→
switch_1/2/4, Sensor→presence_sensor), else `plug`.

## Data

Add `string? Category` to `TuyaDevice` and to `TuyaDeviceView`. `TowerDbContext` unchanged
(new nullable column). `Program.cs`: `ALTER TABLE TuyaDevices ADD COLUMN Category TEXT`
guarded (EnsureCreated won't add it) — wrap in try/catch since `IF NOT EXISTS` isn't valid
for ADD COLUMN in SQLite; ignore "duplicate column" on re-run. Keep `DeviceType` column and
enum (used for legacy default mapping only; no longer drives UI).

## Color codec (`Tower.Core/Tuya/TuyaColor.cs`, pure + unit-tested)

Tuya v3.3+ HSV hex = `HHHHSSSSVVVV` (H 0–360, S 0–1000, V 0–1000). Functions:
- `HsvHexFromRgb(string rgbHex) → string` (browser `#RRGGBB` → Tuya hex)
- `RgbHexFromHsvHex(string tuyaHex) → string` (Tuya hex → `#RRGGBB` for the picker/display)
Round-trip test + a known vector (`000803e803e8` ≈ pure-ish, S/V max).

## Service wiring

- `TuyaDeviceService.GetDeviceViewsAsync` populates `Category`. `SaveDeviceAsync` persists it.
- `AutomationService`: power DPS = `Resolve(device).PowerDps ?? "1"`; AC = `Resolve(device).Ac`;
  automation device list excludes non-`Actuatable` categories (replaces the `!= Sensor` filter).
- No tinytuya service changes: `/command` already forwards arbitrary `dps` and the `ac` payload.

## UI (`Tuya.razor` + `Tuya.razor.css`)

- Scan-assign panel and each device card gain a **category `<select>`** populated from the
  registry (value = category Key). Saving assigns `Category`.
- The five per-`DeviceType` card blocks collapse into one **capability-driven** card that renders,
  in order, whichever apply: power toggle (PowerDps), gang toggles (Gangs), brightness slider
  (BrightnessDps, 10–1000), warm-cool slider (ColorTempDps, 0–1000), color picker
  (`<input type="color">`, ColorDps), AC temp/mode/fan (Ac), read-only energy readout (Energy),
  read-only sensor readouts (Sensors). Unreachable devices keep the paste-key box.
- Color: picking a color sends `{ColorDps: HsvHex, WorkModeDps: "colour"}`; brightness/temp send
  their DPS + `{WorkModeDps: "white"}`. Values initialise from live DPS (current state shown).
- AC: keep existing temp ± and mode buttons; add a fan selector (low/mid/high/auto → `ac.fan`).

## Error handling / edge cases

Missing/unknown DPS → control simply reads its default (existing `Get*` helpers already default).
Offline device → paste-key box (unchanged). Automations remain correct because power-DPS/AC now
come from the category (color bulb power = dps 20, not the old Light guess).

## Testing

`Tower.Core.Tests`: (1) `TuyaColor` RGB↔HSV hex round-trip + the `000803e803e8` vector;
(2) `TuyaCategories.Resolve` falls back from legacy `DeviceType` and honors an explicit Category;
(3) automation power-DPS selection uses the category (color_bulb→"20", plug→"1"). Manual E2E after
deploy: assign Smart Bulb→Color Bulb, set color/brightness, confirm the bulb reflects it.
