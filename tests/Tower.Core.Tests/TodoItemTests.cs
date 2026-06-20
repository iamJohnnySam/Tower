using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;

namespace Tower.Core.Tests;

public class TodoItemTests
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
    public void Can_persist_and_read_todo_item()
    {
        using var db = NewDb();
        var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
        db.Todos.Add(new TodoItem
        {
            Title     = "Buy milk",
            Deadline  = null,
            Done      = false,
            CreatedAt = now,
        });
        db.SaveChanges();

        var loaded = db.Todos.Single();
        Assert.Equal("Buy milk", loaded.Title);
        Assert.Null(loaded.Deadline);
        Assert.False(loaded.Done);
        Assert.Equal(now, loaded.CreatedAt);
    }

    [Fact]
    public void Can_store_deadline_and_telegram_message_id()
    {
        using var db = NewDb();
        var deadline = new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
        db.Todos.Add(new TodoItem
        {
            Title              = "Call dentist",
            Deadline           = deadline,
            Done               = false,
            CreatedAt          = DateTime.UtcNow,
            TelegramMessageId  = 42,
        });
        db.SaveChanges();

        var loaded = db.Todos.Single();
        Assert.Equal(deadline, loaded.Deadline);
        Assert.Equal(42, loaded.TelegramMessageId);
    }
}
