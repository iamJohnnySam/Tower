using FluentFTP;
using Microsoft.Extensions.Logging;
using Tower.Core.Settings;

namespace Tower.Core.Website;

public record FileCompareResult(
    string   Path,
    long     LocalSize,
    long?    RemoteSize,
    DateTime LocalMtime,
    DateTime? RemoteMtime,
    string   Reason
);

public record ScanResult(
    List<FileCompareResult> ToUpload,
    List<string>            RemoteOnly,
    int                     UpToDate
);

public class FtpSyncService(WebsiteOptions opts, SettingsService settings, ILogger<FtpSyncService> logger)
{
    public async Task<(bool ok, string? error)> TestConnectionAsync()
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            return (false, "FTP credentials not configured.");
        try
        {
            using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
            ftp.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            ftp.Config.ValidateAnyCertificate = GetAcceptAnyCert();
            await ftp.Connect();
            await ftp.Disconnect();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<ScanResult> ScanAsync(IProgress<string>? progress = null)
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            throw new InvalidOperationException("FTP credentials not configured.");

        var localPath  = GetLocalPath();
        var remotePath = GetRemotePath();

        if (!Directory.Exists(localPath))
            throw new DirectoryNotFoundException($"Local path not found: {localPath}");

        // Read all settings before any async work so DbContext is only touched synchronously
        // on the Blazor circuit thread, avoiding races with re-render callbacks.
        var excludePatterns = GetExcludePatterns();

        progress?.Report("Connecting to FTP…");
        var remoteFiles = await Task.Run(() => WalkRemoteAsync(opts.FtpHost, user, pass, GetAcceptAnyCert(), remotePath, progress));

        progress?.Report($"Found {remoteFiles.Count} remote files. Counting local files…");
        var localFiles = Directory
            .GetFiles(localPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => NormalizePath(f[localPath.TrimEnd('/').Length..]),
                f => { var fi = new FileInfo(f); return (size: fi.Length, mtime: fi.LastWriteTimeUtc); });

        if (excludePatterns.Length > 0)
        {
            localFiles  = localFiles .Where(kv => !IsExcluded(kv.Key, excludePatterns)).ToDictionary(kv => kv.Key, kv => kv.Value);
            remoteFiles = remoteFiles.Where(kv => !IsExcluded(kv.Key, excludePatterns)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        progress?.Report($"Comparing {localFiles.Count} local vs {remoteFiles.Count} remote files…");
        return Classify(localFiles, remoteFiles);
    }

    public async Task<(int uploaded, int deleted, int failed)> SyncAsync(
        IReadOnlyList<string> filesToUpload,
        IReadOnlyList<string> filesToDelete,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            throw new InvalidOperationException("FTP credentials not configured.");

        int uploaded = 0, deleted = 0, failed = 0;
        var localPath  = GetLocalPath();
        var remotePath = GetRemotePath();

        using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
        ftp.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        ftp.Config.ValidateAnyCertificate = GetAcceptAnyCert();
        await ftp.Connect();

        var remoteBase = remotePath.TrimEnd('/');

        foreach (var rel in filesToUpload)
        {
            ct.ThrowIfCancellationRequested();
            var localFile  = Path.Combine(localPath.TrimEnd('/'), rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var remoteFile = remoteBase + rel;
            try
            {
                await ftp.UploadFile(localFile, remoteFile, FtpRemoteExists.Overwrite, createRemoteDir: true);
                progress.Report($"↑ {rel}");
                uploaded++;
            }
            catch (Exception ex)
            {
                progress.Report($"✗ upload failed {rel}: {ex.Message}");
                logger.LogWarning(ex, "Failed to upload {Path}", rel);
                failed++;
            }
        }

        foreach (var rel in filesToDelete)
        {
            ct.ThrowIfCancellationRequested();
            var remoteFile = remoteBase + rel;
            try
            {
                await ftp.DeleteFile(remoteFile);
                progress.Report($"− deleted {rel}");
                deleted++;
            }
            catch (Exception ex)
            {
                progress.Report($"✗ delete failed {rel}: {ex.Message}");
                logger.LogWarning(ex, "Failed to delete {Path}", rel);
                failed++;
            }
        }

        return (uploaded, deleted, failed);
    }

    private static async Task<Dictionary<string, (long size, DateTime mtime)>> WalkRemoteAsync(
        string host, string user, string pass, bool acceptAnyCert,
        string rootPath, IProgress<string>? progress)
    {
        var files = new Dictionary<string, (long size, DateTime mtime)>();
        var root  = rootPath.TrimEnd('/');
        var queue = new Queue<string>();
        queue.Enqueue(root);

        var ftp = await ConnectFtpAsync(host, user, pass, acceptAnyCert, progress);
        if (ftp is null) return files;

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            progress?.Report($"Scanning {dir}… ({files.Count} files found)");

            var listTask = ftp.GetListing(dir);
            if (await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(25))) != listTask)
            {
                progress?.Report($"⚠ Timeout listing {dir} — skipped, reconnecting…");
                // Dispose via Task.Run: Socket.Disconnect(false) is synchronous and blocks
                // if the server is not responding. Fire-and-forget so we don't inherit the hang.
                DisposeSafely(ftp);
                _ = listTask.ContinueWith(_ => { }, TaskContinuationOptions.None);
                ftp = await ConnectFtpAsync(host, user, pass, acceptAnyCert, progress);
                if (ftp is null) break;
                continue;
            }

            FtpListItem[] items;
            try   { items = await listTask; }
            catch (Exception ex)
            {
                progress?.Report($"⚠ Error listing {dir}: {ex.Message} — skipped");
                DisposeSafely(ftp);
                ftp = await ConnectFtpAsync(host, user, pass, acceptAnyCert, progress);
                if (ftp is null) break;
                continue;
            }

            foreach (var item in items)
            {
                if (item.Type == FtpObjectType.File)
                    files["/" + item.FullName[root.Length..].TrimStart('/')] =
                        (item.Size, item.Modified.ToUniversalTime());
                else if (item.Type == FtpObjectType.Directory)
                    queue.Enqueue(item.FullName);
            }
        }

        DisposeSafely(ftp);
        return files;
    }

    private static async Task<AsyncFtpClient?> ConnectFtpAsync(
        string host, string user, string pass, bool acceptAnyCert, IProgress<string>? progress)
    {
        var ftp = CreateFtpClient(host, user, pass, acceptAnyCert);
        var connectTask = ftp.Connect();
        if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(20))) != connectTask)
        {
            progress?.Report("⚠ FTP connect timed out — scan aborted");
            DisposeSafely(ftp);
            return null;
        }
        try   { await connectTask; return ftp; }
        catch (Exception ex)
        {
            progress?.Report($"⚠ FTP connect failed: {ex.Message} — scan aborted");
            DisposeSafely(ftp);
            return null;
        }
    }

    private static void DisposeSafely(AsyncFtpClient? ftp)
    {
        if (ftp is null) return;
        // Socket.Disconnect(false) inside Dispose() is synchronous and can block indefinitely
        // if the server is not responding to TCP teardown. Run it on the thread pool.
        _ = Task.Run(() => { try { ftp.Dispose(); } catch { } });
    }

    private static AsyncFtpClient CreateFtpClient(string host, string user, string pass, bool acceptAnyCert)
    {
        var ftp = new AsyncFtpClient(host, user, pass);
        ftp.Config.EncryptionMode            = FtpEncryptionMode.Explicit;
        ftp.Config.ValidateAnyCertificate    = acceptAnyCert;
        ftp.Config.ReadTimeout               = 20_000;
        ftp.Config.DataConnectionReadTimeout = 20_000;
        return ftp;
    }

    public static ScanResult Classify(
        Dictionary<string, (long size, DateTime mtime)> localFiles,
        Dictionary<string, (long size, DateTime mtime)> remoteFiles)
    {
        var toUpload   = new List<FileCompareResult>();
        var remoteOnly = new List<string>();
        var upToDate   = 0;

        foreach (var (path, local) in localFiles)
        {
            if (!remoteFiles.TryGetValue(path, out var remote))
            {
                toUpload.Add(new FileCompareResult(path, local.size, null, local.mtime, null, "New"));
            }
            else if (local.size != remote.size)
            {
                toUpload.Add(new FileCompareResult(path, local.size, remote.size, local.mtime, remote.mtime, "Size differs"));
            }
            else
            {
                // FTP returns whole-second precision; truncate both sides before comparing
                // to avoid false positives from sub-second local timestamps.
                var localSec  = TruncateToSeconds(local.mtime);
                var remoteSec = TruncateToSeconds(remote.mtime);
                if (localSec > remoteSec)
                    toUpload.Add(new FileCompareResult(path, local.size, remote.size, local.mtime, remote.mtime, "Local newer"));
                else
                    upToDate++;
            }
        }

        foreach (var path in remoteFiles.Keys)
            if (!localFiles.ContainsKey(path))
                remoteOnly.Add(path);

        toUpload.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        remoteOnly.Sort();

        return new ScanResult(toUpload, remoteOnly, upToDate);
    }

    private static DateTime TruncateToSeconds(DateTime dt) =>
        new(dt.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private string[] GetExcludePatterns()
    {
        var raw = settings.Get("website.exclude_patterns");
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(l => !l.StartsWith('#'))
                  .ToArray();
    }

    private static bool IsExcluded(string relativePath, string[] patterns)
    {
        foreach (var p in patterns)
        {
            if (p.StartsWith('/'))
            {
                if (relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (relativePath.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private (string? user, string? pass) GetCredentials() =>
        (settings.Get("website.ftp_user"), settings.Get("website.ftp_pass"));

    public string GetLocalPath()  => settings.Get("website.local_path")  ?? opts.LocalPath;
    public string GetRemotePath() => settings.Get("website.remote_path") ?? opts.FtpRemotePath;

    private bool GetAcceptAnyCert() =>
        settings.Get("website.ftp_accept_any_cert") == "true";

    private static string NormalizePath(string path) =>
        "/" + path.TrimStart('/').Replace('\\', '/');
}
