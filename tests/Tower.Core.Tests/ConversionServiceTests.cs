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
    public async Task HandleConvertCallback_updates_job_to_queued()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "media-xyz", MediaName = "Film", OriginalPath = "/tmp/film.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now, AlertMessageId = 99
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        // Invoke internal callback handler directly
        await svc.HandleConvertCallbackAsync($"conv:convert:{jobId}", 100L, "cb1", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Queued, updated.Status);
    }

    [Fact]
    public async Task HandleIgnoreCallback_updates_job_to_ignored()
    {
        var (_, scopes) = BuildDb();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "media-ign", MediaName = "Film2", OriginalPath = "/tmp/film2.mkv",
            Status = ConversionStatus.Pending, CreatedAt = DateTime.Now, AlertMessageId = 88
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), "/tmp/conv-test");

        await svc.HandleIgnoreCallbackAsync($"conv:ignore:{jobId}", 100L, "cb2", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Ignored, updated.Status);
    }

    [Fact]
    public async Task ApproveAsync_moves_test_file_to_original_path()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var testFile = Path.Combine(tmpDir, $"conv_test_{Guid.NewGuid()}.mkv");
        var origFile = Path.Combine(tmpDir, $"conv_orig_{Guid.NewGuid()}.mkv");
        await File.WriteAllTextAsync(testFile, "fake-video-data");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m1", MediaName = "Film", OriginalPath = origFile,
            TestPath = testFile, Status = ConversionStatus.AwaitingApproval,
            CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        await svc.HandleApproveCallbackAsync($"conv:approve:{jobId}:777", 100L, "cb3", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Approved, updated.Status);
        Assert.True(File.Exists(origFile));
        Assert.False(File.Exists(testFile));

        // cleanup
        File.Delete(origFile);
    }

    [Fact]
    public async Task RejectAsync_deletes_test_file()
    {
        var (_, scopes) = BuildDb();
        var tmpDir = Path.GetTempPath();
        var testFile = Path.Combine(tmpDir, $"conv_reject_{Guid.NewGuid()}.mkv");
        await File.WriteAllTextAsync(testFile, "fake-video-data");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var job = new ConversionJob
        {
            MediaId = "m2", MediaName = "Film2", OriginalPath = "/tmp/orig.mkv",
            TestPath = testFile, Status = ConversionStatus.AwaitingApproval,
            CreatedAt = DateTime.Now
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();
        int jobId = job.Id;

        var hub = BuildHub(scopes);
        var svc = new ConversionService(scopes, hub,
            new JellyfinOptions { JellyfinUrl = "http://localhost:8096" },
            new TestHttpClientFactory(), tmpDir);

        await svc.HandleRejectCallbackAsync($"conv:reject:{jobId}:888", 100L, "cb4", CancellationToken.None);

        using var scope2 = scopes.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<TowerDbContext>();
        var updated = db2.ConversionJobs.Find(jobId)!;
        Assert.Equal(ConversionStatus.Rejected, updated.Status);
        Assert.False(File.Exists(testFile));
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
}

// Minimal IHttpClientFactory for tests (no real HTTP)
file class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
