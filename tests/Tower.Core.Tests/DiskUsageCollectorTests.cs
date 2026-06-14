using Tower.Core.Metrics;
using Xunit;
namespace Tower.Core.Tests;

public class DiskUsageCollectorTests {
    [Fact] public void Parses_du_and_sorts_desc_dropping_root() {
        var outp = "5000\t/var\n2000\t/home\n9000\t/\n";
        var items = DiskUsageCollector.ParseDu(outp, root:"/");
        Assert.Equal("/var", items[0].Path);   // largest first
        Assert.Equal(2, items.Count);           // root summary line dropped
    }

    [Fact] public void Skips_malformed_lines_and_name_is_basename() {
        var outp = "not-a-number\t/bad\n3000\t/home/atom/dev\nno-tab-here\n\t/empty-size\n";
        var items = DiskUsageCollector.ParseDu(outp, root:"/");
        // only the valid line survives
        Assert.Single(items);
        Assert.Equal("/home/atom/dev", items[0].Path);
        Assert.Equal("dev", items[0].Name);
        Assert.Equal(3000L, items[0].Size);
    }
}
