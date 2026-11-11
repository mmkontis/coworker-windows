using Coworker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coworker.Controls;

/// One step of the command form: the action picker, the number badge and remove button of a
/// multi-step command, and the inputs of the chosen action (app for Open App, address for Open Web Page).
public sealed partial class ActionStepEditor : UserControl
{
    public ActionStepViewModel Step { get; }

    public event Action<ActionStepViewModel>? RemoveRequested;

    public ActionStepEditor(ActionStepViewModel step)
    {
        Step = step;
        InitializeComponent();

        Picker.Catalog = step.Catalog;
        Picker.SelectedId = step.ActionType;
        Picker.Selected += entry => Step.ActionType = entry.Id;

        AppChooser.SetSelection(step.BundleIdentifier, step.AppName, step.AppPath);
        AppChooser.Selected += (bundleIdentifier, name, path) =>
        {
            Step.BundleIdentifier = bundleIdentifier;
            Step.AppName = name;
            Step.AppPath = path;
        };

        step.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionStepViewModel.Catalog)) Picker.Catalog = Step.Catalog;
        };
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(Step);
}
