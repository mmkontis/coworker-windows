using Coworker.Pages;

namespace Coworker.Models;

/// Every destination of the main window, in Mac sidebar order (Home, Record, Recordings, Playbooks,
/// Dictionary, Shortcuts, More, Workflows). A slice that builds a page replaces its page file (the
/// stub keeps the same class name) and, when the tab should appear in the sidebar, flips
/// ShowInSidebar below. Nothing else needs to change. ShowInSidebar means "a primary row of the
/// Transcribe area" (Mac: the dropdown and sections designs); Settings, Analytics and the internal
/// tabs are reached from the profile menu, the "More" row and the dashboard.
public enum AppTab
{
    Home,
    Transcribe,
    History,
    Playbooks,
    Dictionary,
    VoiceCommands,
    Analytics,
    Lists,
    Contacts,
    Workflows,
    Chat,
    Settings,
}

/// <param name="Title">Page title.</param>
/// <param name="NavTitle">Label in the sidebar (Mac: "Recordings" for History, "Record" for Transcribe, "Shortcuts" for voice commands).</param>
/// <param name="Glyph">Segoe MDL2 glyph.</param>
/// <param name="ShowInSidebar">Listed in the sidebar today.</param>
/// <param name="SidebarHighlight">The row that lights up while this tab is open (Analytics lives under Home).</param>
public sealed record AppTabInfo(AppTab Tab, string Title, string NavTitle, string Glyph, Type PageType,
    bool ShowInSidebar = false, AppTab? SidebarHighlight = null);

public static class AppTabs
{
    public static IReadOnlyList<AppTabInfo> All { get; } = new AppTabInfo[]
    {
        new(AppTab.Home, "Home", "Home", "", typeof(DashboardPage), ShowInSidebar: true),
        new(AppTab.Transcribe, "Record", "Record", "", typeof(TranscribePage), ShowInSidebar: true),
        new(AppTab.History, "History", "Recordings", "", typeof(HistoryPage), ShowInSidebar: true),
        new(AppTab.Playbooks, "Playbooks", "Playbooks", "", typeof(PlaybooksPage), ShowInSidebar: true),
        new(AppTab.Dictionary, "Dictionary", "Dictionary", "", typeof(DictionaryPage), ShowInSidebar: true),
        new(AppTab.VoiceCommands, "Shortcuts", "Shortcuts", "", typeof(VoiceCommandsPage), ShowInSidebar: true),
        new(AppTab.Analytics, "Analytics", "Analytics", "", typeof(AnalyticsPage), SidebarHighlight: AppTab.Home),
        new(AppTab.Lists, "Lists", "Lists", "", typeof(ListsPage)),
        new(AppTab.Contacts, "Contacts", "Contacts", "", typeof(ContactsPage)),
        new(AppTab.Workflows, "Workflows", "Workflows", "", typeof(WorkflowsPage)),
        new(AppTab.Chat, "Chat", "Chat", "", typeof(ChatPage)),
        new(AppTab.Settings, "Settings", "Settings", "", typeof(SettingsPage)),
    };

    public static AppTabInfo Get(AppTab tab) => All.First(t => t.Tab == tab);

    public static AppTab HighlightFor(AppTab tab) => Get(tab).SidebarHighlight ?? tab;
}
