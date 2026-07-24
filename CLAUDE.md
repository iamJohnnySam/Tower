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

## Bill & statement mail importers (/bills, /statements pages)

Two Gmail-label sweepers that feed **FinanceTracker** over its REST API
(`FinanceTrackerClient`; base URL + API key from the `Settings` table, since a background worker
can't unlock the encrypted vault).

| | Bills | Statements |
|---|---|---|
| Label | `Bills` | `Statements` |
| Profiles | `Tower.Core/Bills/BillProfiles.cs` | `Tower.Core/Statements/StatementProfiles.cs` |
| Worker | `BillMailWorker` | `StatementMailWorker` |
| Dedup table | `ImportedBills` | `ImportedStatements` (unique `GmailMessageId`) |
| Posts to | `/api/external/transactions` | `/api/external/balances` or `/statements` |

Statement profiles are code-defined (sender + subject regex) — adding one is a few lines, then
redeploy. Settings keys are `bills.*` / `statements.*` (`label_name`, `interval_hours`,
`mail.last_run`/`.last_count`/`.last_error`).

**Bill profiles are NOT in code** — they live in `bill-profiles.xml` in the app directory
(`/home/atom/Tower/`). Edit it and hit **Reload profiles** on `/bills`: no rebuild, no redeploy.

- Loader/schema: `Tower.Core/Bills/BillProfileXml.cs`. The schema (every attribute and child
  element) is documented in a comment at the top of the XML itself.
- `Tower.Core/Bills/bill-profiles.default.xml` is the version-controlled master, **embedded in the
  assembly**. On every start Tower rewrites `bill-profiles.default.xml` in the app directory from it
  as a reference copy, and seeds `bill-profiles.xml` from it only if that file is absent — so live
  edits survive deploys. To reset: delete `bill-profiles.xml` and restart.
- Change a profile permanently (so it survives a rebuild and is in git) by editing the
  `bill-profiles.default.xml` in the repo — but remember the live file wins in prod and won't be
  overwritten, so also apply the change there or delete it.
- A malformed file is rejected **whole**; the previously loaded profiles stay in use and the error
  shows on `/bills`. Startup falls back to the embedded defaults.
- If a matched email's amount can't be parsed, the sweep records it in `bills.mail.last_error`
  (visible on `/bills`) instead of skipping silently — that's how a sender changing its template
  gets noticed. Dialog once went months unimported because its PDF renamed one field.

**Statements specifics** (spec: `docs/superpowers/specs/2026-07-20-statement-ingestion-design.md`):

- Tower **never opens a statement PDF** — it may be password-locked. The bytes go straight to
  FinanceTracker, which owns unlocking, parsing and applying the balance.
- `StatementProfiles.MonthEnd()` maps the email date to the month the statement covers:
  **day <= 14 → previous month, else current month**. It converts to local time first — Gmail's
  `internalDate` is UTC and the +0530 shift can move the day across a month boundary.
- Subject regexes must be anchored tightly. `bocmail1@boc.lk` sends statements for four different
  accounts (masked as `XXXXXXXX12`/`62`/`74`/`40`) plus FD renewal notices, so the BOC profile ends
  `X+12\b`.
- **Email deletion:** trashed only after FinanceTracker returns 2xx. Every failure path (FT down,
  404 unknown account, no PDF, exception) leaves the message in the label to retry next sweep.
