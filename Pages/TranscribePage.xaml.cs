using Coworker.Dictation;
using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Coworker.Pages;

/// The Record tab (see TranscribePage.xaml): start and stop a dictation from the app and read the
/// latest result. It drives the same <see cref="DictationController"/> as the hotkey.
public sealed partial class TranscribePage : Page
{
    private readonly IconElement _wave = SidebarIcons.Waveform();

    public TranscribePage()
    {
        InitializeComponent();
        // The 16 px sound-wave drawing, scaled up to the hero size.
        WaveIcon.Child = new Viewbox { Width = 32, Height = 32, Child = _wave };
        _wave.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CwAccentBrush"];

        Loaded += (_, _) =>
        {
            DictationController.Shared.StateChanged += OnStateChanged;
            TranscribeState.Changed += OnStateChangedUi;
            Update();
        };
        Unloaded += (_, _) =>
        {
            DictationController.Shared.StateChanged -= OnStateChanged;
            TranscribeState.Changed -= OnStateChangedUi;
        };
    }

    private void OnStateChanged(DictationState state) => UiThread.Post(Update);

    private void OnStateChangedUi() => UiThread.Post(Update);

    private void Update()
    {
        var state = DictationController.Shared.State;
        var recording = state is DictationState.Starting or DictationState.Recording;
        var transcribing = state == DictationState.Transcribing;

        MicIcon.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        WaveIcon.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Headline.Text = recording ? "Listening" : "A little less typing.";
        SubText.Text = recording ? "Speak naturally. Stop when you're ready." : "Speak your thoughts. Coworker takes it from here.";

        RecordIcon.Glyph = recording ? "" : "";
        RecordLabel.Text = recording ? "Stop recording" : "Start recording";
        RecordButton.IsEnabled = !transcribing;
        TranscribingRow.Visibility = transcribing ? Visibility.Visible : Visibility.Collapsed;

        var status = TranscribeState.StatusMessage;
        StatusText.Text = status;
        StatusText.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var latest = TranscribeState.LatestText;
        LatestText.Text = latest;
        LatestCard.Visibility = latest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        var dictation = DictationController.Shared;
        if (dictation.State == DictationState.Recording) _ = dictation.StopAsync();
        else dictation.Start();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(TranscribeState.LatestText);
        Clipboard.SetContent(package);
    }

    private void OnRecordingsClick(object sender, RoutedEventArgs e) => NavigationService.Shared.Navigate(AppTab.History);

    private void OnLinkClick(object sender, RoutedEventArgs e) =>
        NavigationService.Shared.Navigate(Enum.Parse<AppTab>((string)((Button)sender).Tag));
}
