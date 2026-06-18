using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tower.Core.Backup;

public record BackupResult(string Name, bool Success, DateTime Ts, long Size, string? Error);

public class BackupService(HttpClient http)
{
    /// <summary>
    /// Creates a consistent copy of <paramref name="srcPath"/> at <paramref name="destPath"/>
    /// using the SQLite Online Backup API — safe even while the source DB is open.
    /// </summary>
    public static void Snapshot(string srcPath, string destPath)
    {
        using var src = new SqliteConnection($"Data Source={srcPath};Mode=ReadOnly");
        using var dst = new SqliteConnection($"Data Source={destPath}");
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>
    /// Snapshots <paramref name="dbPath"/> to a temp file, uploads it to Dropbox at
    /// <c>/server-backups/{name}.sqlite</c>, and returns a <see cref="BackupResult"/>.
    /// No exception is thrown; errors are captured in the result.
    /// </summary>
    public async Task<BackupResult> BackupAsync(
        string name,
        string dbPath,
        string accessToken,
        CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"tower-{name}-{Guid.NewGuid():N}.sqlite");
        try
        {
            if (!File.Exists(dbPath))
                return new(name, false, DateTime.Now, 0, "DB not found");

            Snapshot(dbPath, tmp);

            var bytes = await File.ReadAllBytesAsync(tmp, ct);

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://content.dropboxapi.com/2/files/upload");

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var arg = JsonSerializer.Serialize(new
            {
                path = $"/server-backups/{name}.sqlite",
                mode = "overwrite",
                mute = true
            });
            req.Headers.Add("Dropbox-API-Arg", arg);

            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new(name, false, DateTime.Now, bytes.Length,
                    $"Dropbox {(int)resp.StatusCode}: {body}");
            }

            return new(name, true, DateTime.Now, bytes.Length, null);
        }
        catch (Exception ex)
        {
            return new(name, false, DateTime.Now, 0, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
