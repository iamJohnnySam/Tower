// tests/Tower.Core.Tests/ConversionServiceTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Tests;

public class ConversionServiceTests
{
    // Returns a shared SqliteConnection + IServiceScopeFactory backed by it.
    // All scopes share the same connection so in-memory data persists across scopes.
    static (SqliteConnection conn, IServiceScopeFactory scopes) BuildDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var dbOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite(conn).Options;

        // Create schema once
        using (var seed = new TowerDbContext(dbOpts))
            seed.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(dbOpts);
        services.AddScoped<TowerDbContext>(sp =>
            new TowerDbContext(sp.GetRequiredService<DbContextOptions<TowerDbContext>>()));
        services.AddScoped<SettingsService>();
        services.AddScoped<SubscriberService>();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        return (conn, sp.GetRequiredService<IServiceScopeFactory>());
    }

    static TelegramHub BuildHub(IServiceScopeFactory scopes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        return new TelegramHub(
            sp.GetRequiredService<TelegramApi>(),
            scopes,
            sp.GetRequiredService<LiveState>(),
            NullLogger<TelegramHub>.Instance);
    }

    [Fact]
    public async Task JobExistsForMedia_returns_true_when_pending_job_exists()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        db.ConversionJobs.Add(new ConversionJob
        {
            MediaId = "media-abc", MediaName = "Test", OriginalPath = "/tmp/test.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now
        });
        db.SaveChanges();

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        Assert.True(await svc.JobExistsForMediaAsync("media-abc"));
        Assert.False(await svc.JobExistsForMediaAsync("other-media"));
    }

    [Fact]
    public async Task TryReplaceReadyJobs_swaps_when_idle_and_keeps_backup()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var testFile = Path.Combine(tmpDir, $"conv_test_{Guid.NewGuid()}.mkv");
        var origFile = Path.Combine(tmpDir, $"conv_orig_{Guid.NewGuid()}.mkv");
        var backup   = Path.ChangeExtension(origFile, ".original.bak");
        await File.WriteAllTextAsync(origFile, "ORIGINAL");
        await File.WriteAllTextAsync(testFile, "CONVERTED");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m1", MediaName = "Film", OriginalPath = origFile,
            TestPath = testFile, Status = ConversionStatus.AwaitingReplace,
            CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        // Nobody watching → swap fires
        await svc.TryReplaceReadyJobsAsync(Array.Empty<SessionInfo>(), CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Replaced, updated.Status);
        Assert.Equal("CONVERTED", await File.ReadAllTextAsync(origFile)); // converted file is live
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(backup));    // original preserved as .bak
        Assert.False(File.Exists(testFile));                             // moved out of test dir
        Assert.Equal(backup, updated.BackupPath);

        File.Delete(origFile); File.Delete(backup);
    }

    [Fact]
    public async Task TryReplaceReadyJobs_skips_while_media_is_playing()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var testFile = Path.Combine(tmpDir, $"conv_test_{Guid.NewGuid()}.mkv");
        var origFile = Path.Combine(tmpDir, $"conv_orig_{Guid.NewGuid()}.mkv");
        await File.WriteAllTextAsync(origFile, "ORIGINAL");
        await File.WriteAllTextAsync(testFile, "CONVERTED");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m9", MediaName = "Film", OriginalPath = origFile,
            TestPath = testFile, Status = ConversionStatus.AwaitingReplace,
            CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        // Someone is watching m9 → no swap
        await svc.TryReplaceReadyJobsAsync(new[] { PlayingSession("m9") }, CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.AwaitingReplace, updated.Status);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(origFile)); // untouched
        Assert.True(File.Exists(testFile));

        File.Delete(origFile); File.Delete(testFile);
    }

    [Fact]
    public async Task HandleKeepCallback_deletes_backup_and_marks_approved()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var backup = Path.Combine(tmpDir, $"conv_bak_{Guid.NewGuid()}.original.bak");
        await File.WriteAllTextAsync(backup, "ORIGINAL");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m3", MediaName = "Film", OriginalPath = "/tmp/orig.mkv",
            BackupPath = backup, Status = ConversionStatus.Replaced, CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        await svc.HandleKeepCallbackAsync($"conv:keep:{jobId}:5", 100L, "cb1", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Approved, updated.Status);
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task HandleRevertCallback_restores_backup_and_marks_reverted()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var origFile = Path.Combine(tmpDir, $"conv_orig_{Guid.NewGuid()}.mkv");
        var backup   = Path.Combine(tmpDir, $"conv_bak_{Guid.NewGuid()}.original.bak");
        await File.WriteAllTextAsync(origFile, "CONVERTED"); // converted file is currently live
        await File.WriteAllTextAsync(backup, "ORIGINAL");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m4", MediaName = "Film", OriginalPath = origFile,
            BackupPath = backup, Status = ConversionStatus.Replaced, CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        await svc.HandleRevertCallbackAsync($"conv:revert:{jobId}:5", 100L, "cb2", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Reverted, updated.Status);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(origFile)); // original restored
        Assert.False(File.Exists(backup));                              // backup consumed

        File.Delete(origFile);
    }

    [Fact]
    public async Task RunNextJobAsync_returns_false_when_no_queued_jobs()
    {
        var (_, scopes) = BuildDb();
        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        var result = await svc.RunNextJobAsync(CancellationToken.None);
        Assert.False(result);
    }

    static SessionInfo PlayingSession(string mediaId) => new(
        SessionId: "s", User: "u", Client: "c", Device: "d",
        Playing: true, MediaId: mediaId, Media: "Film", MediaType: "Episode", SeriesName: "",
        SeasonNumber: null, EpisodeNumber: null, Container: "mkv", Method: "Transcode",
        VideoCodec: "hevc", AudioCodec: "aac", TranscodeReasons: Array.Empty<string>(),
        Bitrate: 0, VideoBitDepth: 10);
}

// Minimal IHttpClientFactory for tests (no real HTTP)
file class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
