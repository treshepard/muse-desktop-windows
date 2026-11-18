using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// User preferences that are not window geometry (that lives in
/// window.json): the global hotkeys today. Stored as JSON in the
/// app-data folder and written only when the user changes something.
/// </summary>
internal sealed class AppSettings
{
    // Global hotkeys stay off until the user turns them on: grabbing
    // a key combination system-wide is not something to do by default.
    public bool HotkeysEnabled { get; set; }

    // Each hotkey is one string: optional modifiers (Ctrl, Alt, Shift,
    // Win) joined with '+' ahead of a key name, e.g. "Ctrl+Alt+F9"
    // or just "F9". Key names are System.Windows.Forms.Keys names.
    public string BringToFrontHotkey { get; set; } = "F9";
    public string MuteHotkey { get; set; } = "F10";

    // Start on login stays off until the user turns it on: the app
    // otherwise keeps no persistence at all, and that only changes
    // at the user's own request.
    public bool StartOnLogin { get; set; }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing a preference must never take the app down.
        }
    }
}

/// <summary>
/// Parsing and registration glue for one global hotkey combination.
/// </summary>
internal static class GlobalHotkey
{
    // user32 modifier flags (MOD_NOREPEAT keeps a held key from
    // firing the action repeatedly).
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Parses "Ctrl+Alt+F9" style text into RegisterHotKey arguments.
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = ModNoRepeat;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse<Keys>(parts[^1], ignoreCase: true, out var key))
        {
            return false;
        }
        // Only plain keys, not the modifier entries of the Keys enum.
        if (key is Keys.Control or Keys.Alt or Keys.Shift or Keys.LWin or Keys.RWin
            or Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
        {
            return false;
        }
        virtualKey = (uint)key;
        return true;
    }

    public static bool Register(IntPtr window, int id, string combination)
    {
        return TryParse(combination, out var modifiers, out var virtualKey)
            && RegisterHotKey(window, id, modifiers, virtualKey);
    }

    public static void Unregister(IntPtr window, int id)
    {
        try { UnregisterHotKey(window, id); } catch { }
    }
}
