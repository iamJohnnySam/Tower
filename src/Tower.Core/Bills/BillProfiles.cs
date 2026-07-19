using System.Globalization;
using System.Text.RegularExpressions;

namespace Tower.Core.Bills;

public record BillProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string Category,
    Regex[] AmountRegexes,   // tried in order; first match wins — put the preferred field first
    string Currency,
    bool FromPdf = false,    // when true: amount comes from the attached PDF (via pdftotext), and the PDF is attached instead of the .eml
    bool Preferred = false,  // processed first, so it wins same-order dedup (e.g. PayHere gateway over the merchant email)
    bool AllowZero = false); // import even a 0.00 total (e.g. free Google Play items) instead of skipping

public static class BillProfiles
{
    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static readonly IReadOnlyList<BillProfile> All =
    [
        new BillProfile("PickMe Trip", "pickme.lk",
            Rx(@"^PickMe \| Email Receipt for Trip"),
            "Transportation",
            [Rx(@"Paid Amount\s*LKR\s*([\d,]+\.\d{2})"),           // current template: includes tip
             Rx(@"Total Trip Fare\s*LKR\s*([\d,]+\.\d{2})"),       // ~2021 template
             Rx(@"Fare Amount\s*(?:Rs\.?|LKR)\s*([\d,]+\.\d{2})"), // ~2018 template
             Rx(@"Total Fare\s*(?:Rs\.?|LKR)\s*([\d,]+\.\d{2})")], // ~2015-16 template ("TOTAL FARE Rs.")
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
            Rx(@"Order .* order confirm(ed|ation)"),   // newer "…confirmed" + older 2023 "…confirmation"
            "Online Shopping",
            // orders come in USD ("US $9.15") or LKR — detect per-email via the (?<cur>) group
            [Rx(@"Order total\s*(?<cur>US\s*\$|USD|LKR|Rs\.?)?\s*([\d,]+\.\d{2})")],
            "LKR"),
        // Dialog e-bills: the real charge is in the attached PDF, not the email body (the body only
        // shows the account balance). Read "Total Charges for Bill Period" from the PDF, attach the PDF.
        new BillProfile("Dialog Fixed", "dialog.lk",
            Rx(@"Dialog Fixed_Solutions E-Bill"),   // NOT the "Dialog Mobile E-Bill" from the same sender
            "Home Broadband",
            [Rx(@"Total Charges for Bill Period\s*(?:Rs\.?|LKR)?\s*([\d,]+\.\d{2})")],
            "LKR",
            FromPdf: true),
        new BillProfile("Dialog Mobile", "dialog.lk",
            Rx(@"Dialog Mobile E-Bill"),
            "Phone",
            [Rx(@"Total Charges for Bill Period\s*(?:Rs\.?|LKR)?\s*([\d,]+\.\d{2})")],
            "LKR",
            FromPdf: true),
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
            Rx(@"^Membership (Renewal Receipt|Payment Invoice)"),
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
            "LKR",
            AllowZero: true),   // free items (0.00) are still recorded
        new BillProfile("Dominos", "dominos",
            Rx(@"^Order Successful"),
            "Food",
            [Rx(@"Grand Total\s*:?\s*Rs\.?\s*([\d,]+\.\d{2})")],
            "LKR"),
        // PayHere is a gateway — the merchant is in the subject, so each merchant is its own profile
        new BillProfile("PayHere Dominos", "receipts@mail.payhere.lk",
            Rx(@"^Dominos Pizza Sri Lanka Payment Receipt"),
            "Food",
            [Rx(@"\bTotal\s+LKR\s*([\d,]+\.\d{2})")],
            "LKR",
            Preferred: true),
        new BillProfile("PayHere Riyasewana", "receipts@mail.payhere.lk",
            Rx(@"^Riyasewana Lanka Private Limited Payment Receipt"),
            "Ads",
            [Rx(@"\bTotal\s+LKR\s*([\d,]+\.\d{2})")],
            "LKR",
            Preferred: true),
        new BillProfile("PayablePayments", "payablepayments.lk",
            Rx(@"^Invoice for your Order"),
            "Home",
            [Rx(@"TOTAL AMOUNT\s*:?\s*(?:Rs\.?|LKR)?\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("Namecheap", "namecheap.com",
            Rx(@"^Namecheap Order Summary"),
            "Website",
            [Rx(@"\bTOTAL\s*:?\s*(?:US\$|USD|\$)?\s*([\d,]+\.\d{2})")],   // iterate-positive skips "Sub Total $0.00"
            "USD"),
        new BillProfile("Kandos", "kandos.lk",
            Rx(@"Order Confirmation"),
            "Groceries",
            [Rx(@"Paid Amount\s*Rs\.?\s*([\d,]+\.\d{2})"),
             Rx(@"Total Amount\s*Rs\.?\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("eChannelling", "echannelling.com",
            Rx(@"eChanneling"),
            "e-Channeling",
            [Rx(@"Total Fee\s*:?\s*([\d,]+\.\d{2})\s*LKR")],   // "Total Fee : 114.00 LKR"
            "LKR"),
        new BillProfile("Doc990", "no-reply@doc.lk",
            Rx(@"BOOKING RECEIPT"),
            "e-Channeling",
            [Rx(@"TOTAL CHARGES\s*:?\s*([\d,]+\.\d{2})\s*LKR")],   // read from the attached PDF, not the email body
            "LKR",
            FromPdf: true),
        new BillProfile("Amazon", "amazon.com",
            Rx(@"^Your Amazon\.com order"),
            "Online Shopping",
            [Rx(@"Order Total\s*:?\s*(?<cur>USD|US\s*\$|\$)?\s*([\d,]+\.\d{2})")],
            "USD"),
        new BillProfile("PayHere Viana", "receipts@mail.payhere.lk",
            Rx(@"^Viana Cosmetics Payment Receipt"),
            "Health and Wellness",
            [Rx(@"\bTotal\s+LKR\s*([\d,]+\.\d{2})")],
            "LKR",
            Preferred: true),
        // Chinese Dragon: the order arrives from both the merchant and PayHere — dedup (below) keeps one
        new BillProfile("Chinese Dragon", "chinesedragoncafe.com",
            Rx(@"Order .* confirmed"),
            "Food",
            [Rx(@"\bTotal\s+Rs\.?\s*([\d,]+\.\d{2})")],
            "LKR"),
        new BillProfile("PayHere Chinese Dragon", "receipts@mail.payhere.lk",
            Rx(@"^CHINESE DRAGON CAFE"),
            "Food",
            [Rx(@"\bTotal\s+LKR\s*([\d,]+\.\d{2})")],
            "LKR",
            Preferred: true),
    ];
}

public static class BillParser
{
    /// <summary>Finds the profile whose sender + subject match this email, or null.</summary>
    public static BillProfile? Match(string from, string subject) =>
        BillProfiles.All.FirstOrDefault(p =>
            from.Contains(p.FromContains, StringComparison.OrdinalIgnoreCase) &&
            p.SubjectRegex.IsMatch(subject));

    /// <summary>Extracts the positive paid amount + currency from <paramref name="text"/> (email body or
    /// PDF text) using the profile's patterns. Amount is capture group 1; an optional named group
    /// <c>cur</c> overrides the profile currency per-email (for multi-currency senders like AliExpress).</summary>
    public static (decimal Amount, string Currency)? ExtractAmount(BillProfile profile, string text)
    {
        text = Normalize(text);
        (decimal Amount, string Currency)? zero = null;   // AllowZero fallback if no positive total is found
        foreach (var rx in profile.AmountRegexes)
            foreach (Match m in rx.Matches(text))
                if (decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0)
                {
                    var cur = m.Groups["cur"];
                    var currency = cur.Success && !string.IsNullOrWhiteSpace(cur.Value)
                        ? MapCurrency(cur.Value) : profile.Currency;
                    if (amount > 0) return (amount, currency);         // prefer a positive total (skips 0.00 sub-totals / credits)
                    if (profile.AllowZero) zero ??= (0m, currency);    // remember a 0.00 total for free items
                }
        return zero;
    }

    /// <summary>Convenience: match + extract from an email body (non-PDF profiles).</summary>
    public static (BillProfile Profile, decimal Amount, string Currency)? TryParse(string from, string subject, string body)
    {
        var profile = Match(from, subject);
        if (profile is null) return null;
        return ExtractAmount(profile, body) is { } e ? (profile, e.Amount, e.Currency) : null;
    }

    private static string MapCurrency(string token)
    {
        var t = token.ToUpperInvariant();
        if (t.Contains("US")) return "USD";
        if (t.StartsWith("S$") || t.Contains("SGD")) return "SGD";
        return "LKR";
    }

    // GmailReader yields tag-stripped HTML that still contains entities like &nbsp;. Decode the
    // couple that appear in amounts and collapse whitespace so the regexes are simple.
    private static string Normalize(string body) =>
        Regex.Replace(body.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ");
}
