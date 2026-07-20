using Tower.Core.Statements;
using Xunit;

public class StatementProfilesTests
{
    const string BocSubject = "Account Statement - XXXXXXXX12 - LKR - MR J N G SAMARASINGHE";

    [Theory]
    // spec §8 boundaries
    [InlineData("2026-07-24", "2026-07-31")]
    [InlineData("2026-08-05", "2026-07-31")]
    [InlineData("2026-08-20", "2026-08-31")]
    [InlineData("2026-03-01", "2026-02-28")]
    // the nine real BOC email dates
    [InlineData("2025-11-01", "2025-10-31")]
    [InlineData("2025-11-29", "2025-11-30")]
    [InlineData("2026-01-01", "2025-12-31")]
    [InlineData("2026-01-31", "2026-01-31")]
    [InlineData("2026-02-28", "2026-02-28")]
    [InlineData("2026-04-01", "2026-03-31")]
    [InlineData("2026-05-01", "2026-04-30")]
    [InlineData("2026-05-30", "2026-05-31")]
    public void MonthEnd_picks_the_covered_month(string email, string expected) =>
        Assert.Equal(DateTime.Parse(expected), StatementProfiles.MonthEnd(DateTime.Parse(email)));

    // Gmail's internalDate is UTC; 23:40Z on the 30th is the 1st in Colombo (+0530) and the naive
    // rule would jump a month. ponytail: relies on the host TZ being east of UTC, as the server is.
    [Fact]
    public void MonthEnd_uses_local_time_for_late_utc_mail()
    {
        var utc = new DateTime(2025, 9, 30, 23, 40, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2025, 9, 30), StatementProfiles.MonthEnd(utc));
    }

    [Fact]
    public void Match_finds_the_boc_profile()
    {
        var p = StatementProfiles.Match("BOC SmartGateway <bocmail1@boc.lk>", BocSubject);
        Assert.NotNull(p);
        Assert.Equal("BOC 86177812", p!.Name);
        Assert.Equal("86177812", p.AccountNumber);
        Assert.Null(p.BalanceRegex);   // balance comes from the PDF, not the body
    }

    [Theory]
    [InlineData("Smart Fixed Deposit Opening Confirmation")]                        // same sender, not a statement
    [InlineData("Account Statement - XXXXXXXX62 - LKR - MR J N G SAMARASINGHE")]    // a different BOC account
    [InlineData("Account Statement - XXXXXXXX74 - LKR - MR J N G SAMARASINGHE")]
    [InlineData("Account Statement - XXXXXXXX40 - LKR - MR J N G SAMARASINGHE")]
    public void Match_ignores_other_boc_mail(string subject) =>
        Assert.Null(StatementProfiles.Match("bocmail1@boc.lk", subject));

    // Both senders Sampath has used, and the gateway's subject prefixes, must all match.
    [Theory]
    [InlineData("accounts_statements@sampath.lk", "Your Account Details 1218 XXXX XX85")]
    [InlineData("Sampath Bank <mgr@oper.sampath.lk>", "[MESSAGE ENCRYPTED]Your Account Details 1218 XXXX XX85")]
    [InlineData("mgr@oper.sampath.lk", "[WARNING: UNSCANNABLE EXTRACTION FAILED]Your Account Details 1218 XXXX XX85")]
    public void Match_finds_the_sampath_profile(string from, string subject)
    {
        var p = StatementProfiles.Match(from, subject);
        Assert.NotNull(p);
        Assert.Equal("121852965685", p!.AccountNumber);
    }

    [Fact]
    public void Match_ignores_another_sampath_account() =>
        Assert.Null(StatementProfiles.Match("accounts_statements@sampath.lk", "Your Account Details 1218 XXXX XX21"));

    [Fact]
    public void Match_finds_the_standard_chartered_profile()
    {
        var p = StatementProfiles.Match("ElectronicServices.CB@sc.com",
            "Your Standard Chartered Account statement for 18XXXXXXX01 as of 30/06/2026");
        Assert.NotNull(p);
        Assert.Equal("18502880001", p!.AccountNumber);
    }

    // The Statements label is full of other sc.com mail that must not be treated as a statement.
    [Theory]
    [InlineData("ElectronicServices.CB@sc.com", "Welcome to Standard Chartered eStatement")]
    [InlineData("iBanking.SRILANKA@sc.com", "Standard Chartered Bank - Online Banking - Local Funds Transfer - Successful")]
    [InlineData("Srilanka.PriorityBanking@sc.com", "Notice to our valued clients")]
    [InlineData("ElectronicServices.CB@sc.com", "Your Standard Chartered Account statement for 18XXXXXXX99 as of 30/06/2026")]
    public void Match_ignores_other_sc_mail(string from, string subject) =>
        Assert.Null(StatementProfiles.Match(from, subject));

    [Theory]
    [InlineData("CAL Fixed Income Opportunities Fund - Investment Statement - Mr. J.N.G Samarasinghe", "ILS0310 (FIOF)")]
    [InlineData("Capital Alliance Investment Grade Fund - Investment Statement - Mr. J.N.G Samarasinghe", "ILS0310 (IGF)")]
    [InlineData("Capital Alliance Quantitative Equity Fund - Investment Statement - Mr. J.N.G Samarasinghe", "ILS0310 (QEF)")]
    public void Match_finds_each_cal_fund(string subject, string account)
    {
        var p = StatementProfiles.Match("cali@cal.lk", subject);
        Assert.NotNull(p);
        Assert.Equal(account, p!.AccountNumber);
    }

    // The statement and the fund fact sheet are both PDFs; only one is the statement.
    [Fact]
    public void Cal_selects_the_statement_attachment_not_the_fact_sheet()
    {
        var rx = StatementProfiles.Match("cali@cal.lk",
            "CAL Fixed Income Opportunities Fund - Investment Statement - Mr. J.N.G Samarasinghe")!
            .AttachmentNameRegex;
        Assert.NotNull(rx);
        Assert.True(rx!.IsMatch("CustomerStatmentNEW_CDGTF_01-06-2026_30-06-2026_ILS0310.pdf"));
        Assert.False(rx.IsMatch("FundFactSheetJune26.pdf"));
    }

    [Fact]
    public void Match_ignores_cal_marketing_mail() =>
        Assert.Null(StatementProfiles.Match("cali@cal.lk",
            "Reminder: Update Your Collection Account Details for CAL Unit Trust Transfers"));

    [Theory]
    [InlineData("estatement@combank.net", "Commercial Bank e-Statement - 31 October 2025")]
    [InlineData("e-statement@combank.net", "Commercial Bank - Interactive e-Statement - 30 June 2026")]
    public void Match_finds_commercial_bank_statements(string from, string subject)
    {
        var p = StatementProfiles.Match(from, subject);
        Assert.NotNull(p);
        Assert.Equal("8660032754", p!.AccountNumber);
    }

    // One template for every FD — the number has to come off the attachment filename.
    [Theory]
    [InlineData("e-FD Renewal Acknowledgement", "eFD_RenewalNotice_3022819858.pdf", "3022819858")]
    [InlineData("AUTOMATIC FD E-RENEWAL NOTICE EMAIL", "eFD_RenewalNotice_3006859269.pdf", "3006859269")]
    public void Combank_fd_renewal_reads_the_account_from_the_filename(string subject, string file, string expected)
    {
        var p = StatementProfiles.Match("Commercial_bk@combank.net", subject);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.ResolveAccountNumber(subject, file, ""));
    }

    [Fact]
    public void Combank_fd_renewal_ignores_transfer_notifications() =>
        Assert.Null(StatementProfiles.Match("Commercial_bk@combank.net", "New Fund transfer notification"));

    // A fixed-account profile must not care that ResolveAccountNumber got no filename.
    [Fact]
    public void ResolveAccountNumber_falls_back_to_the_fixed_account() =>
        Assert.Equal("86177812", StatementProfiles
            .Match("bocmail1@boc.lk", BocSubject)!.ResolveAccountNumber(BocSubject, null, ""));

    [Fact]
    public void Match_ignores_a_bills_sender() =>
        Assert.Null(StatementProfiles.Match("support@pickme.lk", "PickMe | Email Receipt for Trip ID 1458530325"));
}
