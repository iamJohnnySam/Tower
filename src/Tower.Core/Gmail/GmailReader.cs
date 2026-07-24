using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    // Move a message to Trash (auto-purges after ~30 days). Requires gmail.modify scope.
    public async Task<bool> TrashMessageAsync(string id, CancellationToken ct = default)
    {
        var token = await tokens.GetAccessTokenAsync(ct)
            ?? throw new InvalidOperationException("Gmail not connected");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/messages/{id}/trash");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
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

    public async Task<List<string>> ListMessageIdsAsync(string labelId, DateTime? after, CancellationToken ct = default, string? fromContains = null)
    {
        var ids = new List<string>();
        string? pageToken = null;
        var terms = new List<string>();
        if (after is { } a) terms.Add($"after:{a:yyyy/MM/dd}");
        if (!string.IsNullOrEmpty(fromContains)) terms.Add($"from:{fromContains}");
        var q = terms.Count > 0 ? $"&q={Uri.EscapeDataString(string.Join(" ", terms))}" : "";
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

    public async Task<(string From, string Subject, string Body, DateTime Date)?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=full", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var payload = root.GetProperty("payload");

        string subject = "", from = "";
        if (payload.TryGetProperty("headers", out var headers))
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.GetProperty("name").GetString() ?? "";
                if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                    subject = h.GetProperty("value").GetString() ?? "";
                else if (name.Equals("From", StringComparison.OrdinalIgnoreCase))
                    from = h.GetProperty("value").GetString() ?? "";
            }

        var date = DateTime.UtcNow;
        if (root.TryGetProperty("internalDate", out var idt) &&
            long.TryParse(idt.GetString(), out var ms))
            date = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        var body = ExtractText(payload);
        return (from, subject, body, date);
    }

    // Full RFC822 message bytes (the .eml), for archiving as an attachment.
    public async Task<byte[]?> GetRawMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=raw", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("raw", out var raw)) return null;
        var b64 = raw.GetString();
        if (string.IsNullOrEmpty(b64)) return null;
        return Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/')
            .PadRight((b64.Length + 3) / 4 * 4, '='));
    }

    // Some senders (older PickMe receipts, Epic Games) ship an effectively HTML-only email whose
    // text/plain part is just a "open this in a real client" stub — treat those as empty so
    // extraction falls through to the HTML body that actually carries the amount.
    private static readonly string[] NoTextPlaceholders =
    [
        "This email has no text content",                              // PickMe, recent receipts
        "It looks like your email client might not support HTML",      // Epic Games
        "To view the message, please use an HTML compatible email",    // PickMe, 2017 receipts
    ];

    // Download a document attachment (filename + bytes), or null if there is none.
    // nameMatch picks a specific one when the mail carries several — CAL ships the statement
    // alongside a fund fact sheet, and "first .pdf wins" is only accidentally right there.
    public async Task<(string FileName, byte[] Bytes)?> GetPdfAttachmentAsync(
        string id, Regex? nameMatch = null, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=full", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var found = FindPdfPart(doc.RootElement.GetProperty("payload"), nameMatch);
        if (found is null) return null;
        var (fileName, attId) = found.Value;

        using var areq = await AuthGet($"{Api}/messages/{id}/attachments/{attId}", ct);
        using var aresp = await http.SendAsync(areq, ct);
        if (!aresp.IsSuccessStatusCode) return null;
        using var adoc = JsonDocument.Parse(await aresp.Content.ReadAsStringAsync(ct));
        if (!adoc.RootElement.TryGetProperty("data", out var d)) return null;
        var b64 = d.GetString();
        if (string.IsNullOrEmpty(b64)) return null;
        var bytes = Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight((b64.Length + 3) / 4 * 4, '='));
        return (fileName, bytes);
    }

    // Senders label these application/octet-stream as often as application/pdf, so the extension
    // is the only reliable signal.
    private static readonly string[] DocExtensions = [".pdf", ".html", ".htm"];

    private static (string FileName, string AttachmentId)? FindPdfPart(JsonElement part, Regex? nameMatch)
    {
        var fn = part.TryGetProperty("filename", out var f) ? f.GetString() : null;
        var wanted = !string.IsNullOrEmpty(fn) && (nameMatch is null
            ? DocExtensions.Any(e => fn!.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            : nameMatch.IsMatch(fn!));
        if (wanted && part.TryGetProperty("body", out var b) && b.TryGetProperty("attachmentId", out var a))
            return (fn!, a.GetString()!);
        if (part.TryGetProperty("parts", out var parts))
            foreach (var child in parts.EnumerateArray())
                if (FindPdfPart(child, nameMatch) is { } r) return r;
        return null;
    }

    // Recursively find the first text/plain part; fall back to any body data.
    private static string ExtractText(JsonElement part)
    {
        if (part.TryGetProperty("mimeType", out var mt) && mt.GetString() == "text/plain")
        {
            var d = Clean(Decode(part));
            if (d.Length > 0) return d;
        }
        if (part.TryGetProperty("parts", out var parts))
            foreach (var child in parts.EnumerateArray())
            {
                var found = ExtractText(child);
                if (!string.IsNullOrEmpty(found)) return found;
            }
        var decoded = Decode(part);
        if (part.TryGetProperty("mimeType", out var fallbackMt) &&
            (fallbackMt.GetString() ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(decoded, "<[^>]+>", " ");
        return Clean(decoded);
    }

    /// <summary>True when a text/plain part is just a "your client can't render HTML" stub rather than
    /// a real body. Treating one as content is silent data loss: the amount only exists in the HTML,
    /// so the bill matches its profile and then parses to nothing.</summary>
    public static bool IsHtmlOnlyStub(string text) =>
        NoTextPlaceholders.Any(p => text.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string s) => IsHtmlOnlyStub(s) ? "" : s;

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
