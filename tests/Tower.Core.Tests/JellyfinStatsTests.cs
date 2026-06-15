using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Models;
using Xunit;
namespace Tower.Core.Tests;
public class JellyfinStatsTests {
    static TowerDbContext NewDb() {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o); db.Database.OpenConnection(); db.Database.EnsureCreated(); return db;
    }
    [Fact] public void Top_media_counts_descending() {
        using var db = NewDb();
        db.PlayHistory.AddRange(
          new PlayHistory { StartedAt = DateTime.Now, MediaName = "A", UserName = "john", PlayMethod = "DirectPlay" },
          new PlayHistory { StartedAt = DateTime.Now, MediaName = "A", UserName = "kid",  PlayMethod = "Transcode" },
          new PlayHistory { StartedAt = DateTime.Now, MediaName = "B", UserName = "john", PlayMethod = "DirectPlay" });
        db.SaveChanges();
        var svc = new JellyfinStats(db);
        var top = svc.TopMedia(10);
        Assert.Equal("A", top[0].Name);
        Assert.Equal(2, top[0].Count);
        Assert.Equal(1, svc.TranscodeCount());           // one Transcode row
        Assert.Equal(2, svc.PerUser(10).First(u => u.User == "john").Count);
    }
}
