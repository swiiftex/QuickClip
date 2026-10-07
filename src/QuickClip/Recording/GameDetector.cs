using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickClip.Recording;

/// <summary>Works out which game a clip belongs to (null = not a game, filed under "Desktop").</summary>
internal sealed class GameDetector
{
    // Apps that are never games, even when fullscreen.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "QuickClip", "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "mpv", "PotPlayerMini64", "Microsoft.Media.Player", "wmplayer", "Spotify",
        "obs64", "obs32", "Code", "devenv", "rider64", "idea64", "notepad", "notepad++", "WindowsTerminal", "cmd", "powershell", "pwsh",
        "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "RiotClientServices", "RiotClientUx", "EADesktop", "EALauncher",
        "GalaxyClient", "UbisoftConnect", "upc", "Overwolf", "Medal", "XboxPcApp", "GameBar", "ApplicationFrameHost", "SearchHost",
        "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "LockApp", "Photos", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK",
        "Resolve", "CapCut", "Ableton Live 12 Suite", "blender", "Unity", "UnrealEditor", "AdobePremierePro", "Photoshop",
    };

    // Store/launcher install roots: the folder right after one of these is the game's name.
    private static readonly string[] GameRoots =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\EA Games\",
        @"\Ubisoft Game Launcher\games\", @"\XboxGames\", @"\Battle.net\Games\", @"\Rockstar Games\", @"\Games\",
    ];

    private static readonly string[] MeaninglessProductNames =
        ["Unreal Engine", "UnrealGame", "UE4Game", "BootstrapPackagedGame", "Unity", "Microsoft® Windows® Operating System"];

    private HashSet<string> _knownGames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _knownGamesLoaded;
    private string? _lastGame;
    private DateTime _lastGameSeen;

    static GameDetector()
    {
        foreach (var app in ChatApps.Executables.Concat(MusicApps.Executables)) NotGames.Add(app);
    }

    /// <summary>Called every few seconds: remembers the game in the foreground, if any.</summary>
    public void Poll()
    {
        var game = DetectForeground();
        if (game == null) return;
        _lastGame = game;
        _lastGameSeen = DateTime.UtcNow;
    }

    /// <summary>The game a clip ending now belongs to: the foreground game, or the last one played during the clip.</summary>
    public string? GameForClip(int clipSeconds)
    {
        var game = DetectForeground();
        if (game != null) return game;
        return _lastGame != null && DateTime.UtcNow - _lastGameSeen <= TimeSpan.FromSeconds(clipSeconds + 5) ? _lastGame : null;
    }

    private string? DetectForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            string? path = ProcessScan.ExePath(pid);
            if (path == null) return null;
            return Classify(path, IsFullscreen(hwnd), KnownGames(), () => WindowTitle(hwnd));
        }
        catch (Exception ex)
        {
            Log.Write("Game detection failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Game name for an executable, or null if it doesn't look like a game.</summary>
    public static string? Classify(string exePath, bool fullscreen, IReadOnlySet<string> knownGames, Func<string>? windowTitle = null)
    {
        string exe = Path.GetFileNameWithoutExtension(exePath);
        if (NotGames.Contains(exe)) return null;
        string? folderName = GameFolderName(exePath);
        bool known = knownGames.Contains(exePath);
        if (!known && folderName == null && !fullscreen) return null;

        string? name = folderName;
        if (name == null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                foreach (var candidate in new[] { info.ProductName, info.FileDescription })
                    if (!string.IsNullOrWhiteSpace(candidate) && !MeaninglessProductNames.Contains(candidate.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        name = candidate.Trim();
                        break;
                    }
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) name = windowTitle?.Invoke();
        if (string.IsNullOrWhiteSpace(name)) name = exe;
        return SanitizeFolderName(name!);
    }

    private static string? GameFolderName(string path)
    {
        foreach (var root in GameRoots)
        {
            int i = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int start = i + root.Length;
            int end = path.IndexOf('\\', start);
            if (end > start) return path[start..end];
        }
        return null;
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c < 32 ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (clean.Length > 60) clean = clean[..60].Trim();
        return clean.Length == 0 ? "Game" : clean;
    }

    /// <summary>Executables Windows itself has recognised as games (Game Bar's list).</summary>
    private IReadOnlySet<string> KnownGames()
    {
        if (DateTime.UtcNow - _knownGamesLoaded < TimeSpan.FromMinutes(10)) return _knownGames;
        _knownGamesLoaded = DateTime.UtcNow;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children != null)
                foreach (var name in children.GetSubKeyNames())
                {
                    using var child = children.OpenSubKey(name);
                    if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) set.Add(exe);
                }
        }
        catch { }
        return _knownGames = set;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        if ((GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false; // a maximised normal window
        if (!GetWindowRect(hwnd, out var rect)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref info)) return false;
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new char[256];
        int n = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, n));
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, [Out] char[] text, int max);
}
