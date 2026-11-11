using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Coworker.Transcription;

/// <summary>
/// The field a take will be typed into, sent with it as <c>field</c> (Mac: DictationPasteController.fieldDescriptor()).
/// The server (Humanlike-node/plugins/coworker/field-format.ts, <c>parseTakeField</c>) works out what kind of field
/// it is and writes the words the way that field wants them: digits in a postal code or card number, MM/YY in an
/// expiry, no sentence period in a name or a one-line field. It describes the field, never what is in it.
/// </summary>
public sealed record TakeField
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// UI Automation control type: "Edit", "Document", "ComboBox".
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    /// LocalizedControlType: "edit", or how a browser names the input ("email", "telephone" in Chromium).
    [JsonPropertyName("role_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleDescription { get; init; }

    /// The field takes several lines (a Document, a textarea or contenteditable in a page, a multi-line edit control).
    [JsonPropertyName("multiline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Multiline { get; init; }

    /// Name, or the Name of the element that labels it.
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonPropertyName("placeholder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placeholder { get; init; }

    /// HelpText and FullDescription.
    [JsonPropertyName("help")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Help { get; init; }

    /// AutomationId: the DOM id in a browser ("billingPostalCode").
    [JsonPropertyName("identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identifier { get; init; }

    /// "tel", "email", "url", "number" or "search", when the field says so.
    [JsonPropertyName("input_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputType { get; init; }

    /// The object the HTTP form carries as a JSON string.
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// One line for the debug log: what the field is called, never what it holds.
    public string Summary() =>
        string.Join(" | ", new[] { Role, RoleDescription, Label, Placeholder, Identifier, InputType }.Where(part => part != null));
}

/// What UI Automation says about the focused element, read on the UIA worker (<c>UiaField.ReadFieldFacts</c>).
/// Plain strings, so the mapping to <see cref="TakeField"/> can be tested without a desktop.
public sealed record UiaFieldFacts(
    string? ControlType,
    string? LocalizedControlType = null,
    string? Name = null,
    string? LabeledByName = null,
    string? HelpText = null,
    string? FullDescription = null,
    string? AutomationId = null,
    string? AriaProperties = null,
    string? FrameworkId = null,
    bool IsPassword = false,
    bool? Win32Multiline = null);

/// Turns <see cref="UiaFieldFacts"/> into the <see cref="TakeField"/> sent with a take.
public static class TakeFieldDescriptor
{
    // The longest string the server keeps per key.
    private const int MaxLength = 200;

    // Browsers give an input's HTML placeholder as its UIA help text.
    private static readonly string[] BrowserFrameworks = { "Chrome", "Gecko" };

    private static readonly Regex WordSplit = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // How browsers and frameworks name an input type in LocalizedControlType, checked in this order
    // ("phone number" is a phone, not a number). Greek covers Chrome and Edge in Greek.
    private static readonly (string InputType, string[] Words, string[] Stems)[] InputTypeNames =
    {
        ("tel", new[] { "tel", "telephone", "phone" }, new[] { "τηλέφων", "τηλεφων" }),
        ("email", new[] { "email", "e mail" }, new[] { "ταχυδρομ" }),
        ("url", new[] { "url", "uri", "web address", "link" }, Array.Empty<string>()),
        ("number", new[] { "number", "numeric", "spinner", "spin button" }, new[] { "αριθμ" }),
        ("search", new[] { "search" }, new[] { "αναζήτησ", "αναζητησ" }),
    };

    /// The field to send, or null for a password field or an element that says nothing about itself.
    public static TakeField? From(UiaFieldFacts facts)
    {
        if (facts.IsPassword) return null;

        var aria = ParseAriaProperties(facts.AriaProperties);
        var role = Clean(facts.ControlType);
        var roleDescription = Clean(facts.LocalizedControlType);

        var label = Clean(facts.Name) ?? Clean(facts.LabeledByName);
        var helpText = Clean(facts.HelpText);
        var placeholder = Clean(Value(aria, "placeholder"))
            ?? (BrowserFrameworks.Contains(facts.FrameworkId ?? "", StringComparer.OrdinalIgnoreCase) ? helpText : null);
        var help = Clean(string.Join(". ", new[] { helpText, Clean(facts.FullDescription) }
            .Where(part => part != null && !SameText(part, label) && !SameText(part, placeholder))
            .Distinct(StringComparer.OrdinalIgnoreCase)));

        var field = new TakeField
        {
            Role = role,
            RoleDescription = roleDescription,
            Multiline = Multiline(aria, role, facts.Win32Multiline),
            Label = label,
            Placeholder = placeholder,
            Help = help,
            Identifier = Identifier(facts.AutomationId),
            InputType = InputType(aria, roleDescription, role),
        };
        return field == new TakeField() ? null : field;
    }

    /// AriaProperties as UI Automation gives them: "multiline=true;required=false", with \ escaping ; = and \.
    /// Keys are lowercase.
    public static Dictionary<string, string> ParseAriaProperties(string? text)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return properties;

        var key = new System.Text.StringBuilder();
        var value = new System.Text.StringBuilder();
        var inValue = false;
        void Flush()
        {
            var name = key.ToString().Trim().ToLowerInvariant();
            if (name.Length > 0 && inValue) properties[name] = value.ToString().Trim();
            key.Clear();
            value.Clear();
            inValue = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                (inValue ? value : key).Append(text[++i]);
            }
            else if (c == ';')
            {
                Flush();
            }
            else if (c == '=' && !inValue)
            {
                inValue = true;
            }
            else
            {
                (inValue ? value : key).Append(c);
            }
        }
        Flush();
        return properties;
    }

    // An explicit ARIA multiline wins (Chromium sets it for textarea and contenteditable); then a Document,
    // then the edit control's own ES_MULTILINE style. An Edit that says nothing is left to the server (one line).
    private static bool? Multiline(Dictionary<string, string> aria, string? role, bool? win32Multiline)
    {
        if (Value(aria, "multiline") is { } multiline && bool.TryParse(multiline, out var isMultiline)) return isMultiline;
        if (string.Equals(role, "Document", StringComparison.OrdinalIgnoreCase)) return true;
        return win32Multiline == true ? true : null;
    }

    private static string? InputType(Dictionary<string, string> aria, string? roleDescription, string? role)
    {
        var declared = Clean(Value(aria, "type") ?? Value(aria, "inputtype") ?? Value(aria, "text-input-type"));
        if (declared != null) return declared.ToLowerInvariant();

        if (roleDescription != null)
        {
            var words = " " + string.Join(' ', WordSplit.Split(roleDescription.ToLowerInvariant()).Where(w => w.Length > 0)) + " ";
            foreach (var (inputType, phrases, stems) in InputTypeNames)
            {
                if (phrases.Any(phrase => words.Contains($" {phrase} ", StringComparison.Ordinal)) ||
                    stems.Any(stem => words.Contains(stem, StringComparison.Ordinal)))
                    return inputType;
            }
        }

        // Chromium exposes <input type="number"> as a spinner.
        return string.Equals(role, "Spinner", StringComparison.OrdinalIgnoreCase) ? "number" : null;
    }

    // Win32 control ids ("15") and generated GUIDs say nothing about the field (Mac drops AppKit's "_NS:123").
    private static string? Identifier(string? automationId)
    {
        var id = Clean(automationId);
        if (id == null || id.All(char.IsDigit) || Guid.TryParse(id, out _)) return null;
        return id;
    }

    private static string? Value(Dictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value : null;

    private static bool SameText(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }
}
