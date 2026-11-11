using Coworker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Services;

public enum AppTheme
{
    Clean,
    Aurora,
}

/// <summary>
/// Clean (Mac default, follows the OS light/dark mode) or Aurora. The sidebar brushes
/// (SidebarSurfaceBrush, SidebarPrimaryText, ... declared in Theme/CoworkerTheme.xaml) are live
/// objects whose colors this service rewrites from the palette in Theme/CleanTheme.xaml or
/// AuroraTheme.xaml, so any page using them restyles instantly. (Swapping merged dictionaries at
/// runtime was tried first: already-open pages do not re-resolve their theme resources.)
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Shared { get; } = new();

    private readonly Dictionary<AppTheme, ResourceDictionary> _palettes = new();
    private FrameworkElement? _root;

    private ThemeService() { }

    public AppTheme Current { get; private set; } = AppTheme.Clean;

    /// Raised after the theme changed (UI thread).
    public event Action<AppTheme>? ThemeChanged;

    /// Loads the persisted theme. Call once at startup, before the main window exists.
    public void Initialize()
    {
        Current = Enum.TryParse<AppTheme>(AppSettings.Instance.Theme, ignoreCase: true, out var saved) ? saved : AppTheme.Clean;
        UpdateBrushes();
    }

    /// Follows the light/dark mode of the window content, so Clean tracks the OS.
    public void Attach(FrameworkElement root)
    {
        _root = root;
        root.ActualThemeChanged += (_, _) => UpdateBrushes();
        UpdateBrushes();
    }

    public void Apply(AppTheme theme)
    {
        if (theme == Current) return;

        Current = theme;
        UpdateBrushes();
        AppSettings.Instance.Theme = theme.ToString().ToLowerInvariant();
        AppSettings.Instance.Save();
        ThemeChanged?.Invoke(theme);
    }

    private void UpdateBrushes()
    {
        if (!_palettes.TryGetValue(Current, out var palette))
            _palettes[Current] = palette = new ResourceDictionary { Source = new Uri($"ms-appx:///Theme/{Current}Theme.xaml") };

        var dark = _root != null
            ? _root.ActualTheme == ElementTheme.Dark
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;

        var themes = palette.ThemeDictionaries;
        if (!(themes.TryGetValue(dark ? "Dark" : "Light", out var variant) || themes.TryGetValue("Default", out variant)))
            return;

        var resources = Application.Current.Resources;
        foreach (var (key, value) in (ResourceDictionary)variant)
        {
            if (resources[key] is Brush live && value is Brush source) CopyBrush(source, live);
        }
    }

    private static void CopyBrush(Brush source, Brush target)
    {
        switch (source)
        {
            case SolidColorBrush s when target is SolidColorBrush t:
                t.Color = s.Color;
                break;
            case LinearGradientBrush s when target is LinearGradientBrush t && s.GradientStops.Count == t.GradientStops.Count:
                for (var i = 0; i < s.GradientStops.Count; i++) t.GradientStops[i].Color = s.GradientStops[i].Color;
                break;
        }
    }
}
