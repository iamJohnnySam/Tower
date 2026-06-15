namespace Tower.Core.Metrics;

public static class SystemStats
{
    /// <summary>
    /// Parses the first "cpu" line from /proc/stat.
    /// Fields: user nice system idle iowait irq softirq steal guest guest_nice
    /// Idle = idle + iowait (p[4] + p[5]).
    /// </summary>
    public static CpuTimes ParseCpuTimes(string procStatFirstLine)
    {
        var p = procStatFirstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // p[0] == "cpu"
        ulong total = 0;
        for (int i = 1; i < p.Length; i++)
            total += ulong.Parse(p[i]);

        ulong idle = ulong.Parse(p[4]) + (p.Length > 5 ? ulong.Parse(p[5]) : 0); // idle + iowait
        return new CpuTimes(idle, total);
    }

    /// <summary>
    /// Computes CPU busy % from two /proc/stat snapshots.
    /// Returns 0 if no ticks elapsed between samples.
    /// </summary>
    public static double CpuPercent(CpuTimes a, CpuTimes b)
    {
        double dt = b.Total - a.Total;
        double di = b.Idle - a.Idle;
        return dt <= 0 ? 0 : Math.Round((1 - di / dt) * 100, 1);
    }

    /// <summary>
    /// Parses /proc/meminfo text into a MemInfo record.
    /// </summary>
    public static MemInfo ParseMemInfo(string meminfo)
    {
        ulong GetField(string key)
        {
            foreach (var line in meminfo.Split('\n'))
            {
                if (line.StartsWith(key + ":"))
                    return ulong.Parse(line.Split(':')[1].Trim().Split(' ')[0]);
            }
            return 0;
        }

        return new MemInfo(
            GetField("MemTotal"),
            GetField("MemAvailable"),
            GetField("SwapTotal"),
            GetField("SwapFree")
        );
    }

    /// <summary>
    /// Computes bytes-per-second rate from two /proc/net/dev byte counter snapshots.
    /// Returns 0 if seconds &lt;= 0 or if counter wrapped (cur &lt; prev).
    /// </summary>
    public static double RatePerSec(ulong prev, ulong cur, double seconds)
        => seconds <= 0 || cur < prev ? 0 : (cur - prev) / seconds;
}
