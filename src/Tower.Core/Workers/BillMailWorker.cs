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

        var ids = await reader.ListMessageIdsAsync(labelId, null, ct);
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

                var parsed = BillParser.TryParse(msg.Value.From, msg.Value.Subject, msg.Value.Body);
                if (parsed == null) continue;                    // not a known bill — leave in place
                var (profile, amount, currency) = parsed.Value;

                // Post the expense (negative). On failure leave the email untouched to retry next sweep.
                var txId = await ft.PostTransactionAsync(-amount, profile.Category,
                    $"{profile.Name} — {msg.Value.Subject}", msg.Value.Date, currency, ct);
                if (txId == null) { lastError = $"POST transaction failed for {id}"; continue; }

                // ponytail: dedup barrier is this local row; a crash in the gap between the remote
                // POST succeeding and this commit can duplicate one transaction. Acceptable.
                db.ImportedBills.Add(new ImportedBill
                {
                    GmailMessageId = id, Profile = profile.Name, Category = profile.Category,
                    Amount = amount, Currency = currency, TransactionId = txId,
                    ImportedAt = DateTime.UtcNow
                });
                db.SaveChanges();
                imported++;
                RunImported = imported;

                // Best-effort: attach the raw .eml, then delete (trash) the email.
                var raw = await reader.GetRawMessageAsync(id, ct);
                if (raw != null)
                    await ft.PostAttachmentAsync(txId.Value, raw, $"{profile.Name}-{id}.eml", ct);
                await reader.TrashMessageAsync(id, ct);
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("bills.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("bills.mail.last_count", imported.ToString());
        settings.Set("bills.mail.last_error", lastError);
        return imported;
    }
}
