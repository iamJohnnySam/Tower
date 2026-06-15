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
            // Filenames sorted descending: app-2026-06-15.log > app-2026-06-14.log
            // ReadLogs reads them in that order and concatenates, then TakeLast(lines).
            // Combined list: [B-1..5, A-1..5], TakeLast(4) = [A-2, A-3, A-4, A-5].
            File.WriteAllLines(Path.Combine(dir, "app-2026-06-14.log"),
                Enumerable.Range(1, 5).Select(i => $"line-A-{i}"));
            File.WriteAllLines(Path.Combine(dir, "app-2026-06-15.log"),
                Enumerable.Range(1, 5).Select(i => $"line-B-{i}"));

            // Ask for last 4 lines across all files (10 total, keep last 4)
            var result = ProjectControl.ReadLogs(dir, service: null, lines: 4);
            var resultLines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(4, resultLines.Length);
            // All 4 lines come from the tail of the concatenated content
            Assert.Contains("line-A-5", result);
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
