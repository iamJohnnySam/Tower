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
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<CpuProfileSlot>().HasKey(x => x.Slot);
        b.Entity<CpuProfileSlot>().Property(x => x.Slot).ValueGeneratedNever();
        b.Entity<Setting>().HasKey(x => x.Key);
        b.Entity<TelegramSubscriber>().HasKey(x => x.ChatId);
        b.Entity<TelegramSubscriber>().Property(x => x.ChatId).ValueGeneratedNever();
        b.Entity<PlayHistory>().HasIndex(x => x.StartedAt);
        b.Entity<PlayHistory>().HasIndex(x => x.MediaName);
    }
}
