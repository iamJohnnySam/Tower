using System.Text.RegularExpressions;

namespace Tower.Core.Statements;

public record StatementProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string AccountNumber,               // resolved to a FinancialAccount by FinanceTracker;
                                        // "" when AccountNumberRegex supplies it instead
    Regex? BalanceRegex = null,         // non-null → balance is in the email body; null → send the document
    Regex? AttachmentNameRegex = null,  // which attachment, when the mail carries several
    Regex? AccountNumberRegex = null,   // group 1, matched over subject + filename + body
    Regex? BodyDateRegex = null,        // group 1 over the body; overrides the month-end rule
    string? BodyDateFormat = null,      // format for BodyDateRegex, e.g. "dd-MMM-yyyy"
    bool SaveEml = false)               // body-value mail has no document — keep the email itself
{
    /// <summary>The date this email's figure is actually true for. Most statements get the
    /// month-end rule; documents that state their own effective date say so and win.</summary>
    public DateTime ResolveDate(string body, DateTime emailDate)
    {
        if (BodyDateRegex?.Match(body) is { Success: true } m &&
            DateTime.TryParseExact(m.Groups[1].Value, BodyDateFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed.Date;
        return StatementProfiles.MonthEnd(emailDate);
    }

    /// <summary>The account this email is for: fixed, or dug out of the mail when the sender
    /// uses one template for many accounts (ComBank sends every FD renewal the same way).</summary>
    public string? ResolveAccountNumber(string subject, string? fileName, string body)
    {
        if (AccountNumberRegex is null) return string.IsNullOrEmpty(AccountNumber) ? null : AccountNumber;
        var m = AccountNumberRegex.Match($"{subject}\n{fileName}\n{body}");
        return m.Success ? m.Groups[1].Value : null;
    }
}

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

        // CAL mails each fund separately but attaches the statement AND a fund fact sheet, so the
        // attachment has to be chosen by name — "first PDF wins" is only accidentally right.
        ..CalFund("FIOF", "CAL Fixed Income Opportunities Fund"),
        ..CalFund("IGF",  "Capital Alliance Investment Grade Fund"),
        ..CalFund("QEF",  "Capital Alliance Quantitative Equity Fund"),

        // Two senders and two subject formats over the years, same account throughout.
        new StatementProfile("Commercial Bank 8660032754", "combank.net",
            Rx(@"^Commercial Bank (- Interactive )?e-Statement - \d"),
            "8660032754"),

        // One template for every FD, so the account number comes off the attachment filename
        // (eFD_RenewalNotice_3022819858.pdf). Both subjects have been used.
        new StatementProfile("Commercial Bank e-FD renewal", "Commercial_bk@combank.net",
            Rx(@"^(e-FD Renewal Acknowledgement|AUTOMATIC FD E-RENEWAL NOTICE EMAIL)"),
            "",
            AccountNumberRegex: Rx(@"RenewalNotice_(\d{6,})")),

        // One HTML file holding every NTB product. Filed against NTB Savings purely so it has an
        // account to carry the password and profile — FinanceTracker fans the contents out to all
        // ten accounts it names.
        new StatementProfile("NTB consolidated", "estatement@info.nationstrust.com",
            Rx(@"^Your Nations Trust Bank Inner Circle .* statement here"),
            "27212033390",
            AttachmentNameRegex: Rx(@"\.html?$")),

        // The renewal notice quotes the deposit as it stood for the term that is ENDING, so its
        // effective date is the day the term opened — not the renewal date, and not month-end.
        // On renewal day the deposit is already worth the larger, renewed figure.
        ..NtbFdRenewal("21676", "300270021676"),
        ..NtbFdRenewal("25766", "300270025766"),
        ..NtbFdRenewal("50812", "300270050812"),
        ..NtbFdRenewal("55640", "300270055640"),
        ..NtbFdRenewal("70827", "300270070827"),

        // FriMi restates one account the consolidated statement already covers. Both write the
        // same (account, date) row, so the later one simply overwrites — no duplicate is created.
        // Older mail masks the FriMi id as 222205XXXX, so the tail must not be pinned.
        new StatementProfile("NTB FriMi 205000150623", "estatement@info.nationstrust.com",
            Rx(@"^Statement for .* on FriMi Id 222205\w+"),
            "205000150623"),

        // BOC names the FD inside the PDF and nowhere else, so this is filed against BOC FD1 as an
        // anchor and FinanceTracker's account-from-document matching routes it to the real FD.
        new StatementProfile("BOC FD renewal", "bocmail1@boc.lk",
            Rx(@"^Fixed Deposit Renewal Notice\b"),
            "86177962"),

        // FinanceTracker holds this one as "Money Market" rather than a number.
        new StatementProfile("Softlogic Money Market", "invest@softlogicinvest.lk",
            Rx(@"^Client eStatement - Softlogic Asset Management"),
            "Money Market"),
    ];

    private static StatementProfile[] NtbFdRenewal(string masked, string accountNumber) =>
    [
        new StatementProfile($"NTB FD renewal {accountNumber}", "estatement@info.nationstrust.com",
            Rx($@"^Advanced Notice on Fixed Deposit Renewal - Account No 3002xxxx{masked}\b"),
            accountNumber,
            // These arrive as HTML only, with "&nbsp;" between label and value — so the gap is
            // "any non-digits", not whitespace. Matching on \s* silently found nothing.
            BalanceRegex: Rx(@"Deposit Amount:?[^\d]{0,40}?([\d,]+\.\d{2})"),
            BodyDateRegex: Rx(@"Date Account opened:?[^\d]{0,40}?(\d{2}-\w{3}-\d{4})"),
            BodyDateFormat: "dd-MMM-yyyy",
            SaveEml: true),
    ];

    // CAL client code is the same across funds; FinanceTracker holds them as "ILS0310 (FIOF)" etc.
    // Their subject spacing is not stable — "CAL CAL Fixed Income…" and a double space before the
    // dash both occur — so every gap is \s+ and the duplicated prefix is optional. An exact-space
    // pattern silently skipped 24 real statements.
    private static StatementProfile[] CalFund(string code, string fundName) =>
    [
        new StatementProfile($"CAL {code}", "cali@cal.lk",
            Rx($@"^(?:CAL\s+)?{Loose(fundName)}\s*-\s*Investment Statement\b"),
            $"ILS0310 ({code})",
            AttachmentNameRegex: Rx(@"^CustomerStatment")),
    ];

    /// <summary>Escapes a literal phrase but lets any run of whitespace match any other.</summary>
    private static string Loose(string phrase) =>
        string.Join(@"\s+", phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

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
