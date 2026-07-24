using Tower.Core.Bills;
using Xunit;

public class BillProfileXmlTests
{
    // Every other test in BillParserTests already runs against profiles parsed from this file,
    // so behaviour is covered. These guard the loader's own promises.

    [Fact]
    public void Shipped_defaults_parse_and_keep_every_parameter()
    {
        var all = BillProfileXml.ParseText(BillProfileXml.DefaultXml);
        Assert.Equal(37, all.Count);

        var dialog = all.Single(p => p.Name == "Dialog");
        Assert.True(dialog.FromPdf);
        Assert.Equal(2, dialog.AmountRegexes.Length);
        Assert.Equal("Phone", dialog.CategoryFor("MOBILE NUMBER: 769014481"));
        Assert.Equal("Home Broadband", dialog.CategoryFor("MOBILE NUMBER: 114103678"));
        Assert.Equal("Dialog", dialog.CategoryFor("nothing recognisable"));   // falls back to @category

        Assert.True(all.Single(p => p.Name == "Anthropic").NoDedup);
        Assert.True(all.Single(p => p.Name == "Adidas Refund (Global-e)").Refund);
        Assert.True(all.Single(p => p.Name == "Epic Games").AllowZero);
        Assert.True(all.Single(p => p.Name == "PayHere Feelo").Preferred);
        Assert.Equal("GBP", all.Single(p => p.Name == "BBC Shop").Currency);
    }

    [Fact]
    public void Round_trips_through_the_writer_without_losing_anything()
    {
        var original = BillProfileXml.ParseText(BillProfileXml.DefaultXml);
        var again = BillProfileXml.ParseText(BillProfileXml.Write(original).ToString());

        Assert.Equal(original.Count, again.Count);
        Assert.Equal(
            original.Select(Describe),
            again.Select(Describe));

        static string Describe(BillProfile p) =>
            string.Join('|', p.Name, p.FromContains, p.SubjectRegex, p.Category, p.Currency,
                p.FromPdf, p.Preferred, p.AllowZero, p.Refund, p.NoDedup,
                string.Join(',', p.AmountRegexes.Select(r => r.ToString())),
                p.CategoryRules is { } c
                    ? string.Join(',', c.Tokens.Select(t => t.ToString())) + "=>" +
                      string.Join(',', c.When.Select(w => $"{w.Prefix}:{w.Category}"))
                    : "");
    }

    [Theory]
    [InlineData("<BillProfiles/>", "no <Profile>")]
    [InlineData("""<BillProfiles><Profile from="a" category="b" currency="LKR"><Subject>x</Subject><Amount>(1)</Amount></Profile></BillProfiles>""", "missing 'name'")]
    [InlineData("""<BillProfiles><Profile name="n" category="b" currency="LKR"><Subject>x</Subject><Amount>(1)</Amount></Profile></BillProfiles>""", "missing 'from'")]
    [InlineData("""<BillProfiles><Profile name="n" from="a" category="b" currency="LKR"><Amount>(1)</Amount></Profile></BillProfiles>""", "needs a <Subject>")]
    [InlineData("""<BillProfiles><Profile name="n" from="a" category="b" currency="LKR"><Subject>x</Subject></Profile></BillProfiles>""", "at least one <Amount>")]
    [InlineData("""<BillProfiles><Profile name="n" from="a" category="b" currency="LKR"><Subject>x</Subject><Amount>([unclosed</Amount></Profile></BillProfiles>""", "profile 'n'")]
    public void Malformed_files_are_rejected_with_a_message_that_names_the_problem(string xml, string expected)
    {
        var ex = Assert.ThrowsAny<Exception>(() => BillProfileXml.ParseText(xml));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_broken_reload_keeps_the_profiles_already_in_use()
    {
        var before = BillProfiles.All;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "<BillProfiles><Profile name=\"broken\"/></BillProfiles>");
            Assert.False(BillProfiles.Reload(path));
            Assert.NotNull(BillProfiles.LoadError);
            Assert.Same(before, BillProfiles.All);   // a typo must not disable importing
        }
        finally { File.Delete(path); }
    }
}
