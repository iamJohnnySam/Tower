using Tower.Core.Models;

namespace Tower.Core.Tuya;

public record EnergySpec(string PowerDps, string VoltageDps, string CurrentDps);
public record SensorReadout(string Label, string Dps, string Unit, double Scale);

public record TuyaCategory(
    string Key,
    string Name,
    string? PowerDps = null,
    string? BrightnessDps = null,
    string? ColorTempDps = null,
    string? ColorDps = null,
    string? WorkModeDps = null,
    int Gangs = 0,
    bool Ac = false,
    EnergySpec? Energy = null,
    IReadOnlyList<SensorReadout>? Sensors = null,
    TuyaDeviceType Legacy = TuyaDeviceType.Plug,
    string? Note = null)
{
    public bool Actuatable => PowerDps != null || Gangs > 0 || Ac;
}

public static class TuyaCategories
{
    public static readonly IReadOnlyList<TuyaCategory> All = new[]
    {
        new TuyaCategory("plug", "Plug", PowerDps: "1", Legacy: TuyaDeviceType.Plug),
        new TuyaCategory("energy_plug", "Energy Plug", PowerDps: "1",
            Energy: new EnergySpec("19", "20", "18"), Legacy: TuyaDeviceType.Plug),
        new TuyaCategory("dimmable_light", "Dimmable Light", PowerDps: "20", BrightnessDps: "22",
            Legacy: TuyaDeviceType.Light),
        new TuyaCategory("color_bulb", "Color Bulb", PowerDps: "20", BrightnessDps: "22",
            ColorTempDps: "23", ColorDps: "24", WorkModeDps: "21", Legacy: TuyaDeviceType.Light),
        new TuyaCategory("switch_1", "Switch (1-gang)", Gangs: 1, Legacy: TuyaDeviceType.Switch1),
        new TuyaCategory("switch_2", "Switch (2-gang)", Gangs: 2, Legacy: TuyaDeviceType.Switch2),
        new TuyaCategory("switch_4", "Switch (4-gang)", Gangs: 4, Legacy: TuyaDeviceType.Switch4),
        new TuyaCategory("ac_remote", "AC / IR Remote", Ac: true, Legacy: TuyaDeviceType.AcRemote),
        new TuyaCategory("presence_sensor", "Presence Sensor",
            Sensors: new[] { new SensorReadout("Presence", "1", "", 1), new SensorReadout("Illuminance", "11", "lux", 1) },
            Legacy: TuyaDeviceType.Sensor),
        new TuyaCategory("climate_sensor", "Climate Sensor",
            Sensors: new[] { new SensorReadout("Temp", "101", "°C", 0.1), new SensorReadout("Humidity", "102", "%", 1) },
            Legacy: TuyaDeviceType.Sensor),
        // Read-only categories: devices with no controls reachable on the current plan.
        new TuyaCategory("hub", "Hub / Gateway", Legacy: TuyaDeviceType.Sensor,
            Note: "Hub device — hosts sub-devices; no direct controls here."),
        new TuyaCategory("ir_ac", "AC Remote (IR)", Legacy: TuyaDeviceType.AcRemote,
            Note: "IR remote — sending commands needs the Tuya IR API (not on your current plan)."),
    };

    static readonly Dictionary<string, TuyaCategory> ByKey = All.ToDictionary(c => c.Key);

    public static TuyaCategory Resolve(string? category, TuyaDeviceType legacy)
    {
        if (category is not null && ByKey.TryGetValue(category, out var c)) return c;
        return legacy switch
        {
            TuyaDeviceType.Light    => ByKey["dimmable_light"],
            TuyaDeviceType.AcRemote => ByKey["ac_remote"],
            TuyaDeviceType.Switch1  => ByKey["switch_1"],
            TuyaDeviceType.Switch2  => ByKey["switch_2"],
            TuyaDeviceType.Switch4  => ByKey["switch_4"],
            TuyaDeviceType.Sensor   => ByKey["presence_sensor"],
            _                       => ByKey["plug"],
        };
    }
}
