using Coworker.Helpers;
using Coworker.Services.Updates;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Coworker.Views;

public enum UpdateProgressMode
{
    None,
    Indeterminate,
    Determinate,
}

/// <param name="IsDefault">Enter presses it; drawn as the accent button.</param>
/// <param name="IsLeading">Sits on the left edge, apart from the others ("Skip This Version").</param>
public sealed record UpdateButton(string Text, bool IsDefault = false, bool IsLeading = false);

/// <summary>What the update window shows right now (one Sparkle-style step).</summary>
public sealed record UpdateView(
    string Title,
    string? Message = null,
    IReadOnlyList<NoteBlock>? ReleaseNotes = null,
    UpdateProgressMode Progress = UpdateProgressMode.None,
    string? Detail = null,
    IReadOnlyList<UpdateButton>? Buttons = null,
    bool Closable = true);

/// <summary>
/// The window behind "Check for Updates…" and the "Update Required" alert: the Windows counterpart of
/// Sparkle's standard update UI (SUUpdateAlert, status window) and the NSAlert in UpdaterDelegate.
/// It only shows what <see cref="Services.UpdateService"/> tells it; UI thread only.
/// </summary>
public sealed class UpdateWindow : Window
{
    private const double ContentWidth = 560;
    private const double IconSize = 64;
    private const double NotesHeight = 220;

    private readonly Grid _root = new() { Padding = new Thickness(24, 24, 24, 20), RowSpacing = 20 };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly Border _notesFrame = new();
    private readonly RichTextBlock _notes = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new();
    private readonly TextBlock _detail = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _buttonRow = new() { ColumnSpacing = 8 };
    private readonly StackPanel _trailing = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _leading = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    private const int DwmUseImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE

    private TaskCompletionSource<int>? _pending;
    private Button? _defaultButton;
    private int _defaultIndex = -1;
    private bool _closable = true;

    public UpdateWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        Title = "Software Update";   // Sparkle: "Software Update"
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "coworker.ico"));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new Image
        {
            Width = IconSize,
            Height = IconSize,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0),
            Source = new BitmapImage(new Uri("ms-appx:///Assets/coworker-icon.png")),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetColumn(icon, 0);
        _root.Children.Add(icon);

        _notes.Padding = new Thickness(12);
        _notesFrame.Height = NotesHeight;
        _notesFrame.CornerRadius = new CornerRadius(8);
        _notesFrame.BorderThickness = new Thickness(1);
        _notesFrame.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        _notesFrame.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        _notesFrame.Child = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_title);
        body.Children.Add(_message);
        body.Children.Add(_notesFrame);
        body.Children.Add(_progress);
        body.Children.Add(_detail);
        Grid.SetColumn(body, 1);
        _root.Children.Add(body);

        _buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_trailing, 1);
        _buttonRow.Children.Add(_leading);
        _buttonRow.Children.Add(_trailing);
        Grid.SetRow(_buttonRow, 1);
        Grid.SetColumnSpan(_buttonRow, 2);
        _root.Children.Add(_buttonRow);

        var enter = new KeyboardAccelerator { Key = VirtualKey.Enter };
        enter.Invoked += (_, e) => { if (_defaultIndex >= 0) { e.Handled = true; Resolve(_defaultIndex); } };
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { if (_closable) { e.Handled = true; Resolve(-1); } };
        _root.KeyboardAccelerators.Add(enter);
        _root.KeyboardAccelerators.Add(escape);

        Content = _root;

        // The system title bar does not follow the app theme by itself; without this it stays light on a dark window.
        _root.Loaded += (_, _) => ApplyCaptionTheme();
        _root.ActualThemeChanged += (_, _) => ApplyCaptionTheme();

        _appWindow.Closing += (_, e) =>
        {
            if (!_closable) e.Cancel = true;
        };
        Closed += (_, _) => Resolve(-1);
    }

    /// Shows one step and waits for a button: returns its index in <see cref="UpdateView.Buttons"/>, or -1 when the
    /// user closed the window (or a newer step replaced this one). The window is created hidden; <paramref name="raise"/>
    /// brings it to the front (false for the short-lived "Checking for updates…" step).
    public Task<int> PresentAsync(UpdateView view, bool raise = true)
    {
        Resolve(-1);   // a newer step replaces whatever was waiting
        Apply(view);
        _pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (raise) Raise();
        return _pending.Task;
    }

    /// Shows a step with no buttons to wait for (checking, downloading, installing).
    public void Present(UpdateView view, bool raise = true)
    {
        Resolve(-1);
        Apply(view);
        if (raise) Raise();
    }

    /// Updates the bar and status line of the current step (download progress).
    public void SetProgress(double fraction, string? detail)
    {
        _progress.IsIndeterminate = false;
        _progress.Value = Math.Clamp(fraction, 0, 1) * 100;
        _detail.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Brings the window to the front (it may open while another app has focus).
    public void Raise()
    {
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        // Return/Space act on the default choice (Sparkle focuses "Install Update"), not the first button.
        _root.DispatcherQueue.TryEnqueue(() => _defaultButton?.Focus(FocusState.Programmatic));
    }

    private void ApplyCaptionTheme()
    {
        var dark = _root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    public void Keep(bool onTop)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = onTop;
    }

    private void Resolve(int result)
    {
        var pending = _pending;
        _pending = null;
        _defaultIndex = -1;
        pending?.TrySetResult(result);
    }

    private void Apply(UpdateView view)
    {
        _title.Text = view.Title;
        _message.Text = view.Message ?? "";
        _message.Visibility = string.IsNullOrEmpty(view.Message) ? Visibility.Collapsed : Visibility.Visible;

        _notesFrame.Visibility = view.ReleaseNotes is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (view.ReleaseNotes is { Count: > 0 }) RenderNotes(view.ReleaseNotes);

        _progress.Visibility = view.Progress == UpdateProgressMode.None ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = view.Progress == UpdateProgressMode.Indeterminate;
        _progress.Value = 0;
        _detail.Text = view.Detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(view.Detail) ? Visibility.Collapsed : Visibility.Visible;

        SetClosable(view.Closable);
        BuildButtons(view.Buttons ?? Array.Empty<UpdateButton>());
        _buttonRow.Visibility = view.Buttons is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _root.RowSpacing = _buttonRow.Visibility == Visibility.Visible ? 20 : 0;

        FitToContent();
    }

    private void BuildButtons(IReadOnlyList<UpdateButton> buttons)
    {
        _leading.Children.Clear();
        _trailing.Children.Clear();
        _defaultIndex = -1;
        _defaultButton = null;

        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var spec = buttons[i];
            var button = new Button { Content = spec.Text, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
            if (spec.IsDefault)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                _defaultIndex = index;
                _defaultButton = button;
            }
            button.Click += (_, _) => Resolve(index);
            (spec.IsLeading ? _leading : _trailing).Children.Add(button);
        }
    }

    private void RenderNotes(IReadOnlyList<NoteBlock> blocks)
    {
        _notes.Blocks.Clear();
        foreach (var block in blocks)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            switch (block.Kind)
            {
                case NoteBlockKind.Heading:
                    paragraph.FontSize = block.Level <= 2 ? 16 : 14;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, 4, 0, 6);
                    break;
                case NoteBlockKind.ListItem:
                    paragraph.Margin = new Thickness(block.Level * 18, 0, 0, 4);
                    paragraph.Inlines.Add(new Run { Text = (block.Marker ?? "•") + "  " });
                    break;
            }

            foreach (var run in block.Runs)
            {
                var text = new Run
                {
                    Text = run.Text,
                    FontWeight = run.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = run.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                };
                if (run.Href != null && Uri.TryCreate(run.Href, UriKind.Absolute, out var uri))
                {
                    var link = new Hyperlink { NavigateUri = uri };
                    link.Inlines.Add(text);
                    paragraph.Inlines.Add(link);
                }
                else
                {
                    paragraph.Inlines.Add(text);
                }
            }
            _notes.Blocks.Add(paragraph);
        }
    }

    private void SetClosable(bool closable)
    {
        _closable = closable;
        Keep(onTop: !closable);

        // The "Update Required" alert has no close button (like the Mac NSAlert): drop the system menu.
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        var wanted = closable ? style | NativeMethods.WS_SYSMENU : style & ~NativeMethods.WS_SYSMENU;
        if (wanted != style)
        {
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, wanted);
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | 0x0004 /* SWP_NOZORDER */);
        }
    }

    /// Sizes the client area to the content (width fixed, height measured) and keeps the window centered.
    private void FitToContent()
    {
        var scale = DisplayInfo.FromWindow(_hwnd).Scale;
        _root.Measure(new Windows.Foundation.Size(ContentWidth, double.PositiveInfinity));
        var height = Math.Max(_root.DesiredSize.Height, 150);
        _appWindow.ResizeClient(new SizeInt32((int)Math.Round(ContentWidth * scale), (int)Math.Round(height * scale)));

        var work = DisplayInfo.FromCursor().WorkArea;
        var size = _appWindow.Size;
        _appWindow.Move(new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2));
    }
}
