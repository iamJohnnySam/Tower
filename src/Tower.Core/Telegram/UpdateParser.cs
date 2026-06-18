using System.Text.Json;

namespace Tower.Core.Telegram;

/// <summary>
/// Immutable result of a single Telegram update (message or callback_query).
/// </summary>
public record ParsedUpdate(
    bool IsCallback,
    long ChatId,
    string Text,
    string Username,
    string FirstName,
    string LastName,
    string CallbackId,
    string CallbackData,
    int MessageId);

/// <summary>
/// Pure, static parser for Telegram getUpdates JSON responses.
/// Never throws — malformed input returns (0, empty).
/// </summary>
public static class UpdateParser
{
    /// <summary>
    /// Parses the raw JSON body returned by the Telegram Bot API getUpdates endpoint.
    /// </summary>
    /// <param name="getUpdatesJson">The raw JSON string from getUpdates.</param>
    /// <returns>
    /// (newOffset, updates): newOffset is max(update_id)+1 across all results (0 if none);
    /// updates contains one <see cref="ParsedUpdate"/> per recognized update (message with
    /// text, or callback_query). Results with neither are skipped.
    /// </returns>
    public static (long newOffset, List<ParsedUpdate> updates) ParseUpdates(string getUpdatesJson)
    {
        var updates = new List<ParsedUpdate>();
        long maxUpdateId = -1;

        try
        {
            using var doc = JsonDocument.Parse(getUpdatesJson);
            var root = doc.RootElement;

            // Require ok:true
            if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                return (0, updates);

            if (!root.TryGetProperty("result", out var results))
                return (0, updates);

            foreach (var update in results.EnumerateArray())
            {
                if (!update.TryGetProperty("update_id", out var updateIdProp))
                    continue;

                var updateId = updateIdProp.GetInt64();
                if (updateId > maxUpdateId)
                    maxUpdateId = updateId;

                if (update.TryGetProperty("message", out var msg))
                {
                    var parsed = ParseMessage(msg);
                    if (parsed != null)
                        updates.Add(parsed);
                }
                else if (update.TryGetProperty("callback_query", out var cbq))
                {
                    var parsed = ParseCallback(cbq);
                    if (parsed != null)
                        updates.Add(parsed);
                }
                // Other update types (edited_message, channel_post, etc.) are skipped
            }
        }
        catch
        {
            // Malformed JSON or unexpected structure — return whatever was parsed so far
            return (0, new List<ParsedUpdate>());
        }

        var newOffset = maxUpdateId >= 0 ? maxUpdateId + 1 : 0;
        return (newOffset, updates);
    }

    private static ParsedUpdate? ParseMessage(JsonElement msg)
    {
        try
        {
            if (!msg.TryGetProperty("chat", out var chat)) return null;
            var chatId = chat.GetProperty("id").GetInt64();

            // Only handle text messages
            if (!msg.TryGetProperty("text", out var textProp)) return null;
            var text = textProp.GetString() ?? "";

            var (username, firstName, lastName) = ExtractFrom(msg);

            return new ParsedUpdate(
                IsCallback: false,
                ChatId: chatId,
                Text: text,
                Username: username,
                FirstName: firstName,
                LastName: lastName,
                CallbackId: "",
                CallbackData: "",
                MessageId: 0);
        }
        catch
        {
            return null;
        }
    }

    private static ParsedUpdate? ParseCallback(JsonElement cbq)
    {
        try
        {
            var callbackId = cbq.TryGetProperty("id", out var idProp)
                ? idProp.GetString() ?? ""
                : "";

            var data = cbq.TryGetProperty("data", out var dataProp)
                ? dataProp.GetString() ?? ""
                : "";

            // chat_id comes from callback_query.message.chat.id
            if (!cbq.TryGetProperty("message", out var cbMsg)) return null;
            if (!cbMsg.TryGetProperty("chat", out var chat)) return null;
            var chatId = chat.GetProperty("id").GetInt64();

            var messageId = cbMsg.TryGetProperty("message_id", out var midProp)
                ? midProp.GetInt32()
                : 0;

            var (username, firstName, lastName) = ExtractFrom(cbq);

            return new ParsedUpdate(
                IsCallback: true,
                ChatId: chatId,
                Text: "",
                Username: username,
                FirstName: firstName,
                LastName: lastName,
                CallbackId: callbackId,
                CallbackData: data,
                MessageId: messageId);
        }
        catch
        {
            return null;
        }
    }

    private static (string username, string firstName, string lastName) ExtractFrom(JsonElement el)
    {
        if (!el.TryGetProperty("from", out var from))
            return ("", "", "");

        var username = from.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        var firstName = from.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "";
        var lastName = from.TryGetProperty("last_name", out var l) ? l.GetString() ?? "" : "";

        return (username, firstName, lastName);
    }
}
