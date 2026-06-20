using FluentFTP;
using Microsoft.Extensions.Logging;
using Tower.Core.Settings;

namespace Tower.Core.Website;

public record ScanResult(
    List<string> ToUpload,
    List<string> RemoteOnly,
    int UpToDate
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

        progress?.Report("Connecting to FTP…");
        var remoteFiles = await WalkRemoteAsync(opts.FtpHost, user, pass, GetAcceptAnyCert(), remotePath, progress);

        progress?.Report($"Found {remoteFiles.Count} remote files. Counting local files…");
        var localFiles = Directory
            .GetFiles(localPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => NormalizePath(f[localPath.TrimEnd('/').Length..]),
                f => { var fi = new FileInfo(f); return (size: fi.Length, mtime: fi.LastWriteTimeUtc); });

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

        var ftp = CreateFtpClient(host, user, pass, acceptAnyCert);
        await ftp.Connect();

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            progress?.Report($"Scanning {dir}… ({files.Count} files found)");

            // Task.WhenAny + Dispose is the only reliable way to interrupt a hung socket read.
            // CancellationToken alone doesn't unblock blocking FTP data-channel reads.
            var listTask    = ftp.GetListing(dir);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));

            if (await Task.WhenAny(listTask, timeoutTask) == timeoutTask)
            {
                progress?.Report($"⚠ Timeout listing {dir} — skipped, reconnecting…");
                ftp.Dispose();
                // Suppress the now-failing background task to avoid unobserved exception noise.
                _ = listTask.ContinueWith(_ => { }, TaskContinuationOptions.None);
                ftp = CreateFtpClient(host, user, pass, acceptAnyCert);
                try   { await ftp.Connect(); }
                catch { progress?.Report("⚠ Reconnect failed — scan aborted"); break; }
                continue;
            }

            FtpListItem[] items;
            try   { items = await listTask; }
            catch (Exception ex) { progress?.Report($"⚠ Error listing {dir}: {ex.Message} — skipped"); continue; }

            foreach (var item in items)
            {
                if (item.Type == FtpObjectType.File)
                    files["/" + item.FullName[root.Length..].TrimStart('/')] =
                        (item.Size, item.Modified.ToUniversalTime());
                else if (item.Type == FtpObjectType.Directory)
                    queue.Enqueue(item.FullName);
            }
        }

        try { ftp.Dispose(); } catch { }
        return files;
    }

    private static AsyncFtpClient CreateFtpClient(string host, string user, string pass, bool acceptAnyCert)
    {
        var ftp = new AsyncFtpClient(host, user, pass);
        ftp.Config.EncryptionMode      = FtpEncryptionMode.Explicit;
        ftp.Config.ValidateAnyCertificate = acceptAnyCert;
        return ftp;
    }

    public static ScanResult Classify(
        Dictionary<string, (long size, DateTime mtime)> localFiles,
        Dictionary<string, (long size, DateTime mtime)> remoteFiles)
    {
        var toUpload   = new List<string>();
        var remoteOnly = new List<string>();
        var upToDate   = 0;

        foreach (var (path, local) in localFiles)
        {
            if (!remoteFiles.TryGetValue(path, out var remote))
                toUpload.Add(path);
            else if (local.size != remote.size || local.mtime > remote.mtime)
                toUpload.Add(path);
            else
                upToDate++;
        }

        foreach (var path in remoteFiles.Keys)
            if (!localFiles.ContainsKey(path))
                remoteOnly.Add(path);

        toUpload.Sort();
        remoteOnly.Sort();

        return new ScanResult(toUpload, remoteOnly, upToDate);
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
