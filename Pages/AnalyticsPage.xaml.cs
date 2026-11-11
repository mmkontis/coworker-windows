using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Coworker.Models;
using Coworker.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace Coworker.Pages;

public sealed partial class AnalyticsPage : Page
{
    private DispatcherTimer? _timer;
    private DateTime _testStartTime;
    private bool _isTestRunning;
    private int _selectedDays = 30;
    private List<ApiService.DailyProductivityPoint> _dailyPoints = new();

    public AnalyticsPage()
    {
        InitializeComponent();
        UpdateRangeButtons();
        LoadSavedWpm();
        Loaded += (_, _) => LoadAnalytics();
    }

    private void LoadSavedWpm()
    {
        var wpm = AppSettings.Instance.TestedTypingWpm;
        if (wpm is > 0)
        {
            SpeedValue.Text = wpm.Value.ToString();
            FootnoteText.Text = $"Time saved is estimated using your tested typing speed of {wpm} words per minute.";
        }
    }

    private async void LoadAnalytics()
    {
        ChartLoading.Visibility = Visibility.Visible;
        ActivityChart.Visibility = Visibility.Collapsed;
        ChartEmpty.Visibility = Visibility.Collapsed;

        try
        {
            _dailyPoints = await ApiService.Shared.FetchDailyProductivity(_selectedDays);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_dailyPoints.Any(p => p.Words > 0))
                {
                    BuildChart();
                    ActivityChart.Visibility = Visibility.Visible;
                }
                else
                {
                    ChartEmpty.Visibility = Visibility.Visible;
                }
                ChartLoading.Visibility = Visibility.Collapsed;
                LoadSummaryCards();
            });
        }
        catch
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ChartLoading.Visibility = Visibility.Collapsed;
                ChartEmpty.Visibility = Visibility.Visible;
                LoadSummaryCards();
            });
        }
    }

    private void LoadSummaryCards()
    {
        var accentBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x19, 0x24, 0x73, 0xF2));
        var secondaryBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x19, 0x4D, 0xD9, 0xFF));
        var glowBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x19, 0xB3, 0xD9, 0xFF));
        var accentFg = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"];
        var secondaryFg = (SolidColorBrush)Application.Current.Resources["CwSecondaryBrush"];
        var glowFg = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xB3, 0xD9, 0xFF));

        var totalWords = _dailyPoints.Sum(p => p.Words);
        var totalTimeSaved = _dailyPoints.Sum(p => p.TimeSaved);
        var avgWords = _dailyPoints.Count > 0 ? totalWords / _dailyPoints.Count : 0;
        var bestDay = _dailyPoints.OrderByDescending(p => p.Words).FirstOrDefault();

        var rangeLabel = _selectedDays switch { 7 => "7D", 30 => "30D", _ => "90D" };
        var wpm = AppSettings.Instance.TestedTypingWpm;
        var wpmText = wpm is > 0 ? $"At {wpm} WPM" : "At 40 WPM";

        var bestDayValue = bestDay != null && bestDay.Words > 0 ? bestDay.Words.ToString("N0") : "—";
        var bestDayHelper = bestDay != null && bestDay.Words > 0
            ? bestDay.Date.ToString("MMM d") : "Your top day will show here";

        var stats = new[]
        {
            new StatViewModel("\uE8F2", "WORDS", totalWords.ToString("N0"),
                $"Avg {avgWords:N0}/day in {rangeLabel}", accentBg, accentFg),
            new StatViewModel("\uE823", "TIME SAVED", FormatTime(totalTimeSaved),
                wpmText, secondaryBg, secondaryFg),
            new StatViewModel("\uE734", "BEST DAY", bestDayValue,
                bestDayHelper, glowBg, glowFg),
        };
        StatsRepeater.ItemsSource = stats;
    }

    private void BuildChart()
    {
        var accentColor = new SKColor(0x24, 0x73, 0xF2);
        var secondaryColor = new SKColor(0x4D, 0xD9, 0xFF);
        var gray = new SolidColorPaint(new SKColor(0x8A, 0x90, 0xA2));
        var separator = new SolidColorPaint(new SKColor(255, 255, 255, 16)) { StrokeThickness = 0.5f };

        var wordValues = _dailyPoints.Select(p => (double)p.Words).ToArray();
        // Real minutes — scaled by its own (right) axis, not faked onto the words axis.
        var timeMinutes = _dailyPoints.Select(p => p.TimeSaved / 60.0).ToArray();

        ActivityChart.XAxes = new Axis[]
        {
            new Axis
            {
                Labels = BuildSparseLabels(),
                LabelsRotation = 0,
                TextSize = 11,
                LabelsPaint = gray,
                SeparatorsPaint = null   // no vertical gridlines — cleaner
            }
        };

        ActivityChart.YAxes = new Axis[]
        {
            // 0 — Words (left)
            new Axis
            {
                TextSize = 11,
                LabelsPaint = gray,
                SeparatorsPaint = separator,
                MinLimit = 0,
                Position = LiveChartsCore.Measure.AxisPosition.Start
            },
            // 1 — Time saved (right), labelled in real time units so it's readable:
            // minutes under an hour, hours (e.g. 1.5h) above.
            new Axis
            {
                TextSize = 11,
                LabelsPaint = gray,
                SeparatorsPaint = null,
                MinLimit = 0,
                Position = LiveChartsCore.Measure.AxisPosition.End,
                Labeler = m => m >= 60 ? $"{m / 60.0:0.#}h" : $"{m:0}m"
            }
        };

        ActivityChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = timeMinutes,
                Name = "Time Saved",
                ScalesYAt = 1,
                Fill = new SolidColorPaint(secondaryColor.WithAlpha(90)),
                Stroke = null,
                MaxBarWidth = 12,
                Rx = 4,
                Ry = 4,
                Padding = 2
            },
            new LineSeries<double>
            {
                Values = wordValues,
                Name = "Words",
                ScalesYAt = 0,
                Stroke = new SolidColorPaint(accentColor) { StrokeThickness = 2.5f },
                Fill = new SolidColorPaint(accentColor.WithAlpha(40)),
                GeometrySize = 6,
                GeometryStroke = new SolidColorPaint(accentColor) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(accentColor),
                LineSmoothness = 0.65,
                // Don't clip the markers to the plot area — the dots that sit on the
                // X axis (value 0) were getting cut in half at the bottom edge.
                ClippingMode = LiveChartsCore.Measure.ClipMode.None
            }
        };
    }

    // ~7 labels max; the month name shows only when it changes (e.g. "May 22",
    // then "26", "30", "Jun 3", "7" …) instead of repeating "May" on every tick.
    private string[] BuildSparseLabels()
    {
        int n = _dailyPoints.Count;
        if (n == 0) return Array.Empty<string>();
        int step = Math.Max(1, (int)Math.Ceiling(n / 7.0));
        string? lastMonth = null;
        var labels = new string[n];
        for (int i = 0; i < n; i++)
        {
            if (i % step != 0) { labels[i] = ""; continue; }
            var d = _dailyPoints[i].Date;
            var month = d.ToString("MMM");
            labels[i] = month != lastMonth ? d.ToString("MMM d") : d.Day.ToString();
            lastMonth = month;
        }
        return labels;
    }

    private void OnRangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var days))
        {
            _selectedDays = days;
            UpdateRangeButtons();
            ChartSubtitle.Text = days switch { 7 => "Last 7 Days", 30 => "Last 30 Days", _ => "Last 90 Days" };
            LoadAnalytics();
        }
    }

    private void UpdateRangeButtons()
    {
        var accentBrush = (SolidColorBrush)Application.Current.Resources["CwAccentBrush"];
        var transparentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        Range7D.Background = _selectedDays == 7 ? accentBrush : transparentBrush;
        Range30D.Background = _selectedDays == 30 ? accentBrush : transparentBrush;
        Range90D.Background = _selectedDays == 90 ? accentBrush : transparentBrush;

        Range7D.Foreground = _selectedDays == 7
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Range30D.Foreground = _selectedDays == 30
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Range90D.Foreground = _selectedDays == 90
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void OnBackToDashboard(object sender, RoutedEventArgs e)
    {
        var mainWindow = App.MainWindow as MainWindow;
        mainWindow?.NavigateTo(AppTab.Home);
    }

    private void OnToggleSpeedTest(object sender, RoutedEventArgs e)
    {
        if (_isTestRunning) StopTest();
        else StartTest();
    }

    private void StartTest()
    {
        _isTestRunning = true;
        SpeedTestArea.Visibility = Visibility.Visible;
        SpeedTestInput.Text = "";
        SpeedValue.Text = "--";
        TimerText.Text = "Time: 0s";
        WordCountText.Text = "Words: 0";
        SpeedTestButtonText.Text = "Stop Test";
        SpeedTestIcon.Glyph = "\uE71A";
        SpeedTestInput.Focus(FocusState.Programmatic);
    }

    private void StopTest()
    {
        _isTestRunning = false;
        var finalWpm = SpeedValue.Text;
        _timer?.Stop();
        _timer = null;
        SpeedTestArea.Visibility = Visibility.Collapsed;
        SpeedTestButtonText.Text = "Test Your Speed";
        SpeedTestIcon.Glyph = "\uE768";

        if (int.TryParse(finalWpm, out var wpm) && wpm > 0)
        {
            var settings = AppSettings.Instance;
            settings.TestedTypingWpm = wpm;
            settings.Save();
            FootnoteText.Text = $"Time saved is estimated using your tested typing speed of {wpm} words per minute.";
            LoadAnalytics();
        }
    }

    private void OnSpeedTestInput(object sender, TextChangedEventArgs e)
    {
        if (!_isTestRunning) return;
        var text = SpeedTestInput.Text;

        if (text.Length == 1 && _timer == null)
        {
            _testStartTime = DateTime.Now;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => UpdateTimer();
            _timer.Start();
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        WordCountText.Text = $"Words: {words}";
        UpdateWpm(words);
    }

    private void UpdateTimer()
    {
        var elapsed = (DateTime.Now - _testStartTime).TotalSeconds;
        TimerText.Text = $"Time: {(int)elapsed}s";
        var words = SpeedTestInput.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        UpdateWpm(words);
    }

    private void UpdateWpm(int words)
    {
        var elapsed = (DateTime.Now - _testStartTime).TotalMinutes;
        if (elapsed > 0.05)
        {
            var wpm = (int)(words / elapsed);
            SpeedValue.Text = wpm.ToString();
        }
    }

    private static string FormatTime(double seconds)
    {
        var hours = (int)seconds / 3600;
        var minutes = (int)seconds / 60 % 60;
        var secs = (int)seconds % 60;
        if (hours > 0) return $"{hours}h {minutes}m";
        if (minutes > 0) return $"{minutes}m {secs:D2}s";
        return $"{secs}s";
    }
}

public record StatViewModel(
    string Icon, string Label, string Value, string Helper,
    SolidColorBrush IconBackground, SolidColorBrush IconForeground);
