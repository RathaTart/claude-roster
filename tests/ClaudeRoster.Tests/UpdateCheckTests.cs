using System.Net;
using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class UpdateCheckTests
{
    private static readonly Version Current = new(0, 4, 0, 0);

    [Fact]
    public void A_newer_release_is_found_with_its_page()
    {
        var update = UpdateCheck.Parse(Release("v0.5.0", "https://github.com/RathaTart/claude-roster/releases/tag/v0.5.0"), Current);

        Assert.Equal(new Version(0, 5, 0), update?.Version);
        Assert.Equal("https://github.com/RathaTart/claude-roster/releases/tag/v0.5.0", update?.Page.ToString());
    }

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("0.4")]
    [InlineData("v0.3.1")]
    [InlineData("nightly")]
    public void The_same_an_older_or_an_odd_tag_is_no_update(string tag) =>
        Assert.Null(UpdateCheck.Parse(Release(tag, "https://github.com/RathaTart/claude-roster/releases/tag/x"), Current));

    [Fact]
    public void A_link_elsewhere_is_replaced_by_the_projects_own_page()
    {
        var update = UpdateCheck.Parse(Release("v9.0.0", "https://example.com/download.exe"), Current);

        Assert.Equal(UpdateCheck.ReleasesPage, update?.Page);
    }

    [Fact]
    public void Drafts_prereleases_and_garbage_are_ignored()
    {
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"v9.0.0","prerelease":true}""", Current));
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"v9.0.0","draft":true}""", Current));
        Assert.Null(UpdateCheck.Parse("not json", Current));
    }

    [Fact]
    public async Task It_asks_GitHub_once_and_survives_being_offline()
    {
        var asked = new List<HttpRequestMessage>();
        var online = new StubHandler(request =>
        {
            asked.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("v1.0.0", "https://github.com/RathaTart/claude-roster/releases/tag/v1.0.0")) };
        });

        Assert.Equal(new Version(1, 0, 0), (await new UpdateCheck(online).FindNewerAsync(Current))?.Version);
        Assert.Equal(UpdateCheck.LatestRelease, Assert.Single(asked).RequestUri);
        Assert.NotEmpty(asked[0].Headers.UserAgent);

        var offline = new StubHandler(_ => throw new HttpRequestException("offline"));
        Assert.Null(await new UpdateCheck(offline).FindNewerAsync(Current));
    }

    private static string Release(string tag, string url) => $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","draft":false,"prerelease":false}""";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
