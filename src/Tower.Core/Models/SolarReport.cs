namespace Tower.Core.Models;

public enum SolarReportType { Daily = 0, Weekly = 1, Monthly = 2 }

public class SolarReport
{
    public int Id { get; set; }
    public SolarReportType ReportType { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public double PeriodYieldKWh { get; set; }
    public double TotalYieldKWh { get; set; }
    public decimal PeriodEarningsLkr { get; set; }
    public decimal TotalEarningsLkr { get; set; }
    public double Co2SavedTons { get; set; }
    public int AlarmQuantity { get; set; }
    public string GmailMessageId { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}
