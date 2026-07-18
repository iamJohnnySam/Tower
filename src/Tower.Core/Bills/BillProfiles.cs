using System.Globalization;
using System.Text.RegularExpressions;

namespace Tower.Core.Bills;

public record BillProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string Category,
    Regex[] AmountRegexes,   // tried in order; first match wins — put the preferred field first
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
            [Rx(@"Paid Amount\s*LKR\s*([\d,]+\.\d{2})"),        // current template: includes tip
             Rx(@"Total Trip Fare\s*LKR\s*([\d,]+\.\d{2})")],   // older template has no "Paid Amount"
            "LKR"),
        new BillProfile("PickMe Delivery", "pickme.lk",
            Rx(@"^PickMe \| Delivery Email Receipt"),
            "Food",
            [Rx(@"Paid by.*?LKR\s*([\d,]+\.\d{2})")],   // "Paid by <method> LKR <amount>" — method sits between
            "LKR"),
        new BillProfile("Keells E-Bill", "keells.com",
            Rx(@"^Keells (E-)?Bill"),   // new "Keells E-Bill | …" and older "Keells Bill - …"
            "Grocery",
            [Rx(@"Total Net Amount\s*(?:Rs\.?|LKR)?\s*([\d,]+\.\d{2})")],   // net of discounts = amount charged
            "LKR"),
        new BillProfile("Daraz Order", "daraz.lk",
            Rx(@"your Order .* is confirmed"),   // "Yay, your Order <id> is confirmed!"
            "Online Shopping",
            [Rx(@"Total \(inclusive of tax.*?(?:Rs\.?|LKR)\s*([\d,]+\.\d{2})")],   // grand total incl. shipping + fees
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
        foreach (var rx in profile.AmountRegexes)
        {
            var m = rx.Match(text);
            if (m.Success &&
                decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0)
                return (profile, amount);
        }
        return null;
    }

    // GmailReader yields tag-stripped HTML that still contains entities like &nbsp;. Decode the
    // couple that appear in amounts and collapse whitespace so the regexes are simple.
    private static string Normalize(string body) =>
        Regex.Replace(body.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ");
}
