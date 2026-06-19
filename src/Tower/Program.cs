using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Tower;
using Tower.Components;
using Tower.MediaBox;
using Tower.Core.Backup;
using Tower.Core.Data;
using Tower.Core.Jellyfin;
using Tower.Core.PiHole;
using Tower.Core.Tuya;
using Tower.Core.Maintenance;
using Tower.Core.Pi;
using Tower.Core.Projects;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;
using Tower.Core.Website;
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

// ── PiHole ───────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<PiHoleClient>();

// ── Website ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new WebsiteOptions
{
    LocalPath     = towerCfg.Website.LocalPath,
    FtpHost       = towerCfg.Website.FtpHost,
    FtpRemotePath = towerCfg.Website.FtpRemotePath,
});

// ── Tuya ─────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<TuyaServiceClient>(c =>
    c.BaseAddress = new Uri("http://localhost:6677/"));
builder.Services.AddScoped<TuyaDeviceService>();

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
builder.Services.AddHttpClient(nameof(DropboxTokenService));
builder.Services.AddSingleton<DropboxTokenService>();
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

// ── Telegram ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<TelegramApi>();
builder.Services.AddScoped<SubscriberService>();
builder.Services.AddSingleton<TelegramHub>();
builder.Services.AddHostedService<TelegramPollWorker>();

// ── gRPC ─────────────────────────────────────────────────────────────────────
builder.Services.AddGrpc();

// ── MediaBox control client (Task 7) ─────────────────────────────────────────
// Singleton: caches the GrpcChannel/client across the process lifetime. Never throws —
// see MediaBoxClient for the never-throws/safe-default design. MediaBoxScheduler (Task 8)
// and the MediaBox tab (Tasks 9-10) consume this; nothing wired to it yet runs unless
// Tower:MediaBoxOrchestrate is explicitly turned on.
builder.Services.AddSingleton<MediaBoxClient>();

// ── MediaBox scheduler (Task 8) ──────────────────────────────────────────────
// Idle unless Tower:MediaBoxOrchestrate is true (the explicit-flip safety gate, same
// pattern as telegram.active). See MediaBoxScheduler for the wired jobs + the dropped
// Watchlist job (no trigger RPC exists for it).
builder.Services.AddHostedService<MediaBoxScheduler>();

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

// ── Dropbox OAuth callback ────────────────────────────────────────────────────
app.MapGet("/dropbox/callback", async (
    string? code, string? error,
    DropboxTokenService tokenSvc) =>
{
    if (!string.IsNullOrEmpty(error))
        return Results.Redirect("/settings?dropbox_error=" + Uri.EscapeDataString(error));

    if (string.IsNullOrEmpty(code))
        return Results.Redirect("/settings?dropbox_error=no_code");

    var (ok, err) = await tokenSvc.ExchangeCodeAsync(code);

    return ok
        ? Results.Redirect("/settings?dropbox=connected")
        : Results.Redirect("/settings?dropbox_error=" + Uri.EscapeDataString(err ?? "unknown"));
});

app.Run();
