using Microsoft.UI.Xaml;

namespace Coworker.Settings;

/// Merges Settings/SettingsStyles.xaml into the application resources the first time a Settings
/// control is created, so the Settings page and the shared model picker resolve its brushes and
/// styles wherever they are hosted. Every key is prefixed "Settings"; nothing existing is overridden.
public static class SettingsResources
{
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("ms-appx:///Settings/SettingsStyles.xaml") });
    }
}
