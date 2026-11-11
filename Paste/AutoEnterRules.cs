using System.Globalization;
using System.Text;
using Coworker.Models;

namespace Coworker.Paste;

/// <summary>
/// Which apps and sites get Return after a dictation paste (Mac: the autoSubmit rules in
/// ClipboardFeature/AppSettings.swift). Auto-Enter must be on and the destination must be in the lists:
/// an empty list means no app, never "everywhere".
/// </summary>
public static class AutoEnterRules
{
    public static bool ShouldAutoSubmit(bool enabled, IEnumerable<string> apps, IEnumerable<string> sites, string? appName, string? site) =>
        enabled && (AppMatches(appName, apps) || SiteMatches(site, sites));

    /// <see cref="ShouldAutoSubmit(bool, IEnumerable{string}, IEnumerable{string}, string?, string?)"/> with the saved settings.
    public static bool ShouldAutoSubmit(string? appName, string? site)
    {
        var settings = AppSettings.Instance;
        return ShouldAutoSubmit(settings.AutoSubmitEnabled, settings.AutoSubmitApps, settings.AutoSubmitSites, appName, site);
    }

    public static bool AppMatches(string? appName, IEnumerable<string> enabledApps)
    {
        var key = AppKey(appName);
        return key != null && enabledApps.Select(AppKey).Contains(key);
    }

    public static bool SiteMatches(string? site, IEnumerable<string> enabledSites)
    {
        var host = NormalizedHost(site);
        return host != null && enabledSites.Contains(host);
    }

    public static bool IsAppSelected(string? appName) => AppMatches(appName, AppSettings.Instance.AutoSubmitApps);

    public static bool IsSiteSelected(string? site) => SiteMatches(site, AppSettings.Instance.AutoSubmitSites);

    /// Turns Auto-Enter on or off for one app (Mac: setAutoSubmit(_:forAppName:)). The caller saves.
    public static void SetApp(string appName, bool enabled)
    {
        var name = CleanedAppName(appName);
        var key = AppKey(name);
        if (name == null || key == null) return;

        var apps = AppSettings.Instance.AutoSubmitApps;
        if (enabled)
        {
            if (!AppMatches(name, apps)) apps.Add(name);
        }
        else
        {
            apps.RemoveAll(existing => AppKey(existing) == key);
        }
    }

    /// Turns Auto-Enter on or off for one site (Mac: setAutoSubmit(_:forSite:)). The caller saves.
    public static void SetSite(string site, bool enabled)
    {
        var host = NormalizedHost(site);
        if (host == null) return;

        var sites = AppSettings.Instance.AutoSubmitSites;
        if (enabled)
        {
            if (!sites.Contains(host)) sites.Add(host);
        }
        else
        {
            sites.RemoveAll(existing => existing == host);
        }
    }

    /// The app name without surrounding space; null for nothing or "unknown".
    public static string? CleanedAppName(string? appName)
    {
        var trimmed = appName?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    /// Names compare without case or accents.
    public static string? AppKey(string? appName)
    {
        var cleaned = CleanedAppName(appName);
        if (cleaned == null) return null;

        var builder = new StringBuilder();
        foreach (var c in cleaned.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().ToLowerInvariant();
    }

    /// "https://www.ChatGPT.com/c/1" or "chatgpt.com" gives "chatgpt.com"; a host without a dot gives null (Mac: BrowserSite.normalizedHost).
    public static string? NormalizedHost(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains('.')) return null;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
