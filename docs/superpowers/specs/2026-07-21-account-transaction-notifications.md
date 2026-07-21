# SC account transaction notifications → routed transactions

Standard Chartered emails John (`iBanking.SRILANKA@sc.com`) whenever money moves on his SC
account. Unlike a bill (always John's expense, fixed category), these route by **who received the
money**, across the three household members who are all FinanceTracker users:

| Member | FinanceTracker user | Beneficiary aliases (as they appear in the mail) |
|---|---|---|
| John | John Samarasinghe | J N G SAMARASINGHE, J.N.G.Samarasinghe, John Samarasinghe, John Neuman |
| Gayathri | Gayathri | Gayathri Karunaratne |
| Caleb | Caleb Samarasinghe | Caleb Samarasinghe, C J Samarasinghe, C. J. Samarasinghe |

Names are matched **normalised** — punctuation stripped, whitespace collapsed, upper-cased — so
`J.N.G.Samarasinghe` and `J N G SAMARASINGHE` are one key.

## Three email variants (verified in the live mailbox)

**1. Local Funds Transfer - Successful** (74). Fields:
`Beneficiary Details <name>, XXXX,LKR` · `Debit Amount: LKR <amt>` · `Charges To Debit LKR <fee>`
· `Transfer Reference <text>` · `Transfer Date(DD/MM/YYYY) <date>`.

Routing on the beneficiary:
- **John (me → me):** record nothing, just delete the email.
- **Gayathri / Caleb:** **income** (+amount) in that member's ledger. Attach the `.eml`.
- **anyone else:** **expense** (−amount) in John's ledger. Attach the `.eml`.
- **Charges > 0** (any recipient): a **separate** John expense (−fee), category `Bank Charges`.
  Fees are almost always 0, so this rarely fires.

Category `Bank Transfer`; description carries the beneficiary + the transfer reference.

**2. Utility Payment Confirmation** (7). Fields: `Beneficiary <company>` · `Amount <n>` (no
decimals) · `Consumer No <n>`. The beneficiary is a utility (CEB…), never a household member, so
always **John's expense** (−amount), category `Utilities`, `.eml` attached. No date in the body —
use the email date.

**3. Local Transfer Standing Order Confirmation** (5). A *setup* of a recurring transfer
(`Frequency: Monthly`, start/end dates), **not** a completed payment — the monthly executions
arrive later as their own "Successful" emails. So: apply the me→me delete rule, otherwise trash
without recording. **Never** creates a transaction (decision: John, 2026-07-21).

## Design — a sibling to bill profiles, in the same Bills sweep

`BillProfile` is fixed-category, John-only, one-transaction-per-mail — none of which fits here. So a
small separate `TransferProfiles` type, handled in `BillMailWorker` after `BillParser.Match` misses.

```
Tower.Core/Bills/TransferProfiles.cs
  record MemberAlias(string Member, string[] Aliases)          // Member = FT first name
  enum TransferKind { Transfer, Utility, StandingOrder }
  record TransferProfile(string Name, Regex SubjectRegex, TransferKind Kind, ...)
  Classify(beneficiary) -> "John" | "Gayathri" | "Caleb" | null(other)
  Parse(body) -> (beneficiary, amount, charges, reference, date)
```

`BillMailWorker`: on a transfer match, build 0–2 posts (main + charge), each `(member, signedValue,
category, description)`, POST them, attach the `.eml` to each, then trash. A me→me transfer or a
standing order yields 0 posts and is trashed anyway.

## FinanceTracker changes

- `POST /transactions` gains optional `member` — resolved to a household user by **first name**
  (`FamilyDirectory`, case-insensitive). Absent → the API-key owner (John). This is what lets
  John's key file income into Gayathri's / Caleb's ledger; acceptable in a single-household app
  where John already sees every member's accounts.
- `POST /transactions/{id}/attachment` — ownership relaxed from "the key owner's transaction" to
  "**any household member's** transaction", so the `.eml` attaches to Gayathri's/Caleb's rows.
- `FinanceTrackerClient.PostTransactionAsync` gains an optional `member`.

## Out of scope

Transfers from accounts other than SC (only `iBanking.SRILANKA@sc.com` is wired). Matching a
beneficiary to a specific FinancialAccount — these are ledger transactions, not per-account.
Reconciling a standing order's executions against its setup.
