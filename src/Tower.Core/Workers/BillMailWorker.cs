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

                var profile = BillParser.Match(msg.Value.From, msg.Value.Subject);
                if (profile == null) continue;                   // not a known bill — leave in place

                // PDF-based profiles (e.g. Doc990): the amount lives in the attached PDF, which we also archive.
                byte[]? pdf = null; string? pdfName = null; var amountText = msg.Value.Body;
                if (profile.FromPdf)
                {
                    var att = await reader.GetPdfAttachmentAsync(id, ct);
                    if (att == null) { lastError = $"No PDF attachment for {id}"; continue; }
                    (pdfName, pdf) = (att.Value.FileName, att.Value.Bytes);
                    amountText = await PdfToTextAsync(pdf, ct);
                }

                var extracted = BillParser.ExtractAmount(profile, amountText);
                if (extracted == null) continue;                 // recognised sender but no parseable amount — leave in place
                var (amount, currency) = extracted.Value;
                var billDate = msg.Value.Date;

                // Cross-source dedup: same amount + category + day already imported (e.g. the PayHere
                // receipt already landed for this order) → record + trash this one, don't double-count.
                var dupKey = DedupKey(profile.Category, amount, billDate);
                if (amount > 0 && seen.Contains(dupKey))   // 0.00 items (free) are never dedup'd — many can share a day
                {
                    db.ImportedBills.Add(new ImportedBill
                    {
                        GmailMessageId = id, Profile = profile.Name, Category = profile.Category,
                        Amount = amount, Currency = currency, BillDate = billDate,
                        TransactionId = null, ImportedAt = DateTime.UtcNow, Error = "duplicate (same amount/date already imported)"
                    });
                    db.SaveChanges();
                    await reader.TrashMessageAsync(id, ct);
                    continue;
                }

                // Post the expense (negative). On failure leave the email untouched to retry next sweep.
                var txId = await ft.PostTransactionAsync(-amount, profile.Category,
                    $"{profile.Name} — {msg.Value.Subject}", billDate, currency, ct);
                if (txId == null) { lastError = $"POST transaction failed for {id}"; continue; }

                // ponytail: dedup barrier is this local row; a crash in the gap between the remote
                // POST succeeding and this commit can duplicate one transaction. Acceptable.
                db.ImportedBills.Add(new ImportedBill
                {
                    GmailMessageId = id, Profile = profile.Name, Category = profile.Category,
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
