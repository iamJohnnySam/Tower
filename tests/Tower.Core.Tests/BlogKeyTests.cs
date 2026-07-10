using Tower.Core.Website;

namespace Tower.Core.Tests;

public class BlogKeyTests
{
    private const string RealFile = """
        <?php
        return [
            'BLOG_API_KEY' => 'oldval',
            'DB_HOST' => 'localhost',
            'DB_USER' => 'root',
            'DB_PASS' => 'p@ss',
            'DB_NAME' => 'blog',
        ];
        """;

    [Fact]
    public void SwapKey_ReplacesOnlyKeyValue_PreservesDbEntries()
    {
        var newToken = BlogKeyService.GenerateToken();

        var result = BlogKeyService.SwapKey(RealFile, newToken);

        Assert.Contains($"'BLOG_API_KEY' => '{newToken}'", result);
        Assert.DoesNotContain("oldval", result);
        // Every DB entry must survive untouched.
        Assert.Contains("'DB_HOST' => 'localhost'", result);
        Assert.Contains("'DB_USER' => 'root'", result);
        Assert.Contains("'DB_PASS' => 'p@ss'", result);
        Assert.Contains("'DB_NAME' => 'blog'", result);
    }

    [Fact]
    public void SwapKey_MissingKey_Throws()
    {
        const string noKey = """
            <?php
            return [
                'DB_HOST' => 'localhost',
                'DB_PASS' => 'p@ss',
            ];
            """;

        Assert.Throws<InvalidOperationException>(() => BlogKeyService.SwapKey(noKey, "newtoken"));
    }

    [Fact]
    public void GenerateToken_Is64LowercaseHexChars()
    {
        var t = BlogKeyService.GenerateToken();
        Assert.Equal(64, t.Length);
        Assert.Matches("^[0-9a-f]{64}$", t);
    }
}
