namespace Tower.Core.Models;

public class SolarSnapshot
{
    public int Id { get; set; }
    public DateTime CapturedAt { get; set; }      // UTC, when Tower polled
    public string? UploadTime { get; set; }        // inverter's own uploadTime
    public double AcPower { get; set; }            // W
    public double YieldToday { get; set; }         // kWh
    public double YieldTotal { get; set; }         // kWh
    public double FeedInPower { get; set; }        // W (+export / -import)
    public double FeedInEnergy { get; set; }       // kWh
    public double ConsumeEnergy { get; set; }      // kWh
    public double Soc { get; set; }                // % battery
    public double BatPower { get; set; }           // W
    public double PowerDc1 { get; set; }           // W
    public string? InverterStatus { get; set; }
}
