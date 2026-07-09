using System.Globalization;
using System.Text.Json;
using Tower.Core.Models;

namespace Tower.Core.Solar;

// Daily historical weather for Colombo from Open-Meteo's free archive API (no key).
// shortwave_radiation_sum (MJ/m²) is the irradiance that drives PV output.
public class WeatherClient(HttpClient http)
{
    private const double Lat = 6.9271, Lon = 79.8612; // Colombo

    public async Task<List<SolarWeather>> GetDailyAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        var url = "https://archive-api.open-meteo.com/v1/archive"
            + $"?latitude={Lat.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={Lon.ToString(CultureInfo.InvariantCulture)}"
            + $"&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}"
            + "&daily=shortwave_radiation_sum,sunshine_duration&timezone=Asia%2FColombo";
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new();
        return ParseArchive(await resp.Content.ReadAsStringAsync(ct));
    }

    public static List<SolarWeather> ParseArchive(string json)
    {
        var list = new List<SolarWeather>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("daily", out var daily)) return list;
        var times = daily.GetProperty("time");
        var rad = daily.GetProperty("shortwave_radiation_sum");
        daily.TryGetProperty("sunshine_duration", out var sun);

        for (int i = 0; i < times.GetArrayLength(); i++)
        {
            if (rad[i].ValueKind != JsonValueKind.Number) continue; // skip days with no reanalysis yet
            var date = DateTime.ParseExact(times[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var sunHours = sun.ValueKind == JsonValueKind.Array && sun[i].ValueKind == JsonValueKind.Number
                ? sun[i].GetDouble() / 3600.0 : 0;
            list.Add(new SolarWeather
            {
                Date = date.Date,
                ShortwaveRadiationMJ = rad[i].GetDouble(),
                SunshineHours = sunHours
            });
        }
        return list;
    }
}
