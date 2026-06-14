# Tower — Design Spec

**Date:** 2026-06-14
**Status:** Approved (design); ready for implementation planning
**Replaces:** `server-monitor` (Python/Flask, port 8888)

## Purpose

Tower is the home-lab command center — "eyes in the sky" for the house. It replaces
`server-monitor` and absorbs the Telegram communication layer from MediaBox, plus
adds richer device, project, and Jellyfin views. Six tabs: Home, Devices, Projects,
Communication, Jellyfin, Settings.

## Decisions

- **Stack:** ASP.NET Core + Blazor Server, .NET 10. Single deployable.
- **Port:** 5600. systemd unit `tower.service`. `deploy.sh` (stop → publish → start).
- **Access:** LAN + Tailscale, no login (same posture as server-monitor).
- **Live updates:** Blazor Server / SignalR. Background pollers as `IHostedService`.
- **Data:** one local SQLite `tower.db` via EF Core (Code-First). DB and log sizes
  are themselves monitored and surfaced in Settings.
- **Telegram cutover:** phased with fallback (feature flag in MediaBox).
- **Delivery:** phased milestones M1 → M2 → M3.

## Architecture

- **Metrics sources:** `/proc`, `/sys/block`, `/proc/net/dev`; shell-outs to
  `smartctl`, `nvidia-smi`/`lspci`, `systemctl`, `journalctl`, `du`, `apt`.
- **Background workers** (replace Python `bg_*` threads):
  - `StatsWorker` — system stats every ~2s (CPU/mem/net/disks/procs/temps/services/GPU/NoIP).
  - `SmartWorker` — per-disk SMART every 5 min.
  - `DiskUsageWorker` — root-FS `du` breakdown every 10 min.
  - `JellyfinWorker` — ffmpeg stats + Jellyfin `/Sessions` poll every 5s; record play events.
  - `MaintenanceScheduler` — apt window check every 1 min.
  - `BackupScheduler` — daily DB backup check every 1 min.
  - `SizeMonitorWorker` — track tower.db + monitored DB/log sizes.
- **CPU profiling:** weekly 168-slot average → `CpuProfile` table; best-low-CPU-window calc.
- **Pi devices:** AtomTV (`http://atomtv:8889`), AtomMiniTV (`http://atomminitv:8889`).
  Tower proxies to existing pi-agents via `HttpClient`. Pi-agents are unchanged.
- **sudo:** `do_maintenance.sh` and `/etc/sudoers` entries (smartctl, shutdown, apt)
  copied into Tower's dir and re-pointed.

## Navigation

Three levels in Devices: primary rail (6 tabs) → device list (Atom / AtomTV /
AtomMiniTV) → per-device submenu. Submenu is `System · Maintenance · Configuration`
for all devices, **plus `Storage` only for Atom (Self)**.

## Tab → functionality map (server-monitor carryover)

### Home (Overview)
At-a-glance: all 3 devices up/down + key metrics, services health, active alerts
(SMART warnings, disk-full, project-down), current Jellyfin sessions, public IP/NoIP.

### Devices → Atom (Self)
- **System** — `collect_stats`: CPU (pct/per-core/load/freq/history), memory+swap,
  network (rates/history/per-NIC), temps, GPU, services status, hostname/uptime.
- **Maintenance** — apt update status; maintenance window scheduler (enabled /
  best-low-CPU window / reboot-enabled); run-now; reboot/shutdown controls;
  `maintenance.log`; last-run result. (server-monitor maintenance functionality.)
- **Configuration** — maintenance window hour, reboot toggle, device-specific settings.
- **Storage** (Atom only) — per-physical-disk SMART + live I/O + alert levels;
  partition usage; root-FS usage breakdown (`du` of `/`, `/var`, `/home/atom/dev`).

### Devices → AtomTV / AtomMiniTV (proxy to pi-agent)
- **System** — pi specs (pi-agent `/api/stats`).
- **Maintenance** — OS update status, auto-shutdown status, shutdown control
  (`/api/shutdown`), idle status, pi logs (`/api/logs`), idle reset (`/api/idle/reset`).
- **Configuration** — idle-timeout setting, pi API keys (`/api/config`).

### Projects
One card per background project (MediaBox:5000, FinanceTracker:5500,
NewsDigest [console], Tower itself). Each card: port-open status, process CPU/mem,
stop/start/restart (systemd), open-in-browser link, **backup DB**, **view logs**.
Bottom: top-20 processes table. Projects defined in a config list
(name, systemd service, port, db path, log dir, working dir, url).

### Communication
Tower owns the Telegram bot. Bot status; subscriber list (migrated from MediaBox
`TelegramAuthStore`); in/out message log; manual send; subscriber management
(kick/block). Command routing via the gRPC bridge.

### Jellyfin
Play history; media stats (most-watched, watch counts, per-user "who's watching");
live sessions; transcode monitoring (ffmpeg count/CPU history + transcode reasons).
Ports `play_history` and `cpu_profile` data from `monitor.db`.

### Settings
Log storage management + retention; monitored DB sizes; Tower API keys (Jellyfin key,
Telegram bot token, Dropbox credentials); Dropbox backup destination + schedule.
**If a key is already present (migrated), it shows "configured" and is not re-prompted.**

## Backups split
- Disk/SMART health → **Storage** (Atom).
- Per-project DB backup buttons → **Projects**.
- Dropbox destination + schedule + status → **Settings**.

## Data model (`tower.db`, EF Core Code-First)
- `PlayHistory` — ported from monitor.db (started_at, media, type, series/season/episode,
  user, play_method, transcode_reasons, codecs, container, client, device).
- `CpuProfile` — ported (slot 0..167, avg_cpu, sample_count).
- `TelegramSubscriber` — migrated from MediaBox auth store (chat_id, name, status, ts).
- `TelegramMessage` — in/out log (direction, chat_id, command, payload, ts).
- `Setting` — key/value (API keys, schedules, window, toggles).
- `ProjectConfig` — project definitions (name, service, port, db_path, log_dir, url).
- Carried non-DB files: maintenance log, weekly usage profile (now in CpuProfile).

## gRPC Telegram bridge
- Tower hosts a gRPC server. MediaBox **dials in** and opens one bidirectional
  stream of `Envelope { oneof: Command | Reply | Notification }`.
  - Inbound TG command → Tower → `Command` → MediaBox executes
    (`/status`, `/downloads`, `/speedmode`, `/watchlist`, `/movie`, `/add`, `/remove`,
    `/scan`, `/youtube*`, `/resetquality`, `/subscribers`, `/kick`, `/block`, `/help`) →
    `Reply` → Tower sends to Telegram.
  - MediaBox events (download done, RSS, crash) → `Notification` → Tower fans out
    to subscribers.
- MediaBox feature flag `UseTowerTelegram`: **on** = local bot disabled, gRPC client
  connects; **off** = existing bot runs (fallback). Shared `tower.proto`.

## Migration & retirement
- Copy Jellyfin API key + Dropbox token from `server-monitor/config.json`.
- Copy Telegram bot token + subscribers from MediaBox config → `tower.db`.
- **After M1 verified:** stop+disable+remove `server-monitor.service`, delete
  `/home/atom/server-monitor/`. Git history remains on GitHub.

## Milestones
- **M1** — Home + Devices (Atom + both Pis) + Settings; replaces server-monitor
  end-to-end. Retire server-monitor after verification.
- **M2** — Projects + Jellyfin tabs.
- **M3** — Communication tab + gRPC bridge + MediaBox cutover (flag flip, then
  strip bot from MediaBox).

## Out of scope (YAGNI)
- Authentication / multi-user.
- Historical long-term metric storage beyond the weekly CPU profile.
- Mobile-native app (responsive web is enough).
