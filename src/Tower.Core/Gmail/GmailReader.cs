using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tower.Core.Gmail;

public class GmailReader(HttpClient http, GmailTokenService tokens)
{
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";

    private async Task<HttpRequestMessage> AuthGet(string url, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct)
            ?? throw new InvalidOperationException("Gmail not connected");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<List<(string Id, string Name)>> ListLabelsAsync(CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/labels", ct);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("labels", out var labels))
            foreach (var l in labels.EnumerateArray())
                list.Add((l.GetProperty("id").GetString()!, l.GetProperty("name").GetString()!));
        return list;
    }

    public async Task<List<string>> ListMessageIdsAsync(string labelId, DateTime? after, CancellationToken ct = default)
    {
        var ids = new List<string>();
        string? pageToken = null;
        var q = after is { } a ? $"&q=after:{a:yyyy/MM/dd}" : "";
        do
        {
            var url = $"{Api}/messages?labelIds={Uri.EscapeDataString(labelId)}&maxResults=100{q}" +
                      (pageToken != null ? $"&pageToken={pageToken}" : "");
            using var req = await AuthGet(url, ct);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("messages", out var msgs))
                foreach (var m in msgs.EnumerateArray())
                    ids.Add(m.GetProperty("id").GetString()!);
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() : null;
        } while (pageToken != null);
        return ids;
    }

    public async Task<(string Subject, string Body)?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=full", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var payload = doc.RootElement.GetProperty("payload");

        string subject = "";
        if (payload.TryGetProperty("headers", out var headers))
            foreach (var h in headers.EnumerateArray())
                if (h.GetProperty("name").GetString()!.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                    subject = h.GetProperty("value").GetString() ?? "";

        var body = ExtractText(payload);
        return (subject, body);
    }

    // Recursively find the first text/plain part; fall back to any body data.
    private static string ExtractText(JsonElement part)
    {
        if (part.TryGetProperty("mimeType", out var mt) && mt.GetString() == "text/plain")
        {
            var d = Decode(part);
            if (d.Length > 0) return d;
        }
        if (part.TryGetProperty("parts", out var parts))
            foreach (var child in parts.EnumerateArray())
            {
                var found = ExtractText(child);
                if (!string.IsNullOrEmpty(found)) return found;
            }
        return Decode(part);
    }

    private static string Decode(JsonElement part)
    {
        if (!part.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("data", out var data)) return "";
        var b64 = data.GetString();
        if (string.IsNullOrEmpty(b64)) return "";
        var bytes = Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight((b64.Length + 3) / 4 * 4, '='));
        return Encoding.UTF8.GetString(bytes);
    }
}
