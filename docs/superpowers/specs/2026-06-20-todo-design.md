# Todo List — Design Spec
Date: 2026-06-20

## Overview

Add a personal todo list to the Tower home page. Todos can be created from the Tower web UI or via Telegram. A background worker sends a Telegram reminder on the morning of a deadline.

---

## 1. Data Model

**Entity:** `TodoItem` in `Tower.Core/Models/TodoItem.cs`

| Field       | Type        | Notes                        |
|-------------|-------------|------------------------------|
| Id          | int         | PK, auto-increment           |
| Title       | string      | Required                     |
| Deadline    | DateTime?   | UTC, nullable                |
| Done        | bool        | Default false                |
| CreatedAt   | DateTime    | UTC, set on create           |
| DoneAt      | DateTime?   | UTC, set when marked done    |

Registered in `TowerDbContext` as `DbSet<TodoItem> Todos`. Table created with `CREATE TABLE IF NOT EXISTS` guard on startup (same pattern as `ConversionJobs`).

---

## 2. Service

**`TodoService`** in `Tower.Core/Todo/TodoService.cs` — scoped service.

Methods:
- `GetOpenAsync()` → `List<TodoItem>` (Done == false, ordered by Deadline asc, CreatedAt asc)
- `GetDueTodayAsync(DateTime today)` → items where `Deadline.Value.Date == today && !Done`
- `AddAsync(string title, DateTime? deadline)` → creates and saves item
- `MarkDoneAsync(int id)` → sets Done=true, DoneAt=UtcNow

---

## 3. Telegram Commands

**`TodoTelegramHandler`** in `Tower.Core/Todo/TodoTelegramHandler.cs` — singleton, registered at startup. Uses `IServiceScopeFactory` to resolve `TodoService` per operation (same pattern as `TelegramHub`).

### Add todo
- Command: `/todo <text>` or `/todo <text> by <date>`
- Only processed if sender is the Telegram admin (checked via `SubscriberService.GetAdmin()`)
- Date parsing: `DateTime.TryParse` for absolute dates (`2026-12-25`, `Dec 25`); weekday keywords (`Monday`–`Sunday`) resolved to the next occurrence of that weekday
- On success: bot replies with `✅ Added: <title>` (+ deadline if set) and a single inline button `[Mark done]` with callback data `todo_done:<id>`
- Non-admin senders are silently ignored

### Mark done via inline button
- Callback prefix: `todo_done:`
- Registered with `TelegramHub.RegisterCallbackHandler("todo_done:", ...)`
- Handler extracts `id`, calls `TodoService.MarkDoneAsync(id)`, edits the original message to `☑ Done: <title>`, answers the callback to clear the Telegram spinner

---

## 4. Reminder Worker

**`TodoReminderWorker`** in `Tower.Core/Workers/TodoReminderWorker.cs` — hosted background service.

- Polls every 60 seconds
- Converts `DateTime.UtcNow` to Sri Lanka time (`TimeZoneInfo` id `"Asia/Colombo"`, UTC+0530) and checks if the local hour == 9 and minute == 0
- Tracks `_lastReminderDate` (in-memory) to prevent duplicate sends within the same day
- On trigger: calls `TodoService.GetDueTodayAsync(today)`, and if any exist, sends one consolidated message to the Telegram admin:
  ```
  📋 Due today:
  • <title 1>
  • <title 2>
  ```
- Registered as `AddHostedService<TodoReminderWorker>()` in `Program.cs`

---

## 5. Home Page UI

New **"To-do"** section appended to `Home.razor` (below Alerts / Public IP).

### List
- Shows only open (not done) todos
- Each row: title + deadline chip if set (e.g. `Due Jun 25`) + "✓" button
- Clicking "✓" calls `TodoService.MarkDoneAsync` and refreshes list
- Empty state: "No open todos"

### Add form
- Inline within the section: text input + `<input type="date">` + "Add" button
- Submitting calls `TodoService.AddAsync` and refreshes list
- No separate page needed

### State
- Loads on `OnInitializedAsync`, refreshes after each mutation
- No live push; manual reload on action is sufficient

---

## 6. Files Created / Modified

| File | Action |
|------|--------|
| `Tower.Core/Models/TodoItem.cs` | New |
| `Tower.Core/Todo/TodoService.cs` | New |
| `Tower.Core/Todo/TodoTelegramHandler.cs` | New |
| `Tower.Core/Workers/TodoReminderWorker.cs` | New |
| `Tower.Core/Data/TowerDbContext.cs` | Add `DbSet<TodoItem>` |
| `Tower/Components/Pages/Home.razor` | Add todo section |
| `Tower/Components/Pages/Home.razor.css` | Add todo section styles |
| `Tower/Program.cs` | Register `TodoService`, `TodoTelegramHandler`, `TodoReminderWorker` |
