using System.Text.Json;
using Coworker.Transcription;
using Xunit;

namespace Coworker.TextRulesTests;

/// How what UI Automation says about the focused element becomes the take's `field`, the object the server's
/// parseTakeField reads (Humanlike-node/plugins/coworker/field-format.ts).
public class TakeFieldTests
{
    [Fact]
    public void ChromePostalCode()
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts(
            ControlType: "Edit", LocalizedControlType: "edit", Name: "Postal code", HelpText: "e.g. 10557",
            AutomationId: "billingPostalCode", AriaProperties: "required=true", FrameworkId: "Chrome"));

        Assert.NotNull(field);
        Assert.Equal("Edit", field!.Role);
        Assert.Equal("edit", field.RoleDescription);
        Assert.Equal("Postal code", field.Label);
        Assert.Equal("e.g. 10557", field.Placeholder);   // Chromium gives the placeholder as help text
        Assert.Null(field.Help);                          // not repeated
        Assert.Equal("billingPostalCode", field.Identifier);
        Assert.Null(field.Multiline);
        Assert.Null(field.InputType);
    }

    [Fact]
    public void PasswordFieldsAreNeverDescribed()
    {
        Assert.Null(TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: "Password", IsPassword: true)));
    }

    [Fact]
    public void NothingToSayGivesNoField()
    {
        Assert.Null(TakeFieldDescriptor.From(new UiaFieldFacts(null, Name: "  ", AutomationId: "")));
    }

    [Theory]
    [InlineData("Document", null, null, true)]
    [InlineData("Edit", "multiline=true;required=false", null, true)]   // Chromium textarea / contenteditable
    [InlineData("Edit", "multiline=false", null, false)]
    [InlineData("Document", "multiline=false", null, false)]
    [InlineData("Edit", null, true, true)]                               // Win32 edit with ES_MULTILINE
    [InlineData("Edit", null, false, null)]                              // one line: the role says so
    [InlineData("ComboBox", null, null, null)]
    public void Multiline(string role, string? aria, bool? win32Multiline, bool? expected)
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts(role, AriaProperties: aria, Win32Multiline: win32Multiline));
        Assert.Equal(expected, field!.Multiline);
    }

    [Theory]
    // Chromium names email, tel and url inputs in LocalizedControlType.
    [InlineData("Edit", "telephone", null, "tel")]
    [InlineData("Edit", "phone number", null, "tel")]
    [InlineData("Edit", "email", null, "email")]
    [InlineData("Edit", "e-mail", null, "email")]
    [InlineData("Edit", "url", null, "url")]
    [InlineData("Edit", "τηλέφωνο", null, "tel")]
    [InlineData("Edit", "search box", null, "search")]
    [InlineData("Spinner", "spinner", null, "number")]                  // <input type="number">
    [InlineData("Spinner", null, null, "number")]
    [InlineData("Edit", "edit", null, null)]
    [InlineData("Document", "document", null, null)]
    // A declared type wins.
    [InlineData("Edit", "edit", "type=email", "email")]
    [InlineData("Edit", "telephone", "inputtype=Number", "number")]
    public void InputType(string role, string? localizedControlType, string? aria, string? expected)
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts(role, LocalizedControlType: localizedControlType, AriaProperties: aria));
        Assert.Equal(expected, field!.InputType);
    }

    [Fact]
    public void LabelFallsBackToTheLabellingElement()
    {
        var named = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: "City", LabeledByName: "Town"));
        var unnamed = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: "", LabeledByName: "ZIP:"));

        Assert.Equal("City", named!.Label);
        Assert.Equal("ZIP:", unnamed!.Label);
    }

    [Fact]
    public void PlaceholderFromAriaPropertiesOutsideBrowsers()
    {
        var aria = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", HelpText: "Where we send the invoice",
            AriaProperties: "placeholder=MM / YY", FrameworkId: "Win32"));
        var native = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", HelpText: "Where we send the invoice", FrameworkId: "XAML"));

        Assert.Equal("MM / YY", aria!.Placeholder);
        Assert.Equal("Where we send the invoice", aria.Help);
        Assert.Null(native!.Placeholder);                 // help text is only a placeholder in a browser
        Assert.Equal("Where we send the invoice", native.Help);
    }

    [Fact]
    public void HelpJoinsHelpTextAndFullDescriptionWithoutRepeats()
    {
        var both = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: "Card", HelpText: "16 digits",
            FullDescription: "As printed on the card", FrameworkId: "XAML"));
        var repeated = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: "Card number", HelpText: "Card number",
            FullDescription: "card number", FrameworkId: "XAML"));

        Assert.Equal("16 digits. As printed on the card", both!.Help);
        Assert.Null(repeated!.Help);
    }

    [Theory]
    [InlineData("billingPostalCode", "billingPostalCode")]
    [InlineData("Field-numberInput", "Field-numberInput")]
    [InlineData("15", null)]                                       // Win32 control id
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", null)]     // generated
    [InlineData("  ", null)]
    public void Identifier(string automationId, string? expected)
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", AutomationId: automationId));
        Assert.Equal(expected, field!.Identifier);
    }

    [Fact]
    public void LongStringsAreCutAt200()
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts("Edit", Name: new string('a', 500)));
        Assert.Equal(200, field!.Label!.Length);
    }

    [Theory]
    [InlineData("multiline=true;required=false", "multiline", "true")]
    [InlineData("Multiline=TRUE", "multiline", "TRUE")]
    [InlineData(@"placeholder=a\;b\=c\\d;required=true", "placeholder", @"a;b=c\d")]
    [InlineData(" placeholder = MM / YY ; busy=false", "placeholder", "MM / YY")]
    public void AriaPropertiesParse(string text, string key, string expected)
    {
        Assert.Equal(expected, TakeFieldDescriptor.ParseAriaProperties(text)[key]);
    }

    [Fact]
    public void AriaPropertiesWithoutPairs()
    {
        Assert.Empty(TakeFieldDescriptor.ParseAriaProperties(null));
        Assert.Empty(TakeFieldDescriptor.ParseAriaProperties(""));
        Assert.Empty(TakeFieldDescriptor.ParseAriaProperties(";;"));
        Assert.Empty(TakeFieldDescriptor.ParseAriaProperties("noequals"));
    }

    [Fact]
    public void JsonUsesTheServerKeysAndLeavesOutWhatIsUnknown()
    {
        var field = TakeFieldDescriptor.From(new UiaFieldFacts(
            ControlType: "Edit", LocalizedControlType: "telephone", Name: "Mobile", HelpText: "+30 69…",
            AutomationId: "phone", AriaProperties: "multiline=false", FrameworkId: "Chrome"))!;

        using var json = JsonDocument.Parse(field.ToJson());
        var keys = json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "identifier", "input_type", "label", "multiline", "placeholder", "role", "role_description" }, keys);
        Assert.Equal("tel", json.RootElement.GetProperty("input_type").GetString());
        Assert.False(json.RootElement.GetProperty("multiline").GetBoolean());
        Assert.Equal("+30 69…", json.RootElement.GetProperty("placeholder").GetString());
    }

    [Fact]
    public void JsonInsideAMessageKeepsTheServerKeys()
    {
        // The WebSocket start carries the field as an object inside a dictionary.
        var field = new TakeField { Role = "Edit", Label = "ZIP" };
        var message = JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = "start", ["field"] = field });

        Assert.Equal("{\"type\":\"start\",\"field\":{\"role\":\"Edit\",\"label\":\"ZIP\"}}", message);
    }
}
