using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Tower;
using Tower.Components;
using Tower.MediaBox;
using Tower.Core.Backup;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Firewall;
using Tower.Core.Jellyfin;
using Tower.Core.PiHole;
using Tower.Core.Tuya;
using Tower.Core.Maintenance;
using Tower.Core.Pi;
using Tower.Core.Projects;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;
using Tower.Core.Todo;
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

// ── Firewall (read-only ufw status) ──────────────────────────────────────────
builder.Services.AddScoped<FirewallService>();

// ── Website ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new WebsiteOptions
{
    LocalPath     = towerCfg.Website.LocalPath,
    FtpHost       = towerCfg.Website.FtpHost,
    FtpRemotePath = towerCfg.Website.FtpRemotePath,
});
builder.Services.AddScoped<FtpSyncService>();

// ── Tuya ─────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<TuyaServiceClient>(c =>
    c.BaseAddress = new Uri("http://localhost:6677/"));
builder.Services.AddScoped<TuyaDeviceService>();

// ── Jellyfin ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new JellyfinOptions { JellyfinUrl = towerCfg.JellyfinUrl });
builder.Services.AddHttpClient<JellyfinClient>();
builder.Services.AddScoped<JellyfinStats>();

// ── Conversion (Task 4+) ──────────────────────────────────────────────────────
builder.Services.AddSingleton(sp => new ConversionService(
    scopes: sp.GetRequiredService<IServiceScopeFactory>(),
    telegram: sp.GetRequiredService<TelegramHub>(),
    jellyfinOpts: sp.GetRequiredService<JellyfinOptions>(),
    httpFactory: sp.GetRequiredService<IHttpClientFactory>(),
    jellyfinLogger: sp.GetRequiredService<ILogger<JellyfinClient>>(),
    conversionTestPath: towerCfg.ConversionTestPath ?? "/tmp"
));

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
builder.Services.AddHostedService<ConversionScheduler>();
builder.Services.AddHostedService<SizeMonitorWorker>();

// ── Telegram ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<TelegramApi>();
builder.Services.AddScoped<SubscriberService>();
builder.Services.AddSingleton<TelegramHub>();
builder.Services.AddHostedService<TelegramPollWorker>();

// ── Secrets (password manager) ───────────────────────────────────────────────
builder.Services.AddScoped<Tower.Core.Secrets.VaultSession>();
builder.Services.AddScoped<Tower.Core.Secrets.SecretService>();

// ── Todo ─────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TodoService>();
builder.Services.AddSingleton<TodoTelegramHandler>();
builder.Services.AddHostedService<TodoReminderWorker>();

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

// ── Solar (SolaX API + Gmail report import) ──────────────────────────────────
builder.Services.AddHttpClient<Tower.Core.Solar.SolaxClient>();          // typed; page + worker resolve from scope
builder.Services.AddSingleton<Tower.Core.Gmail.GmailTokenService>();
builder.Services.AddHttpClient(nameof(Tower.Core.Gmail.GmailTokenService)); // named client used inside GmailTokenService
builder.Services.AddHttpClient<Tower.Core.Gmail.GmailReader>();          // typed; page + worker resolve from scope
builder.Services.AddSingleton<Tower.Core.Workers.SolarMailWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tower.Core.Workers.SolarMailWorker>());
builder.Services.AddHostedService<Tower.Core.Workers.SolaxPollWorker>();

// ── Blazor ───────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ── DB init + one-time key migration ─────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated does not alter existing schemas — create new tables manually
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ConversionJobs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MediaId TEXT NOT NULL,
            MediaName TEXT NOT NULL,
            OriginalPath TEXT NOT NULL,
            TestPath TEXT,
            Status INTEGER NOT NULL DEFAULT 0,
            TranscodeReasons TEXT,
            CreatedAt TEXT NOT NULL,
            StartedAt TEXT,
            CompletedAt TEXT,
            ErrorMessage TEXT,
            AlertMessageId INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_ConversionJobs_MediaId ON ConversionJobs (MediaId);
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS Todos (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Deadline TEXT,
            Done INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            DoneAt TEXT,
            TelegramMessageId INTEGER
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS Secrets (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Project TEXT NOT NULL,
            Label TEXT NOT NULL,
            Value TEXT NOT NULL,
            Notes TEXT,
            UpdatedAt TEXT NOT NULL
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS SolarSnapshots (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CapturedAt TEXT NOT NULL,
            UploadTime TEXT,
            AcPower REAL NOT NULL DEFAULT 0,
            YieldToday REAL NOT NULL DEFAULT 0,
            YieldTotal REAL NOT NULL DEFAULT 0,
            FeedInPower REAL NOT NULL DEFAULT 0,
            FeedInEnergy REAL NOT NULL DEFAULT 0,
            ConsumeEnergy REAL NOT NULL DEFAULT 0,
            Soc REAL NOT NULL DEFAULT 0,
            BatPower REAL NOT NULL DEFAULT 0,
            PowerDc1 REAL NOT NULL DEFAULT 0,
            InverterStatus TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_SolarSnapshots_CapturedAt ON SolarSnapshots (CapturedAt);
        CREATE TABLE IF NOT EXISTS SolarReports (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ReportType INTEGER NOT NULL,
            PeriodStart TEXT NOT NULL,
            PeriodEnd TEXT NOT NULL,
            PeriodYieldKWh REAL NOT NULL DEFAULT 0,
            TotalYieldKWh REAL NOT NULL DEFAULT 0,
            PeriodEarningsLkr TEXT NOT NULL DEFAULT '0',
            TotalEarningsLkr TEXT NOT NULL DEFAULT '0',
            Co2SavedTons REAL NOT NULL DEFAULT 0,
            AlarmQuantity INTEGER NOT NULL DEFAULT 0,
            GmailMessageId TEXT NOT NULL,
            ImportedAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_SolarReports_GmailMessageId ON SolarReports (GmailMessageId);
    ");

    // Reset any jobs stuck in Converting state from a previous run
    var stuckJobs = db.ConversionJobs
        .Where(j => j.Status == Tower.Core.Models.ConversionStatus.Converting)
        .ToList();
    foreach (var j in stuckJobs)
    {
        j.Status = Tower.Core.Models.ConversionStatus.Queued;
        j.StartedAt = null;
    }
    if (stuckJobs.Count > 0)
        db.SaveChanges();

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

// Wire Telegram callbacks for conversion pipeline
var convSvc = app.Services.GetRequiredService<ConversionService>();
var telegramHub = app.Services.GetRequiredService<TelegramHub>();
convSvc.RegisterCallbacks(telegramHub);
app.Services.GetRequiredService<TodoTelegramHandler>().Register();

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

// ── FTP push (called by blog editor after image upload) ──────────────────────
app.MapPost("/api/ftp-push", async (
    FtpPushRequest req,
    FtpSyncService ftpSvc,
    CancellationToken ct) =>
{
    if (req.Files is null || req.Files.Count == 0)
        return Results.BadRequest(new { error = "No files specified" });

    var progress = new Progress<string>();
    try
    {
        var (uploaded, _, failed) = await ftpSvc.SyncAsync(req.Files, [], progress, ct);
        return Results.Ok(new { uploaded, failed });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ── Full scan-and-sync (triggered by Claude after each git push) ─────────────
app.MapPost("/api/website/sync", async (
    FtpSyncService ftpSvc,
    CancellationToken ct) =>
{
    try
    {
        var scan     = await ftpSvc.ScanAsync();
        var toUpload = scan.ToUpload.Select(f => f.Path).ToList();
        var (uploaded, _, failed) = await ftpSvc.SyncAsync(toUpload, [], new Progress<string>(), ct);
        return Results.Ok(new { scanned = scan.ToUpload.Count + scan.UpToDate, uploaded, failed });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

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

// ── Gmail OAuth callback ──────────────────────────────────────────────────────
app.MapGet("/gmail/callback", async (
    string? code, string? error,
    Tower.Core.Gmail.GmailTokenService tokenSvc) =>
{
    if (!string.IsNullOrEmpty(error))
        return Results.Redirect("/gmail?error=" + Uri.EscapeDataString(error));
    if (string.IsNullOrEmpty(code))
        return Results.Redirect("/gmail?error=no_code");
    var (ok, err) = await tokenSvc.ExchangeCodeAsync(code);
    return ok
        ? Results.Redirect("/gmail?connected=1")
        : Results.Redirect("/gmail?error=" + Uri.EscapeDataString(err ?? "unknown"));
});

app.Run();

record FtpPushRequest(List<string> Files);
