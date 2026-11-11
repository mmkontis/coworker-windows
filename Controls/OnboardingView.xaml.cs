using Coworker.Helpers;
using Coworker.Models;
using Coworker.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;

namespace Coworker.Controls;

/// The first-run setup (see OnboardingView.xaml). The steps are a list, so a step (the Mac's Contacts
/// one, say) can be added by adding its panel and an entry here.
public sealed partial class OnboardingView : UserControl
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(175);
    private const int CountdownSeconds = 3;

    private readonly List<StackPanel> _steps;
    private readonly List<Ellipse> _dots = new();
    private DispatcherQueueTimer? _timer;
    private int _index = -1;
    private int _countdown;
    private bool _microphoneAttempted;
    private bool _isLoadingSettings;

    public OnboardingView()
    {
        InitializeComponent();
        _steps = new List<StackPanel> { WelcomeStep, MicrophoneStep, PasteStep, DoneStep };

        foreach (var step in _steps) step.OpacityTransition = new ScalarTransition { Duration = FadeDuration };
        foreach (var _ in _steps)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                CenterPoint = new Vector3(4, 4, 0),
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) },
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(300) },
            };
            _dots.Add(dot);
            Dots.Children.Add(dot);
        }

        Unloaded += (_, _) => _timer?.Stop();
    }

    /// Raised when the last step's countdown ends.
    public event Action? Completed;

    /// Starts over at the welcome step (first run, or Settings > Reset Onboarding).
    public void Restart()
    {
        _microphoneAttempted = false;
        _isLoadingSettings = true;
        AutoPasteToggle.IsOn = AppSettings.Instance.AutoPaste;
        _isLoadingSettings = false;
        _ = ShowStepAsync(0, animate: false);
    }

    private void OnNext(object sender, RoutedEventArgs e) => _ = ShowStepAsync(_index + 1, animate: true);

    private async Task ShowStepAsync(int index, bool animate)
    {
        if (index < 0 || index >= _steps.Count) return;

        _timer?.Stop();
        if (_index >= 0 && animate)
        {
            _steps[_index].Opacity = 0;
            await Task.Delay(FadeDuration);
        }
        foreach (var step in _steps) step.Visibility = Visibility.Collapsed;

        _index = index;
        var next = _steps[index];
        next.Opacity = 0;
        next.Visibility = Visibility.Visible;
        next.Opacity = 1;

        for (var i = 0; i < _dots.Count; i++)
        {
            var current = i == index;
            _dots[i].Opacity = current ? 1 : 0.3;
            _dots[i].Scale = current ? Vector3.One : new Vector3(0.75f, 0.75f, 1);
        }

        if (next == MicrophoneStep) BeginMicrophoneStep();
        else if (next == DoneStep) BeginCountdown();
    }

    // MARK: - Microphone

    private void BeginMicrophoneStep()
    {
        UpdateMicrophoneStep();
        // Mac polls the accessibility permission every second; the same here for the Windows switches.
        _timer = UiThread.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateMicrophoneStep();
        _timer.Start();
    }

    private void UpdateMicrophoneStep()
    {
        var access = MicrophonePermission.Check();
        var granted = access == MicrophoneAccess.Allowed;

        MicrophoneGranted.Visibility = granted ? Visibility.Visible : Visibility.Collapsed;
        GrantMicrophone.Visibility = granted ? Visibility.Collapsed : Visibility.Visible;
        ContinueMicrophone.Visibility = granted ? Visibility.Visible : Visibility.Collapsed;
        SkipMicrophone.Visibility = !granted && _microphoneAttempted ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneButtons.Margin = new Thickness(0, granted ? 16 : 36, 0, 0);

        var hint = access switch
        {
            MicrophoneAccess.Blocked when _microphoneAttempted =>
                "Windows is blocking microphone access. In Settings > Privacy & security > Microphone, turn on Microphone access and Let desktop apps access your microphone.",
            MicrophoneAccess.NoDevice when _microphoneAttempted => "No microphone was found. Connect one to continue.",
            _ => "",
        };
        MicrophoneHint.Text = hint;
        MicrophoneHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Windows has no permission prompt for desktop apps: this opens the privacy page when it is turned off.
    private void OnGrantMicrophone(object sender, RoutedEventArgs e)
    {
        _microphoneAttempted = true;
        if (MicrophonePermission.Check() == MicrophoneAccess.Blocked) MicrophonePermission.OpenSettings();
        UpdateMicrophoneStep();
    }

    // MARK: - Auto-paste

    private void OnAutoPasteToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        AppSettings.Instance.AutoPaste = AutoPasteToggle.IsOn;
        AppSettings.Instance.Save();
    }

    // MARK: - Done

    private void BeginCountdown()
    {
        _countdown = CountdownSeconds;
        UpdateCountdownText();

        _timer = UiThread.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (timer, _) =>
        {
            if (_countdown > 1)
            {
                _countdown--;
                UpdateCountdownText();
                return;
            }

            timer.Stop();
            Completed?.Invoke();
        };
        _timer.Start();
    }

    private void UpdateCountdownText() =>
        CountdownText.Text = $"Coworker is ready to use. You'll be taken to the app in {_countdown} seconds...";
}
