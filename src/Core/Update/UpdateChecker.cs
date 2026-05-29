using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrusaConnect.Core.Update;

/// <summary>The newest release found on GitHub, parsed from releases/latest.</summary>
public sealed record ReleaseInfo(Version Version, string TagName, string ReleaseUrl, string? InstallerUrl);

/// <summary>
/// Checks the public GitHub releases for a newer build. Unauthenticated and
/// read-only (the repo is public), and best-effort: any failure returns null so
/// a failed check never breaks the settings app.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string Owner = "nomadsgalaxy";
    public const string Repo = "PrusaConnect-Widgets";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases/latest";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _latestUrl;

    public UpdateChecker(HttpClient? http = null, string? apiBase = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttpClient = http is null;
        // GitHub's API rejects requests with no User-Agent.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrusaConnectWidget-Updater/1.0 (+windows)");
        }
        string b = string.IsNullOrWhiteSpace(apiBase) ? "https://api.github.com" : apiBase!.TrimEnd('/');
        _latestUrl = $"{b}/repos/{Owner}/{Repo}/releases/latest";
    }

    /// <summary>
    /// The latest published release, or null if there are none yet (404) or the
    /// check failed (offline, rate-limited, unparseable). Never throws except on
    /// cancellation.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _latestUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseTag(tag!, out var ver)) return null;

            string url = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? ReleasesPage) : ReleasesPage;

            // Prefer the installer zip asset, if the release carries one.
            string? installer = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && a.TryGetProperty("browser_download_url", out var d))
                    {
                        installer = d.GetString();
                        break;
                    }
                }
            }
            return new ReleaseInfo(ver, tag!, url, installer);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Parse a release tag like "v1.2.3", "1.2", or "v2-beta" into a Version.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        string s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        int cut = s.IndexOfAny(new[] { '-', '+' });   // drop -beta / +meta suffixes
        if (cut >= 0) s = s.Substring(0, cut);
        if (!s.Contains('.')) s += ".0";              // "1" -> "1.0"
        return Version.TryParse(s, out version!);
    }

    /// <summary>
    /// True if <paramref name="latest"/> is a newer release than the installed
    /// build. Compares major.minor.build only; the package's 4th (revision) digit
    /// is a build counter, not part of the release version.
    /// </summary>
    public static bool IsNewer(Version latest, Version current)
    {
        static int N(int v) => v < 0 ? 0 : v;
        return (latest.Major, latest.Minor, N(latest.Build))
            .CompareTo((current.Major, current.Minor, N(current.Build))) > 0;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
