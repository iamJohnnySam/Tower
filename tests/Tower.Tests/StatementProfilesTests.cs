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

    [Fact]
    public void Match_ignores_a_bills_sender() =>
        Assert.Null(StatementProfiles.Match("support@pickme.lk", "PickMe | Email Receipt for Trip ID 1458530325"));
}
