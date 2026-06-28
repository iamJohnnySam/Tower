using Tower.Core.Workers;

namespace Tower.Core.Tests;

public class ConversionSchedulerTests
{
    [Theory]
    [InlineData(0, false)]   // 0 idle ticks = not opportunistic
    [InlineData(14, false)]  // 14 consecutive idle ticks = not opportunistic
    [InlineData(15, true)]   // 15 idle ticks = opportunistic
    [InlineData(20, true)]   // 20 idle ticks = opportunistic
    public void ShouldFire_opportunistic_when_idle_ticks_reach_15(int idleTicks, bool expected)
    {
        Assert.Equal(expected, ConversionScheduler.IsOpportunistic(idleTicks));
    }

    [Fact]
    public void ShouldFire_window_when_in_target_hour_and_first_10_min()
    {
        // Within window: target hour with minute < 10
        Assert.True(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 0));
        Assert.True(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 5));
        Assert.True(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 9));

        // Outside window: minute >= 10 in target hour
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 10));
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 3, currentMinute: 59));

        // Outside window: different hour
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 4, currentMinute: 0));
        Assert.False(ConversionScheduler.IsInWindow(targetHour: 3, currentHour: 2, currentMinute: 5));
    }
}
