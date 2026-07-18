using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;

namespace Tower.Core.Telegram;

/// <summary>
/// Singleton coordinator for all Telegram I/O in Tower.
///
/// Responsibilities:
///   - Client registry: gRPC StreamUpdates callers register/unregister here;
///     inbound updates are fanned-out via System.Threading.Channels.
///   - Outbound routing: SendAsync / SendPhotoAsync / SendKeyboardAsync /
///     EditAsync / AnswerCallbackAsync — resolve audience, call TelegramApi,
///     persist to TelegramMessage log.
///   - Incoming handling: HandleIncomingAsync — blocked-user gate, subscriber
///     auto-registration (first subscriber becomes admin, mirroring MediaBox),
///     persistence, broadcast.
///   - Poll: PollOnceAsync — single long-poll cycle (called by TelegramPollWorker).
///   - Snapshot: RefreshSnapshot — refreshes LiveState.Comms from the DB + hub state.
///
/// LAYERING: This class lives in Tower.Core and must NOT reference the generated
/// gRPC proto types. The broadcast type is the existing <see cref="ParsedUpdate"/>
/// record. The gRPC service (Task 5, web project) converts proto ↔ internal types.
/// </summary>
public sealed class TelegramHub(
    TelegramApi api,
    IServiceScopeFactory scopes,
    LiveState state,
    ILogger<TelegramHub> logger)
{
    // ── Offset ────────────────────────────────────────────────────────────────

    private long _offset;

    // ── Last error (for snapshot) ─────────────────────────────────────────────

    private string _lastError = "";

    // ── Client registry ───────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, ChannelWriter<ParsedUpdate>> _clients = new();

    // ── Callback dispatcher ───────────────────────────────────────────────────

    private readonly List<(string Prefix, Func<string, long, string, CancellationToken, Task> Handler)> _callbackHandlers = new();

    /// <summary>
    /// Registers a handler invoked when an inbound callback's Data starts with <paramref name="prefix"/>.
    /// The first registered matching prefix wins. Handler receives (callbackData, chatId, callbackId, ct).
    /// </summary>
    public void RegisterCallbackHandler(string prefix, Func<string, long, string, CancellationToken, Task> handler)
    {
        _callbackHandlers.Add((prefix, handler));
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    private readonly List<(string Command, Func<string, long, CancellationToken, Task> Handler)> _commandHandlers = new();

    /// <summary>
    /// Registers a handler for a specific bot command (e.g. "/todo").
    /// Only dispatched for the admin user. <paramref name="command"/> must include the slash.
    /// </summary>
    public void RegisterCommandHandler(string command, Func<string, long, CancellationToken, Task> handler)
    {
        _commandHandlers.Add((command, handler));
    }

    /// <summary>
    /// Registers a new streaming client and returns its Channel so the caller
    /// can read from it with <c>await foreach</c>.
    /// </summary>
    public Channel<ParsedUpdate> RegisterClient(string clientId)
    {
        var channel = Channel.CreateUnbounded<ParsedUpdate>(
            new UnboundedChannelOptions { SingleReader = true });
        _clients[clientId] = channel.Writer;
        logger.LogInformation("TelegramHub: client registered [{ClientId}] (total={Count})",
            clientId, _clients.Count);
        return channel;
    }

    /// <summary>
    /// Unregisters a streaming client and completes its channel writer so the
    /// reader loop (await foreach) exits cleanly.
    /// </summary>
    public void UnregisterClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out var writer))
        {
            writer.TryComplete();
            logger.LogInformation("TelegramHub: client unregistered [{ClientId}] (total={Count})",
                clientId, _clients.Count);
        }
    }

    /// <summary>Current number of registered gRPC streaming clients.</summary>
    public int ClientCount => _clients.Count;

    // ── Broadcast ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fan-out an inbound update to all registered streaming clients.
    /// Uses TryWrite (non-blocking, unbounded channel).
    /// </summary>
    public void BroadcastUpdate(ParsedUpdate u)
    {
        foreach (var (id, writer) in _clients)
        {
            if (!writer.TryWrite(u))
                logger.LogWarning("TelegramHub: failed to write to client [{ClientId}]", id);
        }
    }

    // ── Outbound helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves audience → list of target chatIds.
    /// Returns null if the audience can't be resolved (e.g. Admin with no admin set).
    /// Each call opens its own scope.
    /// </summary>
    private async Task<(string? token, List<long> targets)> ResolveAsync(
        TgAudience aud, long chatId)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var subscribers = scope.ServiceProvider.GetRequiredService<SubscriberService>();

        var token = settings.Get("telegram.bot_token");
        if (string.IsNullOrWhiteSpace(token))
            return (null, []);

        List<long> targets = aud switch
        {
            TgAudience.Chat        => [chatId],
            TgAudience.Admin       => subscribers.GetAdmin() is long a ? [a] : [],
            TgAudience.Subscribers => subscribers.ListActive().Select(s => s.ChatId).ToList(),
            _                      => []
        };

        return (token, targets);
    }

    /// <summary>
    /// Persists an outbound log entry. Opens its own scope.
    /// </summary>
    private void LogOutbound(long chatId, string text)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
            db.Messages.Add(new TelegramMessage
            {
                Ts        = DateTime.UtcNow,
                Direction = "out",
                ChatId    = chatId,
                Payload   = text
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TelegramHub: failed to persist outbound message log");
        }
    }

    // ── Public outbound API ───────────────────────────────────────────────────

    /// <summary>
    /// Sends a text message to the specified audience.
    /// Returns TgResult for the last successful (or last failed) send.
    /// Never throws.
    /// </summary>
    public async Task<TgResult> SendAsync(
        TgAudience aud, long chatId, string text, string? parseMode, CancellationToken ct)
    {
        try
        {
            var (token, targets) = await ResolveAsync(aud, chatId);
            if (token is null) return TgResult.NoToken;
            if (targets.Count == 0) return TgResult.NoTarget;

            TgResult last = TgResult.NoTarget;
            foreach (var target in targets)
            {
                var msgId = await api.SendMessageAsync(token, target, text, parseMode, ct);
                last = TgResult.FromMessageId(msgId);
                LogOutbound(target, text);
            }
            return last;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramHub.SendAsync failed");
            return new TgResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Sends a photo to the specified audience.
    /// Never throws.
    /// </summary>
    public async Task<TgResult> SendPhotoAsync(
        TgAudience aud,
        long chatId,
        string photoUrl,
        string caption,
        IReadOnlyList<IReadOnlyList<(string, string)>>? buttons,
        string? parseMode,
        CancellationToken ct)
    {
        try
        {
            var (token, targets) = await ResolveAsync(aud, chatId);
            if (token is null) return TgResult.NoToken;
            if (targets.Count == 0) return TgResult.NoTarget;

            TgResult last = TgResult.NoTarget;
            foreach (var target in targets)
            {
                var msgId = await api.SendPhotoAsync(token, target, photoUrl, caption, buttons, parseMode, ct);
                last = TgResult.FromMessageId(msgId);
                LogOutbound(target, $"[photo] {caption}");
            }
            return last;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramHub.SendPhotoAsync failed");
            return new TgResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Sends an inline keyboard message to a specific chat.
    /// Returns TgResult with MessageId set (needed for EditAsync later).
    /// Never throws.
    /// </summary>
    public async Task<TgResult> SendKeyboardAsync(
        long chatId,
        string text,
        IReadOnlyList<IReadOnlyList<(string, string)>> buttons,
        string? parseMode,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var token = settings.Get("telegram.bot_token");
            if (string.IsNullOrWhiteSpace(token)) return TgResult.NoToken;

            var msgId = await api.SendInlineKeyboardAsync(token, chatId, text, buttons, parseMode, ct);
            var result = TgResult.FromMessageId(msgId);
            LogOutbound(chatId, text);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramHub.SendKeyboardAsync failed");
            return new TgResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Edits an existing inline-keyboard message.
    /// Never throws.
    /// </summary>
    public async Task<TgResult> EditAsync(
        long chatId,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<(string, string)>>? buttons,
        string? parseMode,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var token = settings.Get("telegram.bot_token");
            if (string.IsNullOrWhiteSpace(token)) return TgResult.NoToken;

            var ok = await api.EditMessageAsync(token, chatId, messageId, text, buttons, parseMode, ct);
            LogOutbound(chatId, $"[edit:{messageId}] {text}");
            return ok ? new TgResult(true, messageId, null) : new TgResult(false, 0, "edit failed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramHub.EditAsync failed");
            return new TgResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Answers a callback query to clear the Telegram spinner.
    /// Never throws.
    /// </summary>
    public async Task<TgResult> AnswerCallbackAsync(
        string callbackId, string? text, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var token = settings.Get("telegram.bot_token");
            if (string.IsNullOrWhiteSpace(token)) return TgResult.NoToken;

            var ok = await api.AnswerCallbackAsync(token, callbackId, text, ct);
            return ok ? new TgResult(true, 0, null) : new TgResult(false, 0, "answer callback failed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramHub.AnswerCallbackAsync failed");
            return new TgResult(false, 0, ex.Message);
        }
    }

    // ── Poll ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs one long-poll cycle (getUpdates with timeout=30).
    /// Returns true if the response was successfully parsed (even if empty).
    /// Returns false if the HTTP call failed (null response).
    /// </summary>
    public async Task<bool> PollOnceAsync(string token, CancellationToken ct)
    {
        var json = await api.GetUpdatesRawAsync(token, _offset, 30, ct);
        if (json is null) return false;

        var (newOffset, updates) = UpdateParser.ParseUpdates(json);

        // Advance offset (never go backwards)
        if (newOffset > _offset)
            _offset = newOffset;

        foreach (var update in updates)
        {
            await HandleIncomingAsync(update, ct);
        }

        return true;
    }

    // ── Incoming ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes a single inbound update:
    ///   1. Ignore blocked users (silently).
    ///   2. Auto-register new users as subscribers; first subscriber becomes admin.
    ///   3. Persist to TelegramMessage log.
    ///   4. Broadcast to all registered gRPC streaming clients.
    /// </summary>
    public async Task HandleIncomingAsync(ParsedUpdate u, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var subscriberSvc = scope.ServiceProvider.GetRequiredService<SubscriberService>();
        var db            = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

        // 1. Gate: ignore blocked users
        if (subscriberSvc.IsBlocked(u.ChatId))
        {
            logger.LogDebug("TelegramHub: ignoring message from blocked user {ChatId}", u.ChatId);
            return;
        }

        var admin = subscriberSvc.GetAdmin();

        // 1b. Admin tapping Approve/Deny on a connection request — handle and stop.
        if (u.IsCallback && admin == u.ChatId &&
            (u.CallbackData.StartsWith("tgapprove:", StringComparison.Ordinal) ||
             u.CallbackData.StartsWith("tgdeny:", StringComparison.Ordinal)))
        {
            await HandleConnectionDecisionAsync(subscriberSvc, u, ct);
            return;
        }

        // 2. Bootstrap: no admin configured yet → first person to talk becomes admin
        //    (mirrors MediaBox). Only reachable before an admin exists.
        if (admin is null && !u.IsCallback && !subscriberSvc.IsActive(u.ChatId))
        {
            subscriberSvc.AddOrReactivate(u.ChatId, DisplayName(u));
            subscriberSvc.SetAdmin(u.ChatId);
            logger.LogInformation("TelegramHub: first subscriber set as admin: {ChatId}", u.ChatId);
            await SendAsync(TgAudience.Chat, u.ChatId,
                "Welcome to Tower! You have been registered as the Telegram admin.", null, ct);
        }
        // 3. Approval gate: anyone who is neither the admin nor an already-approved
        //    subscriber is a stranger. Their message is NOT logged-through, NOT
        //    replied to, and NOT broadcast to MediaBox/handlers. We only ask the
        //    admin to approve (once), then drop the message.
        else if (admin is not null && u.ChatId != admin && !subscriberSvc.IsActive(u.ChatId))
        {
            if (!subscriberSvc.IsPending(u.ChatId))
            {
                var name = DisplayName(u);
                subscriberSvc.SetPending(u.ChatId, name);
                logger.LogWarning(
                    "TelegramHub: connection request from {ChatId} ({Name}) — awaiting admin approval",
                    u.ChatId, name);

                var preview = u.IsCallback ? u.CallbackData : u.Text;
                if (preview.Length > 80) preview = preview[..80] + "…";
                var buttons = new List<IReadOnlyList<(string, string)>>
                {
                    new List<(string, string)>
                    {
                        ("✅ Approve", $"tgapprove:{u.ChatId}"),
                        ("⛔ Deny",    $"tgdeny:{u.ChatId}"),
                    }
                };
                await SendKeyboardAsync(admin.Value,
                    $"🔔 New person wants to use the bot:\n{name} (id {u.ChatId})\nThey said: {preview}\n\nAllow them to connect?",
                    buttons, null, ct);
            }
            else
            {
                logger.LogDebug("TelegramHub: dropping message from pending user {ChatId}", u.ChatId);
            }
            return; // never process a stranger's message before approval
        }

        // 4. Persist inbound message to log
        try
        {
            string? command = null;
            if (!u.IsCallback && u.Text.StartsWith('/'))
            {
                var firstToken = u.Text.Split(' ', 2)[0];
                command = firstToken;
            }

            db.Messages.Add(new TelegramMessage
            {
                Ts        = DateTime.UtcNow,
                Direction = "in",
                ChatId    = u.ChatId,
                Command   = command,
                Payload   = u.IsCallback ? u.CallbackData : u.Text
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TelegramHub: failed to persist inbound message log");
        }

        // 4. Broadcast to registered gRPC clients
        BroadcastUpdate(u);

        // 5. Dispatch to registered internal callback handlers
        if (u.IsCallback && _callbackHandlers.Count > 0)
        {
            // Only dispatch to internal handlers for the admin user
            bool isAdmin;
            using (var cbScope = scopes.CreateScope())
            {
                var cbSubs = cbScope.ServiceProvider.GetRequiredService<SubscriberService>();
                isAdmin = cbSubs.GetAdmin() == u.ChatId;
            }

            if (isAdmin)
            {
                foreach (var (prefix, handler) in _callbackHandlers)
                {
                    if (u.CallbackData.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        try { await handler(u.CallbackData, u.ChatId, u.CallbackId, ct); }
                        catch (Exception ex) { logger.LogError(ex, "Callback handler failed for prefix {Prefix}", prefix); }
                        break;
                    }
                }
            }
        }

        // 6. Dispatch text commands to registered internal handlers (admin only).
        //    Commands may be registered with a slash ("/todo") or without ("run") —
        //    the first token of the message must match the registered form exactly.
        if (!u.IsCallback && u.Text.Length > 0 && _commandHandlers.Count > 0)
        {
            bool isCommandAdmin;
            using (var cmdScope = scopes.CreateScope())
            {
                var cmdSubs = cmdScope.ServiceProvider.GetRequiredService<SubscriberService>();
                isCommandAdmin = cmdSubs.GetAdmin() == u.ChatId;
            }

            if (isCommandAdmin)
            {
                var cmdToken = u.Text.Split(' ', 2)[0].ToLowerInvariant();
                foreach (var (cmd, handler) in _commandHandlers)
                {
                    if (cmdToken == cmd.ToLowerInvariant())
                    {
                        try { await handler(u.Text, u.ChatId, ct); }
                        catch (Exception ex) { logger.LogError(ex, "Command handler failed for {Cmd}", cmd); }
                        break;
                    }
                }
            }
        }
    }

    // ── Connection approval ───────────────────────────────────────────────────

    private static string DisplayName(ParsedUpdate u) =>
        !string.IsNullOrWhiteSpace(u.Username) ? $"@{u.Username}"
        : !string.IsNullOrWhiteSpace(u.FirstName) ? $"{u.FirstName} {u.LastName}".Trim()
        : u.ChatId.ToString();

    /// <summary>
    /// Handles the admin tapping Approve/Deny on a connection request.
    /// Approve → subscriber becomes active and is welcomed; Deny → blocked.
    /// </summary>
    private async Task HandleConnectionDecisionAsync(SubscriberService subs, ParsedUpdate u, CancellationToken ct)
    {
        bool approve = u.CallbackData.StartsWith("tgapprove:", StringComparison.Ordinal);
        var idStr = u.CallbackData[(u.CallbackData.IndexOf(':') + 1)..];
        if (!long.TryParse(idStr, out var targetId))
        {
            await AnswerCallbackAsync(u.CallbackId, "Invalid request", ct);
            return;
        }

        var name = subs.Get(targetId)?.Name ?? targetId.ToString();

        if (approve)
        {
            subs.AddOrReactivate(targetId, null); // keeps existing name, status → active
            logger.LogInformation("TelegramHub: admin approved connection for {ChatId}", targetId);
            await EditAsync(u.ChatId, u.MessageId, $"✅ Approved — {name} can now use the bot.", null, null, ct);
            await SendAsync(TgAudience.Chat, targetId,
                "You've been approved to use this bot. Send /help to get started.", null, ct);
        }
        else
        {
            subs.Block(targetId);
            logger.LogInformation("TelegramHub: admin denied connection for {ChatId}", targetId);
            await EditAsync(u.ChatId, u.MessageId, $"⛔ Denied — {name} is blocked.", null, null, ct);
        }

        await AnswerCallbackAsync(u.CallbackId, null, ct);
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes <see cref="LiveState.Comms"/> from the DB + hub state.
    /// Safe to call from any thread — opens its own scope.
    /// </summary>
    public void RefreshSnapshot()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var db       = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

            var active          = settings.Get("telegram.active") == "true";
            var tokenConfigured = !string.IsNullOrWhiteSpace(settings.Get("telegram.bot_token"));

            // Read last 30 messages (newest first)
            var rows = db.Messages
                .OrderByDescending(m => m.Ts)
                .Take(30)
                .AsNoTracking()
                .ToList();

            var log = rows
                .Select(m => new MsgLogEntry(
                    m.Ts,
                    m.Direction,
                    m.ChatId,
                    m.Payload ?? m.Command ?? ""))
                .ToList();

            state.SetComms(new CommsSnapshot(
                Active:           active,
                TokenConfigured:  tokenConfigured,
                ConnectedClients: _clients.Count,
                LastError:        _lastError,
                RecentLog:        log,
                Updated:          DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TelegramHub.RefreshSnapshot failed");
        }
    }

    /// <summary>
    /// Updates the last-error field (used by TelegramPollWorker) and then
    /// calls <see cref="RefreshSnapshot"/> so the UI reflects the error.
    /// </summary>
    public void SetLastError(string error)
    {
        _lastError = error;
        RefreshSnapshot();
    }

    /// <summary>Clears the last-error field on successful polls.</summary>
    public void ClearLastError()
    {
        _lastError = "";
    }
}
