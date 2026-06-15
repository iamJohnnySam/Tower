using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class JellyfinWorker(
    LiveState state,
    IHttpClientFactory httpFactory,
    IServiceScopeFactory scopes,
    JellyfinOptions opts) : BackgroundService
{
    private readonly Dictionary<string, string> _prevPlaying = new();

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
                    var client = new JellyfinClient(httpFactory.CreateClient(nameof(JellyfinClient)));
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

                            if (!_prevPlaying.TryGetValue(se.SessionId, out var prev) || prev != itemId)
                                db.PlayHistory.Add(MapPlay(se));
                        }

                        if (db.ChangeTracker.HasChanges())
                            await db.SaveChangesAsync(ct);

                        _prevPlaying.Clear();
                        foreach (var kv in newPrev)
                            _prevPlaying[kv.Key] = kv.Value;
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
