namespace Tower.Core.Models;

// SolaxCloud "Abnormal Power Reminder" email — a device fault/underperformance alert.
// The email body has no date, so AlarmDate comes from the email's received time.
public class SolarAlarm
{
    public int Id { get; set; }
    public DateTime AlarmDate { get; set; }
    public string DeviceSn { get; set; } = "";
    public string? Detail { get; set; }          // e.g. "Zero power time 1h"
    public string GmailMessageId { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}
