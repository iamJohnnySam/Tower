using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Settings;
using Tower.Core.Telegram;
using Xunit;
namespace Tower.Core.Tests;
public class SubscriberServiceTests {
    static TowerDbContext NewDb() {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o); db.Database.OpenConnection(); db.Database.EnsureCreated(); return db;
    }
    static SubscriberService Svc(TowerDbContext db) => new(db, new SettingsService(db));

    [Fact]
    public void AddOrReactivate_inserts_new_subscriber() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.AddOrReactivate(111L, "Alice");
        var sub = db.Subscribers.Find(111L);
        Assert.NotNull(sub);
        Assert.Equal("Alice", sub.Name);
        Assert.Equal("active", sub.Status);
    }

    [Fact]
    public void AddOrReactivate_reactivates_kicked_subscriber_and_updates_name() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.AddOrReactivate(222L, "Bob");
        svc.Kick(222L);
        Assert.Empty(svc.ListActive());

        svc.AddOrReactivate(222L, "Bobby");
        Assert.Single(svc.ListActive());
        Assert.Equal("Bobby", db.Subscribers.Find(222L)!.Name);
    }

    [Fact]
    public void Kick_removes_subscriber_from_active_list() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.AddOrReactivate(333L, "Carol");
        Assert.Single(svc.ListActive());
        svc.Kick(333L);
        Assert.Empty(svc.ListActive());
        Assert.Equal("removed", db.Subscribers.Find(333L)!.Status);
    }

    [Fact]
    public void Block_sets_blocked_status_and_IsBlocked_returns_true() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.AddOrReactivate(444L, "Dave");
        svc.Block(444L);
        Assert.Equal("blocked", db.Subscribers.Find(444L)!.Status);
        Assert.True(svc.IsBlocked(444L));
        Assert.False(svc.IsActive(444L));
        Assert.Empty(svc.ListActive());
    }

    [Fact]
    public void IsActive_returns_true_only_for_active_subscribers() {
        using var db = NewDb();
        var svc = Svc(db);
        Assert.False(svc.IsActive(999L));
        svc.AddOrReactivate(999L, "Eve");
        Assert.True(svc.IsActive(999L));
        svc.Kick(999L);
        Assert.False(svc.IsActive(999L));
    }

    [Fact]
    public void ListAll_returns_all_regardless_of_status() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.AddOrReactivate(10L, "A");
        svc.AddOrReactivate(20L, "B");
        svc.Kick(20L);
        svc.AddOrReactivate(30L, "C");
        svc.Block(30L);
        Assert.Equal(3, svc.ListAll().Count);
        Assert.Single(svc.ListActive());
    }

    [Fact]
    public void GetAdmin_returns_null_when_not_set() {
        using var db = NewDb();
        var svc = Svc(db);
        Assert.Null(svc.GetAdmin());
    }

    [Fact]
    public void SetAdmin_then_GetAdmin_returns_chat_id() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.SetAdmin(5050L);
        Assert.Equal(5050L, svc.GetAdmin());
    }

    [Fact]
    public void ImportFrom_upserts_batch_and_sets_admin_when_not_yet_set() {
        using var db = NewDb();
        var svc = Svc(db);
        // Pre-seed one subscriber who will be reactivated
        svc.AddOrReactivate(100L, "Old");
        svc.Kick(100L);

        var batch = new (long chatId, string? name, bool isAdmin)[] {
            (100L, "Reactivated", false),
            (200L, "New", true),
            (300L, "Another", false),
        };
        svc.ImportFrom(batch);

        Assert.Equal(3, svc.ListActive().Count);
        Assert.Equal("Reactivated", db.Subscribers.Find(100L)!.Name);
        Assert.Equal(200L, svc.GetAdmin()); // first isAdmin=true is set as admin
    }

    [Fact]
    public void ImportFrom_does_not_overwrite_existing_admin() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.SetAdmin(777L); // admin already set

        var batch = new (long chatId, string? name, bool isAdmin)[] {
            (200L, "NewAdmin", true),
        };
        svc.ImportFrom(batch);

        Assert.Equal(777L, svc.GetAdmin()); // existing admin preserved
    }

    [Fact]
    public void SetPending_marks_pending_and_is_not_active_or_blocked() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.SetPending(555L, "Stranger");
        Assert.True(svc.IsPending(555L));
        Assert.False(svc.IsActive(555L));   // stranger cannot be processed
        Assert.False(svc.IsBlocked(555L));
        Assert.Equal("Stranger", svc.Get(555L)!.Name);
    }

    [Fact]
    public void Approve_flow_pending_then_active_keeps_name() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.SetPending(556L, "Stranger");
        svc.AddOrReactivate(556L, null);    // admin approves (null name keeps existing)
        Assert.True(svc.IsActive(556L));
        Assert.False(svc.IsPending(556L));
        Assert.Equal("Stranger", svc.Get(556L)!.Name);
    }

    [Fact]
    public void Deny_flow_pending_then_blocked() {
        using var db = NewDb();
        var svc = Svc(db);
        svc.SetPending(557L, "Stranger");
        svc.Block(557L);                     // admin denies
        Assert.True(svc.IsBlocked(557L));
        Assert.False(svc.IsActive(557L));
    }

    [Fact]
    public void IsBlocked_returns_false_for_unknown_chat_id() {
        using var db = NewDb();
        var svc = Svc(db);
        Assert.False(svc.IsBlocked(12345L));
    }
}
