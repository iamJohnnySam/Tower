# M4 — MediaBox headless service, Tower-orchestrated (Design Spec)

**Date:** 2026-06-16
**Status:** Approved (design); ready for implementation planning
**Repos:** Tower (`/home/atom/dev/Tower`) + MediaBox (`/home/atom/dev/MediaBox2026`)
**Builds on:** M3 (Tower owns Telegram; MediaBox↔Tower gRPC bridge already live)

## Purpose

Turn MediaBox into a **headless, callable service** that Tower drives. Remove its web
UI and its self-scheduling; expose its capabilities over a new `MediaBoxControl` gRPC
service that Tower calls. Tower gains the scheduling and a rich MediaBox management tab.

## Decisions (approved)

- **UI:** removed from MediaBox; **Tower absorbs a full MediaBox tab** (browse + control).
- **Transport:** **gRPC** — MediaBox hosts a `MediaBoxControl` server; Tower is the client.
- **Scheduling:** moves to Tower (a `MediaBoxScheduler` hosted service). MediaBox keeps a
  `SelfSchedule` fallback flag, **default false** (Tower drives); flip true to restore
  MediaBox's own timers if Tower's scheduler ever fails.
- **Settings editing:** preserve MediaBox's existing settings-persistence mechanism
  (confirmed during planning) and expose it via `UpdateSettings`.

## Architecture

Two independent gRPC channels between the two processes:
1. **MediaBox → Tower** (existing, M3): Telegram bridge. MediaBox is client, Tower server on **5601**. Unchanged.
2. **Tower → MediaBox** (new, M4): control. MediaBox is server on **5602** (h2c), Tower is client.

MediaBox keeps ALL capability logic (TransmissionClient, JellyfinClient, MediaCatalogService,
MediaScanner, RSS, YouTube, Watchlist, Organizer, MediaDatabase, MediaBoxState) and the
Telegram command handlers. The change is structural: capabilities are **callable** rather than
self-scheduled, and the Blazor UI is gone.

## MediaBox changes

- **Strip the web UI:** delete `Components/` (16 `.razor` files), Razor/Blazor host wiring
  (`AddRazorComponents`/`AddInteractiveServerComponents`/`MapRazorComponents`), `wwwroot`,
  `FallbackComponentActivator`, and the port-5000 web binding. MediaBox becomes a gRPC-only
  ASP.NET host. Keep the `/health` endpoint.
- **De-schedule:** the 7 self-scheduled `BackgroundService`s — RssFeedMonitorService,
  YouTubeDownloadService, MediaScannerService, TransmissionMonitorService,
  DownloadOrganizerService, MovieWatchlistService, MediaCatalogService — are refactored so
  their unit of work is a callable `RunOnceAsync(CancellationToken)` method. They are NO
  LONGER registered via `AddHostedService` (unless `SelfSchedule=true`, the fallback).
- **`SelfSchedule` flag** (in `MediaBoxSettings`, default false): when true, register the
  services as hosted (today's timer behavior) — the fallback. When false, they're plain
  singletons whose `RunOnceAsync` is invoked by the gRPC triggers.
- **New `MediaBoxControl` gRPC server** on port 5602 (`Grpc.AspNetCore`, h2c), shared proto
  `protos/mediabox_control.proto` (in both repos, byte-identical). Methods:
  - *Triggers* (→ `RunResult{ok,message}`): `Scan`, `Organize`, `RssCheck`, `TransmissionPoll`,
    `YouTubeDownload`, `YouTubePause(title?)`, `YouTubeResume(title?)`, `ResetQuality`,
    `ToggleSpeedMode`.
  - *Queries*: `GetStatus()→Status`, `GetDownloads()→DownloadList`, `GetLibrary(LibraryType)→MediaList`,
    `GetWatchlist()→WatchlistItems`, `GetYouTubeSources()→YouTubeSources`, `GetSettings()→SettingsMap`.
  - *Mutations* (→ `RunResult`): `AddWatchlist(name)`, `RemoveWatchlist(name)`,
    `SearchAndAddMovie(name)`, `UpdateSettings(SettingsMap)`.
  Each method wraps logic that already exists in the corresponding service or Telegram command
  handler — the gRPC layer is a thin adapter, never duplicating business logic.
- **Telegram command handlers stay** (they call the same service methods). The bot keeps working.

## Tower changes

- **MediaBox gRPC client** (`Grpc.Net.Client`, proto `GrpcServices=Client`) to
  `http://localhost:5602`, exposed as a typed `MediaBoxControlClient` wrapper (never throws →
  safe defaults if MediaBox is down).
- **`MediaBoxScheduler`** (`IHostedService`): calls the Trigger* RPCs on configured intervals,
  ported from MediaBox's current cadences into Tower config (`Tower:MediaBoxJobs`: e.g.
  rssCheck 30m, organize 10m, transmissionPoll 5m, scan 12h, youtube/​watchlist/​catalog per
  current values). Each job in try/catch; a MediaBox outage logs and retries next tick.
- **MediaBox tab** (under the Projects area, e.g. `/projects/mediabox` or a sub-view of the
  MediaBox project card): sub-sections **Downloads, Library (TV/Movies), Watchlist, YouTube,
  Settings, Logs, Actions** — backed by the gRPC queries/mutations/triggers. Reuse Tower's
  existing dark theme + components. Logs reuse Tower's existing MediaBox log reading.
- **Projects card**: MediaBox port check moves from 5000 → 5602 (or a `GetStatus` gRPC ping);
  MediaBox `Url` (web UI) removed.
- Firewall: open 5602 (LAN-local; Tower and MediaBox are same host, but keep consistent).

## Data flow

- **Scheduled work:** Tower `MediaBoxScheduler` → gRPC Trigger* → MediaBox service `RunOnceAsync`
  → does the work → notifications still go MediaBox → (gRPC Telegram bridge) → Tower → Telegram.
- **User browsing/control (Tower tab):** Tower UI → gRPC query/mutation/trigger → MediaBox.
- **Telegram commands:** unchanged (Tower → gRPC StreamUpdates → MediaBox dispatcher → service).

## Error handling

- MediaBoxControlClient never throws (catch → empty/error result); Tower UI shows "MediaBox
  unreachable" gracefully. Scheduler jobs are independent + retried. MediaBox gRPC handlers
  wrap exceptions into `RunResult{ok=false,message}` (or RpcException Internal) — never crash
  the server.

## Cutover (phased, fallback-protected)

Built on branches (Tower + MediaBox). Deploy MediaBox with `SelfSchedule=false` and the
new gRPC server; deploy Tower with the scheduler + tab. Verify Tower drives all the jobs and
the tab works; the bot/notifications keep working throughout (Telegram bridge untouched).
Rollback: set MediaBox `SelfSchedule=true` (timers resume) — independent of the gRPC tab.

## Milestones (M4 sub-phases)

1. **MediaBox slim-down + gRPC control server** (proto, server, de-schedule, SelfSchedule flag, strip UI).
2. **Tower MediaBox client + scheduler** (client wrapper, MediaBoxScheduler, config).
3. **Tower MediaBox tab** (Downloads/Library/Watchlist/YouTube/Settings/Logs/Actions).
4. **Cutover + verify + merge**.

## Out of scope (YAGNI)

- Re-implementing the MediaBox web UI anywhere except Tower's tab.
- Changing the Telegram bridge (M3) — untouched.
- New capabilities — M4 only relocates UI/scheduling, it doesn't add features.
