using System.Globalization;
using System.Text;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Settings;

/// One row of the Auto-Enter apps list.
public sealed record AutoEnterAppOption(string Key, string AppName, int UsageCount, bool IsEnabled)
{
    public string UsageText => UsageCount switch
    {
        1 => "1 clipboard item",
        > 1 => $"{UsageCount} clipboard items",
        _ => "No recent clipboard history",
    };
}

/// The app list behind "Auto-Enter after dictation": the apps already selected plus every app a
/// clipboard item was copied from, most-used first (Mac: AppSettings.autoSubmitAppOptions in
/// ClipboardFeature/AppSettings.swift). Names are compared without case and diacritics.
public static class AutoEnterApps
{
    public static IReadOnlyList<AutoEnterAppOption> Options(IEnumerable<ClipboardItem> items, IEnumerable<string> enabledNames)
    {
        var byKey = new Dictionary<string, (string Name, int Usage, bool Enabled)>();

        foreach (var name in enabledNames)
        {
            if (Clean(name) is { } clean && Key(clean) is { } key)
                byKey[key] = (clean, 0, true);
        }

        foreach (var item in items)
        {
            if (Clean(item.SourceApp) is not { } name || Key(name) is not { } key) continue;

            var option = byKey.TryGetValue(key, out var existing) ? existing : (Name: name, Usage: 0, Enabled: false);
            if (option.Usage == 0) option.Name = name;
            option.Usage++;
            byKey[key] = option;
        }

        return byKey
            .Select(p => new AutoEnterAppOption(p.Key, p.Value.Name, p.Value.Usage, p.Value.Enabled))
            .OrderByDescending(o => o.UsageCount)
            .ThenBy(o => o.AppName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsSelected(string appName) =>
        Key(appName) is { } key && AppSettings.Instance.AutoSubmitApps.Any(n => Key(n) == key);

    /// Adds or removes the app in AppSettings.AutoSubmitApps (without saving).
    public static void Set(bool enabled, string appName)
    {
        if (Clean(appName) is not { } clean || Key(clean) is not { } key) return;

        var apps = AppSettings.Instance.AutoSubmitApps;
        if (enabled)
        {
            if (!apps.Any(n => Key(n) == key)) apps.Add(clean);
        }
        else
        {
            apps.RemoveAll(n => Key(n) == key);
        }
    }

    // Empty names and "unknown" (the clipboard's placeholder for an unidentified source) are not apps.
    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string? Key(string? name)
    {
        if (Clean(name) is not { } clean) return null;

        var decomposed = clean.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        }
        return folded.ToString().ToLowerInvariant();
    }
}
