using Chameleon.Host.Plugins;

namespace Chameleon.IntegrationTests.Plugins;

public sealed class ProviderSheetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chameleon-sheets-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Sheet(string content)
    {
        File.WriteAllText(Path.Combine(_dir, "demo.md"), content);
        return _dir;
    }

    [Fact]
    public void Two_links_return_the_first() =>
        Assert.Equal("https://one.example/docs", ProviderSheet.ProviderUrl(Sheet("# Demo\n\n## Sources\n\n- https://one.example/docs\n- https://two.example\n\n## Other\n\nhttps://three.example\n"), "demo"));

    [Fact]
    public void Without_a_sources_section_returns_null() =>
        Assert.Null(ProviderSheet.ProviderUrl(Sheet("# Demo\n\n## Notes\n\nhttps://one.example\n"), "demo"));

    [Fact]
    public void Http_links_are_ignored() =>
        Assert.Equal("https://two.example", ProviderSheet.ProviderUrl(Sheet("## Sources\n\n- http://one.example\n- https://two.example\n"), "demo"));

    [Fact]
    public void Only_http_links_return_null() =>
        Assert.Null(ProviderSheet.ProviderUrl(Sheet("## Sources\n\n- http://one.example\n"), "demo"));

    [Fact]
    public void Http_only_sources_ignore_an_https_link_in_a_later_section() =>
        Assert.Null(ProviderSheet.ProviderUrl(Sheet("## Sources\n\n- http://one.example\n\n## Other\n\nhttps://three.example\n"), "demo"));

    [Theory]
    [InlineData("- [docs](https://x.example/y)")]
    [InlineData("- <https://x.example/y>")]
    public void Link_delimiters_are_trimmed(string line) =>
        Assert.Equal("https://x.example/y", ProviderSheet.ProviderUrl(Sheet($"## Sources\n\n{line}\n"), "demo"));

    [Theory]
    [InlineData("../demo")]
    [InlineData("Demo")]
    [InlineData("-demo")]
    public void Invalid_names_return_null(string name)
    {
        Sheet("## Sources\n\n- https://one.example\n");
        Assert.Null(ProviderSheet.ProviderUrl(_dir, name));
    }

    [Fact]
    public void Without_a_sheet_returns_null() => Assert.Null(ProviderSheet.ProviderUrl(_dir, "missing"));
}
