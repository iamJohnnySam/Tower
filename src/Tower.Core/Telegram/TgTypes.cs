namespace Tower.Core.Telegram;

/// <summary>
/// Routing target for outbound Telegram messages.
/// </summary>
public enum TgAudience
{
    /// <summary>Send to a specific chat identified by chatId.</summary>
    Chat = 0,

    /// <summary>Send to the configured admin chat.</summary>
    Admin = 1,

    /// <summary>Fan-out to all active subscribers.</summary>
    Subscribers = 2
}

/// <summary>
/// Result of an outbound Telegram send operation.
/// </summary>
public record TgResult(bool Ok, int MessageId, string? Error)
{
    public static TgResult NoToken { get; } = new(false, 0, "no token");
    public static TgResult NoTarget { get; } = new(false, 0, "no target");

    public static TgResult FromMessageId(int? id) =>
        id.HasValue
            ? new TgResult(true, id.Value, null)
            : new TgResult(false, 0, "send failed");
}
