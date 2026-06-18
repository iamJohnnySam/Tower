using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Maintenance;

public class MaintenanceOptions
{
    public string ScriptPath { get; set; } = "do_maintenance.sh";
    public string LogPath    { get; set; } = "maintenance.log";
}

public record MaintenanceResult(string Status, string? Message = null);

/// <summary>
/// Runs the apt maintenance script with safety gates and records results.
/// Not a BackgroundService — called by MaintenanceScheduler.
/// </summary>
public class MaintenanceRunner(IServiceScopeFactory scopes, MaintenanceOptions opts)
{
    private static readonly object _gate    = new();
    private static          bool   _running = false;

    public MaintenanceResult Run(string trigger)
    {
        // ── Concurrency gate ─────────────────────────────────────────────────
        lock (_gate)
        {
            if (_running)
                return new MaintenanceResult("error", "Already running");
            _running = true;
        }

        var log = new List<string>();
        string action = "error";
        string? errorMsg = null;

        try
        {
            string ts() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            log.Add($"[{ts()}] Maintenance triggered by: {trigger}");

            // ── Gate 1: uptime ───────────────────────────────────────────────
            double uptimeSecs = ReadUptimeSecs();
            if (uptimeSecs < 600)
            {
                action = "skipped";
                log.Add($"[{ts()}] Skipped: uptime {uptimeSecs:F0}s < 600s minimum");
                AppendLog(log);
                return new MaintenanceResult("skipped", $"Uptime {uptimeSecs:F0}s is below the 600s minimum");
            }

            // ── Gate 2: apt-get already running ─────────────────────────────
            if (IsAptRunning())
            {
                action = "skipped";
                log.Add($"[{ts()}] Skipped: apt-get already running");
                AppendLog(log);
                return new MaintenanceResult("skipped", "apt-get already running");
            }

            // ── Run the script ───────────────────────────────────────────────
            log.Add($"[{ts()}] Running: sudo {opts.ScriptPath}");
            var psi = new ProcessStartInfo("sudo", opts.ScriptPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            string stdout, stderr;
            int exitCode;
            try
            {
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start process");

                // Read output with timeout
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                bool finished = proc.WaitForExit(900_000); // 15 minutes
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();

                if (!finished)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    action   = "error";
                    errorMsg = "Script timed out after 15 minutes";
                    log.Add($"[{ts()}] ERROR: {errorMsg}");
                    Persist(action, log);
                    AppendLog(log);
                    return new MaintenanceResult("error", errorMsg);
                }

                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                action   = "error";
                errorMsg = ex.Message;
                log.Add($"[{ts()}] ERROR launching script: {ex.Message}");
                Persist(action, log);
                AppendLog(log);
                return new MaintenanceResult("error", ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(stdout)) log.Add(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) log.Add($"[stderr] {stderr.TrimEnd()}");

            if (exitCode != 0)
            {
                action = "failed";
                log.Add($"[{ts()}] Script exited with code {exitCode}");
            }
            else
            {
                action = "success";
                log.Add($"[{ts()}] Script completed successfully");

                // ── Optional reboot ──────────────────────────────────────────
                bool rebootEnabled = true;
                try
                {
                    using var scope = scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    var val = svc.Get("maint.reboot_enabled");
                    if (val is not null)
                        rebootEnabled = val.Trim().ToLowerInvariant() == "true";
                }
                catch { /* default to true */ }

                if (rebootEnabled && File.Exists("/var/run/reboot-required"))
                {
                    if (uptimeSecs < 1800)
                    {
                        log.Add($"[{ts()}] Reboot required but skipping: uptime {uptimeSecs:F0}s < 1800s");
                    }
                    else
                    {
                        log.Add($"[{ts()}] Reboot required — scheduling reboot in 2 minutes");
                        action = "success+reboot";
                        try
                        {
                            Process.Start(new ProcessStartInfo(
                                "sudo",
                                "/sbin/shutdown -r +2 \"Scheduled maintenance reboot — Tower\"")
                            {
                                UseShellExecute = false,
                            });
                        }
                        catch (Exception ex)
                        {
                            log.Add($"[{ts()}] WARNING: could not schedule reboot: {ex.Message}");
                        }
                    }
                }
            }

            Persist(action, log);
            AppendLog(log);
            return new MaintenanceResult(action);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            log.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED ERROR: {msg}");
            try { AppendLog(log); } catch { }
            return new MaintenanceResult("error", msg);
        }
        finally
        {
            lock (_gate) { _running = false; }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static double ReadUptimeSecs()
    {
        try
        {
            var text = File.ReadAllText("/proc/uptime");
            return double.Parse(text.Split(' ')[0], CultureInfo.InvariantCulture);
        }
        catch { return 0; } // fail-closed: if we can't read uptime, treat as just-booted and skip
    }

    private static bool IsAptRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("pgrep", "-x apt-get")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private void AppendLog(List<string> lines)
    {
        try
        {
            var sep      = new string('-', 60);
            var content  = string.Join('\n', lines) + '\n' + sep + '\n';
            File.AppendAllText(opts.LogPath, content);
        }
        catch { /* non-fatal */ }
    }

    private void Persist(string action, List<string> lines)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SettingsService>();
            svc.Set("maint.last_date",   DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            svc.Set("maint.last_ts",     DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            svc.Set("maint.last_result", action);
            svc.Set("maint.last_log",    string.Join('\n', lines));
        }
        catch { /* non-fatal */ }
    }
}
