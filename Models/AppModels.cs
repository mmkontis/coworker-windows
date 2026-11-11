using CommunityToolkit.Mvvm.ComponentModel;

namespace Coworker.Models;

public partial class DictionaryEntry : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string _definition = "";
    [ObservableProperty] private string? _shortcut;

    public string TypeLabel => Word.Contains(' ') ? "PHRASE" : "WORD";
}

public class DictionaryResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Word { get; set; } = "";
    public string? Phonetic { get; set; }
    public string PartOfSpeech { get; set; } = "";
    public string Definition { get; set; } = "";
    public List<string> Examples { get; set; } = new();
}

public partial class ChatMessage : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Role { get; set; } = "user";
    [ObservableProperty] private string _content = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsUser => Role == "user";
}

public class TranscriptionHistoryItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int WordCount { get; set; }
    public double Duration { get; set; }
    public string? App { get; set; }
    public string? Language { get; set; }
    public string? Model { get; set; }

    public string FormattedDate => Timestamp.ToString("MMM d, yyyy h:mm tt");
    public string FormattedDuration
    {
        get
        {
            var seconds = (int)Duration;
            return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
        }
    }
}

public class ContactInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public List<string> PhoneNumbers { get; set; } = new();
    public List<string> EmailAddresses { get; set; } = new();
    public string Initials => string.Join("", Name.Split(' ').Where(s => s.Length > 0).Take(2).Select(s => s[0])).ToUpper();
    public bool HasPhoneNumber => PhoneNumbers.Count > 0;
}

public class PlaybookItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
    public string? SystemPrompt { get; set; }
    public List<string> TriggerPhrases { get; set; } = new();
    public int ExampleCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class ListItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class TranscriptionModel
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Tier { get; set; }
    public bool Featured { get; set; }
    public string? Icon { get; set; }

    // Fields below come from GET /coworker/models; the server is the only source of model data.
    /// Short plain name for pickers; Name can carry emoji and tier wording.
    public string? ShortName { get; set; }
    /// Label of this model's stop on the quality slider ("Groq", "GPT", ...); null when not on the slider.
    public string? Stop { get; set; }
    public int? StopOrder { get; set; }
    /// Streams text while the user speaks.
    public bool Live { get; set; }
    public double? PricePerMinuteUsd { get; set; }
    public bool IsDefault { get; set; }
}

// MARK: - Chat Hub contexts (workspaces + companies)

/// A workspace the user can chat in. For now this is just "Personal";
/// extends to team workspaces as we ship them.
public class CoworkerWorkspace
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// "personal", "team", etc.
    public string? Kind { get; set; }
}

/// A company the authenticated user is associated with via
/// company_users membership (resolves to coworker_users.company_id).
public class CoworkerCompany
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
    public string? LogoUrl { get; set; }
}

/// Combined response for the Chat top-bar dropdowns. Mirrors the macOS
/// app's CoworkerContexts and the backend's GET /coworker/contexts shape.
public class CoworkerContexts
{
    public List<CoworkerWorkspace> Workspaces { get; set; } = new();
    public List<CoworkerCompany> Companies { get; set; } = new();
    public string? ActiveCompanyId { get; set; }
}
