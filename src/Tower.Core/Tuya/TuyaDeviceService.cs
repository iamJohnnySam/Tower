using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Settings;

namespace Tower.Core.Tuya;

public class TuyaDeviceService(TuyaServiceClient client, TowerDbContext db, SettingsService settings)
{
    public bool IsConfigured => settings.IsConfigured("tuya.api_key");

    public async Task<List<TuyaDeviceView>> GetDeviceViewsAsync()
    {
        var live = await client.GetDevicesAsync();
        var liveById = live.ToDictionary(d => d.Id);

        var dbDevices = await db.TuyaDevices
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync();

        return dbDevices.Select(d =>
        {
            liveById.TryGetValue(d.DeviceId, out var dto);
            return new TuyaDeviceView(
                Id:         d.Id,
                DeviceId:   d.DeviceId,
                Name:       d.Name,
                DeviceType: d.DeviceType,
                Room:       d.Room,
                SortOrder:  d.SortOrder,
                Reachable:  dto is not null,
                Dps:        dto?.Dps ?? []);
        }).ToList();
    }

    public async Task<(List<ScannedDevice> Devices, string? Error)> ScanAsync()
    {
        var key    = settings.Get("tuya.api_key")    ?? "";
        var secret = settings.Get("tuya.api_secret") ?? "";
        var region = settings.Get("tuya.region")     ?? "us";

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            return ([], "Tuya API key and secret must be set in Settings before scanning.");

        var devices = await client.ScanAsync(key, secret, region);
        return devices.Count == 0
            ? ([], "Scan returned no devices. Check your API credentials and ensure devices are on the same network.")
            : (devices, null);
    }

    public async Task SaveDeviceAsync(TuyaDevice device)
    {
        var existing = await db.TuyaDevices
            .FirstOrDefaultAsync(d => d.DeviceId == device.DeviceId);

        if (existing is null)
        {
            db.TuyaDevices.Add(device);
        }
        else
        {
            existing.Name       = device.Name;
            existing.DeviceType = device.DeviceType;
            existing.Room       = device.Room;
            existing.SortOrder  = device.SortOrder;
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteDeviceAsync(int id)
    {
        var d = await db.TuyaDevices.FindAsync(id);
        if (d is not null)
        {
            db.TuyaDevices.Remove(d);
            await db.SaveChangesAsync();
        }
    }

    public Task<bool> SendCommandAsync(string deviceId, TuyaCommandRequest cmd)
        => client.SendCommandAsync(deviceId, cmd);
}
