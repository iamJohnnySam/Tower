namespace Tower.Core.Models;

public class SolarWeather
{
    public DateTime Date { get; set; }               // day (primary key)
    public double ShortwaveRadiationMJ { get; set; } // daily GHI sum, MJ/m² (drives PV output)
    public double SunshineHours { get; set; }
}
