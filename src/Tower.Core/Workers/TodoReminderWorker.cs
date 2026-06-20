using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tower.Core.Telegram;
using Tower.Core.Todo;

namespace Tower.Core.Workers;

public sealed class TodoReminderWorker(
    TelegramHub hub,
    IServiceScopeFactory scopes,
    ILogger<TodoReminderWorker> logger) : BackgroundService
{
    private static readonly TimeZoneInfo _colombo =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo");

    private DateOnly _lastReminderDate = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("TodoReminderWorker starting");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var utcNow = DateTime.UtcNow;
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _colombo);
                var today = DateOnly.FromDateTime(localNow);

                if (IsReminderTime(utcNow) && _lastReminderDate != today)
                {
                    _lastReminderDate = today;
                    await SendRemindersAsync(DateOnly.FromDateTime(utcNow), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "TodoReminderWorker: unhandled exception");
            }

            await Task.Delay(60_000, ct);
        }

        logger.LogInformation("TodoReminderWorker stopped");
    }

    private async Task SendRemindersAsync(DateOnly utcToday, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TodoService>();
        var today = utcToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var due = await svc.GetDueTodayAsync(today);

        if (due.Count == 0) return;

        var lines = string.Join("\n", due.Select(t => $"• {t.Title}"));
        var msg = $"📋 Due today:\n{lines}";

        var result = await hub.SendAsync(TgAudience.Admin, 0, msg, null, ct);
        if (!result.Ok)
            logger.LogWarning("TodoReminderWorker: failed to send reminder: {Error}", result.Error);
        else
            logger.LogInformation("TodoReminderWorker: sent reminder for {Count} item(s)", due.Count);
    }

    /// <summary>Returns true when UTC time maps to 09:00 Sri Lanka (hour=9, minute=0).</summary>
    public static bool IsReminderTime(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _colombo);
        return local.Hour == 9 && local.Minute == 0;
    }
}
