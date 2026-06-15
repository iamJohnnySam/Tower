namespace Tower;

public class TowerConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();
    public List<ProjectDef> Projects { get; set; } = new();
    public string ServerMonitorConfigPath { get; set; } = "/home/atom/server-monitor/config.json";
    public string MaintenanceScriptPath { get; set; } = "do_maintenance.sh";
    public string MaintenanceLogPath { get; set; } = "maintenance.log";
    public string JellyfinUrl { get; set; } = "http://localhost:8096";
}

public class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ""; // "self" or "pi"
    public string? BaseUrl { get; set; }
}

public class ProjectDef
{
    public string Name { get; set; } = "";
    public string? Service { get; set; }
    public int? Port { get; set; }
    public string? DbPath { get; set; }
    public string? LogDir { get; set; }
    public string? Url { get; set; }
}
