using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;

namespace Tower.Core.Todo;

public class TodoService(TowerDbContext db)
{
    public async Task<List<TodoItem>> GetOpenAsync() =>
        await db.Todos
            .Where(t => !t.Done)
            .OrderBy(t => t.Deadline == null ? 1 : 0)
            .ThenBy(t => t.Deadline)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

    public async Task<List<TodoItem>> GetDueTodayAsync(DateTime today) =>
        await db.Todos
            .Where(t => !t.Done && t.Deadline != null && t.Deadline >= today && t.Deadline < today.AddDays(1))
            .ToListAsync();

    public async Task<TodoItem> AddAsync(string title, DateTime? deadline)
    {
        var item = new TodoItem
        {
            Title     = title,
            Deadline  = deadline,
            Done      = false,
            CreatedAt = DateTime.UtcNow,
        };
        db.Todos.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    public async Task<TodoItem?> MarkDoneAsync(int id)
    {
        var item = await db.Todos.FindAsync(id);
        if (item is null) return null;
        item.Done  = true;
        item.DoneAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return item;
    }

    public async Task SetTelegramMessageIdAsync(int id, int messageId)
    {
        var item = await db.Todos.FindAsync(id);
        if (item is null) return;
        item.TelegramMessageId = messageId;
        await db.SaveChangesAsync();
    }
}
