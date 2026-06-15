namespace Tower.Core.Metrics;

public class SmartInfo {
    public string Model = "", Health = "UNKNOWN", Serial = "", Firmware = "", RotationRate = "";
    public int? PowerOnHours, PowerCycles, Reallocated, Pending, Uncorrectable, Temp, Wear;
    public bool Ssd;
    public int Alert; // 0 ok, 1 warn, 2 critical
}
