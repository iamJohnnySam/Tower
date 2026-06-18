# PiHole Tab — Design Spec

**Date:** 2026-06-17  
**Status:** Approved

## Overview

Add a PiHole tab to Tower showing summary stats, blocking status, top blocked domains, and upstream DNS servers. Data is fetched on-demand (page load + manual refresh) to keep Tower idle load minimal — no background worker.

## Architecture

### PiHoleClient (Tower.Core/PiHole/PiHoleClient.cs)

A scoped HTTP client service responsible for:
1. POST `/api/auth` with `{ "password": "<stored>" }` → receive session SID
2. Fetch all four data endpoints using `SID` cookie
3. DELETE `/api/auth` to log out after data is collected
4. Return a `PiHoleData` aggregate (or null on error)

Registered as `AddHttpClient<PiHoleClient>()` in Program.cs.

### PiHoleModels (Tower.Core/PiHole/PiHoleModels.cs)

```
PiHoleData
  SummaryStats stats
  bool blockingEnabled
  List<BlockedDomain> topBlockedDomains
  List<string> upstreamServers
  string? error
  DateTime fetchedAt

SummaryStats
  long queriesToday
  long blockedToday
  double blockedPercent
  long uniqueClients
  long gravityDomains

BlockedDomain
  string domain
  int hits
```

### Settings

Password stored in `SettingsService` under key `pihole.password`. Settings page gains a new "PiHole Password" row in the API Keys section (same pattern as Jellyfin API key).

### Razor Page (/pihole)

`Tower/Components/Pages/PiHole.razor`  
`Tower/Components/Pages/PiHole.razor.css`

Injects `PiHoleClient` and `SettingsService`. On `OnInitializedAsync`, if password is configured, fetches data. Manual Refresh button re-fetches.

## API Calls

Base URL: `http://localhost` (PiHole is local)

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/auth` | POST | Get session SID |
| `/api/stats/summary` | GET | Queries, blocked count, %, clients, gravity size |
| `/api/dns/blocking` | GET | Blocking enabled/disabled |
| `/api/stats/top_domains?blocked=true&count=10` | GET | Top 10 blocked domains |
| `/api/config/dns` | GET | Upstream DNS servers |
| `/api/auth` | DELETE | End session |

SID passed as cookie `SID=<sid>` on all authenticated requests.

## Page Layout

```
/pihole
├── Page title: "PiHole"
├── [if unconfigured] Notice: "Set PiHole password in Settings"
├── [if error] Notice: error message
├── Section: Status
│   ├── Blocking ON/OFF badge (green/red)
│   └── Metrics row: Queries Today | Blocked | % Blocked | Clients | Gravity Domains
├── Section: Top Blocked Domains
│   └── Table: Domain | Hits (top 10)
└── Section: Upstream DNS
    └── List of upstream resolver addresses
```

Refresh button in the page header re-runs all fetches.  
"Updated: HH:mm:ss" timestamp shown after data loads (same pattern as Jellyfin).

## Settings Page Change

Add PiHole password row to the API Keys section in `Settings.razor`:
- Key: `pihole.password`
- Input type: `password`
- Same configured/replace/save/cancel pattern as Jellyfin key

## Error Handling

- Password not configured → show notice with link to Settings (no fetch attempted)
- Auth fails (wrong password) → show "Authentication failed — check PiHole password in Settings"
- Network error / timeout → show "Could not reach PiHole at localhost"
- Partial data → show what's available, skip missing sections

## Files Changed / Created

| File | Change |
|---|---|
| `Tower.Core/PiHole/PiHoleClient.cs` | New — HTTP client + auth logic |
| `Tower.Core/PiHole/PiHoleModels.cs` | New — data model records |
| `Tower.Core/Tower.Core.csproj` | No change needed (same project) |
| `Tower/Components/Pages/PiHole.razor` | New — page component |
| `Tower/Components/Pages/PiHole.razor.css` | New — scoped styles |
| `Tower/Components/Layout/NavMenu.razor` | Add PiHole nav link |
| `Tower/Components/Pages/Settings.razor` | Add PiHole password row |
| `Tower/Program.cs` | Register PiHoleClient |
