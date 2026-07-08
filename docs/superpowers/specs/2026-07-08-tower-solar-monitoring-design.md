# Tower Solar Monitoring — Design

**Date:** 2026-07-08
**Status:** Approved direction, spec under review

## Goal

Move solar *performance* monitoring out of FinanceTracker and into Tower as two new
pages: a generic **Gmail connector** page and a **Solar** page. The Solar page pulls
data from two sources — SolaX inverter reports emailed by SolaxCloud (tagged with the
Gmail label `Solar`) and the SolaxCloud real-time API — and presents an informative,
interactive dashboard of system performance.

FinanceTracker keeps its *financial* solar module (monthly earnings, export rates, grid
tariffs, payback %, tax integration). Only the Gmail-import machinery leaves it.

## Scope

### In
- Remove Gmail-import + daily-log machinery from FinanceTracker (keep financial module).
- Tower: Gmail OAuth connector (`gmail.readonly`) reusing the `DropboxTokenService` pattern.
- Tower: SolaxCloud real-time API polling → local snapshot history.
- Tower: parse `Solar`-label report emails (Daily/Weekly/Monthly) → report history.
- Tower: `/gmail` page (connection management) + `/solar` page (dashboard + config).

### Out (YAGNI)
- Pushing generation figures back into FinanceTracker's `SolarEarning`. (`GeneratedUnits`
  becomes manual entry there. Add later only if it becomes annoying.)
- Any inverter *control* — the SolaX public API is read-only.
- Multi-plant / multi-inverter support. Single plant, single inverter SN.
- Currency handling beyond LKR (reports are LKR; store the raw number + `LKR` literal).

---

## Part A — FinanceTracker removal

Delete, in `/home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/`:

- `Services/GmailSolarService.cs` (+ `IGmailSolarService` DI registration in `Program.cs`).
- `Controllers/GmailController.cs`.
- `Models/SolarDailyLog.cs`; its `DbSet<SolarDailyLog>` in `ApplicationDbContext`; the
  `EnsureSolarDailyLogsTable`/column logic in `ApplyMigrationHelper.cs`. Leave the physical
  `SolarDailyLogs` table in the live DB in place (harmless; dropping it is optional cleanup).
- Gmail fields on `AppSetting` (`GmailAccessToken`, `GmailRefreshToken`, `GmailTokenExpiry`,
  `GmailEnabled`) — remove the properties and the Settings-page Gmail connect/disconnect UI.
- Any Gmail import button/section on `Components/Pages/Solar.razor`.

**Keep:** `SolarAccount`, `SolarEarning`, `SolarExportRate`, `GridTariffSchedule`/`Slab`,
payback stats, and the Tax page's solar-income line. `SolarEarning.GeneratedUnits` stays a
manual field.

Verify FinanceTracker builds and the Solar + Tax pages still render after removal.

---

## Part B — Tower data model

Two tables, created via `CREATE TABLE IF NOT EXISTS` in `src/Tower/Program.cs` (the existing
pattern for `ConversionJobs`/`Todos`/`Secrets`), with matching models in
`src/Tower.Core/Models/` and `DbSet`s in `TowerDbContext`.

### `SolarSnapshot` — one row per SolaX API poll (this is our time-series)
| Column | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| CapturedAt | TEXT (UTC) | when Tower polled |
| UploadTime | TEXT | inverter's own `uploadTime` (may lag / repeat) |
| AcPower | REAL | W (`acpower`) |
| YieldToday | REAL | kWh (`yieldtoday`) |
| YieldTotal | REAL | kWh (`yieldtotal`) |
| FeedInPower | REAL | W (`feedinpower`; +export / −import) |
| FeedInEnergy | REAL | kWh (`feedinenergy`) |
| ConsumeEnergy | REAL | kWh (`consumeenergy`) |
| Soc | REAL | % battery (`soc`) |
| BatPower | REAL | W (`batPower`) |
| PowerDc1 | REAL | W (`powerdc1`) |
| InverterStatus | TEXT | raw status code |

Index on `CapturedAt`. Dedup: if the newest stored `UploadTime` equals the fetched
`uploadTime`, skip the insert (inverter hasn't reported new data) — avoids flatlined
duplicate rows while the panel is idle overnight.

### `SolarReport` — one row per parsed email
| Column | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| ReportType | INTEGER | enum Daily=0 / Weekly=1 / Monthly=2 |
| PeriodStart | TEXT (date) | = PeriodEnd for daily/weekly |
| PeriodEnd | TEXT (date) | report date; range end for monthly |
| PeriodYieldKWh | REAL | Daily/Weekly/Monthly yield (MWh→kWh) |
| TotalYieldKWh | REAL | cumulative (MWh→kWh) |
| PeriodEarningsLkr | REAL | Daily/Weekly/Monthly earnings |
| TotalEarningsLkr | REAL | cumulative earnings |
| Co2SavedTons | REAL | `CO₂ Saved … t` |
| AlarmQuantity | INTEGER | health signal |
| GmailMessageId | TEXT | dedup key |
| ImportedAt | TEXT (UTC) | |

Unique index on `GmailMessageId`.

---

## Part C — Tower services (`Tower.Core`)

### `GmailTokenService` (`Tower.Core/Gmail/`)
Clone of `Backup/DropboxTokenService.cs`:
- Settings keys: `gmail.client_id`, `gmail.client_secret`, `gmail.refresh_token`.
  (Client id/secret reuse the existing Google Cloud project FinanceTracker used; entered
  once on the `/gmail` page.)
- `BuildAuthUrl()` → Google consent URL, scope `https://www.googleapis.com/auth/gmail.readonly`,
  `access_type=offline&prompt=consent`, redirect `http://localhost:8888/gmail/callback`.
- `ExchangeCodeAsync(code)` → POST `https://oauth2.googleapis.com/token`, store refresh token.
- `GetAccessTokenAsync()` → in-memory cached access token, refresh via refresh_token grant.
- `Disconnect()` clears `gmail.refresh_token`.
- `/gmail/callback` minimal endpoint in `Program.cs`, mirroring `/dropbox/callback`.

### `GmailReader` (`Tower.Core/Gmail/`)
Thin Gmail REST wrapper over `HttpClient` (no Google SDK):
- `ListLabelsAsync()` → `GET users/me/labels` (for the `/gmail` page + label picker).
- `ListMessageIdsAsync(labelId, afterEpoch?)` → `GET users/me/messages?labelIds={id}&q=...`,
  paged, returns message ids.
- `GetMessageRawAsync(id)` → `GET users/me/messages/{id}?format=full`; extract the
  `text/plain` (or `text/html`→stripped) body, base64url-decoded. Reuse FinanceTracker's
  `FindTextPart` recursion logic.
- Auth header from `GmailTokenService.GetAccessTokenAsync()`.

### `SolarReportParser` (`Tower.Core/Solar/`)
Pure function `Parse(subject, body) → SolarReport?`. Regex patterns (invariant culture,
strip `,` from numbers before `decimal.Parse`):

| Field | Pattern |
|---|---|
| ReportType | `Plant (Daily\|Weekly\|Monthly) Report` |
| Period yield | `(?:Daily\|Weekly\|Monthly) yield\s+([\d.,]+)\s*(kWh\|MWh)` |
| Total yield | `Total yield\s+([\d.,]+)\s*(kWh\|MWh)` |
| Period earnings | `(?:Daily\|Weekly\|Monthly) earnings\s+LKR\s+([\d.,]+)` |
| Total earnings | `Total earnings\s+LKR\s+([\d.,]+)` |
| CO₂ | `CO₂?\s*Saved\s+([\d.,]+)\s*t` |
| Alarm | `Alarm quantity\s+(\d+)` |
| Date | `Date\s+(\d{4}/\d{2}/\d{2})(?:\s*-\s*(\d{4}/\d{2}/\d{2}))?` |

MWh→kWh: value × 1000 when unit is `MWh`. Date: group1 → `PeriodEnd` (and `PeriodStart`
when no range); range → group1=`PeriodStart`, group2=`PeriodEnd`. Return `null` if
ReportType or Date is missing (logged + skipped, not fatal).

### `SolaxClient` (`Tower.Core/Solar/`)
- `GetRealtimeAsync(tokenId, sn)` → `GET https://www.solaxcloud.com/proxyApp/proxy/api/getRealtimeInfo.do?tokenId={t}&sn={sn}`.
- Parse `success` + `result{}` into a `SolarSnapshot`. Missing fields → null/0, never throw.
- Settings keys: `solax.token_id`, `solax.sn`.

---

## Part D — Tower background workers (`HostedService`)

### `SolaxPollWorker`
Every **5 min** (288/day ≪ 10k/day limit). If `solax.token_id`+`solax.sn` unset → idle.
Fetch realtime, dedup on `uploadTime`, insert `SolarSnapshot`. Errors logged, loop continues.

### `SolarMailWorker`
Every **30 min**. If Gmail not connected or `gmail.label_id` unset → idle. List message ids
under the label (query bounded with `after:` = last import date to keep pages small), skip
ids already in `SolarReport`, fetch+parse+insert new ones. **No delete, no mark-read** —
`gmail.readonly` can't anyway. Errors logged, loop continues. Record last-run summary in
Settings (`solar.mail.last_run`, `solar.mail.last_count`, `solar.mail.last_error`) for the
`/gmail` import log.

Both registered in `Program.cs` alongside the existing workers.

---

## Part E — Tower pages

### `/gmail` — Gmail connector
- **Connection card:** status (connected / not), account label, Connect / Disconnect buttons.
  First-time: inputs for client id/secret (stored to Settings), then Connect → consent →
  `/gmail/callback` → redirect back with `?gmail=connected`. Mirror the Dropbox flow in
  `Settings.razor`, including the paste-the-code fallback for remote browsers.
- **Labels list:** shows `ListLabelsAsync()` so John can confirm the `Solar` label exists.
- **Import log:** last run time, count, last error (from Settings). Manual "Import now" button
  that triggers one `SolarMailWorker` pass.
- Add to `NavMenu.razor`.

### `/solar` — Solar dashboard
Uses the latest `SolarSnapshot`, snapshot history, and `SolarReport` rows.

- **Live tiles** (from newest snapshot + newest daily report):
  AC Power (W now) · Battery SOC (%) · Today's Yield (kWh) · Grid Feed-in / Import (W,
  signed) · Total Yield (kWh) · Inverter Status · Alarm count (from latest report; red if >0).
- **Charts** (hand-rolled SVG, following `Home.razor`/`Jellyfin.razor` + the `Sparkline`
  component):
  1. **Today's power curve** — `AcPower` over today's snapshots.
  2. **Battery SOC today** — `Soc` over today's snapshots.
  3. **Daily yield, last 30 days** — bar chart from Daily `SolarReport` rows.
  4. **Monthly yield** — bar chart from Monthly `SolarReport` rows.
- **Report tabs:** Daily / Weekly / Monthly tables of `SolarReport` (period, yield, earnings,
  CO₂, alarms), newest first.
- **Config section** (on this page, per requirement):
  - SolaX: `tokenId` + inverter `sn` inputs → Settings; "Test" button calls `SolaxClient` once.
  - Gmail label picker: dropdown from `ListLabelsAsync()` → stores `gmail.label_id`.
- Add to `NavMenu.razor`.

---

## Testing

- **`SolarReportParser`** — unit tests in `tests/Tower.Core.Tests/` using the three real
  sample bodies (daily/weekly/monthly) captured 2026-07-08: assert type, yields (incl.
  MWh→kWh), earnings, CO₂, alarm, and the single-date vs range date handling. Plus a
  malformed-body case → `null`.
- **`SolaxClient`** — parse test against a canned `getRealtimeInfo` JSON fixture.
- Follows the existing `Tower.Core.Tests` fixture pattern (e.g. `JellyfinParseTests`).

## Deploy

`bash /home/atom/dev/Tower/deploy.sh` (Tower) and `bash /home/atom/dev/FinanceTracker/deploy.sh`
(FinanceTracker) after each side's change. Store the SolaX token + Gmail client secret in the
`/secrets` vault for the record.

## Open items
- None blocking. SolaX field names assumed from API V6.1 docs; the `SolaxClient` parser
  tolerates missing/renamed fields (null/0, never throws) and will be confirmed against the
  first live response.
