using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickClip.Shell;

/// <summary>Adds/removes the "Edit with QuickClip" entry in Explorer's right-click menu (per user, no admin needed).</summary>
internal static class ShellIntegration
{
    private const string VerbName = "QuickClip";
    private const string MenuText = "Edit with QuickClip";

    public static readonly string[] Extensions =
    [
        // video
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".asf", ".webm", ".flv", ".f4v", ".ts", ".m2ts", ".mts", ".m2t",
        ".mpg", ".mpeg", ".m2v", ".vob", ".3gp", ".3g2", ".ogv", ".mxf", ".divx", ".rm", ".rmvb", ".gif", ".nut", ".y4m",
        // audio
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".ac3", ".eac3", ".dts",
        ".mka", ".amr", ".ape", ".wv", ".caf", ".alac", ".mp2", ".m4b",
    ];

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QuickClip.exe");

    // The verb hangs off "all files" and is filtered to media extensions: per-extension SystemFileAssociations
    // verbs were missing from Explorer's menu when .mp4 opened with the Media Player app, "*" verbs always show.
    private const string VerbKey = $@"Software\Classes\*\shell\{VerbName}";

    private static string LegacyKeyPath(string ext) => $@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbName}";

    public static string AppliesTo => string.Join(" OR ", Extensions.Select(e => $"System.FileExtension:=\"{e}\""));

    /// <summary>
    /// Family name of the sparse package (packaging/AppxManifest.xml) that puts the entry on Windows 11's main
    /// menu. The suffix is derived from the publisher "CN=QuickClip Local".
    /// </summary>
    public const string MenuPackageFamily = "QuickClip.ContextMenu_qvamj0bx9jvkt";

    /// <summary>True when install.ps1 registered the Windows 11 menu package for this user.</summary>
    public static bool IsMenuPackageInstalled()
    {
        uint count = 0, length = 0;
        GetPackagesByPackageFamily(MenuPackageFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        return count > 0;
    }

    public static bool IsRegistered()
    {
        if (IsMenuPackageInstalled()) return true;
        using var key = Registry.CurrentUser.OpenSubKey(VerbKey + @"\command");
        var cmd = key?.GetValue(null) as string;
        return cmd != null && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the classic menu entry, unless the menu package already provides one (it shows in both menus).</summary>
    public static void Register()
    {
        string exe = ExePath;
        RemoveLegacyKeys();
        if (IsMenuPackageInstalled())
        {
            Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);
            NotifyShell();
            return;
        }
        using (var verb = Registry.CurrentUser.CreateSubKey(VerbKey))
        {
            verb.SetValue(null, MenuText);
            verb.SetValue("MUIVerb", MenuText);
            verb.SetValue("Icon", $"\"{exe}\",0");
            verb.SetValue("AppliesTo", AppliesTo);
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);
        RemoveLegacyKeys();
        NotifyShell();
    }

    /// <summary>Earlier versions registered one verb per extension.</summary>
    private static void RemoveLegacyKeys()
    {
        foreach (var ext in Extensions)
            Registry.CurrentUser.DeleteSubKeyTree(LegacyKeyPath(ext), throwOnMissingSubKey: false);
    }

    // FLUSH waits until Explorer has processed the change; otherwise it can be lost when --register exits right away.
    private static void NotifyShell() =>
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x1000 /* SHCNF_IDLIST | SHCNF_FLUSH */, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames,
        ref uint bufferLength, IntPtr buffer);
}

internal static class RecycleBin
{
    /// <summary>Moves a file to the Recycle Bin. Returns false if that failed or Windows would have deleted it permanently.</summary>
    public static bool TrySend(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
