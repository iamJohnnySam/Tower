using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Backup;

/// <summary>
/// Manages Dropbox OAuth tokens. Stores app_key/app_secret/refresh_token in the DB.
/// Caches the short-lived access token in memory and auto-refreshes it when it expires.
/// Falls back to the old raw access_token setting if no refresh_token is stored.
///
/// Dropbox only allows http: for localhost redirect URIs, so we always use
/// http://localhost:8888/dropbox/callback. The user registers this once in the
/// Dropbox App Console. If the redirect doesn't fire (remote browser), the user
/// copies the code from the browser URL bar and pastes it into Settings.
/// </summary>
public class DropboxTokenService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory)
{
    public const string RedirectUri = "http://localhost:8888/dropbox/callback";

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string BuildAuthUrl(string appKey) =>
        "https://www.dropbox.com/oauth2/authorize" +
        $"?client_id={Uri.EscapeDataString(appKey)}" +
        "&response_type=code" +
        "&token_access_type=offline" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

            var refreshToken = settings.Get("dropbox.refresh_token");

            // Legacy fallback: no refresh token yet, use stored access token directly.
            if (string.IsNullOrEmpty(refreshToken))
                return settings.Get("dropbox.access_token");

            var appKey    = settings.Get("dropbox.app_key");
            var appSecret = settings.Get("dropbox.app_secret");
            if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(appSecret))
                return null;

            using var http = httpFactory.CreateClient(nameof(DropboxTokenService));
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = appKey,
                ["client_secret"] = appSecret,
            });

            var resp = await http.PostAsync("https://api.dropboxapi.com/oauth2/token", form, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var token     = json.RootElement.GetProperty("access_token").GetString();
            var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();

            _cachedToken = token;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            return token;
        }
        finally { _lock.Release(); }
    }

    public async Task<(bool Ok, string? Error)> ExchangeCodeAsync(
        string code, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        var appKey    = settings.Get("dropbox.app_key");
        var appSecret = settings.Get("dropbox.app_secret");
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(appSecret))
            return (false, "App Key or Secret not configured");

        using var http = httpFactory.CreateClient(nameof(DropboxTokenService));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"]          = code,
            ["grant_type"]    = "authorization_code",
            ["client_id"]     = appKey,
            ["client_secret"] = appSecret,
            ["redirect_uri"]  = RedirectUri,
        });

        var resp = await http.PostAsync("https://api.dropboxapi.com/oauth2/token", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (false, $"Token exchange failed ({(int)resp.StatusCode}): {body}");
        }

        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        settings.Set("dropbox.refresh_token", json.RootElement.GetProperty("refresh_token").GetString());

        var token     = json.RootElement.GetProperty("access_token").GetString();
        var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
        _cachedToken = token;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        return (true, null);
    }

    public void Disconnect()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;

        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        settings.Set("dropbox.refresh_token", null);
    }
}
