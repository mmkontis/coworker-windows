using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Coworker.E2E;

/// <summary>
/// Notepad, the "other app" Coworker pastes into. The test types into it with real key presses and reads its
/// text back through UI Automation, the way a screen reader would.
/// </summary>
internal sealed class NotepadTarget : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Process? _process;
    private readonly Window _window;
    private readonly AutomationElement _editor;

    private NotepadTarget(UIA3Automation automation, Process? process, Window window, AutomationElement editor)
    {
        _automation = automation;
        _process = process;
        _window = window;
        _editor = editor;
    }

    public static NotepadTarget Launch()
    {
        var automation = new UIA3Automation();
        var process = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        Window? window = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (window == null && DateTime.UtcNow < deadline)
        {
            // Classic Notepad and the Windows 11 app both use the window class "Notepad"; the app may run in
            // another process than the one started, so look on the desktop rather than by process.
            window = automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("Notepad"))?.AsWindow();
            if (window == null) Thread.Sleep(250);
        }
        if (window == null) throw new InvalidOperationException("Notepad's window did not appear");

        var editor = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document).Or(cf.ByControlType(ControlType.Edit)))
            ?? throw new InvalidOperationException("Notepad has no text area in UI Automation");
        Console.WriteLine($"  Notepad: window '{window.Title}', editor {editor.ControlType} '{editor.ClassName}'");
        return new NotepadTarget(automation, process, window, editor);
    }

    public string Text
    {
        get
        {
            var value = _editor.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            value ??= _editor.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1);
            return (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        }
    }

    /// Clicks into Notepad (a real mouse click, so it becomes the foreground window), clears it, types
    /// <paramref name="text"/> and leaves the cursor at the end.
    public void Reset(string text)
    {
        Focus();
        Keyboard.Chord(Keyboard.VK_CONTROL, Keyboard.VK_A);
        Keyboard.Press(Keyboard.VK_DELETE);
        if (text.Length > 0) Keyboard.Type(text);
        Keyboard.Chord(Keyboard.VK_CONTROL, Keyboard.VK_END);
        Thread.Sleep(200);
        if (Text != text) throw new InvalidOperationException($"Notepad holds \"{Text}\" after typing \"{text}\"");
    }

    public void Focus()
    {
        try { _window.SetForeground(); } catch (Exception) { }
        _editor.Click();
        Thread.Sleep(150);
    }

    /// Waits until the text differs from <paramref name="before"/> and then stops changing (a paste that is
    /// checked and retried can land in steps).
    public string WaitForChange(string before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var now = Text;
            if (now != before)
            {
                Thread.Sleep(800);
                var settled = Text;
                if (settled == now) return settled;
                continue;
            }
            Thread.Sleep(200);
        }
        return Text;
    }

    public void Dispose()
    {
        foreach (var notepad in Process.GetProcessesByName("notepad"))
        {
            try { notepad.Kill(); } catch (Exception) { }
        }
        _process?.Dispose();
        _automation.Dispose();
    }
}
