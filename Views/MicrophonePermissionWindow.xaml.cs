using Coworker.Helpers;
using Coworker.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Coworker.Views;

/// The "Microphone Permission Required" window (see MicrophonePermissionWindow.xaml): a small
/// fixed-size window above other windows, centred on the screen, that works while the main window is hidden.
public sealed partial class MicrophonePermissionWindow : Window
{
    private const int ClientWidth = 420;
    private const int ClientHeight = 320;

    public MicrophonePermissionWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "coworker.ico"));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var display = DisplayInfo.FromWindow(hwnd);
        appWindow.ResizeClient(new SizeInt32((int)(ClientWidth * display.Scale), (int)(ClientHeight * display.Scale)));
        var size = appWindow.Size;
        var work = display.WorkArea;
        appWindow.Move(new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2));
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        MicrophonePermission.OpenSettings();
        Close();
    }
}
