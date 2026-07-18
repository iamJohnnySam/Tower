# Bill Mail Importer — design

**Date:** 2026-07-18
**Status:** Approved (design), pending implementation plan
**Repos touched:** Tower (primary), FinanceTracker (one API endpoint + .eml viewer)

## Goal

Automatically turn bill emails forwarded to a Gmail **Bills** folder into expense
transactions in FinanceTracker, with the original email attached to the transaction, then
remove the email from the inbox. Runtime scanning is **deterministic** — no AI in the loop.
Claude authors the per-vendor extraction rules by reading a sample once; the service only
applies fixed rules.

## Where it runs

Tower. It already has everything needed:

- Gmail OAuth with `gmail.modify` scope (`Tower.Core/Gmail/GmailTokenService.cs`).
- `GmailReader` (list/read/trash messages) — `Tower.Core/Gmail/GmailReader.cs`.
- A proven polling-worker pattern — `Tower.Core/Workers/SolarMailWorker.cs`.
- It is the intended caller of FinanceTracker's external REST API and already holds the API key.

FinanceTracker owns the ledger + attachments, so the transaction and the stored email live
there; Tower is a thin producer that POSTs to the existing external API.

## Profiles (deterministic, code-defined)

A static list in `Tower.Core/Bills/BillProfiles.cs` — no DB, no UI. One record per bill type;
adding a type is a few lines that Claude writes from a real sample. Shape:

```
record BillProfile(
    string Name,          // "PickMe Trip"
    string FromContains,  // "pickme.lk"          matched against the From header (case-insensitive)
    Regex SubjectRegex,   // ^PickMe \| Email Receipt for Trip
    string Category,      // FinanceTracker transaction Category
    Regex AmountRegex,    // group 1 = decimal amount (the *paid* total)
    string Currency);     // "LKR"
```

**Matching:** an email matches a profile when `From` contains `FromContains` **and** `Subject`
matches `SubjectRegex`. First matching profile wins. No match → the email is left untouched in
the Bills folder (never trashed, never posted).

**Amount extraction:** the body Tower gets from `GmailReader` is tag-stripped HTML that still
contains HTML entities (e.g. `&nbsp;`). The parser first **normalizes** the body — decode
`&nbsp;`/`&amp;`, collapse whitespace — then applies `AmountRegex`. Group 1 is parsed
invariant-culture, commas stripped, and posted as a **negative** value (expense).

### Initial profiles (authored from real 2026 samples)

| Name | From | Subject match | Field extracted | Category | Currency |
|------|------|---------------|-----------------|----------|----------|
| PickMe Trip | `pickme.lk` | `^PickMe \| Email Receipt for Trip` | **Paid Amount** → `LKR 408.88` | Transportation | LKR |
| PickMe Delivery | `pickme.lk` | `^PickMe \| Delivery Email Receipt` | **Paid by** → `LKR 3158.00` | Food | LKR |

Why these fields, not the obvious one: the trip receipt lists an *Estimated Fare* (308.88) and a
separate *Surge* near the top — ignore those. Use **Paid Amount** (the last total on the receipt,
408.88): unlike *Total Trip Fare* it also includes any tip, so it is the true amount charged. The
delivery receipt's charged total is **Paid by** (subtotal + delivery fee + fuel surcharge).

Regexes (applied after entity-normalization, `Singleline`):
- Trip: `Paid Amount\s*LKR\s*([\d,]+\.\d{2})`
- Delivery: `Paid by\s*LKR\s*([\d,]+\.\d{2})`

## The worker: `Tower.Core/Workers/BillMailWorker.cs`

Mirrors `SolarMailWorker`. Every `bills.interval_hours` (default 6):

1. Resolve the Bills label id by name (`bills.label_name`, default `Bills`) via
   `GmailReader.ListLabelsAsync`. Bail if Gmail not connected, label missing, or FinanceTracker
   API not configured.
2. Sweep all message ids under the label (`ListMessageIdsAsync(labelId, after: null)`).
3. For each id:
   - If already in the `ImportedBills` table → best-effort re-trash (retry a prior failed trash), skip.
   - `GetMessageAsync` → `(From, Subject, Body, Date)`. Match a profile; no match → skip (leave in place).
   - Extract amount. Parse failure → set `last_error`, **leave email in place, no row** (retried next sweep / after a profile fix).
   - `POST /api/external/transactions` → `{ Value = -amount, Category, Date = email date, Currency }`. Failure (e.g. FinanceTracker down) → set `last_error`, **leave email in place, no row** (retried next sweep).
   - **Only on success:** insert the `ImportedBills` row (this is the dedup barrier) and `SaveChanges`.
   - Fetch raw message (`GmailReader.GetRawMessageAsync`) → `POST /api/external/transactions/{id}/attachment` (the `.eml`). Best-effort.
   - `TrashMessageAsync(id)` — Gmail "delete" (auto-purges ~30 days), same as SolarMailWorker.
4. Persist run status to Settings: `bills.mail.last_run`, `bills.mail.last_count`, `bills.mail.last_error`.

**Idempotency:** a row is written **only after a successful transaction post**, so a transient
FinanceTracker outage leaves the email untouched and it retries next sweep. Once the row exists,
the Gmail message id is "known" → never re-posted; failed attach/trash are retried on later
sweeps (attach is best-effort — see Trade-offs). Only the sub-second window between the remote
POST succeeding and the local row committing can duplicate a transaction on a crash — accepted.

## New Tower pieces

- `Bills/BillProfiles.cs` — profile records + `BillParser.TryParse(from, subject, body) → (BillProfile, decimal)?`.
- `Bills/FinanceTrackerClient.cs` — typed `HttpClient`:
  `PostTransactionAsync(...) → int transactionId`, `PostAttachmentAsync(int txId, byte[] eml, string filename)`.
  Base URL + API key read from the **Settings table in plaintext**
  (`financetracker.base_url`, `financetracker.api_key`) — a background worker cannot unlock the
  encrypted `/secrets` vault, so these live alongside the `gmail.*` settings.
- `Models/ImportedBill.cs` + `TowerDbContext` DbSet + migration. Columns: `Id`, `GmailMessageId`
  (unique index), `Profile`, `Category`, `Amount`, `Currency`, `TransactionId` (nullable),
  `ImportedAt`, `Error` (nullable).
- `GmailReader` additions: expose the `From` header in `GetMessageAsync`, and add
  `GetRawMessageAsync(id)` (`?format=raw`, base64url → bytes) for the `.eml`.
- Register `BillMailWorker` + `FinanceTrackerClient` in `src/Tower/Program.cs`.

## FinanceTracker changes

### 1. Attachment upload endpoint

`POST api/external/transactions/{id}/attachment` in `Controllers/ExternalApiController.cs`
(multipart, single file). Resolves `X-Api-Key` → user (existing `AuthAsync`), verifies the
transaction belongs to that user, then calls the **existing**
`AttachmentService.SaveAsync("Transaction", id, userId, stream, fileName, "message/rfc822",
AttachmentSource.Gmail, capturedAt)`. `AttachmentSource.Gmail` already exists. Returns
`{ attachmentId }`.

### 2. `.eml` viewer

Today attachments render as `<a href="/@FilePath" target="_blank">` — PDFs/images render
inline in the new tab, so **PDF needs no change**. Only `.eml` needs a viewer:

- In the attachment link (`Transactions.razor`, `FileAttach.razor`): if `FilePath` ends in
  `.eml`, point the link at `/attachments/eml/{id}` (still `target="_blank"` → new window)
  instead of the raw file.
- New page `Components/Pages/EmlViewer.razor` (route `/attachments/eml/{id}`): loads the
  attachment, parses the `.eml` with **MimeKit** (`MimeMessage.Load(path).HtmlBody ?? TextBody`),
  and renders the body inside a **sandboxed iframe** (`<iframe sandbox srcdoc="...">`) so the
  untrusted email HTML can't run scripts. Shows From/Subject/Date as a header.
- Add the `MimeKit` package to `FinanceTracker.csproj` (parsing raw RFC822 by hand is not worth
  reinventing; MimeKit is the standard, correct-on-encodings choice).

## Config summary (Tower `Settings` table, plaintext)

| Key | Default | Purpose |
|-----|---------|---------|
| `financetracker.base_url` | — | e.g. `http://192.168.1.30:5500` |
| `financetracker.api_key` | — | John's external API key |
| `bills.label_name` | `Bills` | Gmail label to scan |
| `bills.interval_hours` | `6` | Poll cadence |

## Testing

- `Tower.Tests` (new minimal xUnit project) — `BillParser` tests: the two real sample bodies
  parse to `408.88` / `3158.00` with the correct profile, the posted value is **negative**,
  entity-normalization handles `&nbsp;`, and a non-matching subject returns null. This is the
  money path — it gets the one real check.
- Manual: point `financetracker.base_url`/`api_key` at a test row, run `BillMailWorker.RunOnceAsync`
  against the live Bills label, confirm transactions + `.eml` attachments appear and emails move
  to Trash.

## Trade-offs / deliberately skipped (YAGNI)

- **Profiles are code, not a UI/table** — Claude authors them from a sample; a UI is unrequested.
- **Trash, not hard-delete** — matches the existing SolarMailWorker convention; hard-delete needs
  a wider OAuth scope for no real benefit (Trash auto-purges).
- **Attach is best-effort, not retried after the dedup row exists** — a transaction without its
  `.eml` is a cosmetic loss, not a double-charge. Re-attach-on-failure can be added later if it
  ever matters.
- **No OCR** — these are text/HTML receipts.
- **Email received date = transaction date** — receipts arrive at purchase time; close enough.
