namespace Coworker.Models;

// Shell: theme, navigation design, onboarding, launch behaviour, diagnostics.
public partial class AppSettings
{
    /// "clean" (Mac default) or "aurora". Read through ThemeService.
    public string Theme { get; set; } = "clean";

    /// Sidebar layout: "dropdown" (Mac default), "sections" or "classic".
    public string WorkspaceDesign { get; set; } = "dropdown";

    public bool HasCompletedOnboarding { get; set; }

    /// Launch at login (Run key + StartupApproved); the toggle lands with the shell slice.
    public bool AutoStartEnabled { get; set; } = true;

    public bool VerboseLogging { get; set; }
}
