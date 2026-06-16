using Grpc.Core;
using Tower.Core.Telegram;
using Tower.Telegram.Grpc;

namespace Tower;

/// <summary>
/// Real gRPC service implementation that adapts the generated proto types
/// (Tower.Telegram.Grpc) to the internal TelegramHub singleton in Tower.Core.
///
/// Injection:
///   - TelegramHub    (singleton) — all Telegram I/O
///   - IServiceScopeFactory      — SubscriberService is scoped; create per-call
///
/// IMPORTANT: This is a long-lived gRPC service object. Never inject scoped
/// services directly; always open a scope per unary call.
/// </summary>
public sealed class TowerTelegramService(
    TelegramHub hub,
    IServiceScopeFactory scopes)
    : TowerTelegram.TowerTelegramBase
{
    // ── StreamUpdates ─────────────────────────────────────────────────────────

    /// <summary>
    /// Server-streaming RPC: keeps the connection open and writes each
    /// inbound Telegram update to the caller as a proto Update message.
    /// The hub broadcasts ParsedUpdate to all registered clients via channels.
    /// No DB access here — the hub already persisted the message in HandleIncomingAsync.
    /// Unregisters the client in a finally block so cleanup is guaranteed.
    /// </summary>
    public override async Task StreamUpdates(
        StreamRequest request,
        IServerStreamWriter<Update> responseStream,
        ServerCallContext context)
    {
        var clientId = string.IsNullOrEmpty(request.ClientId)
            ? Guid.NewGuid().ToString()
            : request.ClientId;

        var channel = hub.RegisterClient(clientId);
        try
        {
            await foreach (var u in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(ToProto(u));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: client disconnected or server shutting down.
        }
        finally
        {
            hub.UnregisterClient(clientId);
        }
    }

    // ── Unary RPCs ────────────────────────────────────────────────────────────

    public override async Task<SendResult> SendMessage(
        SendMessageRequest request,
        ServerCallContext context)
    {
        try
        {
            var r = await hub.SendAsync(
                MapAudience(request.Audience),
                request.ChatId,
                request.Text,
                NullIfEmpty(request.ParseMode),
                context.CancellationToken);
            return ToResult(r);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<SendResult> SendPhoto(
        SendPhotoRequest request,
        ServerCallContext context)
    {
        try
        {
            var r = await hub.SendPhotoAsync(
                MapAudience(request.Audience),
                request.ChatId,
                request.PhotoUrl,
                request.Caption,
                ToInternalButtons(request.Buttons),
                NullIfEmpty(request.ParseMode),
                context.CancellationToken);
            return ToResult(r);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<SendResult> SendInlineKeyboard(
        InlineKeyboardRequest request,
        ServerCallContext context)
    {
        try
        {
            var buttons = ToInternalButtons(request.Buttons)
                ?? Array.Empty<IReadOnlyList<(string, string)>>();
            var r = await hub.SendKeyboardAsync(
                request.ChatId,
                request.Text,
                buttons,
                NullIfEmpty(request.ParseMode),
                context.CancellationToken);
            return ToResult(r);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<SendResult> EditMessage(
        EditMessageRequest request,
        ServerCallContext context)
    {
        try
        {
            var r = await hub.EditAsync(
                request.ChatId,
                request.MessageId,
                request.Text,
                ToInternalButtons(request.Buttons),
                NullIfEmpty(request.ParseMode),
                context.CancellationToken);
            return ToResult(r);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<Ack> AnswerCallback(
        AnswerCallbackRequest request,
        ServerCallContext context)
    {
        try
        {
            var r = await hub.AnswerCallbackAsync(
                request.CallbackId,
                NullIfEmpty(request.Text),
                context.CancellationToken);
            return new Ack { Ok = r.Ok };
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override Task<Ack> SyncSubscribers(
        SubscriberList request,
        ServerCallContext context)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SubscriberService>();
            svc.ImportFrom(request.Subscribers.Select(s =>
                (s.ChatId, (string?)s.Name, s.IsAdmin)));
            return Task.FromResult(new Ack { Ok = true });
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override Task<SubscriberList> ListSubscribers(
        Empty request,
        ServerCallContext context)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SubscriberService>();
            var admin = svc.GetAdmin();
            var list = new SubscriberList();
            foreach (var s in svc.ListActive())
            {
                list.Subscribers.Add(new Subscriber
                {
                    ChatId  = s.ChatId,
                    Name    = s.Name ?? "",
                    IsAdmin = s.ChatId == admin
                });
            }
            return Task.FromResult(list);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    // ── Conversion helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a ParsedUpdate (internal) to a proto Update (oneof message/callback).
    /// </summary>
    private static Update ToProto(ParsedUpdate u)
    {
        if (u.IsCallback)
        {
            return new Update
            {
                Callback = new CallbackQuery
                {
                    ChatId     = u.ChatId,
                    CallbackId = u.CallbackId,
                    Data       = u.CallbackData,
                    MessageId  = u.MessageId
                }
            };
        }

        return new Update
        {
            Message = new IncomingMessage
            {
                ChatId    = u.ChatId,
                Text      = u.Text,
                Username  = u.Username,
                FirstName = u.FirstName,
                LastName  = u.LastName
            }
        };
    }

    /// <summary>
    /// Maps proto Audience enum to internal TgAudience enum.
    /// </summary>
    private static TgAudience MapAudience(Audience a) => a switch
    {
        Audience.Admin       => TgAudience.Admin,
        Audience.Subscribers => TgAudience.Subscribers,
        _                    => TgAudience.Chat
    };

    /// <summary>
    /// Converts proto ButtonRow/Button rows to the internal (text, callbackData)
    /// tuple list expected by TelegramHub methods.
    /// Returns null when the input is null or empty (callers that accept nullable
    /// pass null directly; callers requiring non-null use ?? with an empty array).
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<(string, string)>>? ToInternalButtons(
        IEnumerable<ButtonRow>? rows)
    {
        if (rows is null) return null;

        var result = rows
            .Select(row => (IReadOnlyList<(string, string)>)row.Buttons
                .Select(b => (b.Text, b.CallbackData))
                .ToList())
            .ToList();

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Returns null for empty/whitespace strings so the hub treats them as absent.
    /// (Proto strings default to "" rather than null.)
    /// </summary>
    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Converts a TgResult (internal) to a proto SendResult.
    /// </summary>
    private static SendResult ToResult(TgResult r) =>
        new() { Ok = r.Ok, MessageId = r.MessageId, Error = r.Error ?? "" };
}
