using Tower.Core.Gmail;
using Xunit;

public class GmailReaderTests
{
    // Each of these shipped as the text/plain part of an HTML-only receipt, and each one silently
    // broke that sender's import until it was added: the profile matched, then found no amount.
    [Theory]
    [InlineData("This email has no text content")]
    [InlineData("It looks like your email client might not support HTML formatted email.\n\nTry opening this email in another email client.")]
    [InlineData("To view the message, please use an HTML compatible email viewer!")]
    [InlineData("  \r\n To view the message, please use an HTML compatible email viewer!")]   // leading whitespace
    public void Html_only_stubs_are_treated_as_empty(string body) =>
        Assert.True(GmailReader.IsHtmlOnlyStub(body));

    [Theory]
    [InlineData("TOTAL FARE Rs.610.29 TOTAL DISTANCE : 16.331 KM")]
    [InlineData("Receipt from Anthropic, PBC $20.00 Paid July 23, 2026 ... Amount paid $20.00")]
    [InlineData("")]
    public void Real_bodies_are_kept(string body) =>
        Assert.False(GmailReader.IsHtmlOnlyStub(body));
}
