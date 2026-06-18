using Tower.Core.Metrics;
using Xunit;
namespace Tower.Core.Tests;

public class SmartCollectorTests {
    [Fact] public void Parses_ssd_fixture_with_real_values() {
        var txt = System.IO.File.ReadAllText("Fixtures/smartctl_ssd.txt");
        var s = SmartCollector.Parse(txt, ssd:true);
        Assert.False(string.IsNullOrEmpty(s.Model));
        Assert.Equal("PASSED", s.Health);
        Assert.Equal(13300, s.PowerOnHours);
        Assert.Equal(38, s.Temp);
    }
    [Fact] public void Parses_hdd_fixture_with_real_values() {
        var txt = System.IO.File.ReadAllText("Fixtures/smartctl_hdd.txt");
        var s = SmartCollector.Parse(txt, ssd:false);
        Assert.False(string.IsNullOrEmpty(s.Model));
        Assert.Contains(s.Health, new[]{"PASSED","FAILED"});
        Assert.True((s.PowerOnHours ?? 0) > 0);
    }
    [Fact] public void Alert_critical_on_uncorrectable() {
        var s = new SmartInfo { Health="PASSED", Uncorrectable=3 };
        Assert.Equal(2, SmartCollector.ComputeAlert(s));
    }
    [Fact] public void Alert_warn_on_reallocated() {
        var s = new SmartInfo { Health="PASSED", Reallocated=1 };
        Assert.Equal(1, SmartCollector.ComputeAlert(s));
    }
}
