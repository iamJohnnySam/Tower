using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tower.Core.Telegram;

/// <summary>
/// Low-level Telegram Bot API HTTP client.
/// Token is passed per-call (Tower reads it from settings).
/// All methods are defensive — they never throw and return null/false on failure.
/// </summary>
public class TelegramApi(HttpClient http, ILogger<TelegramApi> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string BaseUrl(string token) =>
        $"https://api.telegram.org/bot{token}";

    // ─── GetUpdates ──────────────────────────────────────────────────────────

    /// <summary>
    /// Long-polls Telegram for new updates. Returns the raw JSON body, or null on error.
    /// The HttpClient timeout is set to timeoutSec + 10 seconds to accommodate the long poll.
    /// </summary>
    public async Task<string?> GetUpdatesRawAsync(
        string token, long offset, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl(token)}/getUpdates?offset={offset}&timeout={timeoutSec}";

            // Use a per-request timeout slightly longer than the long-poll window
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec + 10));

            var response = await http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("getUpdates returned {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Propagate clean shutdown — don't swallow this
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "getUpdates failed");
            return null;
        }
    }

    // ─── SendMessage ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a text message. Returns the sent message_id, or null on failure.
    /// </summary>
    public async Task<int?> SendMessageAsync(
        string token, long chatId, string text, string? parseMode, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> {
            ["chat_id"] = chatId,
            ["text"] = text
        };
        if (parseMode != null) payload["parse_mode"] = parseMode;

        return await PostAsync(token, "sendMessage", payload, ct);
    }

    // ─── SendPhoto ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a photo (URL) with an optional caption and inline keyboard.
    /// Returns the sent message_id, or null on failure.
    /// </summary>
    public async Task<int?> SendPhotoAsync(
        string token,
        long chatId,
        string photoUrl,
        string caption,
        IReadOnlyList<IReadOnlyList<(string text, string data)>>? buttons,
        string? parseMode,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> {
            ["chat_id"] = chatId,
            ["photo"] = photoUrl,
            ["caption"] = caption
        };
        if (parseMode != null) payload["parse_mode"] = parseMode;
        if (buttons != null) payload["reply_markup"] = BuildKeyboard(buttons);

        return await PostAsync(token, "sendPhoto", payload, ct);
    }

    // ─── SendInlineKeyboard ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a text message with an inline keyboard. Returns the sent message_id, or null on failure.
    /// </summary>
    public async Task<int?> SendInlineKeyboardAsync(
        string token,
        long chatId,
        string text,
        IReadOnlyList<IReadOnlyList<(string text, string data)>> buttons,
        string? parseMode,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["reply_markup"] = BuildKeyboard(buttons)
        };
        if (parseMode != null) payload["parse_mode"] = parseMode;

        return await PostAsync(token, "sendMessage", payload, ct);
    }

    // ─── EditMessage ─────────────────────────────────────────────────────────

    /// <summary>
    /// Edits an existing message's text (and optionally its inline keyboard).
    /// Returns true if the edit succeeded.
    /// </summary>
    public async Task<bool> EditMessageAsync(
        string token,
        long chatId,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<(string text, string data)>>? buttons,
        string? parseMode,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text
        };
        if (parseMode != null) payload["parse_mode"] = parseMode;
        if (buttons != null) payload["reply_markup"] = BuildKeyboard(buttons);

        var result = await PostAsync(token, "editMessageText", payload, ct);
        return result.HasValue;
    }

    // ─── AnswerCallback ──────────────────────────────────────────────────────

    /// <summary>
    /// Answers a callback query to clear the "loading" spinner on the client side.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> AnswerCallbackAsync(
        string token, string callbackId, string? text, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> {
            ["callback_query_id"] = callbackId
        };
        if (text != null) payload["text"] = text;

        var result = await PostAsync(token, "answerCallbackQuery", payload, ct);
        return result.HasValue;
    }

    // ─── Inline keyboard helper ──────────────────────────────────────────────

    /// <summary>
    /// Builds the inline_keyboard reply_markup object.
    /// Shape: { inline_keyboard: [ [ {text, callback_data}, ... ], ... ] }
    /// </summary>
    private static object BuildKeyboard(IReadOnlyList<IReadOnlyList<(string text, string data)>> buttons)
    {
        var rows = buttons.Select(row =>
            row.Select(b => new Dictionary<string, object> {
                ["text"] = b.text,
                ["callback_data"] = b.data
            }).ToArray()
        ).ToArray();

        return new Dictionary<string, object> { ["inline_keyboard"] = rows };
    }

    // ─── Shared POST helper ──────────────────────────────────────────────────

    /// <summary>
    /// Posts a JSON payload to the Telegram Bot API. Returns message_id on success, null on failure.
    /// Retries up to 3 times with backoff on transient/rate-limit errors.
    /// </summary>
    private async Task<int?> PostAsync(
        string token, string method, Dictionary<string, object?> payload, CancellationToken ct)
    {
        const int maxRetries = 3;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var url = $"{BaseUrl(token)}/{method}";

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                logger.LogDebug("Telegram API {Method} attempt {Attempt}/{Max}", method, attempt, maxRetries);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await http.PostAsync(url, content, ct);

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogDebug("Telegram {Method} response {StatusCode}: {Body}",
                    method, response.StatusCode, responseBody);

                if (response.IsSuccessStatusCode)
                {
                    // Try to extract result.message_id (not present for all methods, e.g. answerCallbackQuery)
                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                        {
                            if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                                resultEl.ValueKind == JsonValueKind.Object &&
                                resultEl.TryGetProperty("message_id", out var mid))
                            {
                                return mid.GetInt32();
                            }
                            // ok:true but no message_id (e.g. answerCallbackQuery returns ok:true, result:true)
                            return 0;
                        }
                    }
                    catch (JsonException jex)
                    {
                        logger.LogWarning(jex, "Could not parse Telegram {Method} response; message was sent", method);
                        return 0;
                    }
                }
                else if ((int)response.StatusCode == 429)
                {
                    // Rate limited — back off before retry
                    var delayMs = 1000 * attempt;
                    logger.LogWarning("Telegram rate-limited on {Method}; waiting {Delay}ms", method, delayMs);
                    await Task.Delay(delayMs, ct);
                }
                else if ((int)response.StatusCode is >= 400 and < 500)
                {
                    // Client error (bad request, unauthorized, etc.) — don't retry
                    logger.LogError("Telegram {Method} client error {StatusCode}: {Body}",
                        method, response.StatusCode, responseBody);
                    return null;
                }
                else
                {
                    // Server error — retry with backoff
                    if (attempt < maxRetries)
                    {
                        var delayMs = 1000 * attempt;
                        logger.LogWarning("Telegram {Method} server error {StatusCode}; retrying in {Delay}ms",
                            method, response.StatusCode, delayMs);
                        await Task.Delay(delayMs, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogWarning("Telegram {Method} cancelled", method);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram {Method} exception (attempt {Attempt}/{Max})", method, attempt, maxRetries);
                if (attempt < maxRetries)
                    await Task.Delay(1000 * attempt, ct);
            }
        }

        logger.LogError("Telegram {Method} failed after {MaxRetries} attempts", method, maxRetries);
        return null;
    }
}
