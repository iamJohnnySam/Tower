// tests/Tower.Core.Tests/ConversionJobTests.cs
using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;

namespace Tower.Core.Tests;

public class ConversionJobTests
{
    static TowerDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void Can_persist_and_read_conversion_job()
    {
        using var db = NewDb();
        var job = new ConversionJob
        {
            MediaId         = "abc-123",
            MediaName       = "The Dark Knight",
            OriginalPath    = "/molecule/Media/Movies/The Dark Knight (2008)/tdknight.mkv",
            Status          = ConversionStatus.Queued,
            TranscodeReasons = "VideoCodecNotSupported",
            CreatedAt       = new DateTime(2026, 6, 20, 3, 0, 0),
        };
        db.ConversionJobs.Add(job);
        db.SaveChanges();

        var loaded = db.ConversionJobs.Single(j => j.MediaId == "abc-123");
        Assert.Equal("The Dark Knight", loaded.MediaName);
        Assert.Equal(ConversionStatus.Queued, loaded.Status);
        Assert.Equal("/molecule/Media/Movies/The Dark Knight (2008)/tdknight.mkv", loaded.OriginalPath);
    }

    [Fact]
    public void MediaId_unique_index_prevents_duplicates()
    {
        using var db = NewDb();
        db.ConversionJobs.Add(new ConversionJob { MediaId = "dupe", MediaName = "A", Status = ConversionStatus.Pending, CreatedAt = DateTime.Now });
        db.SaveChanges();
        db.ConversionJobs.Add(new ConversionJob { MediaId = "dupe", MediaName = "B", Status = ConversionStatus.Pending, CreatedAt = DateTime.Now });
        Assert.ThrowsAny<Exception>(() => db.SaveChanges());
    }

    [Fact]
    public void All_status_values_defined()
    {
        var values = Enum.GetNames<ConversionStatus>();
        Assert.Contains("Pending", values);
        Assert.Contains("Queued", values);
        Assert.Contains("Converting", values);
        Assert.Contains("AwaitingApproval", values);
        Assert.Contains("Approved", values);
        Assert.Contains("Rejected", values);
        Assert.Contains("Failed", values);
        Assert.Contains("Ignored", values);
    }
}
