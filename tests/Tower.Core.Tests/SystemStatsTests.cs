using Tower.Core.Metrics;
using Xunit;

namespace Tower.Core.Tests;

public class SystemStatsTests
{
    // --- Core tests from spec ---

    [Fact]
    public void Cpu_percent_from_two_samples()
    {
        var a = SystemStats.ParseCpuTimes("cpu  100 0 100 700 0 0 0 0 0 0");
        var b = SystemStats.ParseCpuTimes("cpu  150 0 150 900 0 0 0 0 0 0");
        // a: total=900 (100+0+100+700+0*6), idle=700+iowait(0)=700
        // b: total=1200, idle=900
        // dt=300, di=200 -> busy% = (1 - 200/300)*100 = 33.3
        Assert.Equal(33.3, SystemStats.CpuPercent(a, b), 1);
    }

    [Fact]
    public void Parses_meminfo()
    {
        var m = SystemStats.ParseMemInfo("MemTotal: 16000000 kB\nMemAvailable: 8000000 kB\nSwapTotal: 2000000 kB\nSwapFree: 2000000 kB\n");
        Assert.Equal(50.0, m.UsedPct, 1);
    }

    [Fact]
    public void Net_rate_is_delta_over_seconds()
    {
        Assert.Equal(1000, SystemStats.RatePerSec(1000, 3000, 2.0), 1);
    }

    // --- Robustness tests (Step 4) ---

    [Fact]
    public void Parses_meminfo_with_no_swap_does_not_divide_by_zero()
    {
        // SwapTotal=0, SwapFree=0 — UsedPct should still compute from MemTotal/MemAvailable
        var m = SystemStats.ParseMemInfo("MemTotal: 8000000 kB\nMemAvailable: 4000000 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n");
        Assert.Equal(50.0, m.UsedPct, 1);
        Assert.Equal(0UL, m.SwapTotalKb);
        Assert.Equal(0UL, m.SwapFreeKb);
    }

    [Fact]
    public void RatePerSec_with_zero_seconds_returns_zero()
    {
        Assert.Equal(0.0, SystemStats.RatePerSec(0, 9999, 0.0), 1);
    }

    [Fact]
    public void CpuPercent_when_no_elapsed_ticks_returns_zero()
    {
        var a = SystemStats.ParseCpuTimes("cpu  100 0 100 700 0 0 0 0 0 0");
        var b = SystemStats.ParseCpuTimes("cpu  100 0 100 700 0 0 0 0 0 0");
        // b.Total == a.Total — no elapsed ticks
        Assert.Equal(0.0, SystemStats.CpuPercent(a, b), 1);
    }
}
