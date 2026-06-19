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

    public async Task<ScanResult> ScanAsync()
    {
        var (user, pass) = GetCredentials();
        if (user is null || pass is null)
            throw new InvalidOperationException("FTP credentials not configured.");

        if (!Directory.Exists(opts.LocalPath))
            throw new DirectoryNotFoundException($"Local path not found: {opts.LocalPath}");

        using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
        ftp.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        ftp.Config.ValidateAnyCertificate = GetAcceptAnyCert();
        await ftp.Connect();

        var remoteItems = await ftp.GetListing(opts.FtpRemotePath, FtpListOption.Recursive);
        var prefix = opts.FtpRemotePath.TrimEnd('/');
        var remoteFiles = remoteItems
            .Where(i => i.Type == FtpObjectType.File && i.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                i => NormalizePath(i.FullName[prefix.Length..]),
                i => (size: i.Size, mtime: i.Modified.ToUniversalTime()));

        var localFiles = Directory
            .GetFiles(opts.LocalPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => NormalizePath(f[opts.LocalPath.TrimEnd('/').Length..]),
                f => { var fi = new FileInfo(f); return (size: fi.Length, mtime: fi.LastWriteTimeUtc); });

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

        using var ftp = new AsyncFtpClient(opts.FtpHost, user, pass);
        ftp.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        ftp.Config.ValidateAnyCertificate = GetAcceptAnyCert();
        await ftp.Connect();

        foreach (var rel in filesToUpload)
        {
            ct.ThrowIfCancellationRequested();
            var localFile  = Path.Combine(opts.LocalPath.TrimEnd('/'), rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var remotePath = opts.FtpRemotePath.TrimEnd('/') + rel;
            try
            {
                await ftp.UploadFile(localFile, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
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
            var remotePath = opts.FtpRemotePath.TrimEnd('/') + rel;
            try
            {
                await ftp.DeleteFile(remotePath);
                progress.Report($"✗ deleted {rel}");
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

    private bool GetAcceptAnyCert() =>
        settings.Get("website.ftp_accept_any_cert") == "true";

    private static string NormalizePath(string path) =>
        "/" + path.TrimStart('/').Replace('\\', '/');
}
