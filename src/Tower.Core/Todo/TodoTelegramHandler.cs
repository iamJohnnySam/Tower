// src/Tower.Core/Todo/TodoTelegramHandler.cs
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Telegram;

namespace Tower.Core.Todo;

public sealed class TodoTelegramHandler(
    TelegramHub hub,
    IServiceScopeFactory scopes)
{
    public void Register()
    {
        hub.RegisterCommandHandler("/todo", HandleTodoCommandAsync);
        hub.RegisterCallbackHandler("todo_done:", HandleDoneCallbackAsync);
    }

    // ── /todo command ─────────────────────────────────────────────────────────

    private async Task HandleTodoCommandAsync(string text, long chatId, CancellationToken ct)
    {
        // Strip "/todo " prefix
        var body = text.Length > 5 ? text[5..].Trim() : "";
        if (string.IsNullOrWhiteSpace(body))
        {
            await hub.SendAsync(TgAudience.Chat, chatId,
                "Usage: /todo <title> [by <date>]", null, ct);
            return;
        }

        var deadline = ParseDeadline(body, out var title);

        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TodoService>();
        var item = await svc.AddAsync(title, deadline);

        var confirmText = deadline.HasValue
            ? $"✅ Added: {item.Title} (due {deadline.Value:MMM d})"
            : $"✅ Added: {item.Title}";

        IReadOnlyList<IReadOnlyList<(string, string)>> buttons = new[]
        {
            new[] { ("Mark done", $"todo_done:{item.Id}") }
        };

        var result = await hub.SendKeyboardAsync(chatId, confirmText, buttons, null, ct);
        if (result.Ok && result.MessageId > 0)
            await svc.SetTelegramMessageIdAsync(item.Id, result.MessageId);
    }

    // ── todo_done: callback ───────────────────────────────────────────────────

    private async Task HandleDoneCallbackAsync(
        string callbackData, long chatId, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(callbackData["todo_done:".Length..], out var id))
        {
            await hub.AnswerCallbackAsync(callbackId, null, ct);
            return;
        }

        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TodoService>();
        var item = await svc.MarkDoneAsync(id);

        await hub.AnswerCallbackAsync(callbackId, "Marked done", ct);

        if (item?.TelegramMessageId is int msgId)
        {
            await hub.EditAsync(chatId, msgId,
                $"☑ Done: {item.Title}", null, null, ct);
        }
    }

    // ── Date parsing (public static for testability) ──────────────────────────

    public static DateTime? ParseDeadline(string text, out string title)
    {
        var byIdx = text.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (byIdx < 0)
        {
            title = text.Trim();
            return null;
        }

        var candidate = text[(byIdx + 4)..].Trim();
        var titlePart = text[..byIdx].Trim();

        // Try weekday name (Monday–Sunday)
        if (Enum.TryParse<DayOfWeek>(candidate, ignoreCase: true, out var dow))
        {
            var today = DateTime.UtcNow.Date;
            var daysUntil = ((int)dow - (int)today.DayOfWeek + 7) % 7;
            if (daysUntil == 0) daysUntil = 7;
            title = titlePart;
            return today.AddDays(daysUntil);
        }

        // Try absolute date (ISO or "Dec 25" etc.)
        if (DateTime.TryParse(candidate, out var parsed))
        {
            var utcDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            // If date has already passed this year, assume next year
            if (utcDate < DateTime.UtcNow.Date)
                utcDate = utcDate.AddYears(1);
            title = titlePart;
            return utcDate;
        }

        // Unparseable — treat the whole text as the title
        title = text.Trim();
        return null;
    }
}
