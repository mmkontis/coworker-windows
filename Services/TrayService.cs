using System.Runtime.InteropServices;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Views;
using Windows.Graphics;

namespace Coworker.Services;

/// <summary>
/// The notification-area icon (Mac: the status item). Left click opens the quick-settings panel,
/// right click the menu; both work while the main window is hidden. The icon comes back after Explorer
/// restarts.
/// </summary>
public sealed class TrayService : IDisposable
{
    public static TrayService Shared { get; } = new();

    private const uint WM_APP_TRAYICON = 0x8001;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_SELECT = 0x0400;
    private const uint NIN_KEYSELECT = 0x0401;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIM_ADD = 0x00;
    private const uint NIM_DELETE = 0x02;
    private const uint NIM_SETVERSION = 0x04;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out NativeMethods.RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private NOTIFYICONDATA _data;
    private bool _created;
    private TrayPanelWindow? _panel;
    private IReadOnlyList<TranscriptionModel>? _models;

    private TrayService() { }

    /// Adds the icon and starts listening to it. Call once, after the main window exists.
    public void Initialize()
    {
        ForegroundTracker.Start();
        _ = LoadModelsAsync();

        var hwnd = WindowService.Shared.Hwnd;
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi > 0 ? dpi : 96);
        var icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"), IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (icon == IntPtr.Zero)
        {
            DebugLog.Write("Tray icon could not be loaded");
            return;
        }

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE,
            uCallbackMessage = WM_APP_TRAYICON,
            hIcon = icon,
            szTip = "Coworker",
            szInfo = "",
            szInfoTitle = "",
            uVersion = NOTIFYICON_VERSION_4,
        };
        AddIcon();

        WindowService.Shared.AddMessageHook(WM_APP_TRAYICON, OnTrayMessage);

        // Explorer restarted: its notification area is empty again.
        var taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        WindowService.Shared.AddMessageHook(taskbarCreated, (IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        {
            AddIcon();
            result = IntPtr.Zero;
            return true;
        });

        SessionService.Shared.SessionChanged += change =>
        {
            if (change == SessionChange.SignedIn) _ = LoadModelsAsync();
        };
    }

    public void Dispose()
    {
        ForegroundTracker.Stop();
        if (!_created) return;

        Shell_NotifyIcon(NIM_DELETE, ref _data);
        DestroyIcon(_data.hIcon);
        _created = false;
    }

    /// Opens the quick-settings panel beside the icon (also the menu's "Coworker Panel…").
    public void ShowPanel()
    {
        _panel ??= new TrayPanelWindow();
        _panel.ShowNear(IconRect() ?? CursorRect());
    }

    private void AddIcon()
    {
        _created = Shell_NotifyIcon(NIM_ADD, ref _data);
        if (_created) Shell_NotifyIcon(NIM_SETVERSION, ref _data);
    }

    private bool OnTrayMessage(IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        // Version 4: the event is in the low word of lParam and the anchor point in wParam.
        var eventId = (uint)(lParam.ToInt64() & 0xFFFF);
        var x = (short)(wParam.ToInt64() & 0xFFFF);
        var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

        switch (eventId)
        {
            case NIN_SELECT or NIN_KEYSELECT:
                UiThread.Post(TogglePanel);
                break;
            case WM_CONTEXTMENU:
                UiThread.Post(() => ShowMenu(x, y));
                break;
        }
        return true;
    }

    private void TogglePanel()
    {
        if (_panel?.IsOpen == true)
        {
            _panel.Dismiss();
            return;
        }

        // The click that closed the panel by taking focus away must not open it again.
        if (_panel?.WasJustDismissed == true) return;
        ShowPanel();
    }

    private void ShowMenu(int x, int y)
    {
        _panel?.Dismiss();
        TrayMenu.Show(x, y, WindowService.Shared.Hwnd, _models);
    }

    private async Task LoadModelsAsync()
    {
        var all = await ApiService.Shared.GetAllModels();
        _models = all.Transcription;
    }

    private RectInt32? IconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _data.hWnd, uID = _data.uID };
        if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return null;
        return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static RectInt32 CursorRect()
    {
        NativeMethods.GetCursorPos(out var point);
        return new RectInt32(point.X, point.Y, 0, 0);
    }
}
