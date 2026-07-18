using System.Globalization;
using System.Text.RegularExpressions;

namespace Tower.Core.Bills;

public record BillProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string Category,
    Regex AmountRegex,
    string Currency);

public static class BillProfiles
{
    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static readonly IReadOnlyList<BillProfile> All =
    [
        new BillProfile("PickMe Trip", "pickme.lk",
            Rx(@"^PickMe \| Email Receipt for Trip"),
            "Transportation",
            Rx(@"Paid Amount\s*LKR\s*([\d,]+\.\d{2})"),
            "LKR"),
        new BillProfile("PickMe Delivery", "pickme.lk",
            Rx(@"^PickMe \| Delivery Email Receipt"),
            "Food",
            Rx(@"Paid by.*?LKR\s*([\d,]+\.\d{2})"),   // "Paid by <method> LKR <amount>" — method (Card/FriMi/Cash) sits between
            "LKR"),
    ];
}

public static class BillParser
{
    /// <summary>Matches an email to a profile and extracts the positive paid amount, or null.</summary>
    public static (BillProfile Profile, decimal Amount)? TryParse(string from, string subject, string body)
    {
        var profile = BillProfiles.All.FirstOrDefault(p =>
            from.Contains(p.FromContains, StringComparison.OrdinalIgnoreCase) &&
            p.SubjectRegex.IsMatch(subject));
        if (profile is null) return null;

        var text = Normalize(body);
        var m = profile.AmountRegex.Match(text);
        if (!m.Success) return null;
        if (!decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;
        return (profile, amount);
    }

    // GmailReader yields tag-stripped HTML that still contains entities like &nbsp;. Decode the
    // couple that appear in amounts and collapse whitespace so the regexes are simple.
    private static string Normalize(string body) =>
        Regex.Replace(body.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ");
}
