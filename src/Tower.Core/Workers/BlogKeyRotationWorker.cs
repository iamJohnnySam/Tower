using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tower.Core.Website;

namespace Tower.Core.Workers;

/// <summary>
/// Once a day, auto-rotates the blog API key when an interval is configured and the
/// current key is older than that interval. Never rotates a key that has never been
/// rotated (rotated_at null) — the first manual rotation sets the baseline.
/// </summary>
public sealed class BlogKeyRotationWorker(
    IServiceScopeFactory scopes,
    ILogger<BlogKeyRotationWorker> logger) : BackgroundService
{
    private const int IntervalMs = 86_400_000; // once per day

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<BlogKeyService>();

                var autoDays  = svc.AutoDays;
                var rotatedAt = svc.RotatedAt;

                if (autoDays is > 0 && rotatedAt is { } last &&
                    DateTime.UtcNow - last >= TimeSpan.FromDays(autoDays.Value))
                {
                    var token = await svc.RotateAsync();
                    logger.LogInformation("BlogKeyRotationWorker: auto-rotated (…{Tail})", token[^6..]);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "BlogKeyRotationWorker: rotation check failed");
            }

            await Task.Delay(IntervalMs, ct);
        }
    }
}
