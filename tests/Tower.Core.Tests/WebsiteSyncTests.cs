using Tower.Core.Website;

namespace Tower.Core.Tests;

public class WebsiteSyncTests
{
    [Fact]
    public void Classify_NewLocalFile_AddedToUpload()
    {
        var local = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/index.php"] = (100, DateTime.UtcNow)
        };
        var remote = new Dictionary<string, (long size, DateTime mtime)>();

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/index.php", result.ToUpload[0].Path);
        Assert.Equal("New",        result.ToUpload[0].Reason);
        Assert.Empty(result.RemoteOnly);
        Assert.Equal(0, result.UpToDate);
    }

    [Fact]
    public void Classify_RemoteOnlyFile_AddedToRemoteOnly()
    {
        var local = new Dictionary<string, (long size, DateTime mtime)>();
        var remote = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/old.php"] = (50, DateTime.UtcNow)
        };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Empty(result.ToUpload);
        Assert.Single(result.RemoteOnly);
        Assert.Equal("/old.php", result.RemoteOnly[0]);
        Assert.Equal(0, result.UpToDate);
    }

    [Fact]
    public void Classify_SameSizeAndRemoteNewer_CountedAsUpToDate()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/style.css"] = (200, now.AddHours(-1)) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/style.css"] = (200, now) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Empty(result.ToUpload);
        Assert.Empty(result.RemoteOnly);
        Assert.Equal(1, result.UpToDate);
    }

    [Fact]
    public void Classify_LocalNewer_AddedToUpload()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/index.php"] = (100, now) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/index.php"] = (100, now.AddHours(-1)) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/index.php",   result.ToUpload[0].Path);
        Assert.Equal("Local newer",  result.ToUpload[0].Reason);
    }

    [Fact]
    public void Classify_DifferentSize_AddedToUpload()
    {
        var now = DateTime.UtcNow;
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/app.js"] = (300, now.AddHours(-1)) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/app.js"] = (200, now.AddHours(-2)) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Single(result.ToUpload);
        Assert.Equal("/app.js",      result.ToUpload[0].Path);
        Assert.Equal("Size differs", result.ToUpload[0].Reason);
    }

    [Fact]
    public void Classify_SubSecondLocalTimestamp_NotFalsePositive()
    {
        // FTP returns whole-second precision; local mtime may have sub-second ticks.
        // A file with local mtime X.999ms vs remote mtime X.000ms should be Up To Date.
        var baseTime = new DateTime(2026, 6, 20, 10, 30, 45, DateTimeKind.Utc);
        var local  = new Dictionary<string, (long size, DateTime mtime)> { ["/file.js"] = (100, baseTime.AddMilliseconds(999)) };
        var remote = new Dictionary<string, (long size, DateTime mtime)> { ["/file.js"] = (100, baseTime) };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Empty(result.ToUpload);
        Assert.Equal(1, result.UpToDate);
    }

    [Fact]
    public void Classify_MixedFiles_AllGroupsCorrect()
    {
        var now = DateTime.UtcNow;
        var local = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/index.php"]  = (100, now),                  // new
            ["/style.css"]  = (200, now.AddHours(-1)),     // up to date
            ["/updated.js"] = (300, now),                  // local newer
        };
        var remote = new Dictionary<string, (long size, DateTime mtime)>
        {
            ["/style.css"]  = (200, now),                  // matches
            ["/updated.js"] = (300, now.AddHours(-1)),     // remote older
            ["/old.html"]   = (50,  now.AddDays(-10)),     // remote only
        };

        var result = FtpSyncService.Classify(local, remote);

        Assert.Equal(2, result.ToUpload.Count);
        Assert.Contains(result.ToUpload, f => f.Path == "/index.php");
        Assert.Contains(result.ToUpload, f => f.Path == "/updated.js");
        Assert.Single(result.RemoteOnly);
        Assert.Equal("/old.html", result.RemoteOnly[0]);
        Assert.Equal(1, result.UpToDate);
    }
}
