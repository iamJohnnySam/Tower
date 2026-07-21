using Tower.Core.Bills;
using Xunit;

namespace Tower.Tests;

public class TransferProfilesTests
{
    // Bodies are the GmailReader-style tag-stripped, space-collapsed text of real emails.
    private const string ToGayathri =
        "Reference Number is: ad0c Transfer From EMPLOYEE BANKING SAVINGS,XXXXXXX0001,LKR " +
        "Beneficiary Details Gayathri Karunaratne, XXXXXX0728,LKR Beneficiary Bank COMM BANK " +
        "Remittance Amount: LKR 70,000.00 Debit Amount: LKR 70,000.00 Charges OUR - All Charges to my account " +
        "Charges To Debit LKR 0 Transfer Reference Jun 2026 100k balance Purpose Code Person to Person Transfer " +
        "Date(DD/MM/YYYY) 09/06/2026 Status Successful";

    private const string ToExternal =
        "Beneficiary Details Ishara traders motor company pvt lt, XXXXXXXX0600,LKR " +
        "Remittance Amount: LKR 4,956,500.00 Debit Amount: LKR 4,956,500.00 " +
        "Transfer Reference 104893 BAW E7 John Purpose Code Any other bill/membership " +
        "Transfer Date(DD/MM/YYYY) 11/07/2026 Status Submitted";

    private const string ToSelf =
        "Beneficiary Details J N G SAMARASINGHE, XXXXXXXX5842, LKR Transfer Amount: LKR 250,000.00 " +
        "Debit Amount: LKR 250,000.00 Charges To Debit LKR 0 Transfer Date(DD/MM/YYYY) 10/02/2026";

    private const string Utility =
        "Reference Number 2022 Debit Account Number XXXXXXX0001 Beneficiary Ceylon Electricity Board " +
        "Consumer No 0801027810 Currency LKR Amount 600 UserSecurityCode 1008";

    private const string StandingOrder =
        "Beneficiary Details J N G SAMARASINGHE, XXXXXXXX5842, LKR Transfer Amount: LKR 250,000.00 " +
        "Frequency: Monthly Start Date: 10/02/2026 End Date: 10/01/2027";

    private static TransferProfile Match(string subject) =>
        TransferProfiles.Match("iBanking.SRILANKA@sc.com", subject)!;

    [Theory]
    [InlineData("J.N.G.Samarasinghe", "John")]
    [InlineData("J N G SAMARASINGHE", "John")]
    [InlineData("John Samarasinghe", "John")]
    [InlineData("Gayathri Karunaratne", "Gayathri")]
    [InlineData("C. J. Samarasinghe", "Caleb")]
    [InlineData("Caleb Samarasinghe", "Caleb")]
    [InlineData("Ishara traders motor company pvt lt", null)]
    public void ClassifyBeneficiary_maps_aliases(string name, string? member) =>
        Assert.Equal(member, TransferProfiles.ClassifyBeneficiary(name));

    [Fact]
    public void Transfer_to_family_is_income_in_their_ledger()
    {
        var posts = TransferProfiles.Plan(Match("Standard Chartered Bank - Online Banking - Local Funds Transfer - Successful"), ToGayathri);
        var p = Assert.Single(posts);
        Assert.Equal("Gayathri", p.Member);
        Assert.Equal(70000m, p.Value);                 // positive = income
        Assert.Equal("Bank Transfer", p.Category);
    }

    [Fact]
    public void Transfer_to_external_is_johns_expense()
    {
        var posts = TransferProfiles.Plan(Match("Local Funds Transfer - Successful"), ToExternal);
        var p = Assert.Single(posts);
        Assert.Null(p.Member);                          // null = John (the key owner)
        Assert.Equal(-4956500m, p.Value);               // negative = expense
    }

    [Fact]
    public void Transfer_to_self_records_nothing()
    {
        var posts = TransferProfiles.Plan(Match("Local Funds Transfer - Successful"), ToSelf);
        Assert.Empty(posts);                            // me → me: caller just trashes the email
    }

    [Fact]
    public void Utility_payment_is_johns_expense()
    {
        var posts = TransferProfiles.Plan(Match("Utility Payment Confirmation"), Utility);
        var p = Assert.Single(posts);
        Assert.Null(p.Member);
        Assert.Equal(-600m, p.Value);
        Assert.Equal("Utilities", p.Category);
        Assert.Contains("Ceylon Electricity Board", p.Description);
    }

    [Fact]
    public void Standing_order_setup_records_nothing()
    {
        var posts = TransferProfiles.Plan(Match("Local Transfer Standing Order Confirmation"), StandingOrder);
        Assert.Empty(posts);
    }

    [Fact]
    public void Non_zero_fee_is_a_separate_john_expense()
    {
        var withFee = ToExternal.Replace("Status Submitted", "Charges To Debit LKR 250.00 Status Submitted");
        var posts = TransferProfiles.Plan(Match("Local Funds Transfer - Successful"), withFee);
        Assert.Equal(2, posts.Count);
        var fee = Assert.Single(posts, x => x.Category == "Bank Charges");
        Assert.Null(fee.Member);
        Assert.Equal(-250m, fee.Value);
    }

    [Fact]
    public void DateOf_prefers_the_transfer_date_over_the_email_date() =>
        Assert.Equal(new DateTime(2026, 6, 9),
            TransferProfiles.DateOf(ToGayathri, new DateTime(2026, 6, 10)));

    [Fact]
    public void DateOf_falls_back_to_email_date_when_absent() =>
        Assert.Equal(new DateTime(2026, 7, 3),
            TransferProfiles.DateOf(Utility, new DateTime(2026, 7, 3)));

    [Fact]
    public void Match_ignores_non_sc_senders() =>
        Assert.Null(TransferProfiles.Match("support@pickme.lk", "Local Funds Transfer - Successful"));
}
