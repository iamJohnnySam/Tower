using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Bills;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Models;
using Tower.Core.Settings;

namespace Tower.Core.Workers;

public class BillMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // Live status of the current sweep (this is a singleton, so the /bills page reads these directly).
    public bool IsRunning { get; private set; }
    public DateTime? RunStartedAt { get; private set; }
    public int RunTotal { get; private set; }      // messages in the Bills label this sweep
    public int RunScanned { get; private set; }    // processed so far
    public int RunImported { get; private set; }   // new bills added so far

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[BillMailWorker] {ex.Message}"); }

            var hours = 6;
            using (var s = scopes.CreateScope())
            {
                var v = s.ServiceProvider.GetRequiredService<SettingsService>().Get("bills.interval_hours");
                if (int.TryParse(v, out var h) && h > 0) hours = h;
            }
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        // One sweep at a time; a concurrent trigger (manual button while the timer runs) is a no-op.
        if (!await _runLock.WaitAsync(0, ct)) return 0;
        IsRunning = true; RunStartedAt = DateTime.UtcNow; RunTotal = RunScanned = RunImported = 0;
        try { return await RunCoreAsync(ct); }
        finally { IsRunning = false; RunStartedAt = null; _runLock.Release(); }
    }

    private async Task<int> RunCoreAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var tokens = scope.ServiceProvider.GetRequiredService<GmailTokenService>();
        var reader = scope.ServiceProvider.GetRequiredService<GmailReader>();
        var ft = scope.ServiceProvider.GetRequiredService<FinanceTrackerClient>();
        if (!tokens.IsConnected || !ft.IsConfigured) return 0;

        var labelName = settings.Get("bills.label_name");
        if (string.IsNullOrWhiteSpace(labelName)) labelName = "Bills";
        var labels = await reader.ListLabelsAsync(ct);
        var labelId = labels.FirstOrDefault(l => l.Name.Equals(labelName, StringComparison.OrdinalIgnoreCase)).Id;
        if (string.IsNullOrEmpty(labelId))
        {
            settings.Set("bills.mail.last_error", $"Gmail label '{labelName}' not found");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var known = db.ImportedBills.Select(x => x.GmailMessageId).ToHashSet();
        // For cross-source dedup: one purchase can arrive twice (merchant + PayHere gateway).
        var seen = db.ImportedBills
            .Where(b => b.TransactionId != null && b.BillDate != null)
            .Select(b => new { b.Category, b.Amount, b.BillDate })
            .AsEnumerable()
            .Select(b => DedupKey(b.Category, b.Amount, b.BillDate!.Value))
            .ToHashSet();

        var ids = await reader.ListMessageIdsAsync(labelId, null, ct);

        // SC account-movement notifications land in the Statements label with the rest of John's
        // bank mail, not in Bills. Pull just that one sender from there so the transfers are handled
        // without asking the user to re-file them; statement PDFs in that label match no profile here
        // and are left for the statement worker.
        var transfersLabel = settings.Get("bills.transfers_label");
        if (string.IsNullOrWhiteSpace(transfersLabel)) transfersLabel = "Statements";
        var transfersLabelId = labels.FirstOrDefault(l => l.Name.Equals(transfersLabel, StringComparison.OrdinalIgnoreCase)).Id;
        if (!string.IsNullOrEmpty(transfersLabelId) && transfersLabelId != labelId)
            foreach (var tid in await reader.ListMessageIdsAsync(transfersLabelId, null, ct, fromContains: "iBanking.SRILANKA@sc.com"))
                if (!ids.Contains(tid)) ids.Add(tid);

        // Process 'preferred' senders (the PayHere gateway) first, so they win same-order dedup over merchant emails.
        var preferredIds = new HashSet<string>();
        foreach (var sender in BillProfiles.All.Where(p => p.Preferred).Select(p => p.FromContains).Distinct())
            foreach (var pid in await reader.ListMessageIdsAsync(labelId, null, ct, fromContains: sender))
                preferredIds.Add(pid);
        if (preferredIds.Count > 0)
            ids = ids.OrderByDescending(preferredIds.Contains).ToList();

        int imported = 0;
        string? lastError = null;
        RunTotal = ids.Count;

        foreach (var id in ids)
        {
            RunScanned++;
            try
            {
                if (known.Contains(id)) { await reader.TrashMessageAsync(id, ct); continue; }

                var msg = await reader.GetMessageAsync(id, ct);
                if (msg == null) continue;

                // SC account-movement notifications route by beneficiary across household members —
                // a different shape from bills, handled here before the plain bill path.
                if (TransferProfiles.Match(msg.Value.From, msg.Value.Subject) is { } transfer)
                {
                    if (await HandleTransferAsync(reader, ft, db, transfer, id, msg.Value, ct)) imported++;
                    RunImported = imported;
                    continue;
                }

                var profile = BillParser.Match(msg.Value.From, msg.Value.Subject);
                if (profile == null) continue;                   // not a known bill — leave in place

                // PDF-based profiles (e.g. Doc990): the amount lives in the attached PDF, which we also archive.
                // ponytail: no attachment (Dialog's "click VIEW BILL" mails) → fall back to the email
                // body rather than bailing; if it holds no amount either, the skip below still catches it.
                byte[]? pdf = null; string? pdfName = null; var amountText = msg.Value.Body;
                if (profile.FromPdf && await reader.GetPdfAttachmentAsync(id, null, ct) is { } att)
                {
                    (pdfName, pdf) = (att.FileName, att.Bytes);
                    amountText = await PdfToTextAsync(pdf, ct);
                }

                // Recognised sender but no parseable amount — leave the email in place and say so.
                // This is how a sender quietly changing its template shows up (Dialog renamed its
                // "Total Charges for Bill Period" field and stopped importing for months); staying
                // silent here makes a broken profile look like a clean sweep.
                var extracted = BillParser.ExtractAmount(profile, amountText);
                if (extracted == null)
                {
                    lastError = $"{profile.Name}: no amount found in \"{msg.Value.Subject}\"";
                    continue;
                }
                var (amount, currency) = extracted.Value;
                var billDate = msg.Value.Date;
                // Most profiles have a fixed category; Dialog reads it off the connection number in the bill.
                var category = profile.CategoryFrom?.Invoke(amountText) ?? profile.Category;

                // Cross-source dedup: same amount + category + day already imported (e.g. the PayHere
                // receipt already landed for this order) → record + trash this one, don't double-count.
                var dupKey = DedupKey(category, amount, billDate);
                // 0.00 items (free) are never dedup'd — many can share a day. Refunds are exempt too:
                // ImportedBills doesn't record the sign, so a refund matching an expense would look
                // like a duplicate of it. NoDedup senders bill parallel subscriptions that legitimately
                // collide. GmailMessageId still stops the same email twice in every case.
                if (amount > 0 && !profile.Refund && !profile.NoDedup && seen.Contains(dupKey))
                {
                    db.ImportedBills.Add(new ImportedBill
                    {
                        GmailMessageId = id, Profile = profile.Name, Category = category,
                        Amount = amount, Currency = currency, BillDate = billDate,
                        TransactionId = null, ImportedAt = DateTime.UtcNow, Error = "duplicate (same amount/date already imported)"
                    });
                    db.SaveChanges();
                    await reader.TrashMessageAsync(id, ct);
                    continue;
                }

                // Post the expense (negative), or a refund (positive). On failure leave the email untouched to retry next sweep.
                var txId = await ft.PostTransactionAsync(profile.Refund ? amount : -amount, category,
                    $"{profile.Name} — {msg.Value.Subject}", billDate, currency, ct: ct);
                if (txId == null) { lastError = $"POST transaction failed for {id}"; continue; }

                // ponytail: dedup barrier is this local row; a crash in the gap between the remote
                // POST succeeding and this commit can duplicate one transaction. Acceptable.
                db.ImportedBills.Add(new ImportedBill
                {
                    GmailMessageId = id, Profile = profile.Name, Category = category,
                    Amount = amount, Currency = currency, BillDate = billDate, TransactionId = txId,
                    ImportedAt = DateTime.UtcNow
                });
                db.SaveChanges();
                if (amount > 0) seen.Add(dupKey);
                imported++;
                RunImported = imported;

                // Best-effort: attach the PDF (PDF profiles) or the raw .eml, then delete (trash) the email.
                if (pdf != null)
                    await ft.PostAttachmentAsync(txId.Value, pdf, pdfName ?? $"{profile.Name}-{id}.pdf", "application/pdf", ct);
                else
                {
                    var raw = await reader.GetRawMessageAsync(id, ct);
                    if (raw != null)
                        await ft.PostAttachmentAsync(txId.Value, raw, $"{profile.Name}-{id}.eml", "message/rfc822", ct);
                }
                await reader.TrashMessageAsync(id, ct);
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("bills.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("bills.mail.last_count", imported.ToString());
        settings.Set("bills.mail.last_error", lastError);
        return imported;
    }

    // An SC account-notification email → 0-2 routed transactions, then trash. Returns true if
    // anything was posted. A me→me transfer or a standing-order setup posts nothing but is still
    // trashed. On a POST failure the email is left in place to retry (nothing is recorded/trashed).
    private static async Task<bool> HandleTransferAsync(GmailReader reader, FinanceTrackerClient ft,
        TowerDbContext db, TransferProfile transfer, string id,
        (string From, string Subject, string Body, DateTime Date) msg, CancellationToken ct)
    {
        var date = TransferProfiles.DateOf(msg.Body, msg.Date);
        var posts = TransferProfiles.Plan(transfer, msg.Body);

        int? firstTxId = null;
        foreach (var post in posts)
        {
            var txId = await ft.PostTransactionAsync(post.Value, post.Category, post.Description,
                date, "LKR", post.Member, ct);
            if (txId == null) return false;   // leave the whole email to retry — no partial record
            firstTxId ??= txId;

            var raw = await reader.GetRawMessageAsync(id, ct);
            if (raw != null)
                await ft.PostAttachmentAsync(txId.Value, raw, $"{transfer.Name}-{id}.eml", "message/rfc822", ct);
        }

        db.ImportedBills.Add(new ImportedBill
        {
            GmailMessageId = id, Profile = transfer.Name, Category = posts.Count > 0 ? posts[0].Category : "—",
            Amount = posts.Count > 0 ? Math.Abs(posts[0].Value) : 0m, Currency = "LKR", BillDate = date,
            TransactionId = firstTxId, ImportedAt = DateTime.UtcNow,
            Error = posts.Count == 0 ? "own transfer / standing order — nothing recorded" : null
        });
        db.SaveChanges();

        await reader.TrashMessageAsync(id, ct);   // recorded (or intentionally not) → email redundant
        return posts.Count > 0;
    }

    // ponytail: same amount + category + day = same purchase (merchant vs PayHere). Coarse, but the
    // window the user cares about is one order; genuine same-day/same-amount/same-category buys are rare.
    private static string DedupKey(string category, decimal amount, DateTime date) =>
        $"{category}|{amount}|{date:yyyy-MM-dd}";

    // ponytail: shell out to the system `pdftotext` (poppler-utils, installed) instead of a .NET PDF lib.
    private static async Task<string> PdfToTextAsync(byte[] pdf, CancellationToken ct)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tmp, pdf, ct);
            using var p = Process.Start(new ProcessStartInfo("pdftotext", $"-layout \"{tmp}\" -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var text = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return text;
        }
        catch (Exception ex) { await Console.Error.WriteLineAsync($"[BillMailWorker] pdftotext: {ex.Message}"); return ""; }
        finally { try { File.Delete(tmp); } catch { /* best-effort */ } }
    }
}
