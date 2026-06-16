using Tower;

namespace Tower.Core.Tests;

/// <summary>
/// Unit tests for MediaBoxScheduler.IsDue — the pure due-calc helper that drives Task 8's
/// per-job cadence logic. MediaBoxScheduler itself lives in the Tower web project (it needs
/// the concrete MediaBoxClient type), so this test project references Tower.csproj directly
/// to reach the static method without duplicating it.
/// </summary>
public class MediaBoxSchedulerTests
{
    [Fact]
    public void IsDue_true_when_interval_elapsed() =>
        Assert.True(MediaBoxScheduler.IsDue(new DateTime(2026, 1, 1, 0, 5, 0), new DateTime(2026, 1, 1, 0, 0, 0), TimeSpan.FromMinutes(5)));

    [Fact]
    public void IsDue_false_before_interval() =>
        Assert.False(MediaBoxScheduler.IsDue(new DateTime(2026, 1, 1, 0, 4, 0), new DateTime(2026, 1, 1, 0, 0, 0), TimeSpan.FromMinutes(5)));

    [Fact]
    public void IsDue_true_for_never_run() =>
        Assert.True(MediaBoxScheduler.IsDue(DateTime.Now, DateTime.MinValue, TimeSpan.FromHours(12)));
}
