using Microsoft.Extensions.Hosting;
using Tower.Core.Projects;
using Tower.Core.State;

namespace Tower.Core.Workers;

/// <summary>
/// Background service that refreshes project status every 5 s.
/// Reads ProjectsOptions (Core-side mirror of TowerConfig.Projects, populated by Program.cs).
/// Writes into LiveState.SetProjects so the UI always has fresh data.
/// </summary>
public class ProjectsWorker(LiveState state, ProjectsOptions opts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var list = new List<ProjectStatus>();

                foreach (var p in opts.Projects)
                {
                    var systemdStatus = ProjectMonitor.Systemd(p.Service);

                    var portOpen = p.Port.HasValue
                        && await ProjectMonitor.IsPortOpenAsync(p.Port.Value, 500);

                    // Match against top-20 visible procs by name or service name (case-insensitive).
                    // A quiet/sleeping project may not appear in top-20 — ProcRunning reflects visibility only.
                    var topProcs = state.Stats.TopProcs;
                    var proc = topProcs.FirstOrDefault(pr =>
                        (!string.IsNullOrEmpty(p.Name)    && pr.Name.Contains(p.Name,    StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(p.Service) && pr.Name.Contains(p.Service, StringComparison.OrdinalIgnoreCase)));

                    double cpu      = proc is not null ? proc.Cpu      : 0;
                    long   memBytes = proc is not null ? proc.MemBytes : 0;
                    bool   running  = proc is not null;

                    list.Add(new ProjectStatus(
                        Name:          p.Name,
                        Service:       p.Service,
                        Port:          p.Port,
                        Url:           p.Url,
                        SystemdStatus: systemdStatus,
                        PortOpen:      portOpen,
                        Cpu:           cpu,
                        MemBytes:      memBytes,
                        ProcRunning:   running));
                }

                state.SetProjects(list);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[projects] {ex.Message}");
            }

            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
