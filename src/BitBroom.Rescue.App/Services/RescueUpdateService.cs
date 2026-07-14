using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BitBroom.Rescue.App.Services;

/// <summary>The newer release we found, plus where to get it.</summary>
public sealed record RescueUpdateInfo(Version Version, string ReleasePageUrl);

/// <summary>
/// Notify-only update check for the portable app. Unlike BitBroom Cleaner (which has an
/// installer and can update itself), Rescue is deliberately portable — often run as admin
/// from a USB stick mid-emergency — so it must NEVER download or replace itself. This does
/// exactly one thing: a single opt-out HTTPS call to the GitHub releases API at startup to
/// see whether a newer version exists, so someone running an old rescue copy learns about
/// fixes. It only ever surfaces a banner linking to the release page; the user downloads a
/// fresh zip themselves. This is the only network code in BitBroom Rescue.
/// </summary>
public static class RescueUpdateService
{
    private const string Owner = "pwnapplehat";
    private const string Repo = "BitBroom.Rescue";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    private const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"BitBroomRescue/{CurrentVersion.ToString(3)} (+https://github.com/{Owner}/{Repo})");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Running version, from the assembly's informational version (set by &lt;Version&gt;).
    /// BBR_TEST_CURRENT_VERSION overrides it so the banner flow can be exercised end-to-end.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            string? test = Environment.GetEnvironmentVariable("BBR_TEST_CURRENT_VERSION");
            if (test is not null && TryNormalize(test, out Version? overridden))
            {
                return overridden!;
            }

            string raw = typeof(RescueUpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            return TryNormalize(raw, out Version? v) ? v! : new Version(1, 0, 0);
        }
    }

    /// <summary>Returns info about a newer release, or null if up to date / offline / check failed.</summary>
    public static async Task<RescueUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(LatestApi, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            if (tag is null || !TryNormalize(tag, out Version? latest))
            {
                return null;
            }

            string url = root.TryGetProperty("html_url", out JsonElement h) && h.GetString() is { Length: > 0 } page
                ? page
                : ReleasesPage;

            return latest! > CurrentVersion ? new RescueUpdateInfo(latest!, url) : null;
        }
        catch
        {
            // Never let a network hiccup affect the app — a recovery tool must start regardless.
            return null;
        }
    }

    private static bool TryNormalize(string s, out Version? v)
    {
        v = null;
        string core = s.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out Version? parsed))
        {
            return false;
        }

        v = new Version(Math.Max(0, parsed.Major), Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }
}
