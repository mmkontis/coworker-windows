using System.Text.Json.Serialization;
using Coworker.Services;

namespace Coworker.Models;

/// <summary>
/// Persisted app settings. Split by area across AppSettings.*.cs so feature slices add their
/// keys in their own file. Reads are instant (in-memory singleton); Save() is debounced and
/// written atomically by <see cref="SettingsStore"/>. Secrets never live here (see CredentialStore).
/// </summary>
public partial class AppSettings
{
    public static AppSettings Instance { get; } = SettingsStore.Load();

    /// Raised on the calling thread after every Save(), so tray, bar and Settings stay in sync.
    public static event Action? Changed;

    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;

    // Session. The access token is stored in the CredentialStore, never in settings.json.
    [JsonIgnore]
    public string? AccessToken
    {
        get => CredentialStore.Shared.Get(CredentialStore.SessionAccessToken);
        set => CredentialStore.Shared.Set(CredentialStore.SessionAccessToken, value);
    }

    public string? UserEmail { get; set; }
    public string? ConnectedUserId { get; set; }

    /// The account's name and Google photo from the server, for the sidebar's account row.
    public string? UserFullName { get; set; }
    public string? UserPhotoUrl { get; set; }
    public string? DeviceId { get; set; }
    public bool IsConnected { get; set; }

    // Legacy chat page: company / workspace scope from the connect-device response
    // (coworker_users.company_id) and the Chat scope dropdown ("all" or a company id).
    public string? CompanyId { get; set; }
    public string SelectedCompanyId { get; set; } = "all";
    public bool IsAcademic { get; set; }
    public bool ChatModeEnabled { get; set; } = true;
    public bool EnableFunctions { get; set; }
    /// Chat completion (Mac default: Fn+Shift). Ctrl+Win stays clear of the Ctrl+Shift dictation hold and
    /// of AltGr (Ctrl+Alt on many keyboards).
    public const string DefaultChatShortcut = "Ctrl+Win";
    public string ChatShortcut { get; set; } = DefaultChatShortcut;

    public int? TestedTypingWpm { get; set; }

    /// The dashboard's period for words and time saved: 7 or 30 days, kept between launches.
    public int DashboardRangeDays { get; set; } = 7;

    public void Save()
    {
        SettingsStore.QueueSave(this);
        Changed?.Invoke();
    }
}
