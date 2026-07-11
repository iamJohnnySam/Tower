namespace Tower.Core.Models;

public enum TuyaDeviceType { Plug, Light, AcRemote, Switch1, Switch2, Switch4, Sensor }

public class TuyaDevice
{
    public int            Id         { get; set; }
    public string         DeviceId   { get; set; } = "";
    public string         Name       { get; set; } = "";
    public TuyaDeviceType DeviceType { get; set; }
    public string?        Room       { get; set; }
    public string?        Category   { get; set; }
    public int            SortOrder  { get; set; }
}
