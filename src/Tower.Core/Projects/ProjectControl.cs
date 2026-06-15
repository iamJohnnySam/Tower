using System.Diagnostics;

namespace Tower.Core.Projects;

public static class ProjectControl
{
    private static bool Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Sends start/stop/restart to a systemd service via sudo.
    /// Only the three named actions are permitted; any other value returns false.
    /// sudoers must allow: sudo systemctl start|stop|restart &lt;service&gt;
    /// </summary>
    public static bool Control(string service, string action) =>
        (action is "start" or "stop" or "restart") && Run("sudo", "systemctl", action, service);

    /// <summary>
    /// Returns the last <paramref name="lines"/> lines of log output.
    /// Priority: (1) newest 3 *.log files under logDir, (2) journalctl -u service, (3) "(no logs configured)".
    /// Never throws.
    /// </summary>
    public static string ReadLogs(string? logDir, string? service, int lines = 200)
    {
        try
        {
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                var files = Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories)
                    .OrderByDescending(f => Path.GetFileName(f))   // pick newest 3
                    .Take(3)
                    .OrderBy(f => Path.GetFileName(f))             // then chronological so TakeLast = most recent
                    .ToList();

                var all = new List<string>();
                foreach (var f in files)
                    all.AddRange(File.ReadLines(f));

                return string.Join("\n", all.TakeLast(lines));
            }

            if (!string.IsNullOrEmpty(service))
            {
                var psi = new ProcessStartInfo("journalctl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(service);
                psi.ArgumentList.Add("--no-pager");
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(lines.ToString());
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return o;
            }

            return "(no logs configured)";
        }
        catch (Exception ex) { return $"(log read error: {ex.Message})"; }
    }
}
