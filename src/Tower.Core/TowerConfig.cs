namespace Tower;

public class TowerConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();
    public List<ProjectDef> Projects { get; set; } = new();
    public string ServerMonitorConfigPath { get; set; } = "/home/atom/server-monitor/config.json";
    public string MaintenanceScriptPath { get; set; } = "do_maintenance.sh";
    public string MaintenanceLogPath { get; set; } = "maintenance.log";
    public string JellyfinUrl { get; set; } = "http://localhost:8096";
    public string MediaBoxGrpcUrl { get; set; } = "http://localhost:5602";
    public bool MediaBoxOrchestrate { get; set; } = false;
    public MediaBoxJobsConfig MediaBoxJobs { get; set; } = new();
    public WebsiteConfig Website { get; set; } = new();
    public string ConversionTestPath { get; set; } = "/molecule/Media/ConversionTest";
}

/// <summary>
/// Job cadences ported from MediaBox's own appsettings.json ("MediaBox" section) so Tower
/// reproduces today's intervals exactly when it takes over scheduling at cutover (Task 11):
///   RssFeedCheckMinutes=30, DownloadOrganizerMinutes=10, TransmissionCheckMinutes=5,
///   MediaScanHours=12, WatchlistCheckHours=6 (-> 360 minutes here).
/// YouTubeMinutes has no direct MediaBox equivalent — MediaBox schedules YouTube downloads
/// per-source at fixed times-of-day (NewsSources[].DownloadTime), not a polling interval — so
/// this uses a reasonable poll cadence for Tower's scheduler to check for due sources.
/// </summary>
public class MediaBoxJobsConfig
{
    public int RssCheckMinutes { get; set; } = 30;
    public int OrganizeMinutes { get; set; } = 10;
    public int TransmissionPollMinutes { get; set; } = 5;
    public int ScanHours { get; set; } = 12;
    public int YouTubeMinutes { get; set; } = 60;
    public int WatchlistMinutes { get; set; } = 360;
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

public class WebsiteConfig
{
    public string LocalPath { get; set; } = "/home/atom/dev/iamJohnnySam.com/public_html";
    public string FtpHost { get; set; } = "x11.x10hosting.com";
    public string FtpRemotePath { get; set; } = "/public_html";
}
