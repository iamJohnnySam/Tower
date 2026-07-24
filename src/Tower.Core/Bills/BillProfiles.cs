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
    bool AllowZero = false,  // import even a 0.00 total (e.g. free Google Play items) instead of skipping
    bool Refund = false,     // money coming back: posted as a positive transaction, not an expense
    bool NoDedup = false,    // sender bills several independent subscriptions: identical amount + day is normal, not a duplicate
    CategoryRules? CategoryRules = null)   // pick the category out of the bill text; falls back to Category
{
    /// <summary>The category for one specific bill. Fixed for most senders; Dialog reads it off the
    /// connection number printed on the bill, which changes from bill to bill.</summary>
    public string CategoryFor(string text)
    {
        if (CategoryRules is not { } rules) return Category;
        foreach (var token in rules.Tokens)
        {
            var m = token.Match(text);
            if (!m.Success) continue;
            var value = m.Groups[1].Value;
            foreach (var (prefix, category) in rules.When)
                if (value.StartsWith(prefix, StringComparison.Ordinal)) return category;
            break;   // token found but no rule matched — fall through to the default
        }
        return Category;
    }
}

/// <summary>Reads a token out of the bill (capture group 1 of the first Tokens pattern that hits) and
/// maps it to a category by prefix. Dialog: 7… is a mobile line, 1… is a broadband line.</summary>
public record CategoryRules(Regex[] Tokens, (string Prefix, string Category)[] When);

public static class BillProfiles
{
    public const string DefaultFileName = "bill-profiles.default.xml";   // reference copy, rewritten from the binary each start
    public const string LiveFileName = "bill-profiles.xml";              // the editable copy; survives deploys

    private static IReadOnlyList<BillProfile>? _all;
    private static readonly Lock Gate = new();

    /// <summary>The profiles in use. Loads the default file on first touch so tests and any code
    /// path that forgets to call <see cref="Reload"/> still work.</summary>
    public static IReadOnlyList<BillProfile> All
    {
        get
        {
            if (_all is { } loaded) return loaded;
            lock (Gate) return _all ??= BillProfileXml.ParseText(BillProfileXml.DefaultXml);
        }
    }

    public static string? SourcePath { get; private set; }
    public static DateTime? LoadedAt { get; private set; }
    public static string? LoadError { get; private set; }

    /// <summary>Re-reads <paramref name="path"/>. On failure the previously loaded profiles stay in
    /// use and the error is kept for /bills to show — a typo in the file must not silently disable
    /// importing, which is the whole reason this moved out of the binary.</summary>
    public static bool Reload(string path)
    {
        try
        {
            var loaded = BillProfileXml.Load(path);
            lock (Gate)
            {
                _all = loaded;
                SourcePath = path;
                LoadedAt = DateTime.UtcNow;
                LoadError = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            return false;
        }
    }

    /// <summary>Writes the shipped defaults next to the app as a always-current reference copy, seeds
    /// the live file from it the first time, then loads the live file. Nothing here overwrites the
    /// live file, so edits made in the deploy directory survive every deploy.</summary>
    public static bool Initialise(string directory)
    {
        var live = Path.Combine(directory, LiveFileName);
        try
        {
            File.WriteAllText(Path.Combine(directory, DefaultFileName), BillProfileXml.DefaultXml);
            if (!File.Exists(live)) File.WriteAllText(live, BillProfileXml.DefaultXml);
        }
        catch (Exception ex) { LoadError = $"seeding {live}: {ex.Message}"; }
        return Reload(live);
    }
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
        if (t.Contains("RM") || t.Contains("MYR")) return "MYR";
        if (t.Contains("£") || t.Contains("GBP")) return "GBP";
        return "LKR";
    }

    // GmailReader yields tag-stripped HTML that still contains entities like &nbsp;. Decode the
    // couple that appear in amounts and collapse whitespace so the regexes are simple.
    private static string Normalize(string body) =>
        Regex.Replace(body.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ");
}
