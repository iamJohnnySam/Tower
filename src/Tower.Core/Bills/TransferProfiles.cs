using System.Globalization;
using System.Text.RegularExpressions;

namespace Tower.Core.Bills;

/// <summary>What a Standard Chartered account-notification email should become.</summary>
public enum TransferKind { Transfer, Utility, StandingOrder }

/// <summary>One planned ledger entry pulled out of a notification email.</summary>
/// <param name="Member">FinanceTracker first name to book against, or null for the API-key owner (John).</param>
/// <param name="Value">Signed: negative = expense, positive = income.</param>
public record TransferPost(string? Member, decimal Value, string Category, string Description);

/// <summary>
/// Standard Chartered emails John on every account movement. Unlike a bill, these route by who
/// received the money — across the three household members who are FinanceTracker users. Kept
/// separate from <see cref="BillProfile"/>, which is fixed-category and John-only.
/// </summary>
public record TransferProfile(string Name, Regex SubjectRegex, TransferKind Kind);

public static class TransferProfiles
{
    private const string From = "iBanking.SRILANKA@sc.com";

    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static readonly IReadOnlyList<TransferProfile> All =
    [
        new TransferProfile("SC Funds Transfer",
            Rx(@"Local Funds Transfer - Successful"), TransferKind.Transfer),
        new TransferProfile("SC Utility Payment",
            Rx(@"^Utility Payment Confirmation"), TransferKind.Utility),
        new TransferProfile("SC Standing Order",
            Rx(@"Standing Order Confirmation"), TransferKind.StandingOrder),
    ];

    // Member = the FinanceTracker first name; aliases are every way the beneficiary line spells them.
    private static readonly (string Member, string[] Aliases)[] Members =
    [
        ("John",     ["J N G Samarasinghe", "John Samarasinghe", "John Neuman"]),
        ("Gayathri", ["Gayathri Karunaratne"]),
        ("Caleb",    ["Caleb Samarasinghe", "C J Samarasinghe"]),
    ];

    public static TransferProfile? Match(string from, string subject) =>
        from.Contains(From, StringComparison.OrdinalIgnoreCase)
            ? All.FirstOrDefault(p => p.SubjectRegex.IsMatch(subject))
            : null;

    /// <summary>Strips punctuation, collapses whitespace, upper-cases — so "J.N.G.Samarasinghe"
    /// and "J N G SAMARASINGHE" are one key.</summary>
    private static string Norm(string s) =>
        Regex.Replace(Regex.Replace(s, @"[^A-Za-z0-9 ]", " "), @"\s+", " ").Trim().ToUpperInvariant();

    /// <summary>The household member this beneficiary is, or null if it is someone external.</summary>
    public static string? ClassifyBeneficiary(string beneficiary)
    {
        var n = Norm(beneficiary);
        foreach (var (member, aliases) in Members)
            if (aliases.Any(a => n == Norm(a)))
                return member;
        return null;
    }

    private static readonly Regex Beneficiary = Rx(@"Beneficiary Details\s+(.+?)\s*,");   // transfer
    private static readonly Regex UtilityBeneficiary = Rx(@"Beneficiary\s+(.+?)\s+Consumer No");
    private static readonly Regex DebitAmount = Rx(@"Debit Amount:?\s*LKR\s*([\d,]+(?:\.\d{2})?)");
    private static readonly Regex Charges = Rx(@"Charges To Debit\s+LKR\s*([\d,]+(?:\.\d{2})?)");
    private static readonly Regex UtilityAmount = Rx(@"\bAmount\s+([\d,]+(?:\.\d{2})?)");
    private static readonly Regex Reference = Rx(@"Transfer Reference\s+(.+?)\s+Purpose Code");
    private static readonly Regex TransferDate = Rx(@"Transfer Date\(DD/MM/YYYY\)\s*(\d{2}/\d{2}/\d{4})");

    // References carry whatever John typed at the bank — often tabs/newlines, and the tag-stripped
    // HTML still holds &nbsp;/&amp;. Decode those, then collapse to one line.
    private static string Clean(string s) =>
        Regex.Replace(s.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ").Trim();

    private static decimal? Money(Match m) =>
        m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Turns one email into 0–2 ledger posts. Empty = record nothing (me→me, or a standing
    /// order): the caller still trashes the email.</summary>
    public static IReadOnlyList<TransferPost> Plan(TransferProfile profile, string body)
    {
        var posts = new List<TransferPost>();

        if (profile.Kind == TransferKind.Utility)
        {
            var amount = Money(UtilityAmount.Match(body));
            var who = Clean(UtilityBeneficiary.Match(body) is { Success: true } u ? u.Groups[1].Value : "Utility");
            if (amount is > 0)
                posts.Add(new TransferPost(null, -amount.Value, "Utilities", $"SC Utility — {who}"));
            return posts;
        }

        // Transfer + StandingOrder both carry a beneficiary; a standing order is a setup, so it
        // never books a transaction — it only needs classifying so me→me still deletes the mail.
        var beneficiary = Clean(Beneficiary.Match(body) is { Success: true } b ? b.Groups[1].Value : "");
        var member = ClassifyBeneficiary(beneficiary);

        if (profile.Kind == TransferKind.StandingOrder || member == "John")
            return posts;   // setup, or money moved between John's own accounts — nothing to record

        var debit = Money(DebitAmount.Match(body));
        if (debit is > 0)
        {
            var reference = Clean(Reference.Match(body) is { Success: true } r ? r.Groups[1].Value : "");
            var desc = $"SC — {beneficiary}{(reference.Length > 0 ? $" ({reference})" : "")}";
            posts.Add(member is null
                ? new TransferPost(null, -debit.Value, "Bank Transfer", desc)            // external → John's expense
                : new TransferPost(member, debit.Value, "Bank Transfer", desc));         // family → their income
        }

        // A non-zero fee is always John's expense, whoever the money went to. Almost always 0.
        var fee = Money(Charges.Match(body));
        if (fee is > 0)
            posts.Add(new TransferPost(null, -fee.Value, "Bank Charges", $"SC transfer fee — {beneficiary}"));

        return posts;
    }

    /// <summary>The transfer's own date (DD/MM/YYYY) when present, else the email date.</summary>
    public static DateTime DateOf(string body, DateTime emailDate) =>
        TransferDate.Match(body) is { Success: true } m &&
        DateTime.TryParseExact(m.Groups[1].Value, "dd/MM/yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : emailDate;
}
