using Tower.Core.Projects;
using Xunit;

namespace Tower.Core.Tests;

public class ProjectMonitorTests
{
    [Fact]
    public async Task Port_open_true_for_listening_port_false_for_closed()
    {
        // start a throwaway TCP listener on an ephemeral port
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        Assert.True(await ProjectMonitor.IsPortOpenAsync(port, 500));
        l.Stop();
        Assert.False(await ProjectMonitor.IsPortOpenAsync(port, 300));
    }
}

public class ProjectControlTests
{
    [Fact]
    public void ReadLogs_returns_last_N_lines_from_log_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tower-test-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Files are read in chronological (ascending filename) order so TakeLast gives most-recent lines.
            // Combined list chronologically: [A-1..5, B-1..5], TakeLast(4) = [B-2, B-3, B-4, B-5].
            File.WriteAllLines(Path.Combine(dir, "app-2026-06-14.log"),
                Enumerable.Range(1, 5).Select(i => $"line-A-{i}"));
            File.WriteAllLines(Path.Combine(dir, "app-2026-06-15.log"),
                Enumerable.Range(1, 5).Select(i => $"line-B-{i}"));

            // Ask for last 4 lines across all files (10 total, keep last 4)
            var result = ProjectControl.ReadLogs(dir, service: null, lines: 4);
            var resultLines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(4, resultLines.Length);
            // All 4 lines come from the tail of the newer file
            Assert.Contains("line-B-5", result);
            Assert.DoesNotContain("line-A-5", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLogs_newest_file_content_wins()
    {
        // Proves: with lines=1, the single returned line must come from the newer file.
        var dir = Path.Combine(Path.GetTempPath(), $"tower-test-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "2026-01-01.log"), "OLD\n");
            File.WriteAllText(Path.Combine(dir, "2026-02-01.log"), "NEW\n");

            var result = ProjectControl.ReadLogs(dir, service: null, lines: 1);
            Assert.Equal("NEW", result.Trim());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLogs_returns_no_logs_configured_when_both_null()
    {
        var result = ProjectControl.ReadLogs(logDir: null, service: null);
        Assert.Equal("(no logs configured)", result);
    }

    [Fact]
    public void Control_rejects_invalid_action()
    {
        // "delete" is not in {start, stop, restart} — must return false without shelling out
        var result = ProjectControl.Control("mediabox", "delete");
        Assert.False(result);
    }
}
