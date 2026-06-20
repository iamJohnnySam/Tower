# Jellyfin Conversion Pipeline Design

**Date:** 2026-06-20  
**Status:** Approved

## Overview

When Jellyfin transcodes the same video more than 3 times, Tower alerts the user via Telegram with inline keyboard buttons to mark the file for offline conversion or ignore it. During low-CPU windows (or any sustained idle period), Tower automatically converts marked files to H.264/AAC in MKV using ffmpeg so they can direct-play in Jellyfin. Converted files are staged in a test folder for manual verification before replacing the original.

---

## Data Model

### `ConversionJob` (new DB table)

| Field | Type | Notes |
|---|---|---|
| `Id` | int PK | auto-increment |
| `MediaId` | string | Jellyfin item GUID (unique index — prevents duplicate queuing) |
| `MediaName` | string | human display title |
| `OriginalPath` | string | absolute file path on disk (resolved from Jellyfin API at alert time) |
| `TestPath` | string? | `/molecule/Media/ConversionTest/{id}_{filename}.mkv` |
| `Status` | enum | `Queued → Converting → AwaitingApproval → Approved / Rejected / Failed / Ignored` |
| `TranscodeReasons` | string? | copied from PlayHistory at alert time |
| `CreatedAt` | DateTime | when user tapped "Convert" |
| `StartedAt` | DateTime? | when ffmpeg started |
| `CompletedAt` | DateTime? | when ffmpeg finished or failed |
| `ErrorMessage` | string? | ffmpeg stderr on failure |
| `AlertMessageId` | int? | Telegram message_id of the alert (to edit it after response) |

`ConversionStatus` enum values: `Queued`, `Converting`, `AwaitingApproval`, `Approved`, `Rejected`, `Failed`, `Ignored`.

### Jellyfin API addition

`JellyfinClient.GetItemPathAsync(baseUrl, apiKey, mediaId)` → `GET /Items/{id}?Fields=Path` → returns the file path string from the `Path` field of the response. Returns null on failure.

### DbContext

`TowerDbContext` gets `DbSet<ConversionJob> ConversionJobs` with:
- `HasIndex(x => x.MediaId).IsUnique()` 

---

## Telegram Interaction Flow

### Alert upgrade (JellyfinWorker)

When a 3rd+ transcode is detected for a media item:

1. Check `ConversionJobs` table — if `MediaId` already has any record, skip (no duplicate alerts).
2. Resolve file path via `JellyfinClient.GetItemPathAsync`.
3. Send inline keyboard alert:

```
🔁 Repeatedly transcoded (4×)
The Dark Knight (2008)
Reason: VideoCodecNotSupported

What would you like to do?
[ ✅ Mark for conversion ]  [ 🚫 Ignore ]
```

4. Store the returned `message_id` so the message can be edited after the user responds.
5. Keep `_alertedRepeat` suppression as-is so the keyboard is only sent once per run.

### Callback: user taps "Mark for conversion" (`conv:convert:{mediaId}:{messageId}`)

- Create `ConversionJob` with `Status = Queued`
- Edit the alert message to: "✅ Queued for conversion — {MediaName}" (no buttons)
- Answer callback to clear spinner

### Callback: user taps "Ignore" (`conv:ignore:{mediaId}:{messageId}`)

- Create `ConversionJob` with `Status = Ignored`
- Edit the alert message to: "🚫 Ignored — {MediaName}" (no buttons)
- Answer callback to clear spinner

### Completion notification (after successful ffmpeg)

```
✅ Conversion complete
The Dark Knight (2008)
Test file ready at: /molecule/Media/ConversionTest/7_The Dark Knight.mkv

Add ConversionTest/ as a Jellyfin library to verify playback, then:
[ ✅ Approve — replace original ]  [ ❌ Reject — delete test file ]
```

### Callback: "Approve" (`conv:approve:{jobId}:{messageId}`)

- `File.Move(testPath, originalPath, overwrite: true)`
- Delete test file if different path
- Mark job `Approved`
- Edit message to: "✅ Approved — original replaced"

### Callback: "Reject" (`conv:reject:{jobId}:{messageId}`)

- `File.Delete(testPath)`
- Mark job `Rejected`
- Edit message to: "❌ Rejected — test file deleted"

### Failure notification (ffmpeg non-zero exit)

Plain text alert: "❌ Conversion failed — {MediaName}\n{error snippet}"  
Job marked `Failed` (re-queueable from Tower UI).

### Callback dispatcher in TelegramHub

`TelegramHub` gains:
```csharp
void RegisterCallbackHandler(string prefix, Func<string, long, string, CancellationToken, Task> handler)
```

In `HandleIncomingAsync`, after persisting, if the update is a callback: iterate registered handlers and invoke the first whose prefix matches `callbackData`. The update is still broadcast to gRPC clients as today.

Callback data format: `{prefix}{mediaId_or_jobId}:{messageId}` — always fits within Telegram's 64-byte limit (prefix ~15 chars + GUID 36 chars + separator + int).

---

## Conversion Service

**`ConversionService`** — singleton in `Tower.Core/Conversion/`.

### Methods

`RegisterCallbacks(TelegramHub hub)` — wires the four `conv:` prefixes at startup.

`EnqueueAsync(se, filePath, alertMessageId, ct)` — creates `Queued` job, edits Telegram alert.

`IgnoreAsync(mediaId, alertMessageId, ct)` — creates `Ignored` job, edits Telegram alert.

`RunNextJobAsync(CancellationToken ct)` — picks oldest `Queued` job:
1. Sets `Status = Converting`, `StartedAt = now`
2. Builds output path: `/molecule/Media/ConversionTest/{id}_{Path.GetFileNameWithoutExtension(original)}.mkv`
3. Ensures test directory exists
4. Runs ffmpeg in `Task.Run`:
   ```
   ffmpeg -i {input} -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k -c:s copy -map 0 -y {output}
   ```
5. On exit 0: sets `Status = AwaitingApproval`, sends approval keyboard
6. On non-zero: sets `Status = Failed`, `ErrorMessage = last 500 chars of stderr`, sends failure text

`ApproveAsync(jobId, approvalMessageId, ct)` — moves test file over original, marks `Approved`, edits message.

`RejectAsync(jobId, approvalMessageId, ct)` — deletes test file, marks `Rejected`, edits message.

`IsConverting` — bool property; true while ffmpeg is running (prevents parallel jobs).

### ffmpeg command

```
ffmpeg -i {input} -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k -c:s copy -map 0 -y {output}
```

- Video: H.264 8-bit, CRF 20 (visually lossless), medium preset
- Audio: AAC 192k (re-encode for universal compatibility)
- Subtitles/attachments: copy all streams (`-map 0 -c:s copy`)
- Container: MKV (`.mkv`)
- `-y`: overwrite output if exists (safe since path includes job ID)

Timeout: 4 hours (large files). Progress not streamed — job is fire-and-forget within `Task.Run`.

---

## Conversion Scheduler

**`ConversionScheduler`** — BackgroundService in `Tower.Core/Workers/`, ticks every 60s.

Fires `ConversionService.RunNextJobAsync()` if **not already converting** AND either:

1. **Window condition**: current hour == `CpuProfile.BestWindow(...)` AND minute < 10 (same gate as `MaintenanceScheduler`)
2. **Opportunistic condition**: `LiveState.Stats.CpuPct < 30` for 15 consecutive 60s ticks

The scheduler tracks a rolling `_idleTicks` counter; increments when CPU < 30%, resets to 0 otherwise.

Only one ffmpeg job runs at a time (`ConversionService.IsConverting` gate).

---

## Tower UI (Jellyfin page)

New "Conversion Queue" section added below Watch Analytics in `Jellyfin.razor`.

Shows a table of all `ConversionJob` rows (newest first) with:
- Media name
- Status badge (colour-coded: amber=Queued/Converting, blue=AwaitingApproval, green=Approved, red=Failed, grey=Ignored/Rejected)
- Created date
- Actions: "Re-queue" button for `Failed` jobs; "Re-enable alerts" button for `Ignored` jobs (deletes the record)

Uses the same manual "Refresh" pattern as Watch Analytics. A `JellyfinConversionStats` helper (mirroring `JellyfinStats`) provides DB queries.

---

## Configuration

`appsettings.json` gains one key under `Tower`:
```json
"ConversionTestPath": "/molecule/Media/ConversionTest"
```

Surfaced via `TowerConfig` and injected into `ConversionService` at startup.

---

## New Files

| File | Purpose |
|---|---|
| `Tower.Core/Models/ConversionJob.cs` | DB model + ConversionStatus enum |
| `Tower.Core/Conversion/ConversionService.cs` | queue, ffmpeg, approval, callback wiring |
| `Tower.Core/Workers/ConversionScheduler.cs` | low-CPU + window trigger (BackgroundService) |
| `Tower.Core/Jellyfin/JellyfinClient.cs` | add `GetItemPathAsync` |
| `Tower.Core/Data/TowerDbContext.cs` | add `DbSet<ConversionJob>` |
| `Tower.Core/Telegram/TelegramHub.cs` | add callback dispatcher |
| `Tower.Core/Workers/JellyfinWorker.cs` | upgrade alert to inline keyboard |
| `Tower/Components/Pages/Jellyfin.razor` | add Conversion Queue section |
| `Tower/TowerConfig.cs` | add `ConversionTestPath` |
| `Tower/Program.cs` | register services, wire callbacks |

---

## Out of Scope

- Hardware-accelerated encoding (NVENC/VAAPI) — can be added later via a settings key
- Per-client target format selection — H.264/AAC covers all Jellyfin clients
- Progress percentage during ffmpeg — job is fire-and-forget; status is Converting until done
- Automatic Jellyfin library refresh after conversion (Jellyfin auto-scans periodically)
