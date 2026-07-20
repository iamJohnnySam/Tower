# Bank Statement Ingestion — Tower → FinanceTracker

**Date:** 2026-07-20
**Repos:** `Tower` (email scan + dispatch), `FinanceTracker` (unlock, parse, apply)

## Problem

Bank/broker statements arrive as Gmail messages in a new **Statements** label. Some carry the
balance in the email body; most carry a **password-locked PDF**. Balances must land on the
`Balance` table against the correct `FinancialAccount` at the correct **month-end** date.

Tower must never open the PDF (it may be locked). Tower's only job is: match the sender,
work out the statement month, and hand the value or the file to FinanceTracker.

## Existing parts reused (do not rebuild)

| Part | Location | Role |
|---|---|---|
| `IPdfUnlockService` | FT `Services/PdfUnlockService.cs` | `IsPdf` / `IsEncrypted` / `TryUnlock` (PdfSharp) |
| `FinancialAccount.StatementPasswordEnc` | FT `Models/FinancialAccount.cs` | Data-Protection-encrypted PDF password, purpose string `FinanceTracker.AccountStatementPassword` |
| `IAttachmentService` | FT `Services/AttachmentService.cs` | `SaveAsync` (orphan via `entityId: 0`), `AttachAsync`, `DeleteAsync` |
| `ExternalApiController` | FT `Controllers/ExternalApiController.cs` | `X-Api-Key` → `AppSettings.ApiKey` → `UserId` |
| `BillProfiles` / `BillMailWorker` | Tower `Tower.Core/Bills`, `Tower.Core/Workers` | The pattern the Statements side mirrors |
| `GmailReader` | Tower `Tower.Core/Gmail/GmailReader.cs` | `ListLabelsAsync`, `ListMessageIdsAsync`, `GetMessageAsync`, `GetPdfAttachmentAsync`, `TrashMessageAsync` |
| `FinanceTrackerClient` | Tower `Tower.Core/Bills/FinanceTrackerClient.cs` | Base URL + API key from the `Settings` table |
| `pdftotext -layout` | poppler-utils, installed | Text extraction; shelled out exactly as `BillMailWorker.PdfToTextAsync` does |

---

## 1. FinanceTracker — data

Two new tables. Both follow the existing raw-SQL migration pattern in `ApplyMigrationHelper`
(`EnsureXTable(connection)`), **not** an EF migration — matching `EnsureReceiptPendingTable` /
`EnsureAttachmentsTable`.

### `StatementProfile`

```csharp
public class StatementProfile
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    public int FinancialAccountId { get; set; }
    /// <summary>Regex over the PDF text; capture group 1 = the closing balance.</summary>
    [Required][StringLength(500)] public string BalanceRegex { get; set; } = "";
    /// <summary>Optional regex; group 1 = statement date. When it matches, it overrides
    /// the month-end date Tower supplied.</summary>
    [StringLength(500)] public string? DateRegex { get; set; }
    /// <summary>Date format for DateRegex group 1, e.g. "dd/MM/yyyy". Null = DateTime.Parse.</summary>
    [StringLength(40)] public string? DateFormat { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
}
```

**One profile per account** (unique index on `FinancialAccountId`). Multiple statement layouts
are handled with regex alternation, not multiple rows — keeps "is there a profile?" a boolean.

### `PendingStatement`

```csharp
public enum PendingStatementStatus { NeedsPassword, NeedsProfile, Applied, Failed }

public class PendingStatement
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    public int FinancialAccountId { get; set; }
    /// <summary>Month-end date supplied by Tower; the fallback when DateRegex doesn't match.</summary>
    public DateTime StatementDate { get; set; }
    /// <summary>Gmail message id — unique, makes ingestion idempotent.</summary>
    [Required][StringLength(120)] public string SourceRef { get; set; } = "";
    /// <summary>The PDF, held as an Attachment. While pending it is an orphan
    /// (EntityType "PendingStatement", EntityId = this.Id); on Applied it is rebound to
    /// EntityType "Balance", EntityId = BalanceId.</summary>
    public int AttachmentId { get; set; }
    public bool IsLocked { get; set; }
    public PendingStatementStatus Status { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? ExtractedBalance { get; set; }
    public int? BalanceId { get; set; }
    [StringLength(500)] public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
```

Storing the PDF as an `Attachment` rather than a bare `FilePath` means the unlocked file
lands in the same `wwwroot/uploads/...` scheme as everything else and gets rebound to the
`Balance` with one `AttachAsync` call.

---

## 2. FinanceTracker — `StatementIngestService`

`Services/StatementIngestService.cs`, registered scoped. Interface `IStatementIngestService`.

```csharp
Task<PendingStatement> IngestAsync(string userId, FinancialAccount account, DateTime statementDate,
                                   string sourceRef, byte[] pdf, string fileName);
Task ProcessAsync(int pendingStatementId);
Task<int> ProcessAllForAccountAsync(string userId, int accountId);   // returns count applied
```

### `IngestAsync`

1. If a `PendingStatement` with this `SourceRef` exists → return it unchanged (idempotent).
2. Save the PDF as an orphan attachment, create the row (`IsLocked` from
   `IPdfUnlockService.IsEncrypted`), bind the attachment to it, then call `ProcessAsync`.

### `ProcessAsync` — the pipeline

1. **Unlock.** If `IsLocked`:
   - Decrypt `account.StatementPasswordEnc`; if absent → `Status = NeedsPassword`, stop.
   - `TryUnlock`; null → `Status = NeedsPassword`, `Error = "Saved password did not work"`, stop.
   - On success: save the unlocked bytes as a new attachment bound to this pending statement,
     **delete the locked attachment** (`IAttachmentService.DeleteAsync`, which removes the file),
     point `AttachmentId` at the new one, `IsLocked = false`.
2. **Profile.** No active `StatementProfile` for `FinancialAccountId` → `Status = NeedsProfile`, stop.
3. **Text.** `pdftotext -layout <file> -`. Empty output → `Status = Failed`,
   `Error = "No text in PDF (scanned image?)"`, stop.
4. **Balance.** `BalanceRegex` over the text, group 1, strip commas, `decimal.Parse` invariant.
   No match → `Status = Failed`, `Error = "Balance regex did not match"`, stop.
5. **Date.** If `DateRegex` matches, parse group 1 (with `DateFormat` when set) and use it;
   otherwise keep `StatementDate`.
6. **Apply.** Upsert `Balance` for (`FinancialAccountId`, date.Date): update if one exists,
   else insert. `Currency = account.Currency`, `ConfirmationStatus = Received`.
7. **File.** `AttachAsync(AttachmentId, "Balance", balance.Id)`.
   `Status = Applied`, `ExtractedBalance`, `BalanceId`, `ProcessedAt`, `Error = null`.

Every terminal state is recorded on the row — the `/statements` page is just a view of it.

### `ProcessAllForAccountAsync`

Re-runs `ProcessAsync` for every non-`Applied` `PendingStatement` on that account. This one
method satisfies **both** backfill requirements:

- password entered once → all that account's locked statements unlock;
- profile added → all that account's waiting statements parse and apply.

Callers: the password box and the profile save button on `/statements`.

---

## 3. FinanceTracker — API (`ExternalApiController`)

Auth is the existing `X-Api-Key` → `AuthAsync` → `auth.UserId`.

Account resolution helper: match `FinancialAccounts` on `UserId == auth.UserId` and
`AccountNumber == req.AccountNumber` (trimmed, ordinal-ignore-case). No match → `404`
`{error}` so Tower leaves the email in place and retries next sweep.

### `POST /api/external/balances`

```csharp
record ApiBalanceRequest(string AccountNumber, DateTime Date, decimal BalanceAmount, string? Currency);
```
Upserts the `Balance` for (account, `Date.Date`) exactly as step 6 above.
→ `200 { balanceId }`.

### `POST /api/external/statements`

`multipart/form-data`: `accountNumber`, `statementDate` (ISO), `sourceRef`, `file`.
Rejects non-PDF and files over 15 MB (`400`). Calls `IngestAsync`.
→ `200 { pendingId, status }` where `status` is the enum name.

### `GET /api/external/catalog` — extend

Add `financialAccounts`: `{ id, label (Name), accountNumber, currency }` for active accounts of
`auth.UserId`. Lets Tower's `/statements` page show which profiles actually resolve.

---

## 4. FinanceTracker — UI

### New page `/statements` — "Statement Inbox" (`Components/Pages/Statements.razor`)

`@attribute [Authorize]`, `@rendermode InteractiveServer`, styled like `PendingReceipts.razor`.

**Waiting section** — `PendingStatement` rows where `Status != Applied`, newest first:

| Account | Statement date | Status | Action |
|---|---|---|---|
| BOC 86177812 | 2026-07-31 | 🔒 Needs password | password box + **Unlock** |
| BOC 86177812 | 2026-06-30 | 📄 Needs profile | **Add profile** (scrolls to editor) |
| … | … | ⚠️ Failed — *error* | **Retry** |

- **Unlock**: verify with `TryUnlock` on that PDF; wrong → inline "Wrong password — try again",
  row unchanged. Right → save to `account.StatementPasswordEnc` (protector purpose
  `FinanceTracker.AccountStatementPassword`, same as `StatementUpload.razor`) then
  `ProcessAllForAccountAsync`, and report "N statements processed".
- Each row links to its stored PDF and has a 🗑️ discard.

**Profiles section** — one row per account that has a profile, plus an add form:
account picker, `BalanceRegex`, `DateRegex`, `DateFormat`.
- **Test** button: runs the regexes against the `pdftotext` output of the newest unlocked
  pending statement for that account and shows the matched balance/date (or "no match")
  — tune without redeploying.
- **Save** → upsert profile → `ProcessAllForAccountAsync` → "N statements processed".

**Applied section** — collapsed list of the last 20 applied, with balance, date and 📎.

Nav entry in `Components/Layout/NavMenu.razor` next to Bank Balances.

### `BankBalances.razor`

Balance rows carrying an `Attachment` with `EntityType == "Balance"` and `EntityId == balance.Id`
show a 📎 chip linking to the PDF. One lookup of the attachment set for the visible balances;
no other change to the page.

---

## 5. Tower — profiles

`src/Tower.Core/Statements/StatementProfiles.cs`, mirroring `BillProfiles.cs`:

```csharp
public record StatementProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string AccountNumber,        // resolved by FinanceTracker
    Regex? BalanceRegex = null); // non-null → balance is in the email body; null → send the PDF

public static class StatementProfiles
{
    public static readonly IReadOnlyList<StatementProfile> All =
    [
        new("BOC 86177812", "bocmail1@boc.lk",
            Rx(@"^Account Statement - X+12\b"), "86177812"),
    ];

    public static StatementProfile? Match(string from, string subject) => …;   // same shape as BillParser.Match
}
```

**Verified against the live mailbox (2026-07-20).** Real subject:
`Account Statement - XXXXXXXX12 - LKR - MR J N G SAMARASINGHE`. The Gmail label `Statements`
exists and already holds these messages. FinanceTracker account 21 "BOC Savings" carries
`AccountNumber = 86177812`, so resolution matches.

The same sender must **not** match on:
- `Smart Fixed Deposit Opening Confirmation`
- the three sibling BOC accounts, whose subjects end `...62`, `...74`, `...40`
  (accounts 86177962 / 96040374 / 96040440) — hence the `12\b` anchor.

### Month-end rule

```csharp
/// Statements arrive up to ~2 weeks either side of the month they cover.
/// Day <= 14 → the statement covers the PREVIOUS month; otherwise the current month.
/// GmailReader hands back a UTC internalDate; the +0530 shift can move the day across a
/// month boundary (a real statement landed 2025-09-30T23:40Z = Oct 1 local), so convert first.
public static DateTime MonthEnd(DateTime emailDateUtc)
{
    var d = emailDateUtc.ToLocalTime().Date;
    var anchor = d.Day <= 14 ? d.AddMonths(-1) : d;
    return new DateTime(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month));
}
```

Checked against all nine BOC statements in the mailbox — every one lands on the right month:

| Email date | → statement date |
|---|---|
| 2025-09-30T23:40Z | 2025-09-30 |
| 2025-11-01 | 2025-10-31 |
| 2025-11-29 | 2025-11-30 |
| 2026-01-01 | 2025-12-31 |
| 2026-01-31 | 2026-01-31 |
| 2026-02-28 | 2026-02-28 |
| 2026-04-01 | 2026-03-31 |
| 2026-05-01 | 2026-04-30 |
| 2026-05-30 | 2026-05-31 |

---

## 6. Tower — worker, model, client

### `ImportedStatement` (Tower `Models`, `TowerDbContext`, unique index on `GmailMessageId`)

```csharp
public class ImportedStatement
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public DateTime StatementDate { get; set; }
    public decimal? Balance { get; set; }   // set on the direct-value path
    public bool SentPdf { get; set; }
    public string? Error { get; set; }
    public DateTime ImportedAt { get; set; }
}
```

Created in `Program.cs` alongside the other manual `CREATE TABLE IF NOT EXISTS` statements
(`EnsureCreated` does not add tables to an existing DB).

### `FinanceTrackerClient` — two methods

```csharp
Task<int?> PostBalanceAsync(string accountNumber, DateTime date, decimal balance, string? currency, CancellationToken ct);
Task<string?> PostStatementAsync(string accountNumber, DateTime statementDate, string sourceRef,
                                 byte[] pdf, string fileName, CancellationToken ct);  // returns status, null on failure
```

### `StatementMailWorker` (`src/Tower.Core/Workers/`)

Structure copied from `BillMailWorker`: `BackgroundService`, `SemaphoreSlim` run lock, public
`IsRunning`/`RunStartedAt`/`RunTotal`/`RunScanned`/`RunImported` for the page, `RunOnceAsync`.
Registered as a singleton **and** as a hosted service (same as `BillMailWorker`).

Settings keys: `statements.label_name` (default `Statements`), `statements.interval_hours`
(default 6), `statements.mail.last_run` / `.last_count` / `.last_error`.

Per message:

1. Skip + trash if `GmailMessageId` already in `ImportedStatements`.
2. `StatementProfiles.Match(from, subject)`; no match → leave the email untouched.
3. `date = StatementProfiles.MonthEnd(msg.Date)`.
4. `BalanceRegex != null` and it matches the body → `PostBalanceAsync`.
   Otherwise → `GetPdfAttachmentAsync`; **no PDF → record error, leave the email** for retry;
   → `PostStatementAsync` with `sourceRef = gmail message id`.
5. Failure (null return) → record `Error`, **leave the email in place**, continue.
   Success → insert `ImportedStatement`, then `TrashMessageAsync` — **delete the email**.

**Tower never runs `pdftotext` on a statement.**

### When the email is deleted

The email is deleted as soon as FinanceTracker has **durably stored** the balance or the PDF —
i.e. `POST /balances` or `POST /statements` returned 2xx. It is *not* held until the balance is
applied: a statement sitting in `NeedsPassword` or `NeedsProfile` already has its PDF saved as an
`Attachment` on the FinanceTracker side, so the email is redundant and keeping it would only
re-import the same statement on the next sweep.

Anything short of a stored copy leaves the email untouched, so nothing is ever lost to a failure:
FinanceTracker unreachable, unknown account number (404), no PDF attachment found, or any
exception all leave the message in the Statements label to retry next sweep.

Deletion is `TrashMessageAsync` — Gmail's Trash, which self-purges after ~30 days. That is the
same thing `BillMailWorker` does, and it leaves a month-long window to recover a message if a
profile turns out to be wrong. `ImportedStatements.GmailMessageId` is the permanent record that
a given email was consumed, so a message that returns to the label is skipped and re-trashed
rather than double-imported.

### `/statements` page (Tower, `Components/Pages/Statements.razor`)

Copy of `Bills.razor`: running banner, Gmail-not-connected warning, interval + last run +
last error + **Import statements now**, the loaded-profile table (name, from, subject,
account number, source = Body/PDF), and the last 20 `ImportedStatement` rows. Nav entry
beside Bills.

---

## 7. Error handling

| Case | Behaviour |
|---|---|
| Unknown `accountNumber` | FT `404`; Tower records the error and leaves the email → fixed by adding the account, retried next sweep |
| Statement email with no PDF and no body match | Error recorded, email left in place |
| Locked PDF, no saved password | `NeedsPassword`, shown on `/statements`, PDF retained |
| Wrong password entered | Inline error, row stays `NeedsPassword`, nothing saved |
| Saved password stops working (bank changed it) | `NeedsPassword` with `Error`; entering a new one overwrites and reprocesses |
| No profile for the account | `NeedsProfile`, PDF retained indefinitely until a profile is saved |
| Balance regex doesn't match | `Failed` + error; **Retry** after fixing the profile |
| Duplicate email (same `SourceRef`) | Existing row returned, email trashed, nothing double-applied |
| Same date already has a `Balance` | Upserted (statement wins over a manual entry) |
| FT unreachable | Tower records the error, leaves every email — next sweep retries |

---

## 8. Tests

**FinanceTracker** (`FinanceTracker.Tests`) — `StatementIngestServiceTests`, SQLite in-memory,
fixture PDFs generated with PdfSharp in the test (one plain, one encrypted with `7812`):

1. Locked PDF, no saved password → `NeedsPassword`, no `Balance`.
2. Password saved then `ProcessAllForAccountAsync` → unlocks, applies, `Status = Applied`,
   locked attachment gone.
3. Unlocked PDF, no profile → `NeedsProfile`; add profile → backfill applies the `Balance`.
4. `DateRegex` match overrides the supplied `StatementDate`.
5. Same `SourceRef` ingested twice → one row.

**Tower** (`tests/`) — `StatementProfilesTests`:

1. `MonthEnd`: 2026-07-24 → 07-31; 2026-08-05 → 07-31; 2026-08-20 → 08-31; 2026-03-01 → 02-28.
2. `Match` on the BOC sender/subject; non-match on a Bills-label sender.

---

## 8a. First-run backfill — expected blast radius

Measured 2026-07-20: the `Statements` label holds **201 messages, all from `bocmail1@boc.lk`,
back to 2022-12**, and nothing else. Of those, all but a handful are `Account Statement -
XXXXXXXX12`; the rest are `Fixed Deposit Renewal Notice` / `Smart Fixed Deposit Opening
Confirmation`, which correctly do not match and stay in the label.

BOC Savings (account 21) had **5** recorded balances before this feature (2021-03-31 →
2026-03-31), 4 of them on month-ends.

The first sweep therefore:
- stores ~190 PDFs and creates ~190 `PendingStatement` rows, all `NeedsPassword` until the
  password is entered once;
- after unlock + profile, writes ~190 monthly balances spanning 2022-12 → 2026-07;
- **overwrites those 4 existing month-end balances** — the statement figure wins, per the upsert
  in §2 step 6. Confirmed as intended by the user on 2026-07-20.

Net-worth and balance-history charts will change shape substantially after the first run. This is
the desired outcome (4 years of monthly history replacing 5 scattered points), not a regression.

## 8b. Duplicate detection & merge

Added 2026-07-20 at the user's request. Detection is **read-only and on demand** — nothing is
ever merged or deleted without an explicit click.

### What counts as a duplicate

**Balances** — same `FinancialAccountId`, dates within **±7 days**, and **exactly equal
`BalanceAmount`**. Amount equality is the load-bearing signal: two balances a few days apart with
*different* amounts are genuine history and must not be flagged. Same-date balances cannot
duplicate — §2 step 6 upserts them.

**Transactions** — same `UserId`, same `Date.Date`, same `Value`, same `Category`, `Value != 0`.
This is the same key `BillMailWorker` already dedups on at import time (`DedupKey`); this feature
catches the pairs that predate that logic or were entered by hand. Zero-value rows are excluded —
free Google Play items legitimately repeat.

### Dismissals

A dismissed pair must stay dismissed, so one small table:

```csharp
public class DuplicateDismissal
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required][StringLength(20)] public string Kind { get; set; } = "";   // "Balance" | "Transaction"
    public int LeftId { get; set; }    // always the LOWER of the two ids
    public int RightId { get; set; }   // always the HIGHER — so a pair has one canonical form
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

Unique index on (`UserId`, `Kind`, `LeftId`, `RightId`). Normalising the id order at write time is
what stops the same pair being dismissed twice in mirror image.

### Merging

`Keep selected` deletes the other row(s) in the group. Two rules that matter:

1. **Attachments follow the survivor.** Before deleting, re-point every `Attachment` matching the
   doomed row (`EntityType` `"Balance"`/`"Transaction"`, `EntityId` = its id) at the survivor's id.
   Otherwise keeping the manual balance over the statement one would orphan the statement PDF —
   the exact file this whole feature exists to capture.
2. **A merged-away `Balance` clears its `PendingStatement` link.** Any `PendingStatement` whose
   `BalanceId` points at the deleted row is repointed to the survivor, so the statement inbox
   doesn't show a dangling "Applied" row.

Transactions carrying a `LoanPaymentId` or a `CarRecordId` are **never offered for merge** — they
own child records, and deleting one would corrupt a loan balance or a fuel log. They're filtered
out of detection entirely rather than shown-and-blocked.

### UI — new page `/duplicates`

One page, two sections (Balances, Transactions), each a list of groups:

```
⚠ 3 possible duplicates

 BOC Savings
  ○ 2026-03-30  1,204,338.11  manual
  ◉ 2026-03-31  1,204,338.11  statement  📎
     [Keep selected]   [Not a duplicate]
```

Radio defaults to the row with an attachment, else the newest. `Not a duplicate` writes a
`DuplicateDismissal`. A count badge appears in the nav when anything is pending.

## 9. Out of scope

- OCR fallback for image-only statements (`Failed` with a clear error instead).
- Per-statement passwords — the password is per account, as specified.
- Editing Tower's email-match profiles at runtime; they stay in code like `BillProfiles`.
- Stock/unit-trust holdings parsing — only the account's closing balance is applied.
