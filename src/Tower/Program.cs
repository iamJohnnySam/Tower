using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Tower;
using Tower.Components;
using Tower.Core.Backup;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.Maintenance;
using Tower.Core.Pi;
using Tower.Core.Projects;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Workers;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: 8888 = HTTP/1.1+2 for Blazor; 5601 = HTTP/2 h2c for gRPC
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(8888);  // Blazor (HTTP/1.1 + HTTP/2)
    o.ListenAnyIP(5601, l => l.Protocols = HttpProtocols.Http2);  // gRPC h2c
});

// ── Strongly-typed config ────────────────────────────────────────────────────
var towerCfg = builder.Configuration.GetSection("Tower").Get<TowerConfig>() ?? new TowerConfig();
builder.Services.Configure<TowerConfig>(builder.Configuration.GetSection("Tower"));

// ── Data / Settings ──────────────────────────────────────────────────────────
builder.Services.AddDbContext<TowerDbContext>(o =>
    o.UseSqlite("Data Source=tower.db").AddInterceptors(new SqlitePragmaInterceptor()));
builder.Services.AddScoped<SettingsService>();

// ── Live state & collectors ──────────────────────────────────────────────────
builder.Services.AddSingleton<LiveState>();
builder.Services.AddHttpClient<PiAgentClient>();

// ── Jellyfin ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new JellyfinOptions { JellyfinUrl = towerCfg.JellyfinUrl });
builder.Services.AddHttpClient<JellyfinClient>();
builder.Services.AddScoped<JellyfinStats>();

// ── Projects + Backup ────────────────────────────────────────────────────────
builder.Services.AddSingleton(new ProjectsOptions
{
    Projects = towerCfg.Projects
        .Select(p => new ProjectDefCore(p.Name, p.Service, p.Port, p.DbPath, p.LogDir, p.Url))
        .ToList()
});
builder.Services.AddHostedService<ProjectsWorker>();
builder.Services.AddHttpClient<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();

// ── Maintenance ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new MaintenanceOptions
{
    ScriptPath = towerCfg.MaintenanceScriptPath,
    LogPath    = towerCfg.MaintenanceLogPath,
});
builder.Services.AddSingleton<MaintenanceRunner>();

// ── Background workers ───────────────────────────────────────────────────────
builder.Services.AddHostedService<JellyfinWorker>();
builder.Services.AddHostedService<StatsWorker>();
builder.Services.AddHostedService<SmartWorker>();
builder.Services.AddHostedService<DiskUsageWorker>();
builder.Services.AddHostedService<CpuProfileRecorder>();
builder.Services.AddHostedService<MaintenanceScheduler>();
builder.Services.AddHostedService<SizeMonitorWorker>();

// ── gRPC ─────────────────────────────────────────────────────────────────────
builder.Services.AddGrpc();

// ── Blazor ───────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ── DB init + one-time key migration ─────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
    db.Database.EnsureCreated();

    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
    try
    {
        var p = towerCfg.ServerMonitorConfigPath;
        if (File.Exists(p))
        {
            foreach (var kv in KeyMigration.FromServerMonitorConfig(File.ReadAllText(p)))
                if (!settings.IsConfigured(kv.Key))
                    settings.Set(kv.Key, kv.Value);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[key-migration] {ex.Message}");
    }
}

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapGrpcService<TowerTelegramService>();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
