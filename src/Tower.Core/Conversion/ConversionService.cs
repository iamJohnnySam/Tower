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
    string conversionTestPath,
    ILogger<JellyfinClient>? jellyfinLogger = null)
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

    // ── Auto-queue (called by JellyfinWorker when the box is struggling) ────────

    /// <summary>
    /// Resolves the file path from Jellyfin, creates a job already Queued for
    /// conversion (no approval prompt), and notifies the admin. No-ops if a job
    /// already exists. Falls back to a plain notice if the path can't be resolved.
    /// </summary>
    public async Task QueueAutomaticAsync(
        string mediaId, string mediaName, string mediaLabel,
        string transcodeReasons, CancellationToken ct)
    {
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

        string? filePath = null;
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(mediaId))
        {
            var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)), jellyfinLogger);
            filePath = await client.GetItemPathAsync(jellyfinOpts.JellyfinUrl, apiKey, mediaId);
        }

        if (filePath is null)
        {
            var fallback = $"🔁 Struggling to play — {mediaLabel}\nReason: {transcodeReasons}\n\n(File path unresolvable — cannot auto-convert)";
            await telegram.SendAsync(TgAudience.Chat, adminChatId, fallback, null, ct);
            return;
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            db.ConversionJobs.Add(new ConversionJob
            {
                MediaId          = mediaId,
                MediaName        = mediaName,
                OriginalPath     = filePath,
                Status           = ConversionStatus.Queued,
                TranscodeReasons = transcodeReasons,
                CreatedAt        = DateTime.Now,
            });
            await db.SaveChangesAsync(ct);
        }

        await telegram.SendAsync(TgAudience.Chat, adminChatId,
            $"🔁 Struggling to play — queued for conversion\n{mediaLabel}\nReason: {transcodeReasons}\n\nI'll swap the file in automatically once no one is watching it.",
            null, ct);
    }

    // ── Keep / Revert callbacks (after an automatic swap) ──────────────────────

    public async Task HandleKeepCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:keep:{jobId}:{msgId}"
        var parts = data["conv:keep:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int msgId)) return;

        string? backup = null, name = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            backup = job.BackupPath;
            name = job.MediaName;
            job.Status = ConversionStatus.Approved;
            await db.SaveChangesAsync(ct);
        }

        try { if (backup is not null && File.Exists(backup)) File.Delete(backup); } catch { /* best effort */ }

        await telegram.EditAsync(chatId, msgId, $"✅ Kept — backup deleted\n{name}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public async Task HandleRevertCallbackAsync(string data, long chatId, string callbackId, CancellationToken ct)
    {
        // data = "conv:revert:{jobId}:{msgId}"
        var parts = data["conv:revert:".Length..].Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int jobId) || !int.TryParse(parts[1], out int msgId)) return;

        string? backup = null, original = null, name = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is null) return;
            backup = job.BackupPath;
            original = job.OriginalPath;
            name = job.MediaName;
        }

        try
        {
            if (backup is null || !File.Exists(backup))
            {
                await telegram.EditAsync(chatId, msgId, $"⚠️ Cannot revert — backup missing\n{name}", null, null, ct);
                await telegram.AnswerCallbackAsync(callbackId, null, ct);
                return;
            }
            File.Move(backup, original!, overwrite: true); // restores original, discards converted file
        }
        catch (Exception ex)
        {
            await telegram.EditAsync(chatId, msgId, $"❌ Revert failed — {name}\n{ex.Message}", null, null, ct);
            await telegram.AnswerCallbackAsync(callbackId, null, ct);
            return;
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is not null) { job.Status = ConversionStatus.Reverted; job.CompletedAt = DateTime.Now; await db.SaveChangesAsync(ct); }
        }

        await telegram.EditAsync(chatId, msgId, $"↩️ Reverted to original\n{name}", null, null, ct);
        await telegram.AnswerCallbackAsync(callbackId, null, ct);
    }

    public void RegisterCallbacks(TelegramHub hub)
    {
        hub.RegisterCallbackHandler("conv:keep:",   HandleKeepCallbackAsync);
        hub.RegisterCallbackHandler("conv:revert:", HandleRevertCallbackAsync);
    }

    // ── Auto-replace: swap in the converted file once nobody is watching ───────

    /// <summary>
    /// For every completed conversion (AwaitingReplace), swap in the converted
    /// file once nobody is actively playing that media. The original is kept as
    /// a .bak; the admin gets a Keep/Revert message.
    /// </summary>
    public async Task TryReplaceReadyJobsAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken ct)
    {
        var nowPlaying = sessions
            .Where(s => s.Playing)
            .Select(s => string.IsNullOrEmpty(s.MediaId) ? s.Media : s.MediaId)
            .ToHashSet();

        List<(int id, string original, string test, string name)> ready = new();
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var jobs = await db.ConversionJobs
                .Where(j => j.Status == ConversionStatus.AwaitingReplace)
                .ToListAsync(ct);
            foreach (var j in jobs)
            {
                if (nowPlaying.Contains(j.MediaId)) continue; // still being watched
                if (j.TestPath is null) continue;
                ready.Add((j.Id, j.OriginalPath, j.TestPath, j.MediaName));
            }
        }

        foreach (var (id, original, test, name) in ready)
            await ReplaceOriginalAsync(id, original, test, name, ct);
    }

    private async Task ReplaceOriginalAsync(int jobId, string original, string test, string mediaName, CancellationToken ct)
    {
        long adminChatId;
        using (var scope = scopes.CreateScope())
        {
            var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            adminChatId = subs.GetAdmin() ?? 0;
        }

        string backup = Path.ChangeExtension(original, ".original.bak");
        try
        {
            if (!File.Exists(test)) throw new FileNotFoundException("Converted file missing", test);
            if (File.Exists(original)) File.Move(original, backup, overwrite: true);
            File.Move(test, original, overwrite: true);
        }
        catch (Exception ex)
        {
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                var job = await db.ConversionJobs.FindAsync(jobId);
                if (job is not null) { job.Status = ConversionStatus.Failed; job.ErrorMessage = ex.Message; await db.SaveChangesAsync(ct); }
            }
            if (adminChatId != 0)
                await telegram.SendAsync(TgAudience.Chat, adminChatId, $"❌ Auto-replace failed — {mediaName}\n{ex.Message}", null, ct);
            return;
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var job = await db.ConversionJobs.FindAsync(jobId);
            if (job is not null)
            {
                job.Status      = ConversionStatus.Replaced;
                job.BackupPath  = backup;
                job.CompletedAt = DateTime.Now;
                await db.SaveChangesAsync(ct);
            }
        }

        if (adminChatId == 0) return;

        // Send, then edit to embed the message id into the callbacks so Keep/Revert
        // can edit this exact message afterwards.
        var text = $"✅ Replaced original — {mediaName}\nThe converted file is now live (original kept as .bak). Keep it or revert?";
        List<IReadOnlyList<(string, string)>> Buttons(int mid) => new()
        {
            new List<(string, string)>
            {
                ("✅ Keep (delete backup)", $"conv:keep:{jobId}:{mid}"),
                ("↩️ Revert to original",   $"conv:revert:{jobId}:{mid}"),
            }
        };

        var sent = await telegram.SendKeyboardAsync(adminChatId, text, Buttons(0), null, ct);
        if (sent.Ok && sent.MessageId > 0)
            await telegram.EditAsync(adminChatId, sent.MessageId, text, Buttons(sent.MessageId), null, ct);
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
                    await MarkAwaitingReplaceAsync(capturedId, capturedName, ct);
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

    private async Task MarkAwaitingReplaceAsync(int jobId, string mediaName, CancellationToken ct)
    {
        long adminChatId = 0;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            var loaded = await db.ConversionJobs.FindAsync(jobId);
            if (loaded is null) return;
            loaded.Status = ConversionStatus.AwaitingReplace;
            await db.SaveChangesAsync(ct);

            var subs = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.SubscriberService>();
            adminChatId = subs.GetAdmin() ?? 0;
        }
        if (adminChatId == 0) return;

        await telegram.SendAsync(TgAudience.Chat, adminChatId,
            $"✅ Conversion complete — {mediaName}\nWaiting until no one is watching it, then I'll replace the original automatically (keeping a .bak).",
            null, ct);
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
