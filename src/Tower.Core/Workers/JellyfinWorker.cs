using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Workers;

public class JellyfinWorker(
    LiveState state,
    IHttpClientFactory httpFactory,
    IServiceScopeFactory scopes,
    JellyfinOptions opts,
    TelegramHub telegram,
    ILogger<JellyfinClient> jellyfinLogger,
    ConversionService conversion) : BackgroundService
{
    private readonly Dictionary<string, string> _prevPlaying = new();
    private readonly HashSet<string> _alertedMedia = new();
    private readonly Dictionary<string, int> _struggleTicks = new();

    // ponytail: struggle thresholds hardcoded like ConversionScheduler's 30%/15-tick knobs.
    // Move to appsettings if this box needs different tuning.
    private const double StruggleCpuPct = 85.0; // box CPU above which a live transcode is "struggling"
    private const int StrugglePolls = 6;         // consecutive 5s polls (~30s) of struggle before auto-queue

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (count, cpu) = FfmpegStats.Collect();

                string apiKey;
                using (var scope = scopes.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    apiKey = settings.Get("jellyfin.api_key") ?? "";
                }

                List<SessionInfo> sessions = new();
                string err = "";
                bool configured = !string.IsNullOrEmpty(apiKey);

                if (configured)
                {
                    var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)), jellyfinLogger);
                    var fetched = await client.SessionsAsync(opts.JellyfinUrl, apiKey);
                    if (fetched is null)
                    {
                        err = "Jellyfin unreachable";
                    }
                    else
                    {
                        sessions = fetched;

                        // Detect new play events and record them
                        var newPrev = new Dictionary<string, string>();
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

                        foreach (var se in sessions)
                        {
                            if (!se.Playing) continue;
                            var itemId = string.IsNullOrEmpty(se.MediaId) ? se.Media : se.MediaId;
                            newPrev[se.SessionId] = itemId;

                            bool isNew = !_prevPlaying.TryGetValue(se.SessionId, out var prev) || prev != itemId;
                            if (isNew)
                            {
                                db.PlayHistory.Add(MapPlay(se));
                                await AlertIfProblematicAsync(se, ct);
                            }
                        }

                        if (db.ChangeTracker.HasChanges())
                            await db.SaveChangesAsync(ct);

                        _prevPlaying.Clear();
                        foreach (var kv in newPrev)
                            _prevPlaying[kv.Key] = kv.Value;

                        // Auto-queue files whose live transcode is straining the box,
                        // and swap in completed conversions once nobody is watching.
                        await EvaluateStruggleAsync(sessions, state.Stats.CpuPct, ct);
                        await conversion.TryReplaceReadyJobsAsync(sessions, ct);
                    }
                }
                else
                {
                    _prevPlaying.Clear();
                }

                state.PushFfmpegHistory(count, cpu);
                var (countHist, cpuHist) = state.SnapshotFfmpegHistory();
                state.SetJellyfin(new JellyfinSnapshot(
                    Sessions: sessions,
                    FfmpegCount: count,
                    FfmpegCpu: cpu,
                    FfmpegCountHistory: countHist,
                    FfmpegCpuHistory: cpuHist,
                    Error: err,
                    ApiConfigured: configured,
                    Updated: DateTime.Now));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[jellyfin] {ex.Message}");
                var (countHist, cpuHist) = state.SnapshotFfmpegHistory();
                state.SetJellyfin(new JellyfinSnapshot(
                    Sessions: Array.Empty<SessionInfo>(),
                    FfmpegCount: 0,
                    FfmpegCpu: 0,
                    FfmpegCountHistory: countHist,
                    FfmpegCpuHistory: cpuHist,
                    Error: $"worker error: {ex.Message}",
                    ApiConfigured: state.Jellyfin.ApiConfigured,
                    Updated: DateTime.Now));
            }

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AlertIfProblematicAsync(SessionInfo se, CancellationToken ct)
    {
        if (!se.Method.Equals("Transcode", StringComparison.OrdinalIgnoreCase)) return;

        var mediaId = string.IsNullOrEmpty(se.MediaId) ? se.Media : se.MediaId;

        // First-play heads-up for HEVC 10-bit (info only — struggle detection drives conversion)
        bool isHevc10bit = se.VideoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                           && se.VideoBitDepth == 10;
        if (isHevc10bit && _alertedMedia.Add(mediaId))
        {
            var msg = $"⚠️ HEVC 10-bit transcode\n{BuildTitle(se)}\n\nThis file requires live CPU transcoding.";
            await telegram.SendAsync(TgAudience.Admin, 0, msg, null, ct);
        }
    }

    /// <summary>
    /// Auto-queues a file for conversion when its live transcode has kept the box
    /// CPU pegged (>= StruggleCpuPct) for StrugglePolls consecutive polls — i.e. the
    /// box is struggling to play it continuously. Fires once per media.
    /// </summary>
    private async Task EvaluateStruggleAsync(List<SessionInfo> sessions, double boxCpu, CancellationToken ct)
    {
        var transcoding = new HashSet<string>();
        foreach (var se in sessions)
        {
            if (!se.Playing || !se.Method.Equals("Transcode", StringComparison.OrdinalIgnoreCase)) continue;
            var mediaId = string.IsNullOrEmpty(se.MediaId) ? se.Media : se.MediaId;
            transcoding.Add(mediaId);

            // Only accumulate struggle ticks while the box is actually strained
            if (boxCpu < StruggleCpuPct) { _struggleTicks[mediaId] = 0; continue; }

            int ticks = _struggleTicks.GetValueOrDefault(mediaId) + 1;
            _struggleTicks[mediaId] = ticks;
            if (ticks < StrugglePolls) continue;

            _struggleTicks[mediaId] = 0; // reset so we don't re-fire every poll
            if (await conversion.JobExistsForMediaAsync(mediaId)) continue;

            var reasons = string.Join(", ", se.TranscodeReasons.DefaultIfEmpty("Unknown"));
            await conversion.QueueAutomaticAsync(
                mediaId:          mediaId,
                mediaName:        string.IsNullOrEmpty(se.SeriesName) ? se.Media : se.SeriesName,
                mediaLabel:       BuildTitle(se),
                transcodeReasons: reasons,
                ct:               ct);
        }

        // Drop ticks for media that is no longer transcoding
        foreach (var key in _struggleTicks.Keys.Where(k => !transcoding.Contains(k)).ToList())
            _struggleTicks.Remove(key);
    }

    private static string BuildTitle(SessionInfo se) =>
        string.IsNullOrEmpty(se.SeriesName)
            ? se.Media
            : $"{se.SeriesName} S{se.SeasonNumber:D2}E{se.EpisodeNumber:D2} — {se.Media}";

    private static PlayHistory MapPlay(SessionInfo s) => new()
    {
        StartedAt      = DateTime.Now,
        SessionKey     = s.SessionId,
        MediaId        = s.MediaId,
        MediaName      = string.IsNullOrEmpty(s.Media) ? "Unknown" : s.Media,
        MediaType      = s.MediaType,
        SeriesName     = s.SeriesName,
        SeasonNumber   = s.SeasonNumber,
        EpisodeNumber  = s.EpisodeNumber,
        UserName       = s.User,
        PlayMethod     = s.Method,
        TranscodeReasons = string.Join(",", s.TranscodeReasons),
        VideoCodec     = s.VideoCodec,
        AudioCodec     = s.AudioCodec,
        Container      = s.Container,
        ClientName     = s.Client,
        DeviceName     = s.Device,
    };
}
