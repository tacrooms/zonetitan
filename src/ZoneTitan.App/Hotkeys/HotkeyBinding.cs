using System.Text;
using System.Windows.Input;
using ZoneTitan.App.Interop;

namespace ZoneTitan.App.Hotkeys;

/// <summary>A parsed hotkey combination such as <c>Ctrl+Alt+G</c>.</summary>
internal sealed record HotkeyBinding(uint Modifiers, uint VirtualKey, string Display)
{
    /// <summary>
    /// Parses strings like "Ctrl+Alt+G" or "Ctrl+Alt+F12". Returns null when the string is
    /// empty or names a key that does not exist — a bad hotkey must surface at bind time
    /// rather than silently never firing (spec §6).
    /// </summary>
    public static HotkeyBinding? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        uint modifiers = 0;
        string? keyPart = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();

            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win" or "windows" or "meta": modifiers |= NativeMethods.MOD_WIN; break;
                default: keyPart = token; break;
            }
        }

        if (keyPart is null)
            return null;

        var key = ParseKey(keyPart);
        if (key is null)
            return null;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        if (vk == 0)
            return null;

        // MOD_NOREPEAT stops a held combination from machine-gunning zone applies.
        return new HotkeyBinding(modifiers | NativeMethods.MOD_NOREPEAT, vk, Format(modifiers, key.Value));
    }

    private static Key? ParseKey(string token)
    {
        if (Enum.TryParse<Key>(token, ignoreCase: true, out var key))
            return key;

        // "1".."9" parse as D1..D9.
        if (token.Length == 1 && char.IsDigit(token[0]))
            return Enum.TryParse<Key>("D" + token, out var digit) ? digit : null;

        return null;
    }

    private static string Format(uint modifiers, Key key)
    {
        var sb = new StringBuilder();

        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) sb.Append("Win+");

        sb.Append(key.ToString().TrimStart('D') is { Length: 1 } digit && key is >= Key.D0 and <= Key.D9
            ? digit
            : key.ToString());

        return sb.ToString();
    }

    /// <summary>Renders as chip glyphs for the UI: <c>⌃ ⌥ G</c> (spec §9).</summary>
    public string ToChipGlyphs()
    {
        var parts = new List<string>();

        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("⌃");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("⌥");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("⇧");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("⊞");

        parts.Add(Display.Split('+').Last());

        return string.Join(" ", parts);
    }
}
