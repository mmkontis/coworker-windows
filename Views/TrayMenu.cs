using System.Runtime.InteropServices;
using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;

namespace Coworker.Views;

/// <summary>
/// The tray icon's right-click menu (Mac StatusMenu): the header, model, language and translation
/// choices, the app actions and the version. A native Windows menu, so it looks and behaves like
/// every other tray menu and works while the main window is hidden.
/// </summary>
internal static class TrayMenu
{
    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_DISABLED = 0x0002;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_POPUP = 0x0010;
    private const uint MF_SEPARATOR = 0x0800;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint WM_NULL = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // Undocumented uxtheme entry points that let menus follow the system dark mode.
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private const int PreferredAppModeAllowDark = 1;

    private static bool _darkModeEnabled;

    /// Shows the menu at a screen point and runs the chosen item. Blocks until the menu closes.
    public static void Show(int x, int y, IntPtr owner, IReadOnlyList<TranscriptionModel>? models)
    {
        EnableDarkMode();

        var actions = new Dictionary<uint, Action>();
        uint nextId = 1;
        var menu = CreatePopupMenu();

        void Item(IntPtr parent, string text, Action? action, bool isChecked = false, bool disabled = false)
        {
            var flags = MF_STRING | (isChecked ? MF_CHECKED : 0) | (disabled ? MF_GRAYED | MF_DISABLED : 0);
            var id = nextId++;
            if (action != null) actions[id] = action;
            AppendMenu(parent, flags, (UIntPtr)id, text);
        }

        void Separator(IntPtr parent) => AppendMenu(parent, MF_SEPARATOR, UIntPtr.Zero, null);

        var subMenus = new List<IntPtr>();
        IntPtr SubMenu(IntPtr parent, string text)
        {
            var sub = CreatePopupMenu();
            subMenus.Add(sub);
            AppendMenu(parent, MF_STRING | MF_POPUP, (UIntPtr)sub, text);
            return sub;
        }

        var settings = AppSettings.Instance;
        var signedIn = SessionService.Shared.IsAuthenticated;
        var recording = DictationController.Shared.State == DictationState.Recording;

        Item(menu, $"Coworker\t{(recording ? "Recording" : "Ready")}", null, disabled: true);
        Separator(menu);

        if (signedIn)
        {
            var choices = TrayOptions.ModelChoices(models);
            var modelName = choices.FirstOrDefault(c => c.Code == settings.SelectedModel).Name ?? settings.SelectedModel;
            var modelMenu = SubMenu(menu, $"Model\t{modelName}");
            foreach (var (code, name) in choices)
                Item(modelMenu, name, () => TrayOptions.SelectModel(code), isChecked: code == settings.SelectedModel);

            var languageMenu = SubMenu(menu, $"Language\t{TrayOptions.LanguageName(settings.SelectedLanguage)}");
            foreach (var (code, name) in TrayOptions.DisplayLanguages)
            {
                var selected = code;
                Item(languageMenu, name, () =>
                {
                    settings.SelectedLanguage = selected;
                    settings.Save();
                }, isChecked: code == settings.SelectedLanguage);
            }

            var translation = settings.TranslationEnabled ? TrayOptions.TranslationName(settings.SelectedTranslationLanguage) : "Off";
            var translationMenu = SubMenu(menu, $"Translation\t{translation}");
            Item(translationMenu, "Off", () =>
            {
                settings.TranslationEnabled = false;
                settings.Save();
            }, isChecked: !settings.TranslationEnabled);
            Separator(translationMenu);
            foreach (var (code, name) in TrayOptions.TranslationLanguages)
            {
                var selected = code;
                Item(translationMenu, name, () =>
                {
                    settings.SelectedTranslationLanguage = selected;
                    settings.TranslationEnabled = true;
                    settings.Save();
                }, isChecked: settings.TranslationEnabled && code == settings.SelectedTranslationLanguage);
            }
            Separator(menu);

            Item(menu, "Open App", () => WindowService.Shared.Show());
            Item(menu, "Coworker Panel…", () => TrayService.Shared.ShowPanel());
            Separator(menu);
        }
        else
        {
            Item(menu, "Sign In…", () => WindowService.Shared.Show());
            Separator(menu);
        }

        Item(menu, "Check for Updates…", () => _ = UpdateService.Shared.CheckForUpdateAsync());
        Item(menu, "Restart", App.Restart);
        Item(menu, "Quit Coworker", App.Quit);
        Item(menu, $"Version {ApiConstants.AppVersion}", null, disabled: true);

        // The owner must be the foreground window or the menu will not close when the user clicks away.
        NativeMethods.SetForegroundWindow(owner);
        var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD, x, y, owner, IntPtr.Zero);
        PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        DestroyMenu(menu);
        if (chosen != 0 && actions.TryGetValue(chosen, out var action)) action();
    }

    private static void EnableDarkMode()
    {
        if (_darkModeEnabled) return;
        _darkModeEnabled = true;

        try
        {
            SetPreferredAppMode(PreferredAppModeAllowDark);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
            // Windows without the entry points: menus stay light.
        }
    }
}
