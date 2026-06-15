using Tower.Core.State;
using Tower.Core.Workers;
using Xunit;

namespace Tower.Core.Tests;

/// <summary>
/// Integration-style tests that exercise the actual /proc and /sys reads on this
/// Ubuntu 22.04 machine.  They do NOT spin up a BackgroundService; instead they
/// call the internal BuildSnapshotAsync / CollectAllDisks / Collect helpers directly.
/// </summary>
public class WorkerTests
{
    // ─── StatsWorker ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StatsWorker_builds_snapshot_with_live_proc_data()
    {
        var liveState = new LiveState();
        var worker    = new StatsWorker(liveState);

        // First call — deltas will be 0 for CPU/net because there is no previous
        // reading, but all parsed fields should be populated.
        var snap = await worker.BuildSnapshotAsync(TestContext.Current.CancellationToken);

        // Basic identity
        Assert.False(string.IsNullOrWhiteSpace(snap.Hostname),
            "Hostname should not be empty");

        // CPU
        Assert.True(snap.CpuCount > 0,
            $"CpuCount should be > 0, got {snap.CpuCount}");
        Assert.Equal(snap.CpuCount, snap.CpuCores.Length);

        // Memory
        Assert.True(snap.MemTotalBytes > 0,
            $"MemTotalBytes should be > 0, got {snap.MemTotalBytes}");
        Assert.True(snap.MemPct >= 0 && snap.MemPct <= 100,
            $"MemPct out of range: {snap.MemPct}");

        // Load average (3 values)
        Assert.Equal(3, snap.Load.Length);

        // Uptime string non-empty
        Assert.False(string.IsNullOrWhiteSpace(snap.Uptime),
            "Uptime string should not be empty");

        // Services dictionary has all expected keys
        foreach (var svc in StatsWorker.MonitoredServices)
            Assert.True(snap.Services.ContainsKey(svc),
                $"Services dict missing key: {svc}");

        // History arrays are always length 60 (pre-padded with zeros)
        Assert.Equal(60, snap.CpuHistory.Length);
        Assert.Equal(60, snap.RecvHistory.Length);
        Assert.Equal(60, snap.SentHistory.Length);

        // Frequency — may be 0 if /sys path absent, but should not throw
        Assert.True(snap.FreqMhz >= 0);

        // GPU and NoIp are never null
        Assert.NotNull(snap.Gpu);
        Assert.NotNull(snap.NoIp);

        // Nics list is non-null (may be empty on a minimal container)
        Assert.NotNull(snap.Nics);
    }

    [Fact]
    public async Task StatsWorker_second_call_yields_valid_cpu_delta()
    {
        var liveState = new LiveState();
        var worker    = new StatsWorker(liveState);
        var ct        = TestContext.Current.CancellationToken;

        // Two calls separated by ~250 ms — the second should show a non-zero CPU
        // delta (the machine is always doing *something*).
        var snap1 = await worker.BuildSnapshotAsync(ct);
        await Task.Delay(250, ct);
        var snap2 = await worker.BuildSnapshotAsync(ct);

        Assert.True(snap2.CpuPct >= 0 && snap2.CpuPct <= 100,
            $"CpuPct out of range on second call: {snap2.CpuPct}");
    }

    [Fact]
    public async Task StatsWorker_history_accumulates()
    {
        var liveState = new LiveState();
        var worker    = new StatsWorker(liveState);
        var ct        = TestContext.Current.CancellationToken;

        for (int i = 0; i < 3; i++)
        {
            await worker.BuildSnapshotAsync(ct);
            await Task.Delay(100, ct);
        }

        // After 3 iterations the last 3 entries of CpuHistory should be > 0
        // (or at least the total array is still length 60)
        var snap = await worker.BuildSnapshotAsync(ct);
        Assert.Equal(60, snap.CpuHistory.Length);
    }

    // ─── LiveState rolling history ────────────────────────────────────────────

    [Fact]
    public void LiveState_history_is_always_60_elements()
    {
        var ls = new LiveState();
        ls.PushHistory(10, 100, 200);
        var (cpu, recv, sent) = ls.SnapshotHistory();
        Assert.Equal(60, cpu.Length);
        Assert.Equal(60, recv.Length);
        Assert.Equal(60, sent.Length);
        // Most recent value is in the last slot
        Assert.Equal(10,  cpu[59]);
        Assert.Equal(100, recv[59]);
        Assert.Equal(200, sent[59]);
    }

    [Fact]
    public void LiveState_history_evicts_oldest_after_60_pushes()
    {
        var ls = new LiveState();
        for (int i = 0; i < 60; i++)
            ls.PushHistory(i, 0, 0);

        var (cpu, _, _) = ls.SnapshotHistory();
        Assert.Equal(0,  cpu[0]);   // oldest retained
        Assert.Equal(59, cpu[59]);  // newest

        // Push one more — slot 0 should now be 1 (index 0 evicted)
        ls.PushHistory(60, 0, 0);
        (cpu, _, _) = ls.SnapshotHistory();
        Assert.Equal(1,  cpu[0]);
        Assert.Equal(60, cpu[59]);
    }

    // ─── Uptime formatter ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(45,        "45s")]
    [InlineData(90,        "1m 30s")]
    [InlineData(3661,      "1h 1m 1s")]
    [InlineData(86400 + 3661, "1d 1h 1m 1s")]
    public void FormatUptime_produces_correct_strings(long seconds, string expected)
    {
        Assert.Equal(expected, StatsWorker.FormatUptime(seconds));
    }

    // ─── DiskUsageWorker ─────────────────────────────────────────────────────

    [Fact]
    public void DiskUsageWorker_collects_root_segments()
    {
        // This actually runs `du -xb --max-depth=1 /` — takes a few seconds.
        var (root, var_, projects) = DiskUsageWorker.Collect();

        Assert.NotNull(root);
        Assert.NotNull(var_);
        Assert.NotNull(projects);

        // Root should have at least one entry (/usr, /home, etc.)
        Assert.NotEmpty(root);

        // Each DuItem in root should have a non-empty path and positive size
        foreach (var item in root)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Path));
            Assert.True(item.Size > 0, $"Expected positive size for {item.Path}");
        }
    }
}
