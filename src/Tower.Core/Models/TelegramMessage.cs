namespace Tower.Core.Models;
public class TelegramMessage { public int Id { get; set; } public DateTime Ts { get; set; } public string Direction { get; set; } = ""; public long ChatId { get; set; } public string? Command { get; set; } public string? Payload { get; set; } }
