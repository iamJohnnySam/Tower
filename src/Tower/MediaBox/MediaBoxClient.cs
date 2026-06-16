using Grpc.Core;
using Grpc.Net.Client;
using MediaBox.Control.Grpc;
using Microsoft.Extensions.Options;
using MediaBoxStatus = MediaBox.Control.Grpc.Status;

namespace Tower.MediaBox;

/// <summary>
/// Singleton wrapper over the generated <see cref="MediaBoxControl.MediaBoxControlClient"/>.
///
/// Design: MediaBox is a separate, independently-deployed process. Tower's UI (Tasks 9-10) and
/// scheduler (Task 8) must never throw, hang, or render an error page just because MediaBox is
/// offline/restarting/unreachable. Every method below therefore:
///   - try/catches ALL exceptions (RpcException, network errors, deadline-exceeded, etc.)
///   - on failure returns a SAFE DEFAULT: triggers/mutations -> (false, "MediaBox unreachable: ...");
///     queries -> an empty proto message (never null, never throws) so callers can render
///     "no data" without null-checking everywhere.
///   - applies a deadline via CallOptions so a wedged MediaBox can't hang Tower's scheduler or a
///     Blazor render forever: 30s for triggers that may do real work (Scan, Organize, YouTube
///     downloads, etc.), 5s for read-only queries.
///
/// The GrpcChannel is created lazily on first use and cached for the lifetime of this singleton
/// (channel creation does not itself connect — gRPC connects lazily per-call — so this is safe to
/// construct even when MediaBox is down).
/// </summary>
public sealed class MediaBoxClient
{
    private static readonly TimeSpan TriggerDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(5);

    private readonly string _address;
    private readonly Lazy<MediaBoxControl.MediaBoxControlClient> _client;

    public MediaBoxClient(IOptions<TowerConfig> cfg)
    {
        _address = string.IsNullOrWhiteSpace(cfg.Value.MediaBoxGrpcUrl)
            ? "http://localhost:5602"
            : cfg.Value.MediaBoxGrpcUrl;
        _client = new Lazy<MediaBoxControl.MediaBoxControlClient>(CreateClient);
    }

    private MediaBoxControl.MediaBoxControlClient CreateClient()
    {
        var channel = GrpcChannel.ForAddress(_address);
        return new MediaBoxControl.MediaBoxControlClient(channel);
    }

    private MediaBoxControl.MediaBoxControlClient Client => _client.Value;

    private static CallOptions TriggerOpts() => new(deadline: DateTime.UtcNow.Add(TriggerDeadline));
    private static CallOptions QueryOpts() => new(deadline: DateTime.UtcNow.Add(QueryDeadline));

    private static (bool ok, string message) Unreachable(Exception ex) =>
        (false, "MediaBox unreachable: " + ex.Message);

    // ── Triggers ──────────────────────────────────────────────────────────────

    public Task<(bool ok, string message)> ScanAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.ScanAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> OrganizeAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.OrganizeAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> RssCheckAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.RssCheckAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> TransmissionPollAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.TransmissionPollAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> YouTubeDownloadAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.YouTubeDownloadAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> YouTubePauseAsync(string title, CancellationToken ct = default) =>
        RunTrigger(c => c.YouTubePauseAsync(new TitleArg { Title = title }, TriggerOpts()), ct);

    public Task<(bool ok, string message)> YouTubeResumeAsync(string title, CancellationToken ct = default) =>
        RunTrigger(c => c.YouTubeResumeAsync(new TitleArg { Title = title }, TriggerOpts()), ct);

    public Task<(bool ok, string message)> ResetQualityAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.ResetQualityAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> ToggleSpeedModeAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.ToggleSpeedModeAsync(new Empty(), TriggerOpts()), ct);

    public Task<(bool ok, string message)> WatchlistCheckAsync(CancellationToken ct = default) =>
        RunTrigger(c => c.WatchlistCheckAsync(new Empty(), TriggerOpts()), ct);

    // ── Queries ───────────────────────────────────────────────────────────────

    public Task<MediaBoxStatus> GetStatusAsync(CancellationToken ct = default) =>
        RunQuery(c => c.GetStatusAsync(new Empty(), QueryOpts()), new MediaBoxStatus(), ct);

    public Task<DownloadList> GetDownloadsAsync(CancellationToken ct = default) =>
        RunQuery(c => c.GetDownloadsAsync(new Empty(), QueryOpts()), new DownloadList(), ct);

    public Task<MediaList> GetLibraryAsync(string type, CancellationToken ct = default) =>
        RunQuery(c => c.GetLibraryAsync(new LibraryQuery { Type = type }, QueryOpts()), new MediaList(), ct);

    public Task<WatchlistItems> GetWatchlistAsync(CancellationToken ct = default) =>
        RunQuery(c => c.GetWatchlistAsync(new Empty(), QueryOpts()), new WatchlistItems(), ct);

    public Task<YouTubeSources> GetYouTubeSourcesAsync(CancellationToken ct = default) =>
        RunQuery(c => c.GetYouTubeSourcesAsync(new Empty(), QueryOpts()), new YouTubeSources(), ct);

    public Task<SettingsMap> GetSettingsAsync(CancellationToken ct = default) =>
        RunQuery(c => c.GetSettingsAsync(new Empty(), QueryOpts()), new SettingsMap(), ct);

    // ── Mutations ─────────────────────────────────────────────────────────────

    public Task<(bool ok, string message)> AddWatchlistAsync(string title, CancellationToken ct = default) =>
        RunTrigger(c => c.AddWatchlistAsync(new TitleArg { Title = title }, TriggerOpts()), ct);

    public Task<(bool ok, string message)> RemoveWatchlistAsync(string title, CancellationToken ct = default) =>
        RunTrigger(c => c.RemoveWatchlistAsync(new TitleArg { Title = title }, TriggerOpts()), ct);

    public Task<(bool ok, string message)> SearchAndAddMovieAsync(string title, CancellationToken ct = default) =>
        RunTrigger(c => c.SearchAndAddMovieAsync(new TitleArg { Title = title }, TriggerOpts()), ct);

    public Task<(bool ok, string message)> UpdateSettingsAsync(IDictionary<string, string> values, CancellationToken ct = default)
    {
        var map = new SettingsMap();
        foreach (var kv in values) map.Values[kv.Key] = kv.Value;
        return RunTrigger(c => c.UpdateSettingsAsync(map, TriggerOpts()), ct);
    }

    // ── Shared safe-call helpers ──────────────────────────────────────────────

    private async Task<(bool ok, string message)> RunTrigger(
        Func<MediaBoxControl.MediaBoxControlClient, AsyncUnaryCall<RunResult>> call,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await call(Client);
            return (result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            return Unreachable(ex);
        }
    }

    private async Task<T> RunQuery<T>(
        Func<MediaBoxControl.MediaBoxControlClient, AsyncUnaryCall<T>> call,
        T empty,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await call(Client);
        }
        catch (Exception)
        {
            return empty;
        }
    }
}
