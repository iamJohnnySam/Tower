using Tower.Core.Jellyfin; using Xunit;
namespace Tower.Core.Tests;
public class FfmpegStatsTests {
    [Fact] public void Counts_ffmpeg_and_sums_cpu() {
        // `ps -eo comm,%cpu --no-headers` style lines
        var outp = "ffmpeg 35.0\nbash 0.1\n/usr/lib/jellyfin-ffmpeg/ffmpeg 12.5\nchrome 4.0\n";
        var (count, cpu) = FfmpegStats.Parse(outp);
        Assert.Equal(2, count);
        Assert.Equal(47.5, cpu, 1);
    }
}
