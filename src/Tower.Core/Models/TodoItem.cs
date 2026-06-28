namespace Tower.Core.Models;

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime? Deadline { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DoneAt { get; set; }
    public int? TelegramMessageId { get; set; }
}
