using Chameleon.Host.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chameleon.IntegrationTests.Plugins;

public class CatalogTests
{
    private const string Json = """
    [{"name":"zendesk","packageId":"Chameleon.Mock.Connectors.Zendesk","version":"0.1.0","title":"Zendesk",
      "description":"d","provider":"https://developer.zendesk.com","repository":"https://github.com/Erbio-Digital/chameleon-mock",
      "license":"Apache-2.0","sdk":"0.1.x","closure":[{"id":"Chameleon.Mock.Connectors.Zendesk","version":"0.1.0","sha512":"x"}],"revoked":null},
     {"name":"old","packageId":"P","version":"0.0.1","title":"t","description":"d","provider":"p","repository":"r",
      "license":"MIT","sdk":"0.0.x","closure":[{"id":"P","version":"0.0.1","sha512":"y"}],"revoked":"compromised"}]
    """;

    [Fact] public void Parse_FindsByName() => Assert.Equal("Chameleon.Mock.Connectors.Zendesk", Catalog.Parse(Json).Find("zendesk")!.PackageId);
    [Fact] public void Parse_NullField_Throws() =>
        Assert.Throws<System.Text.Json.JsonException>(() => Catalog.Parse(Json.Replace("\"name\":\"old\"", "\"name\":null")));
    [Fact] public void Parse_DuplicateName_Throws() =>
        Assert.Throws<System.Text.Json.JsonException>(() => Catalog.Parse(Json.Replace("\"name\":\"old\"", "\"name\":\"Zendesk\"")));
    [Fact] public void Parse_KeepsRevokedReason() => Assert.Equal("compromised", Catalog.Parse(Json).Find("old")!.Revoked);
    [Fact] public void Parse_ReadsClosureHash() => Assert.Equal("x", Catalog.Parse(Json).Find("zendesk")!.Closure[0].Sha512);

    [Fact]
    public void Embedded_IsEmpty() => Assert.Empty(Catalog.Embedded().Entries);

    [Fact]
    public async Task LoadAsync_RemoteFailure_FallsBackToEmbedded()
    {
        using var http = new HttpClient(new FailingHandler());
        var c = await Catalog.LoadAsync("https://example.invalid/catalog.json", offline: false, http, NullLogger.Instance, default);
        Assert.Empty(c.Entries);
    }

    [Fact]
    public async Task LoadAsync_Offline_SkipsRemote()
    {
        using var http = new HttpClient(new FailingHandler());
        var c = await Catalog.LoadAsync("https://example.invalid/catalog.json", offline: true, http, NullLogger.Instance, default);
        Assert.Empty(c.Entries);
    }

    [Fact]
    public async Task LoadAsync_RemoteSuccess_UsesRemote()
    {
        using var http = new HttpClient(new FixedHandler(Json));
        var c = await Catalog.LoadAsync("https://example.test/catalog.json", offline: false, http, NullLogger.Instance, default);
        Assert.Equal(["zendesk", "old"], c.Entries.Select(e => e.Name).ToArray());
    }

    [Theory]
    [InlineData("not json", 200)]
    [InlineData("[]", 200)]
    [InlineData("[]", 500)]
    [InlineData(Json, 404)]
    public async Task LoadAsync_BadRemote_FallsBackToEmbedded(string body, int status)
    {
        using var http = new HttpClient(new FixedHandler(body, (System.Net.HttpStatusCode)status));
        var c = await Catalog.LoadAsync("https://example.test/catalog.json", offline: false, http, NullLogger.Instance, default);
        Assert.Empty(c.Entries);
    }

    [Theory]
    [InlineData("http://example.test/catalog.json")]
    [InlineData("ftp://example.test/catalog.json")]
    public async Task LoadAsync_DisallowedUrl_FallsBackWithoutRequest(string url)
    {
        using var http = new HttpClient(new FixedHandler(Json));
        var c = await Catalog.LoadAsync(url, offline: false, http, NullLogger.Instance, default);
        Assert.Empty(c.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_File_ReadsLocalFile(bool offline)
    {
        var path = Path.Combine(Path.GetTempPath(), "chameleon-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Json);
        try
        {
            using var http = new HttpClient(new FailingHandler());
            var c = await Catalog.LoadAsync(new Uri(path).AbsoluteUri, offline, http, NullLogger.Instance, default);
            Assert.Equal(["zendesk", "old"], c.Entries.Select(e => e.Name).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task LoadAsync_BadFile_FallsBackToEmbedded(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), "chameleon-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        if (content is not null) File.WriteAllText(path, content);
        try
        {
            using var http = new HttpClient(new FailingHandler());
            var c = await Catalog.LoadAsync(new Uri(path).AbsoluteUri, offline: false, http, NullLogger.Instance, default);
            Assert.Empty(c.Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_HttpLoopback_IsAllowed()
    {
        using var http = new HttpClient(new FixedHandler(Json));
        var c = await Catalog.LoadAsync("http://localhost:8080/catalog.json", offline: false, http, NullLogger.Instance, default);
        Assert.Equal(2, c.Entries.Count);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    private sealed class FixedHandler(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
