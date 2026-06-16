using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tower.MediaBox;

namespace Tower;

/// <summary>
/// Tower-driven cadence scheduler for MediaBox (Task 8, M4).
///
/// Tower owns the timers; MediaBox just executes triggers when told to. This worker is
/// gated by <c>Tower:MediaBoxOrchestrate</c> (default false) — the same "explicit flip"
/// safety pattern as Telegram's <c>telegram.active</c> gate: while false, this loop is
/// idle and MediaBox's own SelfSchedule (if enabled there) is what drives it instead.
///
/// Jobs wired (name -> interval -> MediaBoxClient trigger):
///   RssCheck         -> MediaBoxJobs.RssCheckMinutes         -> RssCheckAsync
///   Organize         -> MediaBoxJobs.OrganizeMinutes         -> OrganizeAsync
///   TransmissionPoll -> MediaBoxJobs.TransmissionPollMinutes -> TransmissionPollAsync
///   Scan             -> MediaBoxJobs.ScanHours (*60)         -> ScanAsync
///   YouTube          -> MediaBoxJobs.YouTubeMinutes          -> YouTubeDownloadAsync
///   Watchlist        -> MediaBoxJobs.WatchlistMinutes        -> WatchlistCheckAsync
///
/// YouTube idempotency note: MediaBox's YouTube downloader uses a yt-dlp download archive, so
/// re-triggering YouTubeDownload on every due tick is safe/idempotent — it just re-checks
/// sources and downloads anything new, never re-downloads what's already archived. Periodic
/// "check for new" every YouTubeMinutes is the intended model (there's no separate
/// "watch for new uploads" push from MediaBox).
/// </summary>
public sealed class MediaBoxScheduler(
    MediaBoxClient client,
    IOptionsMonitor<TowerConfig> cfg,
    ILogger<MediaBoxScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private sealed record Job(string Name, Func<MediaBoxJobsConfig, TimeSpan> Interval, Func<Task<(bool ok, string message)>> Trigger);

    private List<Job> BuildJobs() => new()
    {
        new Job("RssCheck",         j => TimeSpan.FromMinutes(j.RssCheckMinutes),         () => client.RssCheckAsync()),
        new Job("Organize",         j => TimeSpan.FromMinutes(j.OrganizeMinutes),         () => client.OrganizeAsync()),
        new Job("TransmissionPoll", j => TimeSpan.FromMinutes(j.TransmissionPollMinutes), () => client.TransmissionPollAsync()),
        new Job("Scan",             j => TimeSpan.FromHours(j.ScanHours),                 () => client.ScanAsync()),
        new Job("YouTube",          j => TimeSpan.FromMinutes(j.YouTubeMinutes),          () => client.YouTubeDownloadAsync()),
        new Job("Watchlist",        j => TimeSpan.FromMinutes(j.WatchlistMinutes),        () => client.WatchlistCheckAsync()),
    };

    /// <summary>
    /// Pure due-calc helper: is it time to run a job again? Exposed as public static so it can
    /// be unit-tested in isolation without spinning up the BackgroundService/DI/gRPC stack.
    /// </summary>
    public static bool IsDue(DateTime now, DateTime lastRun, TimeSpan interval) =>
        now - lastRun >= interval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MediaBoxScheduler starting");

        var jobs = BuildJobs();
        // Never-run sentinel so each job fires on its first active tick, then settles
        // into its own interval from there.
        var lastRun = jobs.ToDictionary(j => j.Name, _ => DateTime.MinValue);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);

                if (!cfg.CurrentValue.MediaBoxOrchestrate)
                {
                    // Safety gate is off — idle. MediaBox may be self-scheduling instead.
                    continue;
                }

                var jobsCfg = cfg.CurrentValue.MediaBoxJobs;
                var now = DateTime.Now;

                foreach (var job in jobs)
                {
                    var interval = job.Interval(jobsCfg);
                    if (!IsDue(now, lastRun[job.Name], interval))
                        continue;

                    try
                    {
                        var (ok, message) = await job.Trigger();
                        lastRun[job.Name] = now;

                        if (ok)
                            logger.LogInformation("MediaBoxScheduler: {Job} ran ok — {Message}", job.Name, message);
                        else
                            logger.LogWarning("MediaBoxScheduler: {Job} failed — {Message}", job.Name, message);
                    }
                    catch (Exception ex)
                    {
                        // MediaBoxClient never throws, but guard anyway — a failed job should
                        // not advance lastRun, so it's retried on the next due tick.
                        logger.LogError(ex, "MediaBoxScheduler: {Job} threw unexpectedly", job.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaBoxScheduler: unhandled exception in scheduler loop");
            }
        }

        logger.LogInformation("MediaBoxScheduler stopped");
    }
}
