// src/Tower.Core/Conversion/ConversionService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.Telegram;

namespace Tower.Core.Conversion;

public class ConversionService(
    IServiceScopeFactory scopes,
    TelegramHub telegram,
    JellyfinOptions jellyfinOpts,
    IHttpClientFactory httpFactory,
    string conversionTestPath)
{
    private int _converting = 0;
    public bool IsConverting => _converting == 1;

    // ── Public query ──────────────────────────────────────────────────────────

    public async Task<bool> JobExistsForMediaAsync(string mediaId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        return await db.ConversionJobs.AnyAsync(j => j.MediaId == mediaId);
    }

    // ── Alert sender (called by JellyfinWorker) ───────────────────────────────

    /// <summary>
    /// Resolves the file path from Jellyfin, creates a Pending job, and sends
    /// an inline keyboard alert to the admin. No-ops if a job already exists.
    /// Falls back to plain text alert if the file path cannot be resolved.
    /// </summary>
    public async Task SendAlertAsync(
        string mediaId, string mediaName, string mediaLabel,
        string transcodeReasons, int transcodeCount,
        CancellationToken ct)
    {
        // Resolve admin chat
        long adminChatId;
        string apiKey;
        using (var scope = scopes.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var subscribers = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            var adminId = subscribers.GetAdmin();
            if (adminId is null) return;
            adminChatId = adminId.Value;
            apiKey = settings.Get("jellyfin.api_key") ?? "";
        }

        // Resolve file path
        string? filePath = null;
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(mediaId))
        {
            var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)));
            filePath = await client.GetItemPathAsync(jellyfinOpts.JellyfinUrl, apiKey, mediaId);
        }

        if (filePath is null)
        {
            // Fallback: plain text (no conversion option)
            var fallback = $"🔁 Repeatedly transcoded ({transcodeCount}×)\n{mediaLabel}\nReason: {transcodeReasons}\n\n(File path unresolvable — cannot offer conversion)";
            await telegram.SendAsync(TgAudience.Chat, adminChatId, fallback, null, ct);
            return;
        }

        // Create Pending job
        int jobId;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = new ConversionJob
            {
                MediaId          = mediaId,
                MediaName        = mediaName,
                OriginalPath     = filePath,
                Status           = ConversionStatus.Pending,
                TranscodeReasons = transcodeReasons,
                CreatedAt        = DateTime.Now,
            };
            db.ConversionJobs.Add(job);
            await db.SaveChangesAsync(ct);
            jobId = job.Id;
        }

        // Send inline keyboard
        var text = $"🔁 Repeatedly transcoded ({transcodeCount}×)\n{mediaLabel}\nReason: {transcodeReasons}\n\nWhat would you like to do?";
        var buttons = new List<IReadOnlyList<(string, string)>>
        {
            new List<(string, string)>
            {
                ("✅ Mark for conversion", $"conv:convert:{jobId}"),
                ("🚫 Ignore", $"conv:ignore:{jobId}"),
            }
        };

        var result = await telegram.SendKeyboardAsync(adminChatId, text, buttons, null, ct);

        // Store message_id so we can edit the message after user responds
        if (result.Ok && result.MessageId > 0)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is not null)
            {
                job.AlertMessageId = result.MessageId;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // ── Callback handlers (called by dispatcher) ──────────────────────────────

    public async Task HandleConvertCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data["conv:convert:".Length..], out int jobId)) return;

        int? alertMsgId = null;
        string mediaName = "";
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            job.Status = ConversionStatus.Queued;
            alertMsgId = job.AlertMessageId;
            mediaName = job.MediaName;
            await db.SaveChangesAsync(ct);
        }

        if (alertMsgId.HasValue)
            await telegram.EditAsync(chatId, alertMsgId.Value, $"✅ Queued for conversion — {mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public async Task HandleIgnoreCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data["conv:ignore:".Length..], out int jobId)) return;

        int? alertMsgId = null;
        string mediaName = "";
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            job.Status = ConversionStatus.Ignored;
            alertMsgId = job.AlertMessageId;
            mediaName = job.MediaName;
            await db.SaveChangesAsync(ct);
        }

        if (alertMsgId.HasValue)
            await telegram.EditAsync(chatId, alertMsgId.Value, $"🚫 Ignored — {mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public async Task HandleApproveCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:approve:{jobId}:{approvalMsgId}"
        var parts = data["conv:approve:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int approvalMsgId)) return;
        await ApproveAsync(jobId, chatId, approvalMsgId, callbackId, ct);
    }

    public async Task HandleRejectCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:reject:{jobId}:{approvalMsgId}"
        var parts = data["conv:reject:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int approvalMsgId)) return;
        await RejectAsync(jobId, chatId, approvalMsgId, callbackId, ct);
    }

    // ── Register all four prefixes into TelegramHub ───────────────────────────

    public void RegisterCallbacks(TelegramHub hub)
    {
        hub.RegisterCallbackHandler("conv:convert:", HandleConvertCallbackAsync);
        hub.RegisterCallbackHandler("conv:ignore:",  HandleIgnoreCallbackAsync);
        hub.RegisterCallbackHandler("conv:approve:", HandleApproveCallbackAsync);
        hub.RegisterCallbackHandler("conv:reject:",  HandleRejectCallbackAsync);
    }

    // ── Approval / rejection ──────────────────────────────────────────────────

    private async Task ApproveAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)
    {
        string? testPath = null, originalPath = null, mediaName = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            testPath     = job.TestPath;
            originalPath = job.OriginalPath;
            mediaName    = job.MediaName;
        }

        try
        {
            if (testPath is not null && originalPath is not null && File.Exists(testPath))
                File.Move(testPath, originalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Mark Failed in DB
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                var job = await db.ConversionJobs.FindAsync(jobId);
                if (job is not null) { job.Status = ConversionStatus.Failed; job.ErrorMessage = ex.Message; await db.SaveChangesAsync(ct); }
            }
            await telegram.EditAsync(chatId, approvalMsgId, $"❌ Approve failed — {mediaName}\n{ex.Message}", null, null, ct);
            await telegram.AnswerCallbackAsync(callbackId, null, ct);
            return;
        }

        // File move succeeded — now update DB
        using (var scope2 = scopes.CreateScope())
        {
            var db = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is not null) { job.Status = ConversionStatus.Approved; job.CompletedAt = DateTime.Now; await db.SaveChangesAsync(ct); }
        }

        await telegram.EditAsync(chatId, approvalMsgId, $"✅ Approved — original replaced\n{mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    private async Task RejectAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct)
    {
        string? testPath = null, mediaName = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            testPath     = job.TestPath;
            mediaName    = job.MediaName;
            job.Status      = ConversionStatus.Rejected;
            job.CompletedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);
        }

        try
        {
            if (testPath is not null && File.Exists(testPath))
                File.Delete(testPath);
        }
        catch { /* best effort */ }

        await telegram.EditAsync(chatId, approvalMsgId, $"❌ Rejected — test file deleted\n{mediaName}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    // ── ffmpeg execution ──────────────────────────────────────────────────────

    public async Task<bool> RunNextJobAsync(CancellationToken ct)
    {
        // Only one job at a time
        if (System.Threading.Interlocked.CompareExchange(ref _converting, 1, 0) != 0) return false;

        try
        {
            // Pick oldest queued job
            ConversionJob? job;
            int capturedId = 0;
            string capturedOriginal = "";
            string capturedTest = "";
            string capturedName = "";
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                job = await db.ConversionJobs
                    .Where(j => j.Status == ConversionStatus.Queued)
                    .OrderBy(j => j.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (job is null) return false;

                // Verify source file exists
                if (!File.Exists(job.OriginalPath))
                {
                    job.Status       = ConversionStatus.Failed;
                    job.ErrorMessage = "Source file not found";
                    job.CompletedAt  = DateTime.Now;
                    await db.SaveChangesAsync(ct);
                    return false;
                }

                // Build output path: {testDir}/{id}_{nameWithoutExt}.mkv
                Directory.CreateDirectory(conversionTestPath);
                var nameNoExt    = Path.GetFileNameWithoutExtension(job.OriginalPath);
                var testFileName = $"{job.Id}_{nameNoExt}.mkv";
                job.TestPath  = Path.Combine(conversionTestPath, testFileName);
                job.Status    = ConversionStatus.Converting;
                job.StartedAt = DateTime.Now;
                await db.SaveChangesAsync(ct);

                // Extract primitives before scope closes — job entity must not be accessed after disposal
                capturedId       = job.Id;
                capturedOriginal = job.OriginalPath;
                capturedTest     = job.TestPath!;
                capturedName     = job.MediaName;
            }

            // Run ffmpeg in Task.Run to avoid blocking the scheduler thread
            await Task.Run(async () =>
            {
                string? stderr = null;
                int exitCode = -1;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/ffmpeg",
                        $"-i \"{capturedOriginal}\" -c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k -c:s copy -map 0 -y \"{capturedTest}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start ffmpeg");

                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    bool finished  = proc.WaitForExit(14_400_000); // 4 hours
                    stderr         = await stderrTask;
                    exitCode       = finished ? proc.ExitCode : -1;
                    if (!finished) try { proc.Kill(entireProcessTree: true); } catch { }
                }
                catch (Exception ex)
                {
                    stderr   = ex.Message;
                    exitCode = -1;
                }

                if (exitCode == 0)
                {
                    await MarkAwaitingApprovalAsync(capturedId, capturedName, ct);
                }
                else
                {
                    var snippet = stderr is null ? "unknown error"
                        : stderr.Length <= 500 ? stderr
                        : "…" + stderr[^500..];
                    await MarkFailedAsync(capturedId, capturedName, snippet, ct);
                }
            }, ct);

            return true;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _converting, 0);
        }
    }

    private async Task MarkAwaitingApprovalAsync(int jobId, string mediaName, CancellationToken ct)
    {
        long adminChatId = 0;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var loaded = await db.ConversionJobs.FindAsync(jobId);
            if (loaded is null) return;
            loaded.Status      = ConversionStatus.AwaitingApproval;
            loaded.CompletedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);

            var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            adminChatId = subs.GetAdmin() ?? 0;
        }
        if (adminChatId == 0) return;

        var text = $"✅ Conversion complete\n{mediaName}\nTest file ready.\n\nAdd ConversionTest/ as a Jellyfin library to verify playback, then:";
        var sent = await telegram.SendKeyboardAsync(adminChatId, text,
            new List<IReadOnlyList<(string, string)>>
            {
                new List<(string, string)>
                {
                    ("✅ Approve — replace original", $"conv:approve:{jobId}:0"),
                    ("❌ Reject — delete test file",  $"conv:reject:{jobId}:0"),
                }
            }, null, ct);

        if (sent.Ok && sent.MessageId > 0)
        {
            await telegram.EditAsync(adminChatId, sent.MessageId, text,
                new List<IReadOnlyList<(string, string)>>
                {
                    new List<(string, string)>
                    {
                        ("✅ Approve — replace original", $"conv:approve:{jobId}:{sent.MessageId}"),
                        ("❌ Reject — delete test file",  $"conv:reject:{jobId}:{sent.MessageId}"),
                    }
                }, null, ct);
        }
    }

    private async Task MarkFailedAsync(int jobId, string mediaName, string error, CancellationToken ct)
    {
        long adminChatId = 0;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var loaded = await db.ConversionJobs.FindAsync(jobId);
            if (loaded is null) return;
            loaded.Status       = ConversionStatus.Failed;
            loaded.ErrorMessage = error;
            loaded.CompletedAt  = DateTime.Now;
            await db.SaveChangesAsync(ct);

            var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            adminChatId = subs.GetAdmin() ?? 0;
        }
        if (adminChatId == 0) return;

        var snippet = error.Length > 300 ? error[..300] + "…" : error;
        await telegram.SendAsync(TgAudience.Chat, adminChatId,
            $"❌ Conversion failed — {mediaName}\n{snippet}", null, ct);
    }
}
