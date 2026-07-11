using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Pi;
using Tower.Core.Tuya;

namespace Tower.Core.Automations;

/// <summary>
/// One step of an automation.
/// Kind: "tuya" (Target = TuyaDevice.DeviceId) or "pi" (Target = DeviceConfig.Id; only supports shutdown).
/// Disabled steps stay in the automation but are skipped at run time. Defaults false so
/// steps saved before this field existed keep running.
/// </summary>
public record AutomationAction(string Kind, string Target, string TargetName, bool On, int? Temp, bool Disabled = false);

public class AutomationService(
    TowerDbContext db,
    TuyaServiceClient tuya,
    PiAgentClient pi,
    IOptions<TowerConfig> cfg)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<AutomationAction> ParseActions(string json)
    {
        try { return JsonSerializer.Deserialize<List<AutomationAction>>(json, Json) ?? []; }
        catch { return []; }
    }

    public static string SerializeActions(List<AutomationAction> actions)
        => JsonSerializer.Serialize(actions, Json);

    public Task<List<Automation>> ListAsync()
        => db.Automations.OrderBy(a => a.Name).ToListAsync();

    public async Task<List<TuyaDevice>> ListTuyaDevicesAsync()
    {
        var all = await db.TuyaDevices.OrderBy(d => d.Room).ThenBy(d => d.Name).ToListAsync();
        return all.Where(d => TuyaCategories.Resolve(d.Category, d.DeviceType).Actuatable).ToList();
    }

    public List<DeviceConfig> PiDevices()
        => cfg.Value.Devices.Where(d => d.Kind == "pi" && !string.IsNullOrWhiteSpace(d.BaseUrl)).ToList();

    public async Task SaveAsync(Automation a)
    {
        if (a.Id == 0) db.Automations.Add(a);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var a = await db.Automations.FindAsync(id);
        if (a is not null)
        {
            db.Automations.Remove(a);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Runs the automation whose name matches case-insensitively. Returns a human-readable summary.</summary>
    public async Task<string> RunByNameAsync(string name)
    {
        var all  = await ListAsync();
        var auto = all.FirstOrDefault(a =>
            string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (auto is null)
        {
            return all.Count == 0
                ? $"No automation named \"{name}\" — none are set up yet."
                : $"No automation named \"{name}\". Available: {string.Join(", ", all.Select(a => a.Name))}";
        }
        return await RunAsync(auto);
    }

    public async Task<string> RunAsync(Automation auto)
    {
        var actions = ParseActions(auto.ActionsJson);
        if (actions.Count == 0) return $"{auto.Name}: no actions configured.";

        var devs = await db.TuyaDevices.ToDictionaryAsync(d => d.DeviceId, d => d);

        var results = new List<string>();
        foreach (var act in actions)
        {
            if (act.Disabled)
            {
                results.Add($"⏸ {act.TargetName} (disabled)");
                continue;
            }

            bool ok;
            string what;

            if (act.Kind == "pi")
            {
                var dev = cfg.Value.Devices.FirstOrDefault(d => d.Id == act.Target);
                ok   = dev?.BaseUrl is not null && await pi.ShutdownAsync(dev.BaseUrl);
                what = $"{act.TargetName} shutdown";
            }
            else
            {
                var cat = devs.TryGetValue(act.Target, out var td)
                    ? TuyaCategories.Resolve(td.Category, td.DeviceType) : null;
                if (cat?.Ac == true)
                {
                    ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                        Ac: new AcCommandPayload(Power: act.On, Temp: act.On && act.Temp is int tc ? Math.Clamp(tc, 16, 30) : null)));
                    what = act.On
                        ? $"{act.TargetName} ON{(act.Temp is int t ? $" {Math.Clamp(t, 16, 30)}°C" : "")}"
                        : $"{act.TargetName} OFF";
                }
                else
                {
                    var key = cat?.PowerDps ?? "1";
                    ok = await tuya.SendCommandAsync(act.Target, new TuyaCommandRequest(
                        Dps: new Dictionary<string, object> { [key] = act.On }));
                    what = $"{act.TargetName} {(act.On ? "ON" : "OFF")}";
                }
            }

            results.Add($"{(ok ? "✅" : "❌")} {what}");
        }

        return $"{auto.Name}:\n{string.Join("\n", results)}";
    }
}
