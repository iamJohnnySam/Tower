using System.Globalization;
using System.Text.Json;
using Tower.Core.Models;

namespace Tower.Core.Solar;

public class SolaxClient(HttpClient http)
{
    private const string BaseUrl =
        "https://www.solaxcloud.com/proxyApp/proxy/api/getRealtimeInfo.do";

    public async Task<SolarSnapshot?> GetRealtimeAsync(string tokenId, string sn, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?tokenId={Uri.EscapeDataString(tokenId)}&sn={Uri.EscapeDataString(sn)}";
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return ParseRealtime(await resp.Content.ReadAsStringAsync(ct));
    }

    public static SolarSnapshot? ParseRealtime(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
        if (!root.TryGetProperty("result", out var r)) return null;

        return new SolarSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            UploadTime = Str(r, "uploadTime"),
            AcPower = Dbl(r, "acpower"),
            YieldToday = Dbl(r, "yieldtoday"),
            YieldTotal = Dbl(r, "yieldtotal"),
            FeedInPower = Dbl(r, "feedinpower"),
            FeedInEnergy = Dbl(r, "feedinenergy"),
            ConsumeEnergy = Dbl(r, "consumeenergy"),
            Soc = Dbl(r, "soc"),
            BatPower = Dbl(r, "batPower"),
            PowerDc1 = Dbl(r, "powerdc1"),
            InverterStatus = Str(r, "inverterStatus"),
        };
    }

    private static double Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ToString() : null;
}
