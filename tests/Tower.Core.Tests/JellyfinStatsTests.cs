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
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);
        db.PlayHistory.AddRange(
          new PlayHistory { StartedAt = t0,                   MediaName = "A", UserName = "john", PlayMethod = "DirectPlay" },
          new PlayHistory { StartedAt = t0.AddMinutes(1),     MediaName = "A", UserName = "kid",  PlayMethod = "Transcode" },
          new PlayHistory { StartedAt = t0.AddMinutes(2),     MediaName = "B", UserName = "john", PlayMethod = "DirectPlay" });
        db.SaveChanges();
        var svc = new JellyfinStats(db);
        var top = svc.TopMedia(10);
        Assert.Equal("A", top[0].Name);
        Assert.Equal(2, top[0].Count);
        Assert.Equal(1, svc.TranscodeCount());           // one Transcode row
        Assert.Equal(2, svc.PerUser(10).First(u => u.User == "john").Count);
        // TotalPlays counts all rows
        Assert.Equal(3, svc.TotalPlays());
        // Recent returns newest-first, limited to n
        var recent = svc.Recent(2);
        Assert.Equal(2, recent.Count);
        Assert.True(recent[0].StartedAt >= recent[1].StartedAt); // newest first
        Assert.Equal("B", recent[0].MediaName);                  // t0+2m is the newest row
    }
}
