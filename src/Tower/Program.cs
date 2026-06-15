using Microsoft.EntityFrameworkCore;
using Tower;
using Tower.Components;
using Tower.Core.Data;
using Tower.Core.Maintenance;
using Tower.Core.Pi;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Workers;

var builder = WebApplication.CreateBuilder(args);

// Bind to HTTP only on 8888 (LAN/Tailscale internal tool; no HTTPS needed)
builder.WebHost.UseUrls("http://0.0.0.0:8888");

// ── Strongly-typed config ────────────────────────────────────────────────────
var towerCfg = builder.Configuration.GetSection("Tower").Get<TowerConfig>() ?? new TowerConfig();
builder.Services.Configure<TowerConfig>(builder.Configuration.GetSection("Tower"));

// ── Data / Settings ──────────────────────────────────────────────────────────
builder.Services.AddDbContext<TowerDbContext>(o => o.UseSqlite("Data Source=tower.db"));
builder.Services.AddScoped<SettingsService>();

// ── Live state & collectors ──────────────────────────────────────────────────
builder.Services.AddSingleton<LiveState>();
builder.Services.AddHttpClient<PiAgentClient>();

// ── Maintenance ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new MaintenanceOptions
{
    ScriptPath = towerCfg.MaintenanceScriptPath,
    LogPath    = towerCfg.MaintenanceLogPath,
});
builder.Services.AddSingleton<MaintenanceRunner>();

// ── Background workers ───────────────────────────────────────────────────────
builder.Services.AddHostedService<StatsWorker>();
builder.Services.AddHostedService<SmartWorker>();
builder.Services.AddHostedService<DiskUsageWorker>();
builder.Services.AddHostedService<CpuProfileRecorder>();
builder.Services.AddHostedService<MaintenanceScheduler>();
builder.Services.AddHostedService<SizeMonitorWorker>();

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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
