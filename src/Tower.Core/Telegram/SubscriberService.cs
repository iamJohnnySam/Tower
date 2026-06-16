using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Settings;
namespace Tower.Core.Telegram;
public class SubscriberService(TowerDbContext db, SettingsService settings) {
    const string AdminKey = "telegram.admin_chat";

    public void AddOrReactivate(long chatId, string? name) {
        var sub = db.Subscribers.Find(chatId);
        if (sub is null) {
            db.Subscribers.Add(new TelegramSubscriber {
                ChatId = chatId,
                Name = name,
                Status = "active",
                AddedAt = DateTime.Now
            });
        } else {
            sub.Status = "active";
            if (name is not null) sub.Name = name;
        }
        db.SaveChanges();
    }

    public void Kick(long chatId) {
        var sub = db.Subscribers.Find(chatId);
        if (sub is null) return;
        sub.Status = "removed";
        db.SaveChanges();
    }

    public void Block(long chatId) {
        var sub = db.Subscribers.Find(chatId);
        if (sub is null) return;
        sub.Status = "blocked";
        db.SaveChanges();
    }

    public List<TelegramSubscriber> ListActive() =>
        db.Subscribers.Where(s => s.Status == "active").ToList();

    public List<TelegramSubscriber> ListAll() =>
        db.Subscribers.ToList();

    public bool IsActive(long chatId) {
        var sub = db.Subscribers.Find(chatId);
        return sub is not null && sub.Status == "active";
    }

    public bool IsBlocked(long chatId) {
        var sub = db.Subscribers.Find(chatId);
        return sub is not null && sub.Status == "blocked";
    }

    public long? GetAdmin() {
        var val = settings.Get(AdminKey);
        return long.TryParse(val, out var id) ? id : null;
    }

    public void SetAdmin(long chatId) =>
        settings.Set(AdminKey, chatId.ToString());

    public void ImportFrom(IEnumerable<(long chatId, string? name, bool isAdmin)> subs) {
        bool adminAlreadySet = GetAdmin().HasValue;
        foreach (var (chatId, name, isAdmin) in subs) {
            AddOrReactivate(chatId, name);
            if (isAdmin && !adminAlreadySet) {
                SetAdmin(chatId);
                adminAlreadySet = true;
            }
        }
    }
}
