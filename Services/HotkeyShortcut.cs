namespace Coworker.Services;

/// Modifier bits; the values match RegisterHotKey's MOD_* flags.
[Flags]
public enum ShortcutModifiers : uint
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A user-configurable shortcut in its stored text form: "Ctrl+Shift" (hold the modifiers alone) or
/// "Ctrl+Shift+D" (modifiers plus one key). Like the Mac's dictationHotkey settings, a shortcut always
/// has at least one modifier: a bare key would hijack that key in every app.
/// </summary>
public readonly record struct HotkeyShortcut(ShortcutModifiers Modifiers, uint Vk)
{
    public static readonly HotkeyShortcut None = new(ShortcutModifiers.None, 0);

    private const uint VkEscape = 0x1B;

    private static readonly (string Name, uint Vk)[] KeyTable = BuildKeyTable();
    private static readonly Dictionary<string, uint> VkByName = KeyTable.ToDictionary(k => k.Name, k => k.Vk, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, string> NameByVk = KeyTable.ToDictionary(k => k.Vk, k => k.Name);

    // Older settings files spelled some keys differently.
    private static readonly Dictionary<string, uint> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Del"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Escape"] = 0x1B,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Return"] = 0x0D,
    };

    public bool IsModifierOnly => Vk == 0 && Modifiers != ShortcutModifiers.None;

    /// Usable as a dictation shortcut: at least one modifier, and not a chord Windows or every app owns.
    public bool IsValid => Modifiers != ShortcutModifiers.None && !IsReserved;

    private bool IsReserved => Vk != 0 && (
        (Modifiers == ShortcutModifiers.Ctrl && Vk is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A) ||   // Ctrl+A/C/V/X/Y/Z
        (Modifiers == ShortcutModifiers.Alt && Vk == 0x73) ||                                              // Alt+F4
        (Modifiers == ShortcutModifiers.Win && Vk == 0x4C) ||                                              // Win+L
        Vk == VkEscape);

    public static bool TryParse(string? text, out HotkeyShortcut shortcut)
    {
        shortcut = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ShortcutModifiers.None;
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Ctrl;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Alt;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Win;
            else if (vk == 0 && (VkByName.TryGetValue(part, out var named) || Aliases.TryGetValue(part, out named)))
                vk = named;
            else
                return false;
        }

        shortcut = new HotkeyShortcut(modifiers, vk);
        return modifiers != ShortcutModifiers.None || vk != 0;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");
        if (Vk != 0) parts.Add(KeyName(Vk) ?? $"0x{Vk:X2}");
        return string.Join("+", parts);
    }

    /// The stored name of a virtual key, or null when it cannot be part of a shortcut.
    public static string? KeyName(uint vk) => NameByVk.GetValueOrDefault(vk);

    /// True for the Ctrl, Shift, Alt and Win keys (either side).
    public static bool IsModifierKey(uint vk) =>
        vk is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    /// The modifier a key press stands for, or None.
    public static ShortcutModifiers ModifierOf(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ShortcutModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => ShortcutModifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => ShortcutModifiers.Alt,
        0x5B or 0x5C => ShortcutModifiers.Win,
        _ => ShortcutModifiers.None,
    };

    private static (string, uint)[] BuildKeyTable()
    {
        var keys = new List<(string, uint)>();
        for (var c = 'A'; c <= 'Z'; c++) keys.Add((c.ToString(), c));
        for (var c = '0'; c <= '9'; c++) keys.Add((c.ToString(), c));
        for (uint f = 1; f <= 24; f++) keys.Add(($"F{f}", 0x6F + f));
        for (uint n = 0; n <= 9; n++) keys.Add(($"Num{n}", 0x60 + n));
        keys.AddRange(new (string, uint)[]
        {
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Backspace", 0x08), ("Delete", 0x2E), ("Esc", 0x1B),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Home", 0x24), ("End", 0x23), ("PgUp", 0x21), ("PgDn", 0x22), ("Ins", 0x2D),
            ("PrintScreen", 0x2C), ("Pause", 0x13), ("ScrollLock", 0x91),
            ("NumMultiply", 0x6A), ("NumAdd", 0x6B), ("NumSubtract", 0x6D), ("NumDecimal", 0x6E), ("NumDivide", 0x6F),
            (";", 0xBA), ("=", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF), ("`", 0xC0),
            ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE),
        });
        return keys.ToArray();
    }
}
