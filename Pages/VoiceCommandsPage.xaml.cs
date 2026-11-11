using Coworker.Controls;
using Coworker.Models;
using Coworker.Services;
using Coworker.Services.VoiceCommands;
using Coworker.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Coworker.Pages;

/// The Shortcuts page (Mac: VoiceCommandsPageView): the user's voice commands, and a form to add or
/// edit one. The commands live on the server; nothing is stored here.
public sealed partial class VoiceCommandsPage : Page
{
    private static readonly TimeSpan FormAnimation = TimeSpan.FromMilliseconds(200);

    private readonly VoiceCommandService _service = VoiceCommandService.Shared;
    private readonly List<ActionStepViewModel> _steps = new();
    private readonly Dictionary<ActionStepViewModel, ActionStepEditor> _editors = new();

    private List<VoiceCommand> _commands = new();
    private IReadOnlyList<ActionCatalogEntry> _catalog;
    private VoiceCommand? _editing;
    private bool _adding;
    private bool _isSaving;
    private bool _formShown;
    private Storyboard? _formStoryboard;
    private string _matchMode = "starts_with";
    private string _iconName = "bolt.fill";
    private string _iconColor = "#007AFF";

    public VoiceCommandsPage()
    {
        InitializeComponent();
        _catalog = _service.ActionsCatalog;
        ResetForm();
        UpdateSubtitle();
        Loaded += OnLoaded;
    }

    private bool IsFormOpen => _adding || _editing != null;

    private bool CanSubmit => TriggerBox.Text.Trim().Length > 0 && _steps.Count > 0 && _steps.All(step => step.ActionType.Length > 0);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = LoadCatalogAsync();
        SetState(PageState.Loading);
        await LoadCommandsAsync();
    }

    // MARK: - Loading

    private enum PageState { Loading, Error, Empty, List }

    private async Task LoadCommandsAsync()
    {
        ErrorBar.IsOpen = false;
        try
        {
            _commands = await _service.LoadAllAsync();
        }
        catch (ApiException ex)
        {
            var message = ApiErrorMessages.For(ex, ApiContext.Default);
            if (_commands.Count == 0) ShowLoadError(message);
            else ShowError(message);
            return;
        }

        UpdateSubtitle();
        RebuildRows();
    }

    private async Task LoadCatalogAsync()
    {
        _catalog = await _service.LoadActionsCatalogAsync();
        foreach (var step in _steps) step.Catalog = _catalog;
        RebuildRows();
    }

    private void ShowLoadError(string message)
    {
        ErrorText.Text = message;
        SetState(PageState.Error);
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void RebuildRows()
    {
        RowsRepeater.ItemsSource = _commands.Select(command => new CommandRowViewModel(command, _catalog)).ToList();
        SetState(_commands.Count == 0 ? PageState.Empty : PageState.List);
    }

    private void SetState(PageState state)
    {
        LoadingPanel.Visibility = state == PageState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == PageState.Error ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == PageState.Empty ? Visibility.Visible : Visibility.Collapsed;
        RowsRepeater.Visibility = state == PageState.List ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSubtitle() => SubtitleText.Text = _commands.Count == 0
        ? "Create voice commands that trigger actions when you speak."
        : $"{_commands.Count} command{(_commands.Count == 1 ? "" : "s")} configured.";

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        SetState(PageState.Loading);
        await LoadCommandsAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => NavigationService.Shared.Navigate(AppTab.Home);

    // MARK: - Form open / close

    private void OnToggleFormClick(object sender, RoutedEventArgs e)
    {
        var open = IsFormOpen;
        _adding = !open;
        _editing = null;
        ResetForm();
        ApplyFormVisibility();
    }

    private void OnCreateFirstClick(object sender, RoutedEventArgs e)
    {
        _adding = true;
        ApplyFormVisibility();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var command = ((CommandRowViewModel)((FrameworkElement)sender).Tag).Command;
        _editing = command;
        _adding = false;

        TriggerBox.Text = command.TriggerPhrase;
        _matchMode = command.MatchMode;
        _iconName = command.IconName;
        _iconColor = command.IconColor;
        EnabledSwitch.IsOn = command.IsEnabled;
        SetSteps(command.AllActions);
        RefreshFormState();
        ApplyFormVisibility();
    }

    private void ResetForm()
    {
        TriggerBox.Text = "";
        _matchMode = "starts_with";
        _iconName = "bolt.fill";
        _iconColor = "#007AFF";
        EnabledSwitch.IsOn = true;
        SetSteps(new[] { new VoiceCommandAction() });
        RefreshFormState();
    }

    // Header button, save button and the shown/hidden state of the form all follow _adding / _editing.
    private void ApplyFormVisibility()
    {
        var open = IsFormOpen;
        NewButtonFill.Visibility = NewButtonContent.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        CancelButtonFill.Visibility = CancelButtonContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RefreshFormState();
        AnimateForm(open);
    }

    // Opacity and a short slide from the top, 0.2 s ease in/out, in both directions.
    private void AnimateForm(bool show)
    {
        if (show == _formShown) return;
        _formShown = show;
        _formStoryboard?.Stop();

        if (show) FormPanel.Visibility = Visibility.Visible;
        if (!FloatingWindow.AnimationsEnabled)
        {
            FormPanel.Opacity = show ? 1 : 0;
            FormTranslate.Y = 0;
            if (!show) FormPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var opacity = new DoubleAnimation { To = show ? 1 : 0, Duration = FormAnimation, EasingFunction = ease };
        var slide = new DoubleAnimation { From = show ? -12 : 0, To = show ? 0 : -12, Duration = FormAnimation, EasingFunction = ease };
        Storyboard.SetTarget(opacity, FormPanel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(slide, FormTranslate);
        Storyboard.SetTargetProperty(slide, "Y");

        if (show) FormPanel.Opacity = 0;
        var storyboard = _formStoryboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) =>
        {
            if (!show && !_formShown) FormPanel.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    // MARK: - Form fields

    private void OnTriggerChanged(object sender, TextChangedEventArgs e) => RefreshFormState();

    private void OnStartsWithClick(object sender, RoutedEventArgs e)
    {
        _matchMode = "starts_with";
        RefreshFormState();
    }

    private void OnContainsClick(object sender, RoutedEventArgs e)
    {
        _matchMode = "contains";
        RefreshFormState();
    }

    // A command made elsewhere can be "exact": neither segment shows as chosen and the mode is kept.
    private void RefreshFormState()
    {
        StartsWithToggle.IsChecked = _matchMode == "starts_with";
        ContainsToggle.IsChecked = _matchMode == "contains";

        var color = CommandIcons.ParseColor(_iconColor);
        IconPreview.Glyph = CommandIcons.Glyph(_iconName);
        IconPreview.Foreground = new SolidColorBrush(color);
        IconPreviewTile.Background = new SolidColorBrush(CommandIcons.WithOpacity(color, 0.12));

        var multi = _steps.Count > 1;
        StepsHint.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        StepsHint.Text = $"({_steps.Count} steps — run sequentially)";

        SaveIcon.Glyph = _editing != null ? "" : "";
        SaveText.Text = _editing != null ? "Save Changes" : "Add Command";
        var canSubmit = CanSubmit && !_isSaving;
        SaveButton.IsEnabled = canSubmit;
        SaveFillEnabled.Visibility = canSubmit ? Visibility.Visible : Visibility.Collapsed;
        SaveFillDisabled.Visibility = canSubmit ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: - Steps

    private void SetSteps(IEnumerable<VoiceCommandAction> actions)
    {
        StepsPanel.ChildrenTransitions = null;   // only adding or removing a step animates, not filling the form
        StepsPanel.Children.Clear();
        _steps.Clear();
        _editors.Clear();
        foreach (var action in actions) AddStep(action);
        NumberSteps();
    }

    private void AddStep(VoiceCommandAction action)
    {
        var step = new ActionStepViewModel(action, _catalog);
        step.ActionTypeChanged += OnStepActionChanged;
        var editor = new ActionStepEditor(step);
        editor.RemoveRequested += OnRemoveStep;
        _steps.Add(step);
        _editors[step] = editor;
        StepsPanel.Children.Add(editor);
    }

    private void NumberSteps()
    {
        for (var i = 0; i < _steps.Count; i++) _steps[i].SetPosition(i + 1, _steps.Count);
    }

    private void AnimateStepChanges() =>
        StepsPanel.ChildrenTransitions ??= new TransitionCollection { new AddDeleteThemeTransition() };

    private void OnAddStepClick(object sender, RoutedEventArgs e)
    {
        AnimateStepChanges();
        AddStep(new VoiceCommandAction());
        NumberSteps();
        RefreshFormState();
    }

    private void OnRemoveStep(ActionStepViewModel step)
    {
        if (_steps.Count <= 1) return;
        AnimateStepChanges();
        StepsPanel.Children.Remove(_editors[step]);
        _editors.Remove(step);
        _steps.Remove(step);
        NumberSteps();
        RefreshFormState();
    }

    // The command takes its icon and color from the first step's action.
    private void OnStepActionChanged(ActionStepViewModel step)
    {
        if (_steps.Count > 0 && _steps[0] == step && _catalog.FirstOrDefault(entry => entry.Id == step.ActionType) is { } entry)
        {
            _iconName = entry.IconName;
            _iconColor = entry.IconColor;
        }
        RefreshFormState();
    }

    // MARK: - Save and delete

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!CanSubmit || _isSaving) return;

        var (actionType, actionConfig) = VoiceCommand.PackActions(_steps.Select(step => step.Step).ToList());
        var input = new VoiceCommandInput(TriggerBox.Text.Trim(), _matchMode, actionType, actionConfig,
            _iconName, _iconColor, EnabledSwitch.IsOn);

        _isSaving = true;
        RefreshFormState();
        ErrorBar.IsOpen = false;
        try
        {
            if (_editing != null)
            {
                await _service.UpdateAsync(_editing.Id, input);
                _editing = null;
            }
            else
            {
                await _service.CreateAsync(input);
                _adding = false;
            }
            ResetForm();
            ApplyFormVisibility();
            await LoadCommandsAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ApiErrorMessages.For(ex, ApiContext.Default));
        }
        finally
        {
            _isSaving = false;
            RefreshFormState();
        }
    }

    // No confirmation, like the Mac.
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var command = ((CommandRowViewModel)((FrameworkElement)sender).Tag).Command;
        ErrorBar.IsOpen = false;
        try
        {
            await _service.DeleteAsync(command.Id);
        }
        catch (ApiException ex)
        {
            ShowError(ApiErrorMessages.For(ex, ApiContext.Default));
            return;
        }

        if (_editing?.Id == command.Id)
        {
            _editing = null;
            ResetForm();
            ApplyFormVisibility();
        }
        await LoadCommandsAsync();
    }
}
