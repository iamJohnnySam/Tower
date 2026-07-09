using System.Text.RegularExpressions;
using Tower.Core.Models;

namespace Tower.Core.Solar;

// Parses SolaxCloud "Abnormal Power Reminder" alert emails. The body has no date,
// so the caller passes the email's received time as alarmDate.
public static class SolarAlarmParser
{
    public static SolarAlarm? Parse(string subject, string body, DateTime alarmDate)
    {
        var text = (subject ?? "") + "\n" + (body ?? "");
        if (!Regex.IsMatch(text, @"Abnormal\s+Power\s+Reminder", RegexOptions.IgnoreCase))
            return null;

        var sn = Match(text, @"Device\s*SN\s+(\S+)");
        var zero = Match(text, @"Zero power time\s+(\S+)");

        return new SolarAlarm
        {
            AlarmDate = alarmDate,
            DeviceSn = sn ?? "",
            Detail = zero != null ? $"Zero power time {zero}" : null,
        };
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
