using System.Runtime.InteropServices;

namespace QuickClip.Recording;

/// <summary>Cheap snapshot of running processes (name + parent), used for chat-app and game detection.</summary>
internal static class ProcessScan
{
    public readonly record struct Entry(uint Pid, uint ParentPid, string ExeName);

    /// <summary>
    /// The running processes. A process keeps its parent's id after the parent exits, and Windows can give that id
    /// to a new process; a parent that started after the child is such a stranger, so the link is dropped (the
    /// parent is 0). Otherwise a game whose launcher had exited could pass for a child of a browser tab.
    /// </summary>
    public static List<Entry> Snapshot()
    {
        var list = new List<Entry>(400);
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add(new Entry(e.th32ProcessID, e.th32ParentProcessID, e.szExeFile));
        }
        finally
        {
            CloseHandle(snap);
        }
        var started = new Dictionary<uint, long>(list.Count);
        foreach (var p in list) started[p.Pid] = StartTime(p.Pid);
        for (int i = 0; i < list.Count; i++)
            if (list[i].ParentPid != 0 && IsStranger(started.GetValueOrDefault(list[i].Pid), started.GetValueOrDefault(list[i].ParentPid, -1)))
                list[i] = list[i] with { ParentPid = 0 };
        return list;
    }

    /// <summary>Whether a process that started at <paramref name="parent"/> can't be the parent of one that started at
    /// <paramref name="child"/> (0 = unknown, -1 = no such process, both counting as possible).</summary>
    internal static bool IsStranger(long child, long parent) => child > 0 && parent > 0 && parent > child;

    /// <summary>When the process started (FILETIME), or 0 when that can't be read.</summary>
    private static long StartTime(uint pid)
    {
        if (pid == 0) return 0;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(h, out long creation, out _, out _, out _) ? creation : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static string? ExePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [Out] char[] name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}

/// <summary>A running app that gets its own audio track: its root process and its name.</summary>
internal sealed record RunningApp(uint Pid, string Name);

internal static class AppFinder
{
    /// <summary>Root process of each running app (the one whose parent isn't the same app), in list order.</summary>
    public static List<RunningApp> Find(IReadOnlyList<ProcessScan.Entry> processes, IEnumerable<string> executables)
    {
        var byPid = processes.ToDictionary(p => p.Pid);
        var result = new List<RunningApp>();
        foreach (var name in executables)
        {
            string exe = name + ".exe";
            foreach (var p in processes)
            {
                if (!string.Equals(p.ExeName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                bool childOfSame = byPid.TryGetValue(p.ParentPid, out var parent) && string.Equals(parent.ExeName, exe, StringComparison.OrdinalIgnoreCase);
                if (!childOfSame) result.Add(new RunningApp(p.Pid, name));
            }
        }
        return result;
    }
}

/// <summary>Voice/chat apps whose audio goes on the Chat track.</summary>
internal static class ChatApps
{
    /// <summary>Executable names (without .exe), most common first.</summary>
    public static readonly string[] Executables =
    [
        "Discord", "DiscordPTB", "DiscordCanary", "Vesktop", "Legcord", "ArmCord",
        "TeamSpeak", "ts3client_win64", "ts3client_win32", "Mumble", "Ventrilo",
        "ms-teams", "Teams", "Zoom", "Skype", "slack", "Guilded", "Element", "Signal", "WhatsApp", "Telegram", "Revolt",
    ];

    public static List<RunningApp> Find(IReadOnlyList<ProcessScan.Entry> processes) => AppFinder.Find(processes, Executables);
}

/// <summary>Music apps whose audio goes on the Music track. Music played in a browser stays on Desktop.</summary>
internal static class MusicApps
{
    /// <summary>Executable names (without .exe), most common first.</summary>
    public static readonly string[] Executables =
    [
        "Spotify", "TIDAL", "AppleMusic", "iTunes", "Amazon Music", "Deezer", "YouTube Music", "Qobuz", "Cider", "Plexamp",
        "foobar2000", "MusicBee", "AIMP", "winamp", "Strawberry", "Audacious",
    ];

    public static List<RunningApp> Find(IReadOnlyList<ProcessScan.Entry> processes) => AppFinder.Find(processes, Executables);
}
