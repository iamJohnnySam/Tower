using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tower.Core.Settings;

namespace Tower.Core.Website;

public partial class BlogKeyService(SettingsService settings, FtpSyncService ftp, ILogger<BlogKeyService> logger)
{
    // Matches: 'BLOG_API_KEY' => 'value'  — group 2 is the value; groups 1 & 3 are kept.
    [GeneratedRegex(@"('BLOG_API_KEY'\s*=>\s*')([^']*)(')")]
    private static partial Regex KeyRegex();

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    public string? GetCurrent() => settings.Get("blogkey.current");

    public DateTime? RotatedAt =>
        DateTime.TryParse(settings.Get("blogkey.rotated_at"), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    public int? AutoDays =>
        int.TryParse(settings.Get("blogkey.auto_days"), out var d) ? d : null;

    public void SetAutoDays(int? days) =>
        settings.Set("blogkey.auto_days", days is > 0 ? days.Value.ToString() : null);

    public string EnsureFetchToken()
    {
        var t = settings.Get("blogkey.fetch_token");
        if (string.IsNullOrWhiteSpace(t))
        {
            t = GenerateToken();
            settings.Set("blogkey.fetch_token", t);
        }
        return t;
    }

    public string RemoteFile => settings.Get("blogkey.remote_file") ?? "/blog_secrets.php";

    /// <summary>Pure regex swap of the BLOG_API_KEY value. Throws if the key is absent.</summary>
    public static string SwapKey(string php, string newToken)
    {
        if (!KeyRegex().IsMatch(php))
            throw new InvalidOperationException("BLOG_API_KEY not found in remote file — aborting (upload skipped).");
        return KeyRegex().Replace(php, m => m.Groups[1].Value + newToken + m.Groups[3].Value);
    }

    public async Task<string> RotateAsync()
    {
        logger.LogInformation("BlogKey rotation starting ({File})", RemoteFile);
        try
        {
            var php      = await ftp.DownloadTextAsync(RemoteFile);   // 1
            var newToken = GenerateToken();
            var updated  = SwapKey(php, newToken);                    // 2 + 3 (throws if missing)
            await ftp.UploadTextAsync(RemoteFile, updated);           // 4
            settings.Set("blogkey.current", newToken);               // 5
            settings.Set("blogkey.rotated_at", DateTime.UtcNow.ToString("O"));
            logger.LogInformation("BlogKey rotation succeeded");
            return newToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BlogKey rotation failed");
            throw;
        }
    }
}
