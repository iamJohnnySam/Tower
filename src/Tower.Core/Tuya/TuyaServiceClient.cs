using System.Net.Http.Json;
using System.Text.Json;

namespace Tower.Core.Tuya;

public class TuyaServiceClient(HttpClient http)
{
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web);

    public async Task<bool> HealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await http.GetAsync("health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<TuyaDeviceDto>> GetDevicesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var json = await http.GetStringAsync("devices", cts.Token);
            return JsonSerializer.Deserialize<List<TuyaDeviceDto>>(json, _opts) ?? [];
        }
        catch { return []; }
    }

    public async Task<ScanResponse> ScanAsync(string apiKey, string apiSecret, string region)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var resp = await http.PostAsJsonAsync("scan",
                new { api_key = apiKey, api_secret = apiSecret, region }, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return new ScanResponse([], $"Service error: {resp.StatusCode}", []);
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            return JsonSerializer.Deserialize<ScanResponse>(json, _opts)
                ?? new ScanResponse([], "Empty response from scan service", []);
        }
        catch (Exception ex) { return new ScanResponse([], ex.Message, []); }
    }

    public async Task<bool> SendCommandAsync(string deviceId, TuyaCommandRequest cmd)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await http.PostAsJsonAsync($"devices/{deviceId}/command", cmd, _opts, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
