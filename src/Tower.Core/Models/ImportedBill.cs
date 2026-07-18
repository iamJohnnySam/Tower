namespace Tower.Core.Models;

public class ImportedBill
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public int? TransactionId { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? Error { get; set; }
}
