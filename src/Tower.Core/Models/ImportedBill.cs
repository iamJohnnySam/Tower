namespace Tower.Core.Models;

public class ImportedBill
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTime? BillDate { get; set; }   // the receipt/email date — used for cross-source dedup (null on pre-existing rows)
    public int? TransactionId { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? Error { get; set; }
}
