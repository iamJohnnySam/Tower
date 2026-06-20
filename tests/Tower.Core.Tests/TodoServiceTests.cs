using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Todo;

namespace Tower.Core.Tests;

public class TodoServiceTests
{
    static TowerDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<TowerDbContext>().UseSqlite("DataSource=:memory:").Options;
        var db = new TowerDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task AddAsync_creates_open_todo()
    {
        using var db = NewDb();
        var svc = new TodoService(db);

        var item = await svc.AddAsync("Buy milk", null);

        Assert.Equal("Buy milk", item.Title);
        Assert.Null(item.Deadline);
        Assert.False(item.Done);
        Assert.True(item.Id > 0);
    }

    [Fact]
    public async Task GetOpenAsync_excludes_done_items()
    {
        using var db = NewDb();
        var svc = new TodoService(db);
        await svc.AddAsync("Open task", null);
        var done = await svc.AddAsync("Done task", null);
        await svc.MarkDoneAsync(done.Id);

        var open = await svc.GetOpenAsync();

        Assert.Single(open);
        Assert.Equal("Open task", open[0].Title);
    }

    [Fact]
    public async Task GetOpenAsync_orders_by_deadline_then_created()
    {
        using var db = NewDb();
        var svc = new TodoService(db);
        var d1 = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 7, 1,  0, 0, 0, DateTimeKind.Utc);
        await svc.AddAsync("No deadline", null);
        await svc.AddAsync("Far deadline", d1);
        await svc.AddAsync("Soon deadline", d2);

        var open = await svc.GetOpenAsync();

        Assert.Equal(3, open.Count);
        Assert.Equal("Soon deadline", open[0].Title);
        Assert.Equal("Far deadline",  open[1].Title);
        Assert.Equal("No deadline",   open[2].Title);
    }

    [Fact]
    public async Task GetDueTodayAsync_returns_only_todays_open_items()
    {
        using var db = NewDb();
        var svc = new TodoService(db);
        var today = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        await svc.AddAsync("Due today",     today);
        await svc.AddAsync("Due tomorrow",  today.AddDays(1));
        await svc.AddAsync("No deadline",   null);
        var done = await svc.AddAsync("Done today", today);
        await svc.MarkDoneAsync(done.Id);

        var due = await svc.GetDueTodayAsync(today);

        Assert.Single(due);
        Assert.Equal("Due today", due[0].Title);
    }

    [Fact]
    public async Task MarkDoneAsync_sets_done_and_done_at()
    {
        using var db = NewDb();
        var svc = new TodoService(db);
        var item = await svc.AddAsync("Finish report", null);

        var result = await svc.MarkDoneAsync(item.Id);

        Assert.NotNull(result);
        Assert.True(result!.Done);
        Assert.NotNull(result.DoneAt);
    }

    [Fact]
    public async Task MarkDoneAsync_returns_null_for_missing_id()
    {
        using var db = NewDb();
        var svc = new TodoService(db);

        var result = await svc.MarkDoneAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetTelegramMessageIdAsync_persists_message_id()
    {
        using var db = NewDb();
        var svc = new TodoService(db);
        var item = await svc.AddAsync("Test", null);

        await svc.SetTelegramMessageIdAsync(item.Id, 777);

        var loaded = db.Todos.Find(item.Id);
        Assert.Equal(777, loaded!.TelegramMessageId);
    }
}
