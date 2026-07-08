using Tower.Core.Models;
using Tower.Core.Solar;
using Xunit;

namespace Tower.Core.Tests;

public class SolarReportParserTests
{
    private const string Daily = @"Plant Daily Report
    Daily yield 31.70kWh
    Monthly yield 154.30kWh
    Total yield 10.52MWh
    Daily earnings LKR 857.66
    Total earnings LKR 290735.60
    CO₂ Saved 7.46t
    Alarm quantity 0
    Date 2026/07/05";

    private const string Weekly = @"Plant Weekly Report
    Weekly yield 198.80kWh
    Total yield 10.52MWh
    Weekly earnings LKR 5378.48
    Total earnings LKR 290735.60
    CO₂ Saved 7.46t
    Alarm quantity 0
    Date 2026/07/05";

    private const string Monthly = @"Plant Monthly Report
    Monthly yield 859.45kWh
    Total yield 9.61MWh
    Monthly earnings LKR 23318.79
    Total earnings LKR 266021.39
    CO₂ Saved 6.82t
    Alarm quantity 0
    Date 2026/05/01 - 2026/05/31";

    [Fact]
    public void Parses_daily_report()
    {
        var r = SolarReportParser.Parse("Plant Daily Report", Daily)!;
        Assert.Equal(SolarReportType.Daily, r.ReportType);
        Assert.Equal(31.70, r.PeriodYieldKWh, 2);
        Assert.Equal(10520.0, r.TotalYieldKWh, 1);        // 10.52 MWh -> kWh
        Assert.Equal(857.66m, r.PeriodEarningsLkr);
        Assert.Equal(290735.60m, r.TotalEarningsLkr);
        Assert.Equal(7.46, r.Co2SavedTons, 2);
        Assert.Equal(0, r.AlarmQuantity);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodStart);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodEnd);
    }

    [Fact]
    public void Parses_weekly_report()
    {
        var r = SolarReportParser.Parse("Plant Weekly Report", Weekly)!;
        Assert.Equal(SolarReportType.Weekly, r.ReportType);
        Assert.Equal(198.80, r.PeriodYieldKWh, 2);
        Assert.Equal(5378.48m, r.PeriodEarningsLkr);
        Assert.Equal(new DateTime(2026, 7, 5), r.PeriodEnd);
    }

    [Fact]
    public void Parses_monthly_report_with_date_range()
    {
        var r = SolarReportParser.Parse("Plant Monthly Report", Monthly)!;
        Assert.Equal(SolarReportType.Monthly, r.ReportType);
        Assert.Equal(859.45, r.PeriodYieldKWh, 2);
        Assert.Equal(9610.0, r.TotalYieldKWh, 1);
        Assert.Equal(23318.79m, r.PeriodEarningsLkr);
        Assert.Equal(new DateTime(2026, 5, 1), r.PeriodStart);
        Assert.Equal(new DateTime(2026, 5, 31), r.PeriodEnd);
    }

    [Fact]
    public void Returns_null_for_unrecognized_body()
    {
        Assert.Null(SolarReportParser.Parse("Newsletter", "nothing solar here"));
    }
}
