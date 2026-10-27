using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <param name="Page">The release's page on GitHub, where the download is.</param>
public sealed record UpdateInfo(Version Version, Uri Page);

/// <summary>
/// The opt-in update check: one anonymous request to GitHub's public API for the latest release. Nothing about the PC
/// or its accounts is sent, and it never downloads or runs anything; the person opens the page themselves.
/// </summary>
public sealed class UpdateCheck(HttpMessageHandler? handler = null)
{
    public static readonly Uri LatestRelease = new("https://api.github.com/repos/RathaTart/claude-roster/releases/latest");
    public static readonly Uri ReleasesPage = new("https://github.com/RathaTart/claude-roster/releases/latest");

    private const string PagePrefix = "https://github.com/RathaTart/claude-roster/releases/";

    private readonly HttpMessageHandler _handler = handler ?? new SocketsHttpHandler();

    /// <summary>This build's version.</summary>
    public static Version Current => typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0);

    /// <returns>The newer release, or null when this is the latest or GitHub couldn't be asked.</returns>
    public async Task<UpdateInfo?> FindNewerAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        // GitHub's API refuses requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeRoster", Current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                ? Parse(await response.Content.ReadAsStringAsync(cancellationToken), current)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Offline, or GitHub is down: try again next time.
            return null;
        }
    }

    /// <summary>Reads GitHub's release JSON: <c>tag_name</c> like "v0.5.0", and <c>html_url</c>.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject release
                || release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true
                || release["tag_name"]?.GetValue<string>() is not { } tag
                || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                return null;
            }

            // Only ever point at this project's own release pages, whatever the reply says.
            var page = release["html_url"]?.GetValue<string>() is { } url && url.StartsWith(PagePrefix, StringComparison.Ordinal)
                       && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? parsed
                : ReleasesPage;
            return Normalize(latest) > Normalize(current) ? new UpdateInfo(latest, page) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>1.2 and 1.2.0.0 are the same version.</summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
