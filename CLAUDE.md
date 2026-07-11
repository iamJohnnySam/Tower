# Tower — Claude working notes

Home-lab command center. .NET 10 Blazor (interactive server) + EF Core/SQLite (`tower.db`).
Serves on **http://0.0.0.0:8888**; gRPC on 5601. Service: `tower`. Prod dir: `/home/atom/Tower`.

Deploy: `bash /home/atom/dev/Tower/deploy.sh` (stop → publish → start; also installs `tower.sudoers`).

## Adding a project card (monitor + start/stop on the /projects page)

Projects load from **`src/Tower/appsettings.json` → `Tower.Projects[]`** (NOT the DB).
To add one:

1. Append an entry to `Tower.Projects` in `src/Tower/appsettings.json`:
   ```json
   { "Name": "DesignWorks", "Service": "designworks", "Port": 5080, "Url": "http://atom:5080" }
   ```
   Optional fields: `DbPath` (SQLite file → enables the DB size/badge; omit for Postgres/none),
   `LogDir` (dir of `*.log` files; omit and Tower falls back to `journalctl -u <service>`).
2. For the start/stop/restart buttons to work, add a line to **`tower.sudoers`**:
   ```
   atom ALL=(root) NOPASSWD: /usr/bin/systemctl start <svc>, /usr/bin/systemctl stop <svc>, /usr/bin/systemctl restart <svc>
   ```
3. Redeploy Tower: `bash /home/atom/dev/Tower/deploy.sh` (this also validates + installs the sudoers file).

The target app needs its own systemd service (`/etc/systemd/system/<svc>.service`) and a
`deploy.sh` at its repo root — see MediaBox/FinanceTracker/DesignWorks for the pattern
(stop → `dotnet publish` to `/home/atom/<Project>` → start). Deploy workflow is stop→publish→start.

## Automations (/automations page)

Named smart-home routines in the `Automations` table (Id, Name, ActionsJson — JSON list of
`{kind:"tuya"|"pi", target, targetName, on, temp}`). Executed by
`Tower.Core/Automations/AutomationService` (Tuya via tinytuya service; Pi = shutdown via
pi-agent). Telegram trigger: `run <name>` or `/run <name>` (case-insensitive, admin only).
New action kinds later = extend the `kind` discriminator, no schema change.
Spec/plan: `docs/superpowers/specs/2026-07-10-automations-design.md`.

## Tuya device categories & controls (/tuya page)

Device controls are driven by a **category** (not the `TuyaDeviceType` enum). Categories are
code-defined in `src/Tower.Core/Tuya/TuyaCategories.cs` — each declares capabilities as DPS codes
(power/brightness/colortemp/color/workmode/gangs/ac/energy/sensors). Assign a category to a device
via the dropdown on its card on `/tuya`; `TuyaDevice.Category` (nullable) stores it, null falls back
to the legacy `DeviceType`. Adding a device = pick a category; a new *category* is a few lines in
that one file. Color codec (RGB↔Tuya HSV hex) is `TuyaColor.cs`. Local keys/IPs live in the separate
`tuyaservice` (tinytuya_service/devices.json), NOT tower.db — see spec/plan under docs/superpowers/.

## Passwords / secrets store (/secrets page)

Project credentials live in the `Secrets` table in `tower.db`. Model: `Secret.cs`
(Id, Project, Label, Value, Notes, UpdatedAt). The page is **gated by a master password**
and each `Value` is **AES-256-GCM encrypted at rest** (`VaultCrypto`), keyed by PBKDF2 of
the master password. Only `Project`/`Label`/`Notes` are plaintext.

- First visit sets the master password (`SecretService.ConfigureAsync` encrypts all existing
  rows). `vault.salt` + `vault.verifier` (SHA256 of key) live in the `Settings` table; the key
  itself is **never stored** — held in memory per Blazor circuit (`VaultSession`, scoped).
- **You (Claude) can no longer read values via `sqlite3` once the vault is configured** — the
  ciphertext is useless without the master password. That's intended. To read a value, John
  unlocks the `/secrets` page. Adding new project creds: insert a row (Project/Label/Notes plain,
  Value = the secret) *before* the vault is configured, or ask John to add it via the UI.
- Check config state: `sqlite3 /home/atom/Tower/tower.db "SELECT Key FROM Settings WHERE Key='vault.salt';"`
  — if a row exists, the vault is encrypted; do not write plaintext into `Secrets.Value`.

When you set up a new project that has credentials, store them here so John can view them.
