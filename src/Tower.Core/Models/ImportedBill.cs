namespace Tower.Core.Models;

public class ImportedBill
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }        // magnitude as printed on the bill; see SignedAmount
    public string Currency { get; set; } = "";
    public DateTime? BillDate { get; set; }   // the receipt/email date — used for cross-source dedup (null on pre-existing rows)
    public int? TransactionId { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>True when this row is money coming back (a refund), posted as a positive transaction.
    /// Stored rather than looked up from the profile, because profiles are runtime-editable now and a
    /// renamed or deleted one would otherwise silently flip the sign of old rows.</summary>
    public bool Refund { get; set; }

    /// <summary>The amount as it affects spending: refunds net off rather than adding to a total.</summary>
    public decimal SignedAmount => Refund ? -Amount : Amount;
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
