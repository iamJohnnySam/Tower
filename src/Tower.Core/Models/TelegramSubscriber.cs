namespace Tower.Core.Models;
public class TelegramSubscriber { public long ChatId { get; set; } public string? Name { get; set; } public string Status { get; set; } = "active"; public DateTime AddedAt { get; set; } }
