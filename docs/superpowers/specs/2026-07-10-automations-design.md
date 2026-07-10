# Automations — Design Spec (2026-07-10)

## Goal

A new **Automations** page in Tower (Smart Assistant nav section) where John defines named
automations — ordered lists of smart-home actions — and triggers them from Telegram with
`run <name>` (case-insensitive) or a Run button on the page.

Motivating example — **Bedtime**: Baby Room bulb ON, Baby Room AC ON at 27°C,
AtomTV shutdown, AtomMiniTV shutdown.

## Scope

In: Tuya device on/off, AC power+temperature, Pi TV shutdown; manual triggers only
(Telegram + UI button). Out (later): delays, scheduled triggers, other Tower features as
action kinds — the `kind` discriminator is the extension seam.

## Data

New `Automations` table: `Id INTEGER PK`, `Name TEXT`, `ActionsJson TEXT`.
Created via the existing `CREATE TABLE IF NOT EXISTS` block in `Program.cs`
(EnsureCreated does not alter existing schemas). EF: `DbSet<Automation> Automations`.

Action record (serialized as a JSON array, web casing):

```csharp
record AutomationAction(string Kind, string Target, string TargetName, bool On, int? Temp);
// Kind: "tuya" (Target = TuyaDevice.DeviceId) | "pi" (Target = DeviceConfig.Id, shutdown only)
```

## Components

- **`Tower.Core/Automations/AutomationService.cs`** (scoped) — CRUD (`ListAsync`,
  `SaveAsync`, `DeleteAsync`), device lookups for the editor (`ListTuyaDevicesAsync`,
  `PiDevices` from `TowerConfig.Devices` where Kind=="pi"), and execution:
  - `RunByNameAsync(name)` — trim + `OrdinalIgnoreCase` match; unknown name replies with
    the available names.
  - `RunAsync(auto)` — steps in order; per-step ✅/❌ lines; failures never abort the run.
  - Tuya AC (`TuyaDeviceType.AcRemote`): one command, `AcCommandPayload(Power, Temp)`
    (tinytuya maps power→dps1, temp→dps3). Other Tuya: power DPS by type — Light→"20",
    else "1". Pi: `PiAgentClient.ShutdownAsync(BaseUrl)`.
- **`Tower.Core/Automations/AutomationTelegramHandler.cs`** (singleton, `Register()` in
  `Program.cs` like `TodoTelegramHandler`) — registers `run` and `/run` on
  `TelegramHub`; bare `run` lists automations; replies with the run summary.
- **`TelegramHub` change** — step-6 command dispatch currently requires a leading `/`.
  Relax the gate to "message non-empty"; matching stays exact-first-token, so `/todo`
  behavior is unchanged and slash-less registrations become possible. Admin-only gate
  unchanged.
- **UI `/automations`** (`Automations.razor` + scoped css, nav link after Tuya under
  Smart Assistant) — list (name, action summary, Run/Edit/Delete), editor (name input;
  action rows: device select grouped Tuya+Pi, On/Off select — Pi shows Shutdown only,
  temp number input when AC+On; add/remove row), Telegram usage hint, run-result notice.
  Reuses global classes (`page-title`, `atom-table`, `config-input`, `btn-*`).

## Error handling

`TuyaServiceClient` and `PiAgentClient` never throw (return false/null on failure) —
a failed step renders ❌ in the summary and the run continues. Empty automation → "no
actions configured". Missing Pi BaseUrl → ❌.

## Testing

Unit test in `Tower.Core.Tests`: `AutomationAction` JSON round-trip via
`ParseActions`/`SerializeActions`, and malformed JSON → empty list. Manual E2E after
deploy: create a test automation on `/automations`, run via button and via Telegram
`run <name>`.
