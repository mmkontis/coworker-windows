using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using NetSparkleUpdater;
using NetSparkleUpdater.AppCastHandlers;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;

namespace Coworker.Services.Updates;

/// <summary>
/// Requires a valid Ed25519 signature on every installer (NetSparkle's own OnlyVerifySoftwareDownloads
/// mode would accept an item with no signature at all) but, like the Mac Sparkle setup, does not
/// require the feed itself to be signed: an empty feed signature is accepted, a present one must verify.
/// </summary>
internal sealed class Ed25519DownloadVerifier : ISignatureVerifier
{
    private readonly Ed25519Checker _strict;

    public Ed25519DownloadVerifier(string publicKeyBase64)
    {
        // Strict makes AppCastHelper drop feed items without a signature and reject unsigned downloads.
        _strict = new Ed25519Checker(SecurityMode.Strict, publicKey: publicKeyBase64, publicKeyFile: null,
            readFileBeingVerifiedInChunks: true);
    }

    public SecurityMode SecurityMode
    {
        get => _strict.SecurityMode;
        set => _strict.SecurityMode = value;
    }

    public bool HasValidKeyInformation() => _strict.HasValidKeyInformation();

    /// Used for the feed (and release notes): unsigned is fine, signed must be valid.
    public ValidationResult VerifySignature(string signature, byte[] dataToVerify) =>
        string.IsNullOrWhiteSpace(signature) ? ValidationResult.Unchecked : _strict.VerifySignature(signature, dataToVerify);

    public ValidationResult VerifySignatureOfString(string signature, string dataToVerify) =>
        string.IsNullOrWhiteSpace(signature) ? ValidationResult.Unchecked : _strict.VerifySignatureOfString(signature, dataToVerify);

    /// Used for the installer: always required.
    public ValidationResult VerifySignatureOfFile(string signature, string binaryPath) =>
        _strict.VerifySignatureOfFile(signature, binaryPath);
}

/// <summary>Fetches the feed with a hard timeout and no caching (Mac: URLRequest .reloadIgnoringLocalCacheData, 5 s).</summary>
internal sealed class HttpAppCastDownloader : IAppCastDataDownloader
{
    private readonly HttpClient _http;

    /// The feed as last downloaded (for the checks NetSparkle's item model has no field for).
    public string LastFeed { get; private set; } = "";

    public HttpAppCastDownloader(string userAgent, TimeSpan timeout)
    {
        _http = new HttpClient { Timeout = timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    public string DownloadAndGetAppCastData(string url) => DownloadAndGetAppCastDataAsync(url).GetAwaiter().GetResult();

    public async Task<string> DownloadAndGetAppCastDataAsync(string url)
    {
        // The feed is not signed (parity with the Mac feed), so NetSparkle's probes for
        // <feed>.signature / <feed>.dsa are answered locally instead of costing two 404 round trips.
        if (url.EndsWith(".signature", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".dsa", StringComparison.OrdinalIgnoreCase))
            return "";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LastFeed = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return LastFeed;
    }

    public Encoding GetAppCastEncoding() => new UTF8Encoding(false);
}

/// <summary>
/// Keeps only builds newer than the running one that match this CPU. NetSparkle's own OS check accepts any
/// sparkle:os containing "win", so without this a windows-arm64 item would be offered to an x64 install.
/// An item whose os names no CPU ("windows") is offered to everyone.
/// </summary>
internal sealed class WindowsBuildFilter : IAppCastFilter
{
    private readonly Architecture _architecture;

    public WindowsBuildFilter(Architecture architecture) => _architecture = architecture;

    public IEnumerable<AppCastItem> GetFilteredAppCastItems(SemVerLike installed, IEnumerable<AppCastItem> items) =>
        items.Where(item => SemVerLike.Parse(item.Version ?? "").CompareTo(installed) > 0 && Matches(item.OperatingSystem));

    public bool Matches(string? operatingSystem)
    {
        var os = (operatingSystem ?? "").ToLowerInvariant();
        Architecture? named =
            os.Contains("arm64") ? Architecture.Arm64 :
            os.Contains("x64") || os.Contains("amd64") ? Architecture.X64 :
            os.Contains("x86") || os.Contains("win32") ? Architecture.X86 :
            null;
        return named == null || named == _architecture;
    }
}

/// <summary>NetSparkle reads the installed version from this; it is the build number (sparkle:version).</summary>
internal sealed class FixedAssemblyAccessor : IAssemblyAccessor
{
    public FixedAssemblyAccessor(string build, string product)
    {
        AssemblyVersion = build;
        AssemblyProduct = product;
        AssemblyTitle = product;
    }

    public string AssemblyCompany => "Humanlike";
    public string AssemblyCopyright => "";
    public string AssemblyDescription => "";
    public string AssemblyProduct { get; }
    public string AssemblyTitle { get; }
    public string AssemblyVersion { get; }
}

internal sealed class DelegateLogger : ILogger
{
    private readonly Action<string>? _log;

    public DelegateLogger(Action<string>? log) => _log = log;

    public void PrintMessage(string message, params object[]? arguments)
    {
        if (_log == null) return;
        try { _log("NetSparkle: " + (arguments is not { Length: > 0 } ? message : string.Format(message, arguments))); }
        catch (FormatException) { _log("NetSparkle: " + message); }
    }
}

/// <summary>Mac UpdaterDelegate helpers (UpdaterDelegate.swift), kept pure so they can be unit-tested.</summary>
public static class FeedRules
{
    /// Foundation's String.compare(_, options: .numeric): digit runs compare as numbers, so
    /// "1.10" &gt; "1.9". Returns &lt;0, 0 or &gt;0.
    public static int CompareNumeric(string? left, string? right)
    {
        left ??= "";
        right ??= "";
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                var si = i;
                var sj = j;
                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;
                var a = left[si..i].TrimStart('0');
                var b = right[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(left[i]).CompareTo(char.ToLowerInvariant(right[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }

    /// The newest build (sparkle:version, falling back to shortVersionString) among the feed's Windows
    /// items; null when the feed has none. Mac: extractLatestVersion, minus its "first match wins" quirk.
    public static string? LatestBuild(string appcastXml)
    {
        try
        {
            var doc = XDocument.Parse(appcastXml);
            XNamespace sp = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            string? best = null;
            foreach (var item in doc.Descendants("item"))
            {
                var enclosure = item.Element("enclosure");
                var os = (string?)enclosure?.Attribute(sp + "os");
                if (!string.IsNullOrWhiteSpace(os) && !os.Contains("win", StringComparison.OrdinalIgnoreCase)) continue;

                var build = (string?)item.Element(sp + "version") ?? (string?)enclosure?.Attribute(sp + "version")
                    ?? (string?)item.Element(sp + "shortVersionString") ?? (string?)enclosure?.Attribute(sp + "shortVersionString");
                if (string.IsNullOrWhiteSpace(build)) continue;
                if (best == null || CompareNumeric(build, best) > 0) best = build.Trim();
            }
            return best;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public enum FeedChoice { Local, Production }

    /// Mac chooseFeed: a reachable local feed wins only when it is strictly newer than production.
    public static FeedChoice ChooseFeed(string? localBuild, string? productionBuild)
    {
        if (localBuild != null && productionBuild != null)
            return CompareNumeric(localBuild, productionBuild) > 0 ? FeedChoice.Local : FeedChoice.Production;
        return localBuild != null ? FeedChoice.Local : FeedChoice.Production;
    }

    /// Builds the feed marks with sparkle:tag "critical" (the second marker Mac's hasCriticalUpdate
    /// accepts next to sparkle:criticalUpdate, which NetSparkle reads by itself).
    public static ISet<string> CriticalTaggedBuilds(string appcastXml)
    {
        var builds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var doc = XDocument.Parse(appcastXml);
            XNamespace sp = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            foreach (var item in doc.Descendants("item"))
            {
                var tagged = item.Elements(sp + "tag").Any(t => string.Equals(t.Value.Trim(), "critical", StringComparison.OrdinalIgnoreCase));
                if (!tagged) continue;
                var enclosure = item.Element("enclosure");
                var build = (string?)item.Element(sp + "version") ?? (string?)enclosure?.Attribute(sp + "version");
                if (!string.IsNullOrWhiteSpace(build)) builds.Add(build.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
        }
        return builds;
    }
}
