# M4 — MediaBox headless + Tower-orchestrated — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax. Spans TWO repos — MediaBox (`/home/atom/dev/MediaBox2026`, branch `m4-mediabox-headless`) and Tower (`/home/atom/dev/Tower`, branch `m4-tower-orchestrate`). Each repo's tasks commit to that repo. NOTHING is deployed until the coordinated cutover (Task 11), which is HELD for user go-ahead. During the build, production keeps running today's MediaBox (UI + self-scheduling) untouched.

**Goal:** Make MediaBox a headless gRPC service (no web UI, no self-scheduling) that Tower drives — Tower schedules the recurring jobs and hosts a full MediaBox management tab — over a new `Tower → MediaBox` gRPC control channel, keeping the existing `MediaBox → Tower` Telegram bridge intact.

**Architecture:** Two gRPC channels: (1) existing MediaBox→Tower Telegram on Tower:5601 (untouched); (2) new Tower→MediaBox control on MediaBox:5602 (h2c). MediaBox keeps all capability logic but exposes it via a `MediaBoxControl` gRPC server; its background services become callable (`RunOnceAsync`) behind a `SelfSchedule` fallback flag (default false). Tower gains a `MediaBoxControlClient`, a `MediaBoxScheduler`, and a MediaBox tab — gated by a Tower `MediaBox:Orchestrate` flag. Both flags flip together at cutover.

**Tech Stack:** .NET 10, `Grpc.AspNetCore` (MediaBox server), `Grpc.Net.Client` (Tower client), Blazor Server (Tower tab), xUnit.

---

## Shared contract — `protos/mediabox_control.proto`

Authored in MediaBox at `MediaBox2026/MediaBox2026/Protos/mediabox_control.proto`, copied byte-identical into Tower at `Tower/protos/mediabox_control.proto`.

```proto
syntax = "proto3";
option csharp_namespace = "MediaBox.Control.Grpc";
package mediabox.control;

service MediaBoxControl {
  // Triggers (run a unit of work once)
  rpc Scan(Empty) returns (RunResult);
  rpc Organize(Empty) returns (RunResult);
  rpc RssCheck(Empty) returns (RunResult);
  rpc TransmissionPoll(Empty) returns (RunResult);
  rpc YouTubeDownload(Empty) returns (RunResult);
  rpc YouTubePause(TitleArg) returns (RunResult);
  rpc YouTubeResume(TitleArg) returns (RunResult);
  rpc ResetQuality(Empty) returns (RunResult);
  rpc ToggleSpeedMode(Empty) returns (RunResult);
  // Queries
  rpc GetStatus(Empty) returns (Status);
  rpc GetDownloads(Empty) returns (DownloadList);
  rpc GetLibrary(LibraryQuery) returns (MediaList);
  rpc GetWatchlist(Empty) returns (WatchlistItems);
  rpc GetYouTubeSources(Empty) returns (YouTubeSources);
  rpc GetSettings(Empty) returns (SettingsMap);
  // Mutations
  rpc AddWatchlist(TitleArg) returns (RunResult);
  rpc RemoveWatchlist(TitleArg) returns (RunResult);
  rpc SearchAndAddMovie(TitleArg) returns (RunResult);
  rpc UpdateSettings(SettingsMap) returns (RunResult);
}

message Empty {}
message TitleArg { string title = 1; }
message LibraryQuery { string type = 1; }  // "tv" | "movies"
message RunResult { bool ok = 1; string message = 2; }
message Status { int32 tv_shows = 1; int32 movies = 2; int32 active_downloads = 3; bool speed_mode = 4; string summary = 5; }
message DownloadItem { string name = 1; double percent = 2; string status = 3; int64 size_bytes = 4; double rate_down = 5; }
message DownloadList { repeated DownloadItem items = 1; }
message MediaItem { string name = 1; string year = 2; int32 seasons = 3; string path = 4; }
message MediaList { repeated MediaItem items = 1; }
message WatchlistItem { string name = 1; string status = 2; string quality = 3; }
message WatchlistItems { repeated WatchlistItem items = 1; }
message YouTubeSource { string title = 1; string url = 2; bool paused = 3; }
message YouTubeSources { repeated YouTubeSource items = 1; }
message SettingsMap { map<string,string> values = 1; }
```

The exact field population comes from EXISTING code — implementers MUST read MediaBox's Telegram command handlers (`HandleCommandAsync` cases for /status, /downloads, /watchlist, /youtube*) and the services (TransmissionClient, MediaCatalogService, MovieWatchlistService, YouTubeDownloadService) to map data faithfully. The gRPC layer is a thin adapter — never duplicate business logic.

---

## PART A — MediaBox (repo /home/atom/dev/MediaBox2026, branch `m4-mediabox-headless`)

All built behind flags / on a branch; production unchanged until cutover.

### Task 1: gRPC control server scaffold (alongside the existing app)

**Files:** `MediaBox2026/MediaBox2026/Protos/mediabox_control.proto`, `MediaBox2026/MediaBox2026/MediaBox2026.csproj`, `MediaBox2026/MediaBox2026/Services/MediaBoxControlService.cs`, `MediaBox2026/MediaBox2026/Program.cs`.

- [ ] **Step 1** — `git checkout -b m4-mediabox-headless`. Create the proto (above). Add packages: `dotnet add MediaBox2026/MediaBox2026/MediaBox2026.csproj package Grpc.AspNetCore`; add `<Protobuf Include="Protos\mediabox_control.proto" GrpcServices="Server" />`.
- [ ] **Step 2** — In Program.cs: `builder.Services.AddGrpc();`. Add a SECOND Kestrel endpoint for the control server WITHOUT disturbing the existing `UseUrls("http://0.0.0.0:5000")`. Replace the `UseUrls` line with `builder.WebHost.ConfigureKestrel(o => { o.ListenAnyIP(5000); o.ListenAnyIP(5602, l => l.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2); });` (5000 = HTTP/1.1+2 for the still-present Blazor UI; 5602 = h2c gRPC). Add `app.MapGrpcService<MediaBoxControlService>();`.
- [ ] **Step 3** — Create `MediaBoxControlService : MediaBoxControl.MediaBoxControlBase` with every RPC throwing `RpcException(Unimplemented)` for now (real impls in Tasks 3–5). Build clean: `dotnet build MediaBox2026/MediaBox2026.csproj`.
- [ ] **Step 4** — Commit: `git add -A && git commit -m "Add MediaBoxControl gRPC server scaffold on :5602"`

### Task 2: Make the 7 background services callable + SelfSchedule flag

**Files:** the 7 service files in `MediaBox2026/MediaBox2026/Services/` (RssFeedMonitorService, YouTubeDownloadService, MediaScannerService, TransmissionMonitorService, DownloadOrganizerService, MovieWatchlistService, MediaCatalogService); `Models/MediaModels.cs` (add flag); `Program.cs`.

- [ ] **Step 1** — Add `public bool SelfSchedule { get; set; } = false;` to `MediaBoxSettings` in `Models/MediaModels.cs`.
- [ ] **Step 2** — For EACH of the 7 services: extract the per-cycle work from the `ExecuteAsync` loop body into a `public async Task RunOnceAsync(CancellationToken ct)` method (read each — e.g. TransmissionMonitorService's loop calls `MonitorAsync` + `CheckPendingLargeTorrentsAsync`, so `RunOnceAsync` = those; MediaScannerService's loop body becomes RunOnceAsync; etc.). The existing `ExecuteAsync` (BackgroundService) is kept but its loop body now just `await RunOnceAsync(ct)` on the same interval — so flag-ON behavior is byte-for-byte identical. Do this as a PURE extraction (no logic change).
- [ ] **Step 3** — In Program.cs, gate the `AddHostedService(...)` registrations for those 7 services on `settings.SelfSchedule`: when true → register hosted (today's behavior); when false → register the service as a plain singleton only (so its `RunOnceAsync` is callable by the gRPC triggers, but no timer loop runs). Read the flag the same way M3 reads `UseTowerTelegram` (`builder.Configuration.GetSection("MediaBox").GetValue<bool>("SelfSchedule")`).
- [ ] **Step 4** — Build clean. Commit: `"Make background services callable (RunOnceAsync) + SelfSchedule flag (default off)"`

### Task 3: Implement MediaBoxControl triggers

**Files:** `Services/MediaBoxControlService.cs`.

- [ ] Inject the 7 services + TransmissionClient + MediaBoxState. Implement each trigger RPC to call the matching `RunOnceAsync`/existing method and return `RunResult{ok,message}`, mirroring what the Telegram command handler for that action does (read HandleCommandAsync cases /scan, /youtube, /youtubepause, /youtuberesume, /resetquality, /speedmode; and Organize→DownloadOrganizerService.RunOnceAsync, RssCheck→RssFeedMonitorService.RunOnceAsync, TransmissionPoll→TransmissionMonitorService.RunOnceAsync). Each wrapped in try/catch → `RunResult{ok=false,message=ex.Message}`; never throw out of the handler. `ToggleSpeedMode` calls the same Transmission turtle toggle the /speedmode handler uses.
- [ ] Build clean. Commit: `"Implement MediaBoxControl trigger RPCs"`

### Task 4: Implement MediaBoxControl queries + settings reader

**Files:** `Services/MediaBoxControlService.cs`, `Services/MediaBoxSettingsIo.cs` (new — extracted from Settings.razor).

- [ ] **Step 1** — Create `Services/MediaBoxSettingsIo.cs`: extract the read/serialize logic mirroring `Settings.razor`'s view of editable settings into `Dictionary<string,string> Read()` (the non-sensitive MediaBoxSettings values shown in the UI) and (Task 5) `Write(Dictionary<string,string>)`. Read `Components/Pages/Settings.razor` to see exactly which fields are editable and how they map to `appsettings.json`.
- [ ] **Step 2** — Implement `GetStatus` (counts from MediaBoxState/MediaCatalogService — mirror /status handler: TV/movie counts, active downloads, speed mode), `GetDownloads` (TransmissionClient active torrents — mirror /downloads), `GetLibrary` (MediaCatalogService TV or movies by `query.type`), `GetWatchlist` (MovieWatchlistService items — mirror /watchlist), `GetYouTubeSources` (YouTubeDownloadService sources + paused state — mirror /youtube listing), `GetSettings` (→ `SettingsMap` from `MediaBoxSettingsIo.Read()`). Each defensive (try/catch → empty result).
- [ ] Build clean. Commit: `"Implement MediaBoxControl query RPCs + settings reader"`

### Task 5: Implement MediaBoxControl mutations + settings writer

**Files:** `Services/MediaBoxControlService.cs`, `Services/MediaBoxSettingsIo.cs`.

- [ ] **Step 1** — Add `MediaBoxSettingsIo.Write(Dictionary<string,string> values)` that persists to `appsettings.json` (non-sensitive) — extract the EXACT persistence logic from `Settings.razor.SaveSettings` (which does `File.WriteAllBytes` on appsettings.json). Sensitive/secret fields are out of scope for the Tower UI (leave Secrets handling as-is / not editable from Tower for M4 — note this). 
- [ ] **Step 2** — Implement `AddWatchlist`/`RemoveWatchlist` (MovieWatchlistService — mirror /add, /remove handlers), `SearchAndAddMovie` (mirror /movie search+add), `UpdateSettings` (`MediaBoxSettingsIo.Write` from the SettingsMap). Each → `RunResult`, never throws.
- [ ] Build clean. Commit: `"Implement MediaBoxControl mutation RPCs + settings writer"`

### Task 6: Strip the web UI — MediaBox becomes gRPC-only

**Files:** delete `Components/` (16 razor), `wwwroot/`, `Components/App.razor` host bits; `Program.cs`; remove `FallbackComponentActivator`.

- [ ] **Step 1** — Remove the Blazor/Razor host wiring from Program.cs: `AddRazorComponents()/.AddInteractiveServerComponents()`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`, `AddSingleton<IComponentActivator, FallbackComponentActivator>()`, static-files/antiforgery middleware that only served the UI. KEEP the `/health` MapGet, the gRPC server, the Telegram bridge (TowerUpdateConsumer/notifier), and all services.
- [ ] **Step 2** — Delete `Components/` (all 16 .razor), `wwwroot/`, `FallbackComponentActivator.cs`, and remove the Razor-related `<PackageReference>`s if any are now unused (leave anything still referenced). Change Kestrel to a single gRPC endpoint: `o.ListenAnyIP(5602, l => l.Protocols = HttpProtocols.Http2);` and a small HTTP/1.1 endpoint ONLY if `/health` is still wanted (keep 5000 HTTP/1.1 just for `/health`, or move `/health` to a gRPC `GetStatus` and drop 5000 — simplest: keep a tiny `o.ListenAnyIP(5000)` HTTP/1.1 for `/health`). 
- [ ] **Step 3** — Build clean (`dotnet build`). The app is now headless: gRPC control on 5602 + `/health` on 5000 + the Telegram gRPC client. Commit: `"Strip Blazor UI — MediaBox is now a headless gRPC service"`

---

## PART B — Tower (repo /home/atom/dev/Tower, branch `m4-tower-orchestrate`)

### Task 7: MediaBox gRPC client wrapper + config

**Files:** `Tower/protos/mediabox_control.proto` (verbatim copy, Client), `src/Tower/Tower.csproj`, `src/Tower.Core/MediaBox/MediaBoxClient.cs`, `src/Tower/TowerConfig.cs`, `src/Tower/Program.cs`.

- [ ] **Step 1** — `git checkout -b m4-tower-orchestrate`. Copy the proto verbatim; add `<Protobuf Include="..\..\protos\mediabox_control.proto" GrpcServices="Client" />` to Tower.csproj. (Generated types land in the web project — put the client wrapper where it can see them, OR generate into Tower.Core. Simplest: client wrapper in `src/Tower/` web project alongside generated types, like the Telegram service. Adjust namespaces accordingly.)
- [ ] **Step 2** — Create a `MediaBoxClient` wrapper over `MediaBoxControl.MediaBoxControlClient` (channel to `cfg.MediaBoxGrpcUrl`, default `http://localhost:5602`). Every method try/catch → safe default (RunResult{ok=false} / empty list / null) so a MediaBox outage never throws into Tower UI/scheduler. Add `MediaBoxGrpcUrl` + a `MediaBox:Orchestrate` bool (default false) + a `MediaBoxJobs` interval section to `TowerConfig`/appsettings. Register the client in Program.cs (`AddSingleton` + the generated client/channel).
- [ ] **Step 3** — Build clean; `dotnet test tests/Tower.Core.Tests` unchanged. Commit: `"Add Tower MediaBox gRPC client + config (Orchestrate flag, default off)"`

### Task 8: MediaBoxScheduler (Tower drives the cadences)

**Files:** `src/Tower.Core/Workers/MediaBoxScheduler.cs` (or `src/Tower/`), `src/Tower/Program.cs`, `src/Tower/appsettings.json`.

- [ ] **Step 1** — Port the cadences from MediaBox into `appsettings.json` `Tower:MediaBoxJobs`: e.g. `{ "RssCheckMinutes":30, "OrganizeMinutes":10, "TransmissionPollMinutes":5, "ScanHours":12, "YouTubeMinutes":<current>, "WatchlistMinutes":2 }` (read MediaBox's current `MediaScanHours` etc. from its appsettings to match).
- [ ] **Step 2** — `MediaBoxScheduler : BackgroundService` (inject `MediaBoxClient`, the jobs config, SettingsService). Only runs when `MediaBox:Orchestrate` == true (read each minute; idle otherwise — the safety gate, like Tower's `telegram.active`). When active, track per-job "last run" timestamps and call the matching trigger when due (`RssCheck`, `Organize`, `TransmissionPoll`, `Scan`, `YouTubeDownload`, watchlist). Each call in try/catch (client never-throws anyway); a due job that fails logs + retries next tick. Add a small unit test for the "is this job due?" timestamp logic (pure function). Register as hosted service.
- [ ] **Step 3** — Build + test clean. Commit: `"Add MediaBoxScheduler (Tower-driven job cadences, Orchestrate-gated)"`

### Task 9: MediaBox tab — shell + Downloads + Library

**Files:** `src/Tower/Components/Pages/MediaBox.razor` (`@page "/mediabox"`, linked from the Projects MediaBox card), `MediaBox.razor.css`.

- [ ] Inject `MediaBoxClient`. Tabbed sub-views (reuse the device-submenu pill pattern + global CSS). Build **Downloads** (from `GetDownloads` — name, % bar, status, size, rate; auto-refresh 5s, `_disposed` guard + IAsyncDisposable) and **Library** (from `GetLibrary("tv")`/`GetLibrary("movies")` — tables). Show "MediaBox unreachable" if the client returns empty/offline. Commit: `"Add Tower MediaBox tab: Downloads + Library"`

### Task 10: MediaBox tab — Watchlist + YouTube + Settings + Actions + Logs

**Files:** `src/Tower/Components/Pages/MediaBox.razor` (extend).

- [ ] **Watchlist** (GetWatchlist table + Add/Remove via mutations + a SearchAndAddMovie box). **YouTube** (GetYouTubeSources + per-source Pause/Resume + Download-all trigger). **Settings** (GetSettings → editable form → UpdateSettings; confirm on save). **Actions** (buttons: Scan/Organize/RssCheck/TransmissionPoll/ResetQuality/ToggleSpeedMode → triggers, show RunResult). **Logs** (reuse Tower's existing MediaBox log reading from Projects — `ProjectControl.ReadLogs(logDir, "mediabox")`). All mutations/triggers behind a brief confirm where destructive. Build + test. Commit: `"Add Tower MediaBox tab: Watchlist, YouTube, Settings, Actions, Logs"`

---

## PART C — Cutover (HELD for user go-ahead)

### Task 11: Coordinated cutover + verify + merge

**Do NOT run without explicit approval.** Production MediaBox (UI + self-scheduling) runs untouched until this task.

- [ ] **Step 1** — Deploy MediaBox headless branch: set MediaBox `SelfSchedule=true` in deployed appsettings FIRST (so it keeps scheduling during the brief window), deploy (gRPC server comes up on 5602, UI gone). Verify `/health` + gRPC reachable (`grpcurl -plaintext localhost:5602 list`).
- [ ] **Step 2** — Deploy Tower orchestrate branch (scheduler present but `MediaBox:Orchestrate=false` → idle; MediaBox tab live, reachable via the new client). Verify the MediaBox tab loads real data (Downloads/Library/Status) over gRPC.
- [ ] **Step 3** — Flip together: set Tower `MediaBox:Orchestrate=true` (Tower scheduler starts driving) AND MediaBox `SelfSchedule=false` + restart mediabox (its timers stop). Now exactly one scheduler (Tower). Verify over ~the shortest job interval that Tower triggers fire (logs) and notifications still arrive in Telegram.
- [ ] **Step 4** — Verify parity: every trigger works from the Tower Actions tab; queries render; Settings save persists; the Telegram bot + notifications still work (Telegram bridge untouched). Firewall: `sudo ufw allow 5602/tcp`.
- [ ] **Step 5** — Rollback path (documented): set MediaBox `SelfSchedule=true` + restart mediabox → its own timers resume (independent of Tower). Tower `Orchestrate=false` to stop double-driving.
- [ ] **Step 6** — Merge both branches (MediaBox `m4-mediabox-headless`→master; Tower `m4-tower-orchestrate`→main), push, redeploy from the merged defaults. Final verify.

---

## Self-Review

**Spec coverage:** UI removal → Task 6. De-schedule + SelfSchedule fallback (default false) → Task 2. gRPC control server + ~18 methods → Tasks 1,3,4,5. Settings persistence preserved (extracted from Settings.razor) → Tasks 4,5. Tower client (never-throws) → Task 7. Tower scheduler (Orchestrate-gated, ported cadences) → Task 8. Tower MediaBox tab (Downloads/Library/Watchlist/YouTube/Settings/Actions/Logs) → Tasks 9,10. Two-channel architecture (5601 untouched, 5602 new) → Tasks 1,7. Phased cutover with both fallback flags + Telegram bridge untouched → Task 11.

**Placeholder scan:** No TBDs. gRPC handler tasks point at the EXACT existing command handlers/services to mirror (rather than restating their logic) — implementers must read them; the proto + wrapper contracts are concrete.

**Type consistency:** `MediaBoxControl` proto messages, `RunOnceAsync`, `SelfSchedule`, `MediaBoxSettingsIo.Read/Write`, `MediaBoxClient`, `MediaBox:Orchestrate`, `MediaBoxScheduler` used consistently across tasks.

**Risk controls:** built on branches, nothing deployed until Task 11; MediaBox keeps current behavior (UI + timers) in production throughout the build; `SelfSchedule` (MediaBox) + `Orchestrate` (Tower) flags flip together at cutover with a one-line rollback; the M3 Telegram bridge is never touched.
