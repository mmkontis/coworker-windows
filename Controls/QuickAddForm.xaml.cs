using CommunityToolkit.Mvvm.Messaging;
using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Coworker.Controls;

public enum QuickAddKind
{
    Dictionary,
    Shortcut,
}

/// The "Add to dictionary" / "Add to shortcuts" form of the tray panel (see QuickAddForm.xaml). A
/// save creates the entry, announces the change and offers Undo in a toast.
public sealed partial class QuickAddForm : UserControl
{
    private readonly QuickAddKind _kind;
    private bool _saving;

    public QuickAddForm(QuickAddKind kind)
    {
        InitializeComponent();
        _kind = kind;

        TitleText.Text = Title(kind);
        PhraseBox.PlaceholderText = kind == QuickAddKind.Dictionary ? "Word or phrase" : "Shortcut phrase";
        ExpansionBox.Visibility = kind == QuickAddKind.Shortcut ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PhraseBox.Focus(FocusState.Programmatic);
    }

    public static string Title(QuickAddKind kind) => kind == QuickAddKind.Dictionary ? "Add to dictionary" : "Add to shortcuts";

    /// Asks the host to close the form.
    public event Action? Dismissed;

    private string Phrase => PhraseBox.Text.Trim();
    private string Expansion => ExpansionBox.Text.Trim();

    private bool CanSave => !_saving && Phrase.Length > 0 && (_kind == QuickAddKind.Dictionary || Expansion.Length > 0);

    private void OnTextChanged(object sender, TextChangedEventArgs e) => AddButton.IsEnabled = CanSave;

    private void OnPhraseKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !CanSave) return;
        e.Handled = true;
        Save();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    private void OnAdd(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (!CanSave) return;

        var word = Phrase;
        var shortcut = _kind == QuickAddKind.Shortcut ? Expansion : null;
        _saving = true;
        ErrorText.Visibility = Visibility.Collapsed;
        Spinner.IsActive = true;
        Spinner.Visibility = Visibility.Visible;
        AddButton.IsEnabled = false;

        try
        {
            var entry = await ApiService.Shared.AddDictionaryEntry(word, word, shortcut);
            WeakReferenceMessenger.Default.Send(new DictionaryChangedMessage());
            Dismissed?.Invoke();
            ToastService.Show(ToastKind.Success, null, _kind == QuickAddKind.Dictionary ? "Added to dictionary" : "Shortcut saved",
                actionTitle: entry == null ? null : "Undo", action: entry == null ? null : () => _ = UndoAsync(entry.Id));
        }
        catch (ApiException)
        {
            ErrorText.Text = "Couldn't save. Check your connection and try again.";
            ErrorText.Visibility = Visibility.Visible;
            _saving = false;
            Spinner.IsActive = false;
            Spinner.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = CanSave;
        }
    }

    private static async Task UndoAsync(string id)
    {
        try
        {
            await ApiService.Shared.DeleteDictionaryEntry(id);
            WeakReferenceMessenger.Default.Send(new DictionaryChangedMessage());
            ToastService.Show(ToastKind.Success, null, "Entry removed");
        }
        catch (ApiException)
        {
            ToastService.Show(ToastKind.Error, null, "Couldn't undo. Try again in Dictionary.", 7);
        }
    }
}
