namespace Tower.Core.Models;

public enum ConversionStatus
{
    Pending,          // alert sent to Telegram, awaiting user response
    Queued,           // user tapped "Convert", waiting for scheduler
    Converting,       // ffmpeg running
    AwaitingApproval, // ffmpeg done, test file ready
    Approved,         // user approved, original replaced
    Rejected,         // user rejected, test file deleted
    Failed,           // ffmpeg failed
    Ignored,          // user tapped "Ignore", no further alerts
    AwaitingReplace,  // ffmpeg done, waiting until nobody's watching to swap in
    Replaced,         // swapped in (original kept as .bak), awaiting keep/revert
    Reverted,         // rolled back to the original from .bak
}

public class ConversionJob
{
    public int Id { get; set; }
    public string MediaId { get; set; } = "";
    public string MediaName { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string? TestPath { get; set; }
    public string? BackupPath { get; set; }
    public ConversionStatus Status { get; set; }
    public string? TranscodeReasons { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int? AlertMessageId { get; set; }
}
