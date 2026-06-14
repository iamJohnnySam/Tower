namespace Tower.Core.Metrics;

public record CpuTimes(ulong Idle, ulong Total);

public record MemInfo(ulong TotalKb, ulong AvailableKb, ulong SwapTotalKb, ulong SwapFreeKb)
{
    public double UsedPct => TotalKb == 0 ? 0 : 100.0 * (TotalKb - AvailableKb) / TotalKb;
}

public record NetCounters(ulong RecvBytes, ulong SentBytes);
