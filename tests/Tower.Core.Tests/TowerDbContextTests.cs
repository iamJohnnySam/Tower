using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
using Xunit;
public class TowerDbContextTests {
    static TowerDbContext NewDb() {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o); db.Database.OpenConnection(); db.Database.EnsureCreated(); return db;
    }
    [Fact] public void Can_persist_and_read_setting() {
        using var db = NewDb();
        db.Settings.Add(new Setting { Key = "jellyfin.api_key", Value = "abc" });
        db.SaveChanges();
        Assert.Equal("abc", db.Settings.Find("jellyfin.api_key")!.Value);
    }
    [Fact] public void Can_persist_and_read_cpu_profile_slot() {
        using var db = NewDb();
        db.CpuProfile.Add(new CpuProfileSlot { Slot = 42, AvgCpu = 35.5, SampleCount = 10 });
        db.SaveChanges();
        var slot = db.CpuProfile.Find(42);
        Assert.NotNull(slot);
        Assert.Equal(35.5, slot.AvgCpu);
        Assert.Equal(10, slot.SampleCount);
    }
}
