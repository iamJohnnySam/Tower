using System.Net.Sockets;
using System.Diagnostics;

namespace Tower.Core.Projects;

public static class ProjectMonitor
{
    public static async Task<bool> IsPortOpenAsync(int port, int timeoutMs)
    {
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            return done == connect && c.Connected;
        }
        catch { return false; }
    }

    public static string Systemd(string? service)
    {
        if (string.IsNullOrEmpty(service)) return "n/a";
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("is-active");
            psi.ArgumentList.Add(service);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return o;
        }
        catch { return "unknown"; }
    }
}
