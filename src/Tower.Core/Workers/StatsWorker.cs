using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Tower.Core.Metrics;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class StatsWorker(LiveState state) : BackgroundService
{
    private const int IntervalMs = 2000;

    public static readonly string[] MonitoredServices =
    [
        "pihole-FTL", "jellyfin", "smbd", "nmbd", "tailscaled",
        "ssh", "cron", "nginx", "apache2", "docker", "fail2ban"
    ];

    // Mutable state that persists across iterations
    private CpuTimes   _prevCpuAll  = new(0, 0);
    private CpuTimes[] _prevCpuCores = [];
    private ulong      _prevRecvBytes;
    private ulong      _prevSentBytes;
    private DateTime   _prevTime = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the CPU delta by taking a first reading before the loop.
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/stat", stoppingToken);
            _prevCpuAll   = SystemStats.ParseCpuTimes(lines[0]);
            _prevCpuCores = ParseAllCoreTimes(lines);
            (_prevRecvBytes, _prevSentBytes) = ReadNetTotals();
            _prevTime = DateTime.UtcNow;
        }
        catch { /* non-fatal: first iteration delta will be 0 */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = await BuildSnapshotAsync(stoppingToken);
                state.SetStats(snap);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[StatsWorker] {ex.Message}");
            }

            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    // ─── Public so tests can call it directly ─────────────────────────────────

    public async Task<StatsSnapshot> BuildSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // ── /proc/stat — CPU ─────────────────────────────────────────────────
        var statLines   = await File.ReadAllLinesAsync("/proc/stat", ct);
        var curCpuAll   = SystemStats.ParseCpuTimes(statLines[0]);
        var curCpuCores = ParseAllCoreTimes(statLines);

        double cpuPct = SystemStats.CpuPercent(_prevCpuAll, curCpuAll);

        double[] corePcts;
        if (_prevCpuCores.Length == curCpuCores.Length && curCpuCores.Length > 0)
        {
            corePcts = new double[curCpuCores.Length];
            for (int i = 0; i < curCpuCores.Length; i++)
                corePcts[i] = SystemStats.CpuPercent(_prevCpuCores[i], curCpuCores[i]);
        }
        else
        {
            corePcts = curCpuCores.Length > 0 ? new double[curCpuCores.Length] : [];
        }

        _prevCpuAll   = curCpuAll;
        _prevCpuCores = curCpuCores;

        // ── /proc/loadavg ────────────────────────────────────────────────────
        var load = ReadLoadAvg();

        // ── /proc/cpuinfo — frequency ────────────────────────────────────────
        double freqMhz    = await ReadCpuFreqMhzAsync(ct);
        double freqMaxMhz = ReadCpuMaxFreqMhz();

        // ── /proc/meminfo ────────────────────────────────────────────────────
        var memText = await File.ReadAllTextAsync("/proc/meminfo", ct);
        var mem     = SystemStats.ParseMemInfo(memText);
        ulong memUsed   = (mem.TotalKb - mem.AvailableKb) * 1024;
        ulong memTotal  = mem.TotalKb  * 1024;
        ulong memAvail  = mem.AvailableKb * 1024;
        ulong swapUsed  = (mem.SwapTotalKb - mem.SwapFreeKb) * 1024;
        ulong swapTotal = mem.SwapTotalKb * 1024;
        double swapPct  = mem.SwapTotalKb == 0 ? 0
            : 100.0 * (mem.SwapTotalKb - mem.SwapFreeKb) / mem.SwapTotalKb;

        // ── /proc/net/dev — network rates ────────────────────────────────────
        double elapsed = (now - _prevTime).TotalSeconds;
        (ulong curRecv, ulong curSent) = ReadNetTotals();
        double recvRate = SystemStats.RatePerSec(_prevRecvBytes, curRecv, elapsed);
        double sentRate = SystemStats.RatePerSec(_prevSentBytes, curSent, elapsed);
        _prevRecvBytes = curRecv;
        _prevSentBytes = curSent;
        _prevTime      = now;

        // ── Push to rolling history ───────────────────────────────────────────
        state.PushHistory(cpuPct, recvRate, sentRate);
        var (cpuHist, recvHist, sentHist) = state.SnapshotHistory();

        // ── NICs (NetworkInterface + /proc/net/dev) ──────────────────────────
        var nics = BuildNics();

        // ── Top procs (ps) ────────────────────────────────────────────────────
        var procs = await ReadTopProcsAsync(ct);

        // ── Services ──────────────────────────────────────────────────────────
        var services = new Dictionary<string, string>();
        foreach (var svc in MonitoredServices)
            services[svc] = ServiceCollector.Status(svc);

        // ── Temperatures (hwmon) ──────────────────────────────────────────────
        var temps = ReadHwmonTemps();

        // ── GPU + NoIP (both are internally cached) ───────────────────────────
        var gpu  = GpuCollector.Collect();
        var noip = NoIpCollector.Collect();

        // ── Hostname / Uptime ─────────────────────────────────────────────────
        string hostname = System.Net.Dns.GetHostName();
        string uptime   = await ReadUptimeStringAsync(ct);

        return new StatsSnapshot
        {
            Ts            = now,
            Hostname      = hostname,
            Uptime        = uptime,
            CpuPct        = cpuPct,
            CpuCores      = corePcts,
            CpuCount      = corePcts.Length,
            Load          = load,
            FreqMhz       = freqMhz,
            FreqMaxMhz    = freqMaxMhz,
            MemPct        = mem.UsedPct,
            MemUsedBytes  = memUsed,
            MemTotalBytes = memTotal,
            MemAvailBytes = memAvail,
            SwapPct       = swapPct,
            SwapUsedBytes = swapUsed,
            SwapTotalBytes= swapTotal,
            NetRecvRate   = recvRate,
            NetSentRate   = sentRate,
            Nics          = nics,
            CpuHistory    = cpuHist,
            RecvHistory   = recvHist,
            SentHistory   = sentHist,
            TopProcs      = procs,
            Services      = services,
            Temps         = temps,
            Gpu           = gpu,
            NoIp          = noip,
        };
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    // Parse cpu0, cpu1, … lines from /proc/stat
    private static CpuTimes[] ParseAllCoreTimes(string[] statLines)
    {
        var list = new List<CpuTimes>();
        foreach (var line in statLines)
        {
            if (line.StartsWith("cpu") && line.Length > 3 && char.IsDigit(line[3]))
                list.Add(SystemStats.ParseCpuTimes(line));
        }
        return [.. list];
    }

    private static double[] ReadLoadAvg()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');
            return
            [
                double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
            ];
        }
        catch { return [0, 0, 0]; }
    }

    // Average cpu MHz from /proc/cpuinfo
    private static async Task<double> ReadCpuFreqMhzAsync(CancellationToken ct)
    {
        try
        {
            var text   = await File.ReadAllTextAsync("/proc/cpuinfo", ct);
            var values = new List<double>();
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase)) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                if (double.TryParse(line[(colon + 1)..].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var mhz))
                    values.Add(mhz);
            }
            return values.Count > 0 ? values.Average() : 0;
        }
        catch { return 0; }
    }

    // Max freq from cpufreq (kHz → MHz)
    private static double ReadCpuMaxFreqMhz()
    {
        try
        {
            var path = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";
            if (!File.Exists(path)) return 0;
            var text = File.ReadAllText(path).Trim();
            return ulong.TryParse(text, out var khz) ? khz / 1000.0 : 0;
        }
        catch { return 0; }
    }

    // Read /proc/net/dev and sum bytes for all non-lo interfaces
    private static (ulong Recv, ulong Sent) ReadNetTotals()
    {
        ulong recv = 0, sent = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/net/dev"))
            {
                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                var iface = trimmed[..colon].Trim();
                if (iface == "lo") continue;
                var parts = trimmed[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 9) continue;
                if (ulong.TryParse(parts[0], out var r)) recv += r;   // bytes received
                if (ulong.TryParse(parts[8], out var s)) sent += s;   // bytes sent
            }
        }
        catch { /* return zeros */ }
        return (recv, sent);
    }

    // Build NicInfo list combining NetworkInterface + /proc/net/dev cumulative bytes
    private static List<NicInfo> BuildNics()
    {
        // Collect cumulative counters from /proc/net/dev
        var devCounters = new Dictionary<string, (ulong Recv, ulong Sent)>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/net/dev"))
            {
                var trimmed = line.Trim();
                var colon   = trimmed.IndexOf(':');
                if (colon < 0) continue;
                var iface = trimmed[..colon].Trim();
                var parts = trimmed[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 9) continue;
                ulong.TryParse(parts[0], out var r);
                ulong.TryParse(parts[8], out var s);
                devCounters[iface] = (r, s);
            }
        }
        catch { /* best-effort */ }

        var nics = new List<NicInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Name == "lo") continue;

                bool up        = nic.OperationalStatus == OperationalStatus.Up;
                long speedMbps = nic.Speed / 1_000_000;

                string ip = "";
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ip = addr.Address.ToString();
                        break;
                    }
                }

                devCounters.TryGetValue(nic.Name, out var counters);
                nics.Add(new NicInfo(nic.Name, up, speedMbps, ip, counters.Sent, counters.Recv));
            }
        }
        catch { /* return whatever we built */ }
        return nics;
    }

    // Run `ps` and parse the top 20 processes by CPU
    private static async Task<List<ProcInfo>> ReadTopProcsAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("ps",
                "-eo pid,comm,user,%cpu,rss --sort=-%cpu --no-headers")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var result = new List<ProcInfo>();
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[0], out var pid)) continue;
                var name = parts[1];
                var user = parts[2];
                double.TryParse(parts[3],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var cpu);
                long.TryParse(parts[4], out var rssKb);
                result.Add(new ProcInfo(pid, name, user, cpu, rssKb * 1024));
                if (result.Count >= 20) break;
            }
            return result;
        }
        catch { return []; }
    }

    // Read all hwmon temps from /sys/class/hwmon/hwmon*/
    private static List<TempInfo> ReadHwmonTemps()
    {
        var temps = new List<TempInfo>();
        try
        {
            var hwmonRoot = "/sys/class/hwmon";
            if (!Directory.Exists(hwmonRoot)) return temps;

            foreach (var dir in Directory.EnumerateDirectories(hwmonRoot))
            {
                string chip = "";
                try
                {
                    var namePath = Path.Combine(dir, "name");
                    chip = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : Path.GetFileName(dir);
                }
                catch { chip = Path.GetFileName(dir); }

                try
                {
                    foreach (var inputFile in Directory.EnumerateFiles(dir, "temp*_input"))
                    {
                        try
                        {
                            var raw = File.ReadAllText(inputFile).Trim();
                            if (!long.TryParse(raw, out var milli)) continue;
                            double tempC = milli / 1000.0;

                            // Derive the base like "temp1" from "temp1_input"
                            var fileName = Path.GetFileName(inputFile);
                            var baseName = fileName[..fileName.IndexOf('_')];
                            var labelPath = Path.Combine(dir, baseName + "_label");
                            var label = File.Exists(labelPath) ? File.ReadAllText(labelPath).Trim() : chip;

                            temps.Add(new TempInfo(chip, label, tempC));
                        }
                        catch { /* skip this sensor */ }
                    }
                }
                catch { /* skip this hwmon dir */ }
            }
        }
        catch { /* no hwmon at all */ }
        return temps;
    }

    // Format uptime from /proc/uptime
    private static async Task<string> ReadUptimeStringAsync(CancellationToken ct)
    {
        try
        {
            var text    = await File.ReadAllTextAsync("/proc/uptime", ct);
            var seconds = (long)double.Parse(
                text.Split(' ')[0],
                System.Globalization.CultureInfo.InvariantCulture);
            return FormatUptime(seconds);
        }
        catch { return ""; }
    }

    public static string FormatUptime(long totalSeconds)
    {
        long days  = totalSeconds / 86400;
        long hours = (totalSeconds % 86400) / 3600;
        long mins  = (totalSeconds % 3600) / 60;
        long secs  = totalSeconds % 60;

        if (days > 0)  return $"{days}d {hours}h {mins}m {secs}s";
        if (hours > 0) return $"{hours}h {mins}m {secs}s";
        if (mins > 0)  return $"{mins}m {secs}s";
        return $"{secs}s";
    }
}
