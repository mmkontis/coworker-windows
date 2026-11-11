using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Coworker.Controls;

/// The "Auto Enter for the active app" card (see AutoEnterCard.xaml). The switch adds or removes the
/// app's process name in AppSettings.AutoSubmitApps, and turning it on enables Auto Enter overall (Mac).
/// Per-site control in browsers is not offered on Windows yet.
public sealed partial class AutoEnterCard : UserControl
{
    public const double CardWidth = 340;
    public const double CardHeight = 52;

    private ForegroundApp? _app;
    private bool _isLoading;

    public AutoEnterCard()
    {
        InitializeComponent();
    }

    public async void SetTarget(ForegroundApp app)
    {
        _app = app;
        AppName.Text = app.DisplayName;
        ToolTipService.SetToolTip(AppName, app.DisplayName);

        var settings = AppSettings.Instance;
        _isLoading = true;
        Toggle.IsOn = settings.AutoSubmitEnabled && IsListed(settings, app);
        _isLoading = false;

        AppIcon.Source = null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(app.ExecutablePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 24, ThumbnailOptions.UseCurrentScale);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (_app == app) AppIcon.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // No icon: the name alone still identifies the app.
        }
    }

    private static bool IsListed(AppSettings settings, ForegroundApp app) =>
        settings.AutoSubmitApps.Any(a => string.Equals(a, app.ProcessName, StringComparison.OrdinalIgnoreCase));

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _app == null) return;

        var settings = AppSettings.Instance;
        settings.AutoSubmitApps.RemoveAll(a => string.Equals(a, _app.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (Toggle.IsOn)
        {
            settings.AutoSubmitApps.Add(_app.ProcessName);
            settings.AutoSubmitEnabled = true;
        }
        settings.Save();
    }
}
