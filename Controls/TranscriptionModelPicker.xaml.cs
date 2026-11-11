using Coworker.Helpers;
using Coworker.Models;
using Coworker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Coworker.Controls;

/// <summary>
/// The transcription model control of Settings: a menu of every model and a four-stop capsule
/// slider (Groq, GPT, Qwen, MAI). A pick is saved as the user's choice, so a server default no
/// longer replaces it (Mac: TranscriptionModelPicker in Views/TranscriptionModelPicker.swift).
/// </summary>
public sealed partial class TranscriptionModelPicker : UserControl
{
    private const double Inset = 22;
    private const double TrackHeight = 44;

    private readonly TranscriptionModelCatalog _catalog = TranscriptionModelCatalog.Shared;
    private readonly List<Button> _stopButtons = new();
    private readonly Ellipse[] _dots;
    private bool _dragging;
    private int _shownIndex = -1;

    public TranscriptionModelPicker()
    {
        SettingsResources.EnsureLoaded();
        InitializeComponent();

        _dots = new[] { Dot0, Dot1, Dot2, Dot3 };
        Slider.Count = TranscriptionModelCatalog.Stops.Count;
        Slider.IndexRequested = index => ChooseStop(index);
        BuildStopLabels();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// Shows the "Add OpenRouter model…" entry of the menu while set. It runs the add dialog and
    /// returns the new model's id (or null when cancelled), which is then selected. Assigned by
    /// the OpenRouter section.
    public static Func<Task<string?>>? AddOpenRouterModel { get; set; }

    private static string Selection => AppSettings.Instance.SelectedModel;

    private static int StopIndex(string code)
    {
        for (var i = 0; i < TranscriptionModelCatalog.Stops.Count; i++)
        {
            if (TranscriptionModelCatalog.Stops[i].Code == code) return i;
        }
        return -1;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppSettings.Changed += OnSettingsChanged;
        _catalog.Changed += Refresh;
        _catalog.EnsureRefreshed();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppSettings.Changed -= OnSettingsChanged;
        _catalog.Changed -= Refresh;
    }

    private void OnSettingsChanged() => UiThread.Post(Refresh);

    // MARK: - Menu

    private void OnMenuOpening(object sender, object e)
    {
        ModelMenu.Items.Clear();
        foreach (var model in _catalog.Choices())
        {
            var code = model.Code;
            var item = new ToggleMenuFlyoutItem
            {
                Text = TranscriptionModelCatalog.DisplayName(model),
                IsChecked = code == Selection,
            };
            ToolTipService.SetToolTip(item, model.Description);
            item.Click += (_, _) => Choose(code);
            ModelMenu.Items.Add(item);
        }

        if (AddOpenRouterModel is { } add)
        {
            ModelMenu.Items.Add(new MenuFlyoutSeparator());
            var addItem = new MenuFlyoutItem { Text = "Add OpenRouter model…" };
            addItem.Click += async (_, _) =>
            {
                if (await add() is { } id) Choose(id);
            };
            ModelMenu.Items.Add(addItem);
        }
    }

    // MARK: - Slider

    private void ChooseStop(int index)
    {
        index = Math.Clamp(index, 0, TranscriptionModelCatalog.Stops.Count - 1);
        Choose(TranscriptionModelCatalog.Stops[index].Code);
    }

    private void Choose(string code)
    {
        TranscriptionModelCatalog.Select(code);
        Refresh();
    }

    private double StopX(int index) =>
        Inset + Travel * index / (TranscriptionModelCatalog.Stops.Count - 1);

    private double Travel => Math.Max(1, Slider.ActualWidth - Inset * 2);

    private void OnSliderSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    private void Layout()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            Canvas.SetLeft(_dots[i], StopX(i) - 3);
            Canvas.SetTop(_dots[i], TrackHeight / 2 - 3);
        }

        var index = StopIndex(Selection);
        if (index >= 0)
        {
            FillCapsule.Width = StopX(index) + Inset;
            Canvas.SetLeft(Knob, StopX(index) - 17);
            Canvas.SetTop(Knob, TrackHeight / 2 - 17);
            Canvas.SetLeft(KnobShadow, StopX(index) - 18);
            Canvas.SetTop(KnobShadow, TrackHeight / 2 - 18 + 1);
        }
        PositionStopLabels();
    }

    private void Refresh()
    {
        var selection = Selection;
        var model = _catalog.Choices().FirstOrDefault(m => m.Code == selection);
        SelectedNameText.Text = model != null ? TranscriptionModelCatalog.DisplayName(model) : selection;

        var index = StopIndex(selection);
        var visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        FillCapsule.Visibility = visibility;
        Knob.Visibility = visibility;
        KnobShadow.Visibility = visibility;
        Slider.NotifyIndexChanged(_shownIndex, index);
        Slider.Index = index;
        _shownIndex = index;
        AutomationProperties.SetHelpText(Slider, index >= 0 ? TranscriptionModelCatalog.Stops[index].Label : "Custom selection");

        for (var i = 0; i < _stopButtons.Count; i++)
        {
            var selected = i == index;
            var text = (TextBlock)_stopButtons[i].Content;
            text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            text.Foreground = (Brush)Application.Current.Resources[selected ? "CwAccentBrush" : "TextFillColorSecondaryBrush"];
            AutomationProperties.SetItemStatus(_stopButtons[i], selected ? "Selected" : "");
        }

        Layout();
    }

    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = Slider.CapturePointer(e.Pointer);
        Slider.Focus(FocusState.Pointer);
        ChooseNearest(e);
    }

    private void OnSliderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) ChooseNearest(e);
    }

    private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Slider.ReleasePointerCaptures();
    }

    private void ChooseNearest(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(Slider).Position.X;
        var last = TranscriptionModelCatalog.Stops.Count - 1;
        var index = (int)Math.Round((x - Inset) / Travel * last, MidpointRounding.AwayFromZero);
        var code = TranscriptionModelCatalog.Stops[Math.Clamp(index, 0, last)].Code;
        if (code != Selection) Choose(code);
    }

    private void OnSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var last = TranscriptionModelCatalog.Stops.Count - 1;
        var current = Math.Max(StopIndex(Selection), 0);
        switch (e.Key)
        {
            case VirtualKey.Right or VirtualKey.Up:
                ChooseStop(Math.Min(last, current + 1));
                break;
            case VirtualKey.Left or VirtualKey.Down:
                ChooseStop(Math.Max(0, current - 1));
                break;
            case VirtualKey.Home:
                ChooseStop(0);
                break;
            case VirtualKey.End:
                ChooseStop(last);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // MARK: - Stop labels

    private void BuildStopLabels()
    {
        var plain = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var (label, code) in TranscriptionModelCatalog.Stops)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12 },
                Background = plain,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 0),
                MinWidth = 0,
                MinHeight = 0,
                Height = 18,
            };
            foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
                button.Resources[key] = plain;
            AutomationProperties.SetName(button, $"Select {label}");
            var stopCode = code;
            button.Click += (_, _) => Choose(stopCode);
            button.SizeChanged += (_, _) => PositionStopLabels();
            _stopButtons.Add(button);
            LabelCanvas.Children.Add(button);
        }
    }

    private void PositionStopLabels()
    {
        for (var i = 0; i < _stopButtons.Count; i++)
            Canvas.SetLeft(_stopButtons[i], StopX(i) - _stopButtons[i].ActualWidth / 2);
    }
}
