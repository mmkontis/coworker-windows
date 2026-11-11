using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Coworker.Services.VoiceCommands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Coworker.Models;

/// One row of the Shortcuts list.
public sealed class CommandRowViewModel
{
    public CommandRowViewModel(VoiceCommand command, IReadOnlyList<ActionCatalogEntry> catalog)
    {
        Command = command;

        var color = CommandIcons.ParseColor(command.IconColor);
        Glyph = CommandIcons.Glyph(command.IconName);
        IconBrush = new SolidColorBrush(color);
        IconTileBrush = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.12));

        var steps = command.AllActions;
        ActionsSummary = string.Join(" → ", steps.Select(step => ActionName(step.ActionType, catalog)));
        IsMultiAction = steps.Count > 1;
        StepsLabel = $"{steps.Count} steps";

        // The Mac labels an "exact" command "Contains". The editor cannot create one, so it only shows up
        // for commands made elsewhere, and naming it correctly costs nothing.
        ModeLabel = command.MatchMode switch
        {
            "starts_with" => "Starts with",
            "exact" => "Exact",
            _ => "Contains",
        };
    }

    public VoiceCommand Command { get; }

    /// The raw stored phrase, alternatives separated by pipes.
    public string Title => Command.TriggerPhrase;
    public string Glyph { get; }
    public Brush IconBrush { get; }
    public Brush IconTileBrush { get; }
    public string ModeLabel { get; }
    public string ActionsSummary { get; }
    public bool IsMultiAction { get; }
    public string StepsLabel { get; }
    public bool IsEnabled => Command.IsEnabled;
    public bool IsDisabled => !Command.IsEnabled;

    public string EditLabel => $"Edit {Command.TriggerPhrase}";
    public string DeleteLabel => $"Delete {Command.TriggerPhrase}";

    /// The catalog name of an action; without a catalog entry the id with spaces and capitals.
    public static string ActionName(string actionType, IReadOnlyList<ActionCatalogEntry> catalog) =>
        catalog.FirstOrDefault(entry => entry.Id == actionType)?.Name
        ?? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(actionType.Replace('_', ' ').ToLower(CultureInfo.CurrentCulture));
}

/// One step in the command form. Wraps the step so config keys the editor does not own survive a save.
public sealed class ActionStepViewModel : ObservableObject
{
    private IReadOnlyList<ActionCatalogEntry> _catalog;
    private int _number = 1;
    private bool _isMulti;
    private bool _isLast = true;

    public ActionStepViewModel(VoiceCommandAction action, IReadOnlyList<ActionCatalogEntry> catalog)
    {
        Step = action;
        _catalog = catalog;
    }

    public VoiceCommandAction Step { get; }

    /// Raised after the user picked another action.
    public event Action<ActionStepViewModel>? ActionTypeChanged;

    public string ActionType
    {
        get => Step.ActionType;
        set
        {
            if (Step.ActionType == value) return;
            Step.ActionType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpenApp));
            OnPropertyChanged(nameof(IsOpenWebPage));
            ActionTypeChanged?.Invoke(this);
        }
    }

    public string Url
    {
        get => Step.GetConfig("url") ?? "";
        set => SetConfig("url", value);
    }

    public string BundleIdentifier
    {
        get => Step.GetConfig("bundle_identifier") ?? "";
        set => SetConfig("bundle_identifier", value);
    }

    public string AppName
    {
        get => Step.GetConfig("app_name") ?? "";
        set => SetConfig("app_name", value);
    }

    public string AppPath
    {
        get => Step.GetConfig("app_path") ?? "";
        set => SetConfig("app_path", value);
    }

    public IReadOnlyList<ActionCatalogEntry> Catalog
    {
        get => _catalog;
        set => SetProperty(ref _catalog, value);
    }

    public bool IsOpenApp => ActionType == "open_app";
    public bool IsOpenWebPage => ActionType == "open_web_page";

    /// 1-based position, shown in a badge when the command has several steps.
    public int Number
    {
        get => _number;
        private set => SetProperty(ref _number, value);
    }

    public bool IsMulti
    {
        get => _isMulti;
        private set => SetProperty(ref _isMulti, value);
    }

    /// The "then" line under every step but the last, in a multi-step command.
    public bool ShowConnector => IsMulti && !_isLast;

    /// The config inputs line up under the picker, past the number badge.
    public Thickness DetailPadding => new(IsMulti ? 30 : 0, 0, 0, 0);

    public void SetPosition(int number, int count)
    {
        Number = number;
        _isLast = number == count;
        IsMulti = count > 1;
        OnPropertyChanged(nameof(ShowConnector));
        OnPropertyChanged(nameof(DetailPadding));
    }

    private void SetConfig(string key, string value, [CallerMemberName] string property = "")
    {
        if ((Step.GetConfig(key) ?? "") == value) return;
        Step.SetConfig(key, value);
        OnPropertyChanged(property);
    }
}

/// One action in the picker's list.
public sealed class ActionOptionViewModel
{
    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(0xFF, 0x24, 0x73, 0xF2);

    public ActionOptionViewModel(ActionCatalogEntry entry, bool isSelected, bool isFirst)
    {
        Entry = entry;

        var color = CommandIcons.TryParseColor(entry.IconColor) ?? Accent;
        Glyph = CommandIcons.Glyph(entry.IconName);
        IconBrush = new SolidColorBrush(color);
        IconTileBrush = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.12));
        RowBrush = new SolidColorBrush(isSelected ? CommandIcons.WithOpacity(Accent, 0.08) : Windows.UI.Color.FromArgb(0, 0, 0, 0));
        CheckVisibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        DividerVisibility = isFirst ? Visibility.Collapsed : Visibility.Visible;
    }

    public ActionCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public bool HasDescription => Entry.Description.Length > 0;
    public string Glyph { get; }
    public Brush IconBrush { get; }
    public Brush IconTileBrush { get; }
    public Brush RowBrush { get; }
    public Visibility CheckVisibility { get; }
    public Visibility DividerVisibility { get; }
}

/// One installed app in the app picker. The icon is loaded from the shell the first time a row asks
/// for it, so a long list only pays for the rows that are scrolled into view.
public sealed class AppOptionViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public AppOptionViewModel(InstalledApp app, bool isSelected, bool isFirst)
    {
        App = app;
        RowBrush = new SolidColorBrush(isSelected
            ? Windows.UI.Color.FromArgb(0x14, 0x24, 0x73, 0xF2)
            : Windows.UI.Color.FromArgb(0, 0, 0, 0));
        CheckVisibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        DividerVisibility = isFirst ? Visibility.Collapsed : Visibility.Visible;
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Detail => App.Path.Length > 0 ? App.Path : App.Id;
    public Brush RowBrush { get; }
    public Visibility CheckVisibility { get; }
    public Visibility DividerVisibility { get; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    private async Task LoadIconAsync()
    {
        var icon = await ShellApps.LoadIconAsync(App.ShellPath, 48);
        if (icon == null) return;
        _icon = icon.ToImageSource();
        OnPropertyChanged(nameof(Icon));
    }
}

internal static class AppIconExtensions
{
    /// Call on the UI thread.
    public static ImageSource ToImageSource(this AppIcon icon)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(icon.Width, icon.Height);
        using (var pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(icon.Bgra);
        bitmap.Invalidate();
        return bitmap;
    }
}
