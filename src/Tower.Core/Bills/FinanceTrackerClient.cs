using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Bills;

/// <summary>Thin client for FinanceTracker's external REST API. Base URL + API key come from the
/// Settings table (plaintext) — a background worker can't unlock the encrypted secrets vault.</summary>
public class FinanceTrackerClient(HttpClient http, IServiceScopeFactory scopes)
{
    private record TxReq(decimal Value, string Category, string? Description, DateTime? Date, string? Currency);

    private (string? BaseUrl, string? ApiKey) Config()
    {
        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
        return (s.Get("financetracker.base_url"), s.Get("financetracker.api_key"));
    }

    public bool IsConfigured
    {
        get { var (b, k) = Config(); return !string.IsNullOrWhiteSpace(b) && !string.IsNullOrWhiteSpace(k); }
    }

    public async Task<int?> PostTransactionAsync(decimal value, string category, string? description,
        DateTime date, string currency, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/external/transactions");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(new TxReq(value, category, description, date, currency));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("transactionId", out var id) ? id.GetInt32() : null;
    }

    public async Task<bool> PostAttachmentAsync(int transactionId, byte[] content, string fileName,
        string contentType = "message/rfc822", CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return false;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/api/external/transactions/{transactionId}/attachment");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = form;
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    private record BalanceReq(string AccountNumber, DateTime Date, decimal BalanceAmount, string? Currency);

    /// <summary>Upserts a bank balance for (account, date). Returns the balance id, null on failure.</summary>
    public async Task<int?> PostBalanceAsync(string accountNumber, DateTime date, decimal balance,
        string? currency, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/external/balances");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(new BalanceReq(accountNumber, date, balance, currency));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("balanceId", out var id) ? id.GetInt32() : null;
    }

    /// <summary>Hands a (possibly password-locked) statement PDF to FinanceTracker. Returns the
    /// pending status name (e.g. "NeedsPassword"), null on failure.</summary>
    public async Task<string?> PostStatementAsync(string accountNumber, DateTime statementDate, string sourceRef,
        byte[] pdf, string fileName, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(accountNumber), "accountNumber");
        form.Add(new StringContent(statementDate.ToString("yyyy-MM-dd")), "statementDate");
        form.Add(new StringContent(sourceRef), "sourceRef");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/external/statements");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = form;
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("status", out var s) ? s.GetString() : null;
    }
}
