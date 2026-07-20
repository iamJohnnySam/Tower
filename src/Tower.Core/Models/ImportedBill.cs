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

public class ImportedStatement
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public DateTime StatementDate { get; set; }
    public decimal? Balance { get; set; }   // set only on the direct-value (email body) path
    public bool SentPdf { get; set; }
    public string? Error { get; set; }
    public DateTime ImportedAt { get; set; }
}
