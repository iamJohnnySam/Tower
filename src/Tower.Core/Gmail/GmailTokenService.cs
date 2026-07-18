using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Gmail;

/// <summary>
/// Google OAuth for gmail.modify (read + trash imported reports). Stores client_id/client_secret/refresh_token in the
/// Settings table (keys gmail.*). Caches the short-lived access token in memory.
/// Redirect is http://localhost:8888/gmail/callback (Google allows http for loopback).
/// For a remote browser the callback won't load, but the ?code= is in the URL bar —
/// paste it into the /gmail page (same fallback as Dropbox).
/// </summary>
public class GmailTokenService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory)
{
    public const string RedirectUri = "http://localhost:8888/gmail/callback";
    public const string Scope = "https://www.googleapis.com/auth/gmail.modify";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected
    {
        get
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            return !string.IsNullOrEmpty(settings.Get("gmail.refresh_token"))
                && string.IsNullOrEmpty(settings.Get("gmail.disconnected_at"));
        }
    }

    /// <summary>True when credentials are stored (a token exists), regardless of health.
    /// Distinguishes "never set up" from "set up but token went dead".</summary>
    public bool IsConfigured
    {
        get
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            return !string.IsNullOrEmpty(settings.Get("gmail.refresh_token"))
                && !string.IsNullOrEmpty(settings.Get("gmail.client_id"))
                && !string.IsNullOrEmpty(settings.Get("gmail.client_secret"));
        }
    }

    /// <summary>ISO timestamp of when the token was last found dead, or null while healthy.</summary>
    public string? DisconnectedAt
    {
        get
        {
            using var scope = scopes.CreateScope();
            return scope.ServiceProvider.GetRequiredService<SettingsService>().Get("gmail.disconnected_at");
        }
    }

    public string BuildAuthUrl(string clientId) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString(Scope)}" +
        "&access_type=offline&prompt=consent" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            return _cachedToken;

        string? result = null;
        bool justDisconnected = false;

        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var refreshToken = settings.Get("gmail.refresh_token");
            var clientId = settings.Get("gmail.client_id");
            var clientSecret = settings.Get("gmail.client_secret");
            if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                using var http = httpFactory.CreateClient(nameof(GmailTokenService));
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                });
                var resp = await http.PostAsync(TokenEndpoint, form, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    _cachedToken = json.RootElement.GetProperty("access_token").GetString();
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
                    result = _cachedToken;
                    if (!string.IsNullOrEmpty(settings.Get("gmail.disconnected_at")))
                        settings.Set("gmail.disconnected_at", null);      // recovered
                }
                else if (string.IsNullOrEmpty(settings.Get("gmail.disconnected_at")))
                {
                    // Refresh of a stored token failed (typically invalid_grant) → token is dead.
                    // Record it once; the alert below fires only on this connected→disconnected edge.
                    settings.Set("gmail.disconnected_at", DateTime.UtcNow.ToString("O"));
                    justDisconnected = true;
                }
            }
        }
        finally { _lock.Release(); }

        if (justDisconnected)
            await NotifyDisconnectedAsync(ct);

        return result;
    }

    // One-shot admin alert when Gmail goes dead. Never lets a notification failure affect token logic.
    private async Task NotifyDisconnectedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<Tower.Core.Telegram.TelegramHub>();
            await hub.SendAsync(Tower.Core.Telegram.TgAudience.Admin, 0,
                "⚠️ Tower: Gmail disconnected — the OAuth token was rejected (expired or revoked). " +
                "Solar + bill imports are paused. Open Tower → Gmail and tap Reconnect.", null, ct);
        }
        catch { /* best-effort */ }
    }

    public async Task<(bool Ok, string? Error)> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var clientId = settings.Get("gmail.client_id");
        var clientSecret = settings.Get("gmail.client_secret");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return (false, "Client ID or Secret not configured");

        using var http = httpFactory.CreateClient(nameof(GmailTokenService));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code.Trim(),
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = RedirectUri,
        });
        var resp = await http.PostAsync(TokenEndpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
            return (false, $"Token exchange failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");

        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("refresh_token", out var rt))
            return (false, "No refresh_token returned (revoke access and retry with prompt=consent)");
        settings.Set("gmail.refresh_token", rt.GetString());
        settings.Set("gmail.disconnected_at", null);   // reconnected → healthy again
        _cachedToken = json.RootElement.GetProperty("access_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
        return (true, null);
    }

    public void Disconnect()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<SettingsService>().Set("gmail.refresh_token", null);
    }
}
