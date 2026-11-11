namespace Coworker.Models;

/// Navigation layout, independent of the colour theme. Mac: WorkspaceDesign (AppSharedTypes.swift).
public enum WorkspaceDesign
{
    Sections,
    Dropdown,
    Classic,
}

/// The top-level groups of the workspace picker. Mac: WorkspaceArea; its Agents area is out of scope.
public enum WorkspaceArea
{
    Transcribe,
    Workflows,
}

/// <param name="Title">The label shown in the sidebar (differs per design, see <see cref="SidebarModel"/>).</param>
public sealed record SidebarItem(AppTab Tab, string Title);

/// What each navigation design lists and how items are titled. Mac: SidebarView.swift.
public static class SidebarModel
{
    // Mac SidebarView.canShowMoreMenu: Contacts and Lists stay behind this internal account.
    private const string InternalEmail = "minas@theunivation.com";

    public static WorkspaceDesign Design
    {
        get => Enum.TryParse<WorkspaceDesign>(AppSettings.Instance.WorkspaceDesign, ignoreCase: true, out var design)
            ? design
            : WorkspaceDesign.Dropdown;
        set
        {
            AppSettings.Instance.WorkspaceDesign = value.ToString().ToLowerInvariant();
            AppSettings.Instance.Save();
        }
    }

    public static string Title(this WorkspaceDesign design) => design switch
    {
        WorkspaceDesign.Sections => "Expandable sections",
        WorkspaceDesign.Dropdown => "Workspace dropdown",
        _ => "Classic sidebar",
    };

    public static string Title(this WorkspaceArea area) => area switch
    {
        WorkspaceArea.Transcribe => "Transcribe",
        _ => "Workflows",
    };

    /// The tab that opens when an area is picked.
    public static AppTab Tab(this WorkspaceArea area) => area switch
    {
        WorkspaceArea.Transcribe => AppTab.Home,
        _ => AppTab.Workflows,
    };

    /// The area a tab belongs to; Settings belongs to none (the picker keeps its current area).
    public static WorkspaceArea? AreaFor(AppTab tab) => tab switch
    {
        AppTab.Workflows => WorkspaceArea.Workflows,
        AppTab.Settings => null,
        _ => WorkspaceArea.Transcribe,
    };

    /// The rows under an area in the dropdown and sections designs.
    public static IReadOnlyList<SidebarItem> AreaItems(WorkspaceArea area) => area switch
    {
        WorkspaceArea.Transcribe => AppTabs.All.Where(t => t.ShowInSidebar).Select(t => new SidebarItem(t.Tab, t.NavTitle)).ToList(),
        _ => new[] { new SidebarItem(AppTab.Workflows, "All workflows") },
    };

    /// Rows behind the "More" expander (Mac also lists Reader and Chat here; both are out of scope).
    public static IReadOnlyList<SidebarItem> MoreItems(string? email)
    {
        var items = new List<SidebarItem> { new(AppTab.Analytics, "Analytics") };
        if (ShowsInternalTabs(email))
        {
            items.Add(new(AppTab.Contacts, "Contacts"));
            items.Add(new(AppTab.Lists, "Lists"));
        }
        return items;
    }

    /// The flat list of the classic design (Mac primaryTabs + Workflows, without Reader).
    public static IReadOnlyList<SidebarItem> ClassicItems() => new SidebarItem[]
    {
        new(AppTab.Home, "Home"),
        new(AppTab.Dictionary, "Dictionary"),
        new(AppTab.Playbooks, "Playbooks"),
        new(AppTab.History, "History"),
        new(AppTab.Workflows, "Workflows"),
    };

    /// The classic design keeps Commands (and the internal tabs) in the profile menu under "More".
    public static IReadOnlyList<SidebarItem> ClassicMoreItems(string? email)
    {
        var items = new List<SidebarItem> { new(AppTab.VoiceCommands, "Commands") };
        if (ShowsInternalTabs(email))
        {
            items.Add(new(AppTab.Contacts, "Contacts"));
            items.Add(new(AppTab.Lists, "Lists"));
        }
        return items;
    }

    private static bool ShowsInternalTabs(string? email) =>
        string.Equals(email?.Trim(), InternalEmail, StringComparison.OrdinalIgnoreCase);

    /// Sidebar footer name: the first two words of the email's local part, capitalised
    /// ("minas.kontis@x.com" is "Minas Kontis"), or the email itself when nothing readable is left.
    public static string DisplayName(string email)
    {
        var at = email.IndexOf('@');
        var local = at < 0 ? email : email[..at];
        var words = local.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        var name = string.Join(' ', words);
        return name.Length == 0 ? email : name;
    }
}
