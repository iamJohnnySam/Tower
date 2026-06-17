using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Tuya;

namespace Tower.Core.Tests;

public class TuyaDeviceServiceTests
{
    private static TowerDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task SaveDeviceAsync_inserts_new_device()
    {
        using var db = NewDb();
        var svc = new TuyaDeviceService(null!, db, null!);

        await svc.SaveDeviceAsync(new TuyaDevice
        {
            DeviceId   = "abc123",
            Name       = "Test Plug",
            DeviceType = TuyaDeviceType.Plug,
            Room       = "Kitchen",
            SortOrder  = 1,
        });

        var d = db.TuyaDevices.Single(x => x.DeviceId == "abc123");
        Assert.Equal("Test Plug", d.Name);
        Assert.Equal(TuyaDeviceType.Plug, d.DeviceType);
        Assert.Equal("Kitchen", d.Room);
    }

    [Fact]
    public async Task SaveDeviceAsync_updates_existing_device()
    {
        using var db = NewDb();
        var svc = new TuyaDeviceService(null!, db, null!);

        await svc.SaveDeviceAsync(new TuyaDevice
        {
            DeviceId = "abc123", Name = "Old Name", DeviceType = TuyaDeviceType.Plug
        });
        await svc.SaveDeviceAsync(new TuyaDevice
        {
            DeviceId = "abc123", Name = "New Name", DeviceType = TuyaDeviceType.Light, Room = "Bedroom"
        });

        Assert.Equal(1, db.TuyaDevices.Count());
        var d = db.TuyaDevices.Single();
        Assert.Equal("New Name", d.Name);
        Assert.Equal(TuyaDeviceType.Light, d.DeviceType);
        Assert.Equal("Bedroom", d.Room);
    }

    [Fact]
    public async Task DeleteDeviceAsync_removes_device()
    {
        using var db = NewDb();
        var svc = new TuyaDeviceService(null!, db, null!);

        await svc.SaveDeviceAsync(new TuyaDevice
        {
            DeviceId = "abc123", Name = "Plug", DeviceType = TuyaDeviceType.Plug
        });
        var id = db.TuyaDevices.Single().Id;

        await svc.DeleteDeviceAsync(id);

        Assert.Empty(db.TuyaDevices);
    }

    [Fact]
    public void IsConfigured_returns_false_when_api_key_not_set()
    {
        using var db = NewDb();
        // SettingsService with empty DB: IsConfigured("tuya.api_key") = false
        var settingsOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite("DataSource=:memory:").Options;
        var settingsDb = new TowerDbContext(settingsOpts);
        settingsDb.Database.OpenConnection();
        settingsDb.Database.EnsureCreated();

        var settings = new Tower.Core.Settings.SettingsService(settingsDb);
        var svc = new TuyaDeviceService(null!, db, settings);

        Assert.False(svc.IsConfigured);
    }
}
