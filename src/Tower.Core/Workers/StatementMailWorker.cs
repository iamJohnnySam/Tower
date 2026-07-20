using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Bills;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.Statements;

namespace Tower.Core.Workers;

/// <summary>Scans the Gmail "Statements" label and hands each statement to FinanceTracker —
/// the balance from the email body where a profile has one, otherwise the raw PDF bytes.
/// Tower never opens the PDF: it may be password-locked and only FinanceTracker holds the key.</summary>
public class StatementMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // Live status of the current sweep (this is a singleton, so the /statements page reads these directly).
    public bool IsRunning { get; private set; }
    public DateTime? RunStartedAt { get; private set; }
    public int RunTotal { get; private set; }      // messages in the Statements label this sweep
    public int RunScanned { get; private set; }    // processed so far
    public int RunImported { get; private set; }   // statements handed over so far

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[StatementMailWorker] {ex.Message}"); }

            var hours = 6;
            using (var s = scopes.CreateScope())
            {
                var v = s.ServiceProvider.GetRequiredService<SettingsService>().Get("statements.interval_hours");
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

        var labelName = settings.Get("statements.label_name");
        if (string.IsNullOrWhiteSpace(labelName)) labelName = "Statements";
        var labels = await reader.ListLabelsAsync(ct);
        var labelId = labels.FirstOrDefault(l => l.Name.Equals(labelName, StringComparison.OrdinalIgnoreCase)).Id;
        if (string.IsNullOrEmpty(labelId))
        {
            settings.Set("statements.mail.last_error", $"Gmail label '{labelName}' not found");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var known = db.ImportedStatements.Select(x => x.GmailMessageId).ToHashSet();

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

                var profile = StatementProfiles.Match(msg.Value.From, msg.Value.Subject);
                if (profile == null) continue;                   // not a known statement — leave in place

                var date = profile.ResolveDate(msg.Value.Body, msg.Value.Date);

                // The attachment is fetched before the account is resolved because senders that
                // reuse one template for many accounts (ComBank FD renewals) put the number in
                // the filename and nowhere else machine-readable.
                var att = profile.BalanceRegex != null ? null
                    : await reader.GetPdfAttachmentAsync(id, profile.AttachmentNameRegex, ct);

                var accountNumber = profile.ResolveAccountNumber(
                    msg.Value.Subject, att?.FileName, msg.Value.Body);
                if (accountNumber == null)
                { lastError = $"Could not resolve an account number for {id}"; continue; }

                var row = new ImportedStatement
                {
                    GmailMessageId = id, Profile = profile.Name, AccountNumber = accountNumber,
                    StatementDate = date, ImportedAt = DateTime.UtcNow
                };

                // Body path: the balance is in the email itself, no PDF involved.
                var bodyMatch = profile.BalanceRegex?.Match(msg.Value.Body);
                if (bodyMatch is { Success: true } m &&
                    decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var bal))
                {
                    if (await ft.PostBalanceAsync(accountNumber, date, bal, null, ct) == null)
                    { lastError = $"POST balance failed for {id}"; continue; }   // leave the email, retry next sweep
                    row.Balance = bal;
                }
                else
                {
                    // Document path: hand the bytes over untouched — the file may be locked.
                    if (att == null) { lastError = $"No document attachment for {id}"; continue; }
                    var status = await ft.PostStatementAsync(accountNumber, date, id,
                        att.Value.Bytes, att.Value.FileName, ct);
                    if (status == null) { lastError = $"POST statement failed for {id}"; continue; }
                    row.SentPdf = true;
                    row.Error = status is "Applied" or "NeedsPassword" or "NeedsProfile" ? null : status;
                }

                // ponytail: same barrier as BillMailWorker — a crash between the remote POST and this
                // commit re-sends one statement; FinanceTracker dedups on sourceRef, so it's harmless.
                db.ImportedStatements.Add(row);
                db.SaveChanges();
                imported++;
                RunImported = imported;

                // FinanceTracker has a durable copy now — the email is redundant.
                await reader.TrashMessageAsync(id, ct);
            }
            catch (Exception ex) { lastError = ex.Message; }   // email stays put, retried next sweep
        }

        settings.Set("statements.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("statements.mail.last_count", imported.ToString());
        settings.Set("statements.mail.last_error", lastError);
        return imported;
    }
}
