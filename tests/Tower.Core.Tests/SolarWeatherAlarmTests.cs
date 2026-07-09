using Tower.Core.Solar;
using Xunit;

namespace Tower.Core.Tests;

public class WeatherClientTests
{
    private const string Json = @"{
      ""daily"": {
        ""time"": [""2026-05-25"", ""2026-05-26"", ""2026-05-27""],
        ""shortwave_radiation_sum"": [18.12, 19.54, null],
        ""sunshine_duration"": [36000, 18000, 0]
      }
    }";

    [Fact]
    public void Parses_daily_radiation_and_sunshine()
    {
        var rows = WeatherClient.ParseArchive(Json);
        Assert.Equal(2, rows.Count);                       // null-radiation day skipped
        Assert.Equal(new DateTime(2026, 5, 25), rows[0].Date);
        Assert.Equal(18.12, rows[0].ShortwaveRadiationMJ, 2);
        Assert.Equal(10.0, rows[0].SunshineHours, 2);      // 36000 s -> 10 h
        Assert.Equal(19.54, rows[1].ShortwaveRadiationMJ, 2);
    }
}

public class SolarAlarmParserTests
{
    private const string Body = @"Abnormal Power Reminder
X3-MIC/PRO-G2
    Plant name Hayley's - Mr John Samarasinghe - 0771589981
    Device SN MC208TK2601058
    Device reg no. SRFZNNUACY
    Zero power time 1h
    User ID iam******Sam";

    [Fact]
    public void Parses_abnormal_power_reminder()
    {
        var d = new DateTime(2026, 6, 24, 8, 0, 0);
        var a = SolarAlarmParser.Parse("Abnormal Power Reminder", Body, d)!;
        Assert.Equal("MC208TK2601058", a.DeviceSn);
        Assert.Equal("Zero power time 1h", a.Detail);
        Assert.Equal(d, a.AlarmDate);
    }

    [Fact]
    public void Returns_null_for_a_normal_report()
    {
        Assert.Null(SolarAlarmParser.Parse("Plant Daily Report", "Daily yield 31.70kWh\nDate 2026/07/05", DateTime.UtcNow));
    }
}
