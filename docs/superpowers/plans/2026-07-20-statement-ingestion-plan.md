# Plan — Bank Statement Ingestion

Spec: `docs/superpowers/specs/2026-07-20-statement-ingestion-design.md` (same file in both repos).

Order matters only across the repo boundary: **FT API must exist before Tower's worker can be
tested end to end.** Within a repo, tasks are sequential. Task 1+2 (FT) and Task 4 (Tower
profiles/model) are independent and can run in parallel.

---

## Task 1 — FT: models, DbContext, migration

**Files**
- `FinanceTracker/FinanceTracker/Models/StatementProfile.cs` (new)
- `FinanceTracker/FinanceTracker/Models/PendingStatement.cs` (new, incl. `PendingStatementStatus`)
- `FinanceTracker/FinanceTracker/Data/ApplicationDbContext.cs` — add `DbSet<StatementProfile> StatementProfiles`,
  `DbSet<PendingStatement> PendingStatements`; unique index on `StatementProfile.FinancialAccountId`,
  unique index on `PendingStatement.SourceRef`
- `FinanceTracker/FinanceTracker/ApplyMigrationHelper.cs` — `EnsureStatementProfilesTable` +
  `EnsurePendingStatementsTable`, called from `ApplyMigration` next to `EnsureAttachmentsTable`

**Acceptance:** `dotnet build` clean; app starts against the existing `financetracker.db` and both
tables exist (`sqlite3 … ".tables"`). Follow the raw-SQL `EnsureXTable` pattern already in the file —
do **not** add an EF migration.

---

## Task 2 — FT: `StatementIngestService`

**Files**
- `FinanceTracker/FinanceTracker/Services/StatementIngestService.cs` (new)
- `FinanceTracker/FinanceTracker/Program.cs` — `AddScoped<IStatementIngestService, StatementIngestService>()`

Implement `IngestAsync` / `ProcessAsync` / `ProcessAllForAccountAsync` exactly as spec §2.
`pdftotext -layout` is shelled out — copy `PdfToTextAsync` from
`Tower/src/Tower.Core/Workers/BillMailWorker.cs` verbatim (it's ~15 lines; a shared package is not
worth it). Password decryption uses `IDataProtectionProvider.CreateProtector("FinanceTracker.AccountStatementPassword")`
— the same purpose string as `StatementUpload.razor`, or saved passwords won't decrypt.

**Acceptance:** builds; Task 3's tests exercise it.

---

## Task 3 — FT: tests for the ingest service

**Files**
- `FinanceTracker.Tests/StatementIngestServiceTests.cs` (new)

Five cases from spec §8. Generate the fixture PDFs in-test with PdfSharp (one plain containing
`Closing Balance   1,234.56` and a date line, one saved with `PdfDocumentSecurityLevel` + user
password `7812`). Match the existing test project's setup style for `ApplicationDbContext`.

**Acceptance:** `dotnet test` — all five pass, plus the existing suite still green.

---

## Task 4 — Tower: profiles + model + client (independent of Tasks 1–3)

**Files**
- `Tower/src/Tower.Core/Statements/StatementProfiles.cs` (new) — record, `All` with the BOC entry,
  `Match`, `MonthEnd` (spec §5)
- `Tower/src/Tower.Core/Models/` — `ImportedStatement` (append to the shared models file, matching
  how `ImportedBill` is declared)
- `Tower/src/Tower.Core/Data/TowerDbContext.cs` — `DbSet` + unique index on `GmailMessageId`
- `Tower/src/Tower/Program.cs` — `CREATE TABLE IF NOT EXISTS ImportedStatements …` beside the
  existing manual table creations
- `Tower/src/Tower.Core/Bills/FinanceTrackerClient.cs` — `PostBalanceAsync`, `PostStatementAsync`
  (spec §6; reuse the existing `Config()` / `X-Api-Key` pattern, and `MultipartFormDataContent`
  exactly as `PostAttachmentAsync` does)
- `Tower/tests/` — `StatementProfilesTests` (spec §8)

**Acceptance:** `dotnet build` + `dotnet test` clean in the Tower solution; the `MonthEnd`
boundary cases pass.

---

## Task 5 — FT: API endpoints (depends on Tasks 1–2)

**Files**
- `FinanceTracker/FinanceTracker/Controllers/ExternalApiController.cs`

Add `ApiBalanceRequest`, `POST balances`, `POST statements` (multipart), the shared
`ResolveAccountAsync(ctx, auth, accountNumber)` helper, and `financialAccounts` on `catalog`
(spec §3). Keep the file's existing shape — DTO records at the top, one region-comment per endpoint.

**Acceptance:** with the app running, `curl -H "X-Api-Key: …" -F accountNumber=86177812
-F statementDate=2026-07-31 -F sourceRef=test1 -F file=@sample.pdf …/api/external/statements`
returns `{pendingId, status:"NeedsPassword"}`; a bad account number returns 404; repeating the
same `sourceRef` returns the same `pendingId`.

---

## Task 6 — Tower: `StatementMailWorker` + page (depends on Tasks 4, 5)

**Files**
- `Tower/src/Tower.Core/Workers/StatementMailWorker.cs` (new) — spec §6
- `Tower/src/Tower/Program.cs` — register singleton + hosted service, mirroring `BillMailWorker`
- `Tower/src/Tower/Components/Pages/Statements.razor` (+ reuse `Bills.razor.css` if one exists,
  else no separate stylesheet)
- `Tower/src/Tower/Components/Layout/NavMenu.razor` — entry beside Bills

**Acceptance:** `/statements` renders, shows the BOC profile, "Import statements now" completes
and reports counts; a message in the Statements label from `bocmail1@boc.lk` produces a
`PendingStatement` in FinanceTracker and is trashed in Gmail.

---

## Task 7 — FT: `/statements` page + BankBalances chip (depends on Tasks 1–2)

**Files**
- `FinanceTracker/FinanceTracker/Components/Pages/Statements.razor` (new) — spec §4
- `FinanceTracker/FinanceTracker/Components/Layout/NavMenu.razor` — entry beside Bank Balances
- `FinanceTracker/FinanceTracker/Components/Pages/BankBalances.razor` — 📎 chip on balance rows
  with a `Balance` attachment

Styling follows `PendingReceipts.razor`; the password box reuses the markup from
`StatementUpload.razor`.

**Acceptance:** entering `7812` on a waiting BOC statement unlocks it and reports how many were
processed; saving a profile backfills every waiting statement for that account; the applied
balance shows on Bank Balances with a 📎 to the unlocked PDF.

---

## Task 9 — FT: duplicate detection & merge (depends on Task 7 — shares NavMenu.razor)

**Files**
- `FinanceTracker/FinanceTracker/Models/DuplicateDismissal.cs` (new)
- `Data/ApplicationDbContext.cs` — DbSet + unique index on (UserId, Kind, LeftId, RightId)
- `ApplyMigrationHelper.cs` — `EnsureDuplicateDismissalsTable`
- `Services/DuplicateFinderService.cs` (new) — `FindBalanceDupesAsync`, `FindTransactionDupesAsync`,
  `MergeAsync(kind, keepId, dropIds)`, `DismissAsync(kind, idA, idB)`
- `Components/Pages/Duplicates.razor` (new) — `/duplicates`
- `Components/Layout/NavMenu.razor` — entry + pending count badge
- `FinanceTracker.Tests/DuplicateFinderServiceTests.cs` (new)

Spec §8b is the contract. The three things most likely to be got wrong, in order:
1. Attachments must be re-pointed to the survivor **before** the losing row is deleted.
2. `PendingStatement.BalanceId` must be re-pointed too.
3. Transactions with `LoanPaymentId` or `CarRecordId` must be excluded from detection entirely.

**Acceptance:** `dotnet test` green, including: a ±7-day equal-amount pair is found; a ±7-day
*unequal*-amount pair is NOT found; a same-day equal-value transaction pair is found; a
loan-payment transaction is never returned; merging moves the attachment and deletes only the
loser; a dismissed pair does not reappear.

---

## Task 8 — Wire-up, docs, deploy

- BOC account in FinanceTracker must have `AccountNumber = 86177812` — verify, don't assume.
- Gmail label `Statements` — Tower reads the name from `statements.label_name`.
- `CLAUDE.md` in both repos: short section pointing at the spec, like the Bills one.
- Deploy both: `bash /home/atom/dev/FinanceTracker/deploy.sh`, `bash /home/atom/dev/Tower/deploy.sh`
  (stop → publish → start).

**Acceptance:** both services active; `/statements` reachable on 8888 and 5500.
