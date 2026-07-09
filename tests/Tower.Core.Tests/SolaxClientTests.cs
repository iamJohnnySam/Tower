using Tower.Core.Solar;
using Xunit;

namespace Tower.Core.Tests;

public class SolaxClientTests
{
    private const string Json = @"{
      ""success"": true, ""exception"": ""Query success!"",
      ""result"": {
        ""inverterSN"": ""ABC"", ""sn"": ""SN123"",
        ""acpower"": 2450.0, ""yieldtoday"": 12.3, ""yieldtotal"": 10520.4,
        ""feedinpower"": 1800.0, ""feedinenergy"": 5000.1, ""consumeenergy"": 3000.2,
        ""soc"": 87.0, ""batPower"": -500.0, ""powerdc1"": 1300.0,
        ""inverterStatus"": ""102"", ""uploadTime"": ""2026-07-08 10:15:00""
      }
    }";

    [Fact]
    public void Parses_realtime_result()
    {
        var s = SolaxClient.ParseRealtime(Json)!;
        Assert.Equal(2450.0, s.AcPower);
        Assert.Equal(12.3, s.YieldToday);
        Assert.Equal(10520.4, s.YieldTotal);
        Assert.Equal(1800.0, s.FeedInPower);
        Assert.Equal(87.0, s.Soc);
        Assert.Equal(-500.0, s.BatPower);
        Assert.Equal("102", s.InverterStatus);
        Assert.Equal("2026-07-08 10:15:00", s.UploadTime);
    }

    [Fact]
    public void Returns_null_when_not_success()
    {
        Assert.Null(SolaxClient.ParseRealtime(@"{""success"":false,""exception"":""error""}"));
    }
}
