using System.Text.RegularExpressions;

namespace Tower.Core.Statements;

public record StatementProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string AccountNumber,        // resolved to a FinancialAccount by FinanceTracker
    Regex? BalanceRegex = null); // non-null → balance is in the email body; null → send the PDF

public static class StatementProfiles
{
    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static readonly IReadOnlyList<StatementProfile> All =
    [
        new StatementProfile("BOC 86177812", "bocmail1@boc.lk",
            // subject masks the account: "Account Statement - XXXXXXXX12 - LKR - MR J N G SAMARASINGHE".
            // The \b matters: the same sender also statements accounts ending 62/74/40.
            Rx(@"^Account Statement - X+12\b"),
            "86177812"),

        new StatementProfile("Sampath 121852965685", "sampath.lk",
            // "Your Account Details 1218 XXXX XX85". Two senders over the years
            // (accounts_statements@ and mgr@oper.), hence the bare domain match, and the subject
            // is sometimes prefixed "[MESSAGE ENCRYPTED]" by the mail gateway — so no ^ anchor.
            Rx(@"Your Account Details 1218 X+ XX85\b"),
            "121852965685"),

        new StatementProfile("Standard Chartered 18502880001", "ElectronicServices.CB@sc.com",
            // "Your Standard Chartered Account statement for 18XXXXXXX01 as of 30/06/2026".
            // The same address also sends "Welcome to Standard Chartered eStatement", and the
            // Statements label carries plenty of other sc.com mail (iBanking transfer notices,
            // RM correspondence) — hence the anchored subject and the exact sender.
            Rx(@"^Your Standard Chartered Account statement for 18X+01\b"),
            "18502880001"),
    ];

    /// <summary>Finds the profile whose sender + subject match this email, or null.</summary>
    public static StatementProfile? Match(string from, string subject) =>
        All.FirstOrDefault(p =>
            from.Contains(p.FromContains, StringComparison.OrdinalIgnoreCase) &&
            p.SubjectRegex.IsMatch(subject));

    /// <summary>Statements arrive up to ~2 weeks either side of the month they cover.
    /// Day &lt;= 14 → the statement covers the PREVIOUS month; otherwise the current month.
    /// Gmail hands us UTC (internalDate); the bank's "month" is local (+0530), so convert first —
    /// e.g. 2025-09-30T23:40Z is 2025-10-01 locally and must still land on 2025-09-30.</summary>
    public static DateTime MonthEnd(DateTime emailDate)
    {
        var d = emailDate.ToLocalTime().Date;   // Kind=Unspecified is already treated as local
        var anchor = d.Day <= 14 ? d.AddMonths(-1) : d;
        return new DateTime(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month));
    }
}
