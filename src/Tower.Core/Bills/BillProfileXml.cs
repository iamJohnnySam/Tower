using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tower.Core.Bills;

/// <summary>Reads bill profiles from XML so a new sender — or a sender that quietly changed its
/// template — can be handled by editing a file in the deploy directory and hitting "Reload" on
/// /bills, with no rebuild. Every parameter of <see cref="BillProfile"/> is representable.</summary>
public static class BillProfileXml
{
    /// <summary>Parses <paramref name="path"/>. Throws <see cref="InvalidDataException"/> naming the
    /// offending profile if anything is missing or a regex doesn't compile — a half-loaded profile
    /// set would silently stop importing some bills, which is exactly the failure we're avoiding.</summary>
    public static IReadOnlyList<BillProfile> Load(string path) => Parse(XDocument.Load(path), path);

    public static IReadOnlyList<BillProfile> ParseText(string xml) => Parse(XDocument.Parse(xml), "<string>");

    /// <summary>The profile set shipped inside the binary. Embedded rather than copied next to the
    /// exe so it is always present — for tests, for a first run before any file exists, and as the
    /// seed for the editable live file.</summary>
    public static string DefaultXml
    {
        get
        {
            using var s = typeof(BillProfileXml).Assembly.GetManifestResourceStream("bill-profiles.default.xml")
                ?? throw new InvalidOperationException("embedded bill-profiles.default.xml is missing");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }

    private static IReadOnlyList<BillProfile> Parse(XDocument doc, string source)
    {
        var root = doc.Root ?? throw new InvalidDataException($"{source}: empty document");
        var profiles = new List<BillProfile>();

        foreach (var e in root.Elements("Profile"))
        {
            // Read the name loosely first so the error below can still say which profile failed,
            // then require it — an unnamed profile would show up as "<unnamed>" on /bills and on
            // every row it imported.
            var name = (string?)e.Attribute("name") ?? "<unnamed>";
            try
            {
                Attr(e, "name");
                var amounts = e.Elements("Amount").Select(a => Rx(a.Value)).ToArray();
                if (amounts.Length == 0) throw new InvalidDataException("needs at least one <Amount>");

                var subject = e.Element("Subject")?.Value
                    ?? throw new InvalidDataException("needs a <Subject>");

                profiles.Add(new BillProfile(
                    name,
                    Attr(e, "from"),
                    Rx(subject),
                    Attr(e, "category"),
                    amounts,
                    Attr(e, "currency"),
                    FromPdf: Flag(e, "fromPdf"),
                    Preferred: Flag(e, "preferred"),
                    AllowZero: Flag(e, "allowZero"),
                    Refund: Flag(e, "refund"),
                    NoDedup: Flag(e, "noDedup"),
                    CategoryRules: Rules(e.Element("CategoryRules"))));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"{source}: profile '{name}' — {ex.Message}", ex);
            }
        }

        if (profiles.Count == 0) throw new InvalidDataException($"{source}: no <Profile> elements");

        var dup = profiles.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"{source}: duplicate profile name '{dup.Key}'");

        return profiles;
    }

    private static CategoryRules? Rules(XElement? e)
    {
        if (e is null) return null;
        var tokens = e.Elements("Token").Select(t => Rx(t.Value)).ToArray();
        if (tokens.Length == 0) throw new InvalidDataException("<CategoryRules> needs at least one <Token>");
        var when = e.Elements("When")
            .Select(w => (Attr(w, "startsWith"), Attr(w, "category")))
            .ToArray();
        if (when.Length == 0) throw new InvalidDataException("<CategoryRules> needs at least one <When>");
        return new CategoryRules(tokens, when);
    }

    private static string Attr(XElement e, string name, string? fallback = null) =>
        (string?)e.Attribute(name) ?? fallback
        ?? throw new InvalidDataException($"missing '{name}' attribute");

    private static bool Flag(XElement e, string name) =>
        (string?)e.Attribute(name) is { } v && bool.Parse(v);

    // Same options the hand-written profiles used, so behaviour is identical after the move.
    private static Regex Rx(string pattern) =>
        new(pattern.Trim(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Serialises profiles back to the XML schema. Used to generate the initial file from
    /// the previously hand-written list, and handy for diffing what's actually loaded.</summary>
    public static XDocument Write(IEnumerable<BillProfile> profiles) =>
        new(new XElement("BillProfiles", profiles.Select(p =>
        {
            var e = new XElement("Profile",
                new XAttribute("name", p.Name),
                new XAttribute("from", p.FromContains),
                new XAttribute("category", p.Category),
                new XAttribute("currency", p.Currency));
            if (p.FromPdf) e.Add(new XAttribute("fromPdf", "true"));
            if (p.Preferred) e.Add(new XAttribute("preferred", "true"));
            if (p.AllowZero) e.Add(new XAttribute("allowZero", "true"));
            if (p.Refund) e.Add(new XAttribute("refund", "true"));
            if (p.NoDedup) e.Add(new XAttribute("noDedup", "true"));
            e.Add(new XElement("Subject", p.SubjectRegex.ToString()));
            foreach (var a in p.AmountRegexes) e.Add(new XElement("Amount", a.ToString()));
            if (p.CategoryRules is { } r)
                e.Add(new XElement("CategoryRules",
                    r.Tokens.Select(t => new XElement("Token", t.ToString()))
                        .Concat<object>(r.When.Select(w =>
                            new XElement("When", new XAttribute("startsWith", w.Prefix),
                                                 new XAttribute("category", w.Category))))));
            return e;
        })));
}
