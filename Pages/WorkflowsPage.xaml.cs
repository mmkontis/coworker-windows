using Coworker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Coworker.Pages;

/// The Workflows tab (see WorkflowsPage.xaml). The page is cached by the frame, and the web view
/// itself lives in <see cref="WorkflowBrowser"/>, so the editor is the same when the tab is reopened.
public sealed partial class WorkflowsPage : Page
{
    private static WorkflowBrowser Browser => WorkflowBrowser.Shared;

    private UIElement? _hosted;

    public WorkflowsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Browser.StateChanged += Update;
        Browser.Prepare(SessionService.Shared.UserEmail);
        Update();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Browser.StateChanged -= Update;

    private void Update()
    {
        // The browser builds a new web view when the account or server changes.
        if (_hosted != Browser.View)
        {
            if (_hosted != null) WebHost.Children.Remove(_hosted);
            _hosted = Browser.View;
            if (_hosted != null) WebHost.Children.Insert(0, _hosted);
        }

        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
        Spinner.IsActive = Browser.IsLoading;
        Spinner.Visibility = Browser.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        ErrorPanel.Visibility = Browser.ErrorMessage == null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = Browser.ErrorMessage ?? "";
        RuntimeLink.Visibility = Browser.IsRuntimeMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Browser.GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => Browser.GoForward();

    private void OnShowWorkflows(object sender, RoutedEventArgs e) => Browser.ShowWorkflows();

    private void OnReload(object sender, RoutedEventArgs e) => Browser.Reload();

    private void OnOpenInBrowser(object sender, RoutedEventArgs e) =>
        _ = Windows.System.Launcher.LaunchUriAsync(Browser.CurrentUrl);

    private void OnGetRuntime(object sender, RoutedEventArgs e) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(WorkflowBrowser.RuntimeDownloadUrl));
}
