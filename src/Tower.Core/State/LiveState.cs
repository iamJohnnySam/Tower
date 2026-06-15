using Tower.Core.Metrics;

namespace Tower.Core.State;

// ─── Jellyfin snapshot ────────────────────────────────────────────────────────

public record JellyfinSnapshot(
    IReadOnlyList<Tower.Core.Jellyfin.SessionInfo> Sessions,
    int FfmpegCount,
    double FfmpegCpu,
    double[] FfmpegCountHistory,
    double[] FfmpegCpuHistory,
    string Error,
    bool ApiConfigured,
    DateTime Updated)
{
    public static JellyfinSnapshot Empty { get; } = new(
        Sessions: Array.Empty<Tower.Core.Jellyfin.SessionInfo>(),
        FfmpegCount: 0,
        FfmpegCpu: 0,
        FfmpegCountHistory: new double[60],
        FfmpegCpuHistory: new double[60],
        Error: "",
        ApiConfigured: false,
        Updated: DateTime.MinValue);
}

// ─── Small value types used in StatsSnapshot ─────────────────────────────────

public record NicInfo(string Name, bool Up, long SpeedMbps, string Ip, ulong Sent, ulong Recv);

public record SizeInfo(string Name, string Path, long Bytes, bool Exists);
public record ProcInfo(int Pid, string Name, string User, double Cpu, long MemBytes);
public record TempInfo(string Chip, string Label, double Temp);
public record PartitionInfo(string Mount, string Fstype, ulong TotalBytes, ulong UsedBytes, ulong FreeBytes, double Pct);

// ─── Snapshot (one point-in-time view for rendering) ─────────────────────────

public class StatsSnapshot
{
    public DateTime Ts { get; init; }
    public string Hostname { get; init; } = "";
    public string Uptime { get; init; } = "";

    // CPU
    public double CpuPct { get; init; }
    public double[] CpuCores { get; init; } = [];
    public int CpuCount { get; init; }
    public double[] Load { get; init; } = [];
    public double FreqMhz { get; init; }
    public double FreqMaxMhz { get; init; }

    // Memory / Swap
    public double MemPct { get; init; }
    public ulong MemUsedBytes { get; init; }
    public ulong MemTotalBytes { get; init; }
    public ulong MemAvailBytes { get; init; }
    public double SwapPct { get; init; }
    public ulong SwapUsedBytes { get; init; }
    public ulong SwapTotalBytes { get; init; }

    // Network
    public double NetRecvRate { get; init; }
    public double NetSentRate { get; init; }
    public List<NicInfo> Nics { get; init; } = [];

    // 60-point rolling history (copies from LiveState queues)
    public double[] CpuHistory { get; init; } = [];
    public double[] RecvHistory { get; init; } = [];
    public double[] SentHistory { get; init; } = [];

    // Processes
    public List<ProcInfo> TopProcs { get; init; } = [];

    // Services
    public Dictionary<string, string> Services { get; init; } = [];

    // Temperatures
    public List<TempInfo> Temps { get; init; } = [];

    // GPU / NoIP (never null — always at least "none" / "unknown")
    public GpuStats Gpu { get; init; } = new GpuStats("none");
    public NoIpStatus NoIp { get; init; } = new NoIpStatus("unknown");

    // Mounted partitions (from DriveInfo)
    public List<PartitionInfo> Partitions { get; init; } = new();
}

// ─── LiveState singleton ──────────────────────────────────────────────────────

public class LiveState
{
    private readonly object _lock = new();

    // ── Stats ──
    private StatsSnapshot _stats = new StatsSnapshot { Ts = DateTime.UtcNow };
    private readonly Queue<double> _cpuQ  = new(60);
    private readonly Queue<double> _recvQ = new(60);
    private readonly Queue<double> _sentQ = new(60);

    // ── SMART ──
    private List<SmartInfo> _disks = [];

    // ── Disk usage ──
    private List<DuItem> _rootSegments    = [];
    private List<DuItem> _varSegments     = [];
    private List<DuItem> _projectSizes    = [];

    // ── DB / log file sizes ──
    private List<SizeInfo> _sizes = [];

    // ── Jellyfin ──
    private JellyfinSnapshot _jellyfin = JellyfinSnapshot.Empty;
    private readonly Queue<double> _ffmpegCountQ = new(60);
    private readonly Queue<double> _ffmpegCpuQ   = new(60);

    // ─── Public read properties ──────────────────────────────────────────────

    public StatsSnapshot Stats
    {
        get { lock (_lock) return _stats; }
    }

    public IReadOnlyList<SmartInfo> Disks
    {
        get { lock (_lock) return _disks.AsReadOnly(); }
    }

    public IReadOnlyList<DuItem> RootSegments
    {
        get { lock (_lock) return _rootSegments.AsReadOnly(); }
    }

    public IReadOnlyList<DuItem> VarSegments
    {
        get { lock (_lock) return _varSegments.AsReadOnly(); }
    }

    public IReadOnlyList<DuItem> ProjectSizes
    {
        get { lock (_lock) return _projectSizes.AsReadOnly(); }
    }

    public IReadOnlyList<SizeInfo> Sizes
    {
        get { lock (_lock) return _sizes.AsReadOnly(); }
    }

    public JellyfinSnapshot Jellyfin
    {
        get { lock (_lock) return _jellyfin; }
    }

    // ─── Write methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Appends a data point to the rolling 60-pt history queues.
    /// Call before SetStats so the snapshot captures the freshly-appended values.
    /// </summary>
    public void PushHistory(double cpu, double recv, double sent)
    {
        lock (_lock)
        {
            Enqueue(_cpuQ,  cpu);
            Enqueue(_recvQ, recv);
            Enqueue(_sentQ, sent);
        }
    }

    /// <summary>
    /// Returns copies of the three history queues as fixed-length (60) arrays.
    /// Pre-padded with zeros on the left so the arrays are always length 60.
    /// Call inside the same lock as PushHistory if you need atomicity,
    /// or just call after PushHistory (StatsWorker pattern).
    /// </summary>
    public (double[] Cpu, double[] Recv, double[] Sent) SnapshotHistory()
    {
        lock (_lock)
        {
            return (ToArray(_cpuQ), ToArray(_recvQ), ToArray(_sentQ));
        }
    }

    public void SetStats(StatsSnapshot s)
    {
        lock (_lock) _stats = s;
    }

    public void SetDisks(List<SmartInfo> disks)
    {
        lock (_lock) _disks = [.. disks];
    }

    public void SetUsage(List<DuItem> root, List<DuItem> var, List<DuItem> projects)
    {
        lock (_lock)
        {
            _rootSegments = [.. root];
            _varSegments  = [.. var];
            _projectSizes = [.. projects];
        }
    }

    public void SetSizes(List<SizeInfo> sizes)
    {
        lock (_lock) _sizes = [.. sizes];
    }

    public void SetJellyfin(JellyfinSnapshot s)
    {
        lock (_lock) _jellyfin = s;
    }

    /// <summary>
    /// Appends a data point to the rolling 60-pt ffmpeg history queues.
    /// Call before SetJellyfin so the snapshot captures the freshly-appended values.
    /// </summary>
    public void PushFfmpegHistory(double count, double cpu)
    {
        lock (_lock)
        {
            Enqueue(_ffmpegCountQ, count);
            Enqueue(_ffmpegCpuQ,   cpu);
        }
    }

    /// <summary>
    /// Returns the ffmpeg count history as a fixed-length (60) array.
    /// Right-aligned, zero-padded on the left.
    /// </summary>
    public double[] FfmpegCountHistory()
    {
        lock (_lock) return ToArray(_ffmpegCountQ);
    }

    /// <summary>
    /// Returns the ffmpeg CPU history as a fixed-length (60) array.
    /// Right-aligned, zero-padded on the left.
    /// </summary>
    public double[] FfmpegCpuHistory()
    {
        lock (_lock) return ToArray(_ffmpegCpuQ);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void Enqueue(Queue<double> q, double v)
    {
        if (q.Count == 60) q.Dequeue();
        q.Enqueue(v);
    }

    private static double[] ToArray(Queue<double> q)
    {
        var arr = new double[60];
        var src = q.ToArray();          // oldest → newest
        // right-align: last src.Length elements fill the right side
        src.CopyTo(arr, 60 - src.Length);
        return arr;
    }
}
