using Tower.Core.Maintenance;
using Xunit;
namespace Tower.Core.Tests;
public class CpuProfileTests {
    [Fact] public void Best_window_picks_lowest_cpu_hour_when_enough_samples() {
        var cpu = new double[168]; var samples = new int[168];
        for (int d=0; d<7; d++) for (int h=0; h<24; h++) { int i=d*24+h; cpu[i]=h==4?2.0:50.0; samples[i]=20; }
        Assert.Equal(4, CpuProfile.BestWindow(cpu, samples));
    }
    [Fact] public void Best_window_defaults_to_3_when_sparse() {
        Assert.Equal(3, CpuProfile.BestWindow(new double[168], new int[168]));
    }
    [Fact] public void SlotFor_monday_midnight_is_0_and_sunday_23_is_167() {
        Assert.Equal(0, CpuProfile.SlotFor(DayOfWeek.Monday, 0));
        Assert.Equal(167, CpuProfile.SlotFor(DayOfWeek.Sunday, 23));
    }
}
