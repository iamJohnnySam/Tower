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
            Rx(@"your Order.*(confirmed|placed)"),   // "…is confirmed!", "…has been placed!", "…is placed!"
            "Online Shopping",
            // amounts here may lack decimals (e.g. "Rs 1532"), so the .00 is optional
            [Rx(@"Total \(inclusive of tax.*?(?:Rs\.?|LKR)\s*([\d,]+(?:\.\d{2})?)"),   // "…is confirmed!" template
             Rx(@"Total Payment \(VAT Incl.*?(?:Rs\.?|LKR)\s*([\d,]+(?:\.\d{2})?)"),   // "…has been placed!" template
             Rx(@"\bTotal\s+(?:Rs\.?|LKR)\s*([\d,]+(?:\.\d{2})?)")],                    // oldest "…is placed!" template ("Total Rs 1723")
            "LKR"),
        new BillProfile("Pizza Hut", "gamma.lk",
            Rx(@"^Online Order Confirmation"),
            "Food",
            [Rx(@"Total Amount\s*(?:Rs\.?|LKR)?\s*([\d,]+\.\d{2})")],   // grand total (after Sub Total)
            "LKR"),
        new BillProfile("AliExpress Order", "aliexpress.com",
            Rx(@"Order .* order confirmed"),
            "Online Shopping",
            [Rx(@"Order total\s*(?:LKR|Rs\.?|USD|\$)?\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("Dialog Fixed", "dialog.lk",
            Rx(@"Dialog Fixed_Solutions E-Bill"),   // NOT the "Dialog Mobile E-Bill" from the same sender
            "Home Broadband",
            // amount payable sits before the due date; -? lets a credit-balance month parse negative → skipped
            [Rx(@"(?:Rs\.?|LKR)\s*(-?[\d,]+\.\d{2})\s*Pay on or before")],
            "LKR"),
        new BillProfile("Dialog Mobile", "dialog.lk",
            Rx(@"Dialog Mobile E-Bill"),
            "Phone",
            [Rx(@"(?:Rs\.?|LKR)\s*(-?[\d,]+\.\d{2})\s*Pay on or before")],   // credit months (negative) are skipped
            "LKR"),
        // ── Foreign-currency receipts: stored in their own currency; FinanceTracker converts to base (LKR) via FX ──
        new BillProfile("Anthropic", "anthropic.com",
            Rx(@"^Your receipt from Anthropic"),
            "AI",
            [Rx(@"Amount paid\s*(?:US\$|USD|\$)\s*([\d,]+\.\d{2})")],
            "USD"),
        new BillProfile("GitHub", "github.com",
            Rx(@"^\[GitHub\] Payment Receipt"),
            "AI",
            [Rx(@"\bTotal:\s*(?:US\$|USD|\$)?\s*([\d,]+\.\d{2})")],
            "USD"),
        new BillProfile("NETS NPC", "nets.com.sg",
            Rx(@"^NPC"),
            "Transport",
            [Rx(@"A total of\s*(?:S\$|SGD|\$)\s*([\d,]+\.\d{2})")],
            "SGD"),
        new BillProfile("PickMe Membership", "pickme.lk",
            Rx(@"^Membership Renewal Receipt"),
            "Membership",
            [Rx(@"Paid Amount\s*LKR\s*([\d,]+\.\d{2})"),
             Rx(@"Total\s*LKR\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("Keells Order", "keells.com",
            Rx(@"^Keells Order Confirmation"),   // online home-delivery order (not the in-store E-Bill)
            "Groceries",
            [Rx(@"Total Amount\s*\(Rs\.?\)\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("Google Play", "googleplay-noreply@google.com",
            Rx(@"^Your Google Play Order Receipt"),
            "Apps & Subscriptions",
            // total shown in LKR via the Sinhala rupee mark (රු.); skip any non-digit currency token
            [Rx(@"Total\s*:\s*[^\d]*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("Dominos", "dominos",
            Rx(@"^Order Successful"),
            "Food",
            [Rx(@"Grand Total\s*:?\s*Rs\.?\s*([\d,]+\.\d{2})")],
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
