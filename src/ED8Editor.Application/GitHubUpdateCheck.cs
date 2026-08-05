using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ED8Editor.Application;

/// <summary>A published release, as far as updating cares about it.</summary>
/// <param name="Version">
/// The tag, read as a version. Tags are written <c>v1.2.3</c> as often as
/// <c>1.2.3</c>, and the leading letter is decoration.
/// </param>
/// <param name="Notes">
/// What changed, as the release itself states it. Shown to the reader unaltered:
/// this is the author's own account of their release, and rewriting it here would
/// only make it disagree with the page it came from.
/// </param>
/// <param name="DownloadUrl">Where the build is, or null when the release has none.</param>
public sealed record GitHubRelease(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    string? DownloadUrl,
    long DownloadSize);

/// <summary>
/// Asks GitHub whether there is a newer build, and what changed in it.
///
/// Reading the answer is kept apart from fetching it, so the part that can be wrong
/// — parsing a tag, deciding what counts as newer — is testable without a network,
/// and the part that needs a network has nothing to decide.
///
/// A check that fails is not an error the editor reports: no network, a rate limit,
/// a repository that has published nothing yet all mean the same thing to someone
/// starting the tool, which is that it starts.
/// </summary>
public static class GitHubUpdateCheck
{
    /// <summary>Where the releases are published.</summary>
    public const string Repository = "TwnKey/ED8Editor";

    private static readonly Uri Latest =
        new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>
    /// The release described by GitHub's answer, or null when it describes none.
    /// </summary>
    public static GitHubRelease? ParseRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // A draft is not published and a prerelease is not what someone starting
            // the editor asked to be given.
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

            var tag = Text(root, "tag_name");
            if (ParseVersion(tag) is not { } version) return null;

            string? url = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    url = Text(asset, "browser_download_url");
                    size = asset.TryGetProperty("size", out var bytes)
                        && bytes.TryGetInt64(out var value) ? value : 0;
                    break;
                }
            }

            return new GitHubRelease(
                version,
                tag,
                Text(root, "name") is { Length: > 0 } named ? named : tag,
                Text(root, "body"),
                url,
                size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A tag as a version. <c>v1.2.3</c>, <c>1.2.3</c> and <c>1.2</c> all read;
    /// anything else does not, and a tag nobody can order is not one to compare
    /// against.
    /// </summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        // A tag may carry a suffix the version does not: 1.2.3-hotfix.
        var cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? Normalise(version) : null;
    }

    /// <summary>
    /// Whether a release is worth offering, given what is running.
    ///
    /// Only newer counts. Re-offering the version already installed is how an
    /// updater teaches people to dismiss it.
    /// </summary>
    public static bool IsNewer(GitHubRelease release, Version running)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(running);
        return release.Version > Normalise(running);
    }

    /// <summary>
    /// Asks GitHub for the latest release. Null when there is none, when the network
    /// says no, or when the answer is not one this understands.
    /// </summary>
    public static async Task<GitHubRelease?> FetchLatestAsync(
        HttpClient client,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
            // GitHub refuses a request with no user agent, and asking for the v3
            // media type keeps the shape of the answer fixed.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ED8Editor", "1.0"));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) return null;
            return ParseRelease(await response.Content.ReadAsStringAsync(cancellation));
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A version with its unstated parts as zero, so 1.2 and 1.2.0.0 compare equal.
    /// Left alone, <see cref="Version"/> treats an absent build as -1 and orders it
    /// below one that is present.
    /// </summary>
    private static Version Normalise(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
