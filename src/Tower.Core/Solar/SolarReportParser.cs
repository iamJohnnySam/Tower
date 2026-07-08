using System.Globalization;
using System.Text.RegularExpressions;
using Tower.Core.Models;

namespace Tower.Core.Solar;

public static class SolarReportParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SolarReport? Parse(string subject, string body)
    {
        var text = (subject ?? "") + "\n" + (body ?? "");

        var typeMatch = Regex.Match(text, @"Plant\s+(Daily|Weekly|Monthly)\s+Report", RegexOptions.IgnoreCase);
        if (!typeMatch.Success) return null;
        var type = typeMatch.Groups[1].Value.ToLowerInvariant() switch
        {
            "daily" => SolarReportType.Daily,
            "weekly" => SolarReportType.Weekly,
            _ => SolarReportType.Monthly
        };

        var dateMatch = Regex.Match(text, @"Date\s+(\d{4}/\d{2}/\d{2})(?:\s*-\s*(\d{4}/\d{2}/\d{2}))?");
        if (!dateMatch.Success) return null;
        var end = ParseDate(dateMatch.Groups[1].Value);
        var start = dateMatch.Groups[2].Success ? end : end;
        if (dateMatch.Groups[2].Success)
        {
            start = ParseDate(dateMatch.Groups[1].Value);
            end = ParseDate(dateMatch.Groups[2].Value);
        }

        return new SolarReport
        {
            ReportType = type,
            PeriodStart = start,
            PeriodEnd = end,
            PeriodYieldKWh = YieldKWh(text, @"(?:Daily|Weekly|Monthly)\s+yield"),
            TotalYieldKWh = YieldKWh(text, @"Total\s+yield"),
            PeriodEarningsLkr = Money(text, @"(?:Daily|Weekly|Monthly)\s+earnings"),
            TotalEarningsLkr = Money(text, @"Total\s+earnings"),
            Co2SavedTons = Num(text, @"CO₂?\s*Saved\s+([\d.,]+)\s*t"),
            AlarmQuantity = (int)Num(text, @"Alarm quantity\s+(\d+)"),
        };
    }

    private static DateTime ParseDate(string s) =>
        DateTime.ParseExact(s, "yyyy/MM/dd", Inv);

    // "<label> 31.70kWh" or "10.52MWh" -> kWh
    private static double YieldKWh(string text, string labelPattern)
    {
        var m = Regex.Match(text, labelPattern + @"\s+([\d.,]+)\s*(kWh|MWh)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value.Replace(",", ""), Inv);
        return m.Groups[2].Value.Equals("MWh", StringComparison.OrdinalIgnoreCase) ? v * 1000.0 : v;
    }

    private static decimal Money(string text, string labelPattern)
    {
        var m = Regex.Match(text, labelPattern + @"\s+LKR\s+([\d.,]+)", RegexOptions.IgnoreCase);
        return m.Success ? decimal.Parse(m.Groups[1].Value.Replace(",", ""), Inv) : 0m;
    }

    private static double Num(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value.Replace(",", ""), Inv) : 0;
    }
}
