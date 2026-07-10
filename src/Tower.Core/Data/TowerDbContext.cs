using Microsoft.EntityFrameworkCore;
using Tower.Core.Models;
namespace Tower.Core.Data;
public class TowerDbContext(DbContextOptions<TowerDbContext> options) : DbContext(options) {
    public DbSet<PlayHistory> PlayHistory => Set<PlayHistory>();
    public DbSet<CpuProfileSlot> CpuProfile => Set<CpuProfileSlot>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<TelegramSubscriber> Subscribers => Set<TelegramSubscriber>();
    public DbSet<TelegramMessage> Messages => Set<TelegramMessage>();
    public DbSet<ProjectConfig> Projects => Set<ProjectConfig>();
    public DbSet<TuyaDevice>    TuyaDevices  => Set<TuyaDevice>();
    public DbSet<ConversionJob> ConversionJobs => Set<ConversionJob>();
    public DbSet<TodoItem> Todos => Set<TodoItem>();
    public DbSet<Automation> Automations => Set<Automation>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<SolarSnapshot> SolarSnapshots => Set<SolarSnapshot>();
    public DbSet<SolarReport> SolarReports => Set<SolarReport>();
    public DbSet<SolarWeather> SolarWeather => Set<SolarWeather>();
    public DbSet<SolarAlarm> SolarAlarms => Set<SolarAlarm>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<CpuProfileSlot>().HasKey(x => x.Slot);
        b.Entity<CpuProfileSlot>().Property(x => x.Slot).ValueGeneratedNever();
        b.Entity<Setting>().HasKey(x => x.Key);
        b.Entity<Setting>().Property(x => x.Key).ValueGeneratedNever();
        b.Entity<TelegramSubscriber>().HasKey(x => x.ChatId);
        b.Entity<TelegramSubscriber>().Property(x => x.ChatId).ValueGeneratedNever();
        b.Entity<PlayHistory>().HasIndex(x => x.StartedAt);
        b.Entity<PlayHistory>().HasIndex(x => x.MediaName);
        b.Entity<TelegramMessage>().HasIndex(x => x.ChatId);
        b.Entity<ConversionJob>().HasIndex(x => x.MediaId).IsUnique();
        b.Entity<SolarSnapshot>().HasIndex(x => x.CapturedAt);
        b.Entity<SolarReport>().HasIndex(x => x.GmailMessageId).IsUnique();
        b.Entity<SolarReport>().HasIndex(x => new { x.ReportType, x.PeriodEnd });
        b.Entity<SolarWeather>().HasKey(x => x.Date);
        b.Entity<SolarWeather>().Property(x => x.Date).ValueGeneratedNever();
        b.Entity<SolarAlarm>().HasIndex(x => x.GmailMessageId).IsUnique();
        b.Entity<SolarAlarm>().HasIndex(x => x.AlarmDate);
    }
}
