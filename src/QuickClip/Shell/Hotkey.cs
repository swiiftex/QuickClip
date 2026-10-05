using System.Windows.Input;

namespace QuickClip.Shell;

/// <summary>A global keyboard shortcut: Win32 modifier flags + virtual-key code.</summary>
public readonly record struct Hotkey(int Modifiers, int VirtualKey)
{
    public const int Alt = 0x1, Control = 0x2, Shift = 0x4, Windows = 0x8;

    public bool IsEmpty => VirtualKey == 0;

    public static Hotkey FromWpf(ModifierKeys modifiers, Key key)
    {
        int m = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) m |= Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) m |= Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) m |= Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) m |= Windows;
        return new Hotkey(m, KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>Keys that can't be a shortcut on their own.</summary>
    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "None";
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(KeyInterop.KeyFromVirtualKey(VirtualKey)));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + ((int)key - (int)Key.NumPad0),
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        Key.Snapshot => "Print Screen",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        Key.Scroll => "Scroll Lock",
        Key.Pause => "Pause",
        _ => key.ToString(),
    };
}
