# Tuya Devices Tab — Design Spec

**Date:** 2026-06-17  
**Status:** Approved

---

## Overview

Add a Tuya tab to Tower that lets the user control ~10 local-network Tuya devices (smart plugs, lights, AC IR remotes) directly from the browser. Device communication uses the Tuya local LAN protocol, mediated by a small Python tinytuya FastAPI service running on the same server. Tower's C# code never speaks the Tuya protocol directly.

---

## Architecture

Three layers:

1. **Python tinytuya service** — owns the Tuya protocol and device discovery
2. **Tower C# (`Tower.Core/Tuya/`)** — HTTP client + display metadata in SQLite
3. **Tuya Blazor page (`Components/Pages/Tuya.razor`)** — device grid UI

---

## Layer 1: Python tinytuya Service

### Location

`/home/atom/dev/Tower/tinytuya_service/`
- `tinytuya_service.py` — FastAPI application (~80 lines)
- `requirements.txt` — `tinytuya`, `fastapi`, `uvicorn`
- `devices.json` — tinytuya-format device store (written by scan, read at startup)
- `tuya_ac.service` — systemd unit file, runs alongside Tower

### Port

`6677` (configurable via `tuya.service.url` Tower setting)

### Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/health` | Liveness check — returns `{"ok": true}` |
| `GET` | `/devices` | Return all devices from `devices.json` with current DPS state polled over LAN |
| `GET` | `/devices/{id}/state` | Poll a single device's current DPS state |
| `POST` | `/scan` | Run tinytuya cloud scan, rewrite `devices.json`, return discovered devices |
| `POST` | `/devices/{id}/command` | Send a command to a device |

### Command body

Simple plugs and lights send raw DPS:
```json
{ "dps": { "1": true } }
```

AC remotes send a high-level payload that the service translates to the correct IR blaster DPS encoding:
```json
{ "ac": { "power": true, "temp": 24, "mode": "cool", "fan": "auto" } }
```

### Scan body

```json
{ "api_key": "...", "api_secret": "...", "region": "us" }
```

The service calls `tinytuya.Cloud` with these credentials, fetches all devices + local keys, and overwrites `devices.json`. Returns the list of discovered devices.

### AC IR blaster handling

Tuya IR blasters appear in tinytuya as parent devices with AC sub-device DPS. The Python service handles the translation between high-level AC commands (`power`, `temp`, `mode`, `fan`) and the encoded DPS payload required by the specific blaster firmware. Tower sends only the high-level payload.

---

## Layer 2: Tower C# (`Tower.Core/Tuya/`)

### Database — `TuyaDevice` table

Added to `TowerDbContext`. Stores display metadata layered on top of the Python service's device records.

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `int` | PK, auto-increment |
| `DeviceId` | `string` | Tuya device ID — matches Python service |
| `Name` | `string` | User-assigned display name |
| `DeviceType` | `TuyaDeviceType` enum | `Plug`, `Light`, `AcRemote` |
| `Room` | `string?` | Optional grouping label |
| `SortOrder` | `int` | Card display order |

### New files

**`TuyaModels.cs`**
- `TuyaDeviceType` — enum: `Plug`, `Light`, `AcRemote`
- `TuyaDeviceDto` — device record from Python service (id, name, ip, version, dps dict)
- `TuyaStateDto` — current DPS snapshot
- `TuyaCommandRequest` — body for `/command`
- `ScannedDevice` — raw discovery result (id, name, ip, key, version)
- `TuyaDeviceView` — merged view: `TuyaDevice` DB row + live `TuyaStateDto`

**`TuyaServiceClient.cs`**  
`HttpClient` wrapper registered as `AddHttpClient<TuyaServiceClient>`. Methods:
- `Task<bool> HealthAsync()`
- `Task<List<TuyaDeviceDto>> GetDevicesAsync()`
- `Task<TuyaStateDto?> GetStateAsync(string deviceId)`
- `Task<List<ScannedDevice>> ScanAsync(string apiKey, string apiSecret, string region)`
- `Task SendCommandAsync(string deviceId, TuyaCommandRequest cmd)`

**`TuyaDeviceService.cs`**  
Scoped service. Merges Python service data with Tower DB metadata. Methods:
- `Task<List<TuyaDeviceView>> GetDeviceViewsAsync()` — fetches live state, joins with DB rows
- `Task SaveDeviceAsync(TuyaDevice device)` — upsert into DB
- `Task DeleteDeviceAsync(int id)`

### Settings keys

Stored via `SettingsService`, entered on the Settings page:

| Key | Default | Purpose |
|-----|---------|---------|
| `tuya.service.url` | `http://localhost:6677` | Python service base URL |
| `tuya.api_key` | — | Tuya developer API key |
| `tuya.api_secret` | — | Tuya developer API secret |
| `tuya.region` | `us` | Tuya cloud region (`us`/`eu`/`cn`/`in`) |

### DI Registration (Program.cs)

```csharp
builder.Services.AddHttpClient<TuyaServiceClient>();
builder.Services.AddScoped<TuyaDeviceService>();
```

No background worker — state is fetched on-demand.

---

## Layer 3: UI (`Components/Pages/Tuya.razor`)

### Route

`/tuya`

### Page states

1. **Unconfigured** — `tuya.api_key` not set → notice with link to Settings
2. **Service unreachable** — Python service health check fails → error banner
3. **No devices** — service reachable but DB is empty → "Scan Devices" prompt
4. **Normal** — device grid with live state

### Header

Page title "Tuya" + "Refresh" button + "Scan Devices" button.

### Device grid

Responsive CSS grid. Cards optionally grouped by `Room` label.

**Plug card**
- Device name
- On/off toggle

**Light card**
- Device name
- On/off toggle
- Brightness slider (0–100%, DPS `2` or `22` depending on device)

**AC Remote card**
- Device name
- On/off toggle
- Temperature display with − and + buttons (range 16–30 °C)
- Mode selector: Cool / Heat / Fan / Dry / Auto

### Scan flow

1. User clicks "Scan Devices"
2. Tower calls `/scan` with stored credentials
3. Discovered devices appear in an assignment panel below the header
4. Each discovered device: editable name field, type selector (Plug / Light / AC Remote), optional room field
5. User clicks "Save" → Tower writes to `TuyaDevice` table, panel closes, grid renders

### Nav rail entry

Added to `NavMenu.razor` with a plug/bolt icon and label "Tuya", `href="/tuya"`.

---

## Settings Page additions

A new "Tuya" section in `Settings.razor` with fields for:
- Service URL (`tuya.service.url`)
- API Key (`tuya.api_key`)  
- API Secret (`tuya.api_secret`)
- Region dropdown (`tuya.region`)

---

## Error handling

- Python service unreachable: banner, no crash
- Individual device poll failure: card shows "Unreachable" state, other cards unaffected
- Scan failure: inline error message in scan panel
- Command failure: toast-style inline error on the card

---

## Out of scope

- IR learning (capturing new IR codes from physical remotes)
- Scheduling / automations
- Push notifications on device state changes
- Real-time state push (no WebSocket/SSE — polling only, on-demand)
