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

    // ── ffmpeg execution (implemented in Task 5) ──────────────────────────────

    public Task<bool> RunNextJobAsync(CancellationToken ct) => Task.FromResult(false); // stub

    // ── Approval / rejection (implemented in Task 5) ──────────────────────────

    private Task ApproveAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct) => Task.CompletedTask; // stub
    private Task RejectAsync(int jobId, long chatId, int approvalMsgId, string callbackId, CancellationToken ct) => Task.CompletedTask; // stub
}
