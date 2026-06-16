using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tower.Core.Settings;
using Tower.Core.Telegram;

namespace Tower.Core.Workers;

/// <summary>
/// Background service that drives the Telegram long-poll loop.
///
/// Gating: only calls Telegram's getUpdates when BOTH:
///   - <c>telegram.active</c> setting == "true", AND
///   - <c>telegram.bot_token</c> setting is non-empty.
///
/// When inactive, the worker sleeps 5 s between checks (DORMANT mode).
/// This prevents Tower from touching Telegram (avoiding the one-consumer
/// 409 conflict with MediaBox) until the explicit M3 cutover (Task 13).
///
/// When active, PollOnceAsync blocks for up to ~30 s (long-poll timeout),
/// so no additional delay is needed between iterations.
/// </summary>
public sealed class TelegramPollWorker(
    TelegramHub hub,
    IServiceScopeFactory scopes,
    ILogger<TelegramPollWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("TelegramPollWorker starting");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Read settings from a short-lived scope
                string? token;
                bool active;

                using (var scope = scopes.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    token  = settings.Get("telegram.bot_token");
                    active = settings.Get("telegram.active") == "true";
                }

                if (!active || string.IsNullOrWhiteSpace(token))
                {
                    // DORMANT — not yet activated; refresh snapshot and idle
                    hub.RefreshSnapshot();
                    await Task.Delay(5_000, ct);
                    continue;
                }

                // ACTIVE — run one long-poll cycle (~30 s)
                logger.LogDebug("TelegramPollWorker: polling (offset={Offset})", 0);
                var ok = await hub.PollOnceAsync(token, ct);

                if (ok)
                    hub.ClearLastError();

                // Refresh snapshot after each poll so the UI stays current
                hub.RefreshSnapshot();

                // No extra delay: the long-poll itself already blocks ~30 s.
                // If the poll returned immediately (e.g. empty result), yield briefly
                // to avoid a tight loop in edge cases (fresh token, empty queue).
                if (ok)
                {
                    // Small courtesy yield between back-to-back long polls
                    await Task.Delay(100, ct);
                }
                else
                {
                    // Poll failed — short back-off before retry
                    hub.SetLastError("getUpdates returned null — Telegram API may be unreachable");
                    await Task.Delay(5_000, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TelegramPollWorker: unhandled exception in poll loop");
                hub.SetLastError(ex.Message);
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("TelegramPollWorker stopped");
    }
}
