using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickClip.Recording;
using QuickClip.Updates;

namespace QuickClip.Shell;

/// <summary>
/// The part of QuickClip that lives while no window is open: a hidden window that owns the tray icon and
/// the global "save clip" hotkey, and drives the recorder.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private const int HotkeyId = 1;
    private const int WM_HOTKEY = 0x0312;
    private const int TrayCallback = 0x8000 + 1; // WM_APP + 1
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private HwndSource? _window;
    private IntPtr _icon;
    private bool _iconAdded;
    private DispatcherTimer? _updateTimer;
    private bool _updateBalloonShown;   // the last notification was about an update
    private AppVersion? _notifiedVersion;

    public event Action? OpenRequested;
    /// <summary>The user clicked the "update available" notification.</summary>
    public event Action? UpdateRequested;
    public event Action? QuitRequested;

    /// <summary>True if the configured shortcut is registered (false if another app already uses it).</summary>
    public bool HotkeyActive { get; private set; }

    public void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("QuickClip host")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
        });
        _window.AddHook(WndProc);
        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out _icon, 1);
        AddTrayIcon();
        RegisterHotkey();

        var recorder = Recorder.Instance;
        recorder.ClipSaved += clip => _dispatcher.BeginInvoke(() => OnClipSaved(clip));
        recorder.SaveFailed += error => _dispatcher.BeginInvoke(() => Notify("Clip not saved", error));
        recorder.StateChanged += _ => _dispatcher.BeginInvoke(UpdateTooltip);
        _ = recorder.ApplySettingsAsync();

        // Look for a new release shortly after start (not during it), then once a day while running.
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(24);
            if (AppSettings.Current.CheckForUpdates) await CheckForUpdateAsync();
        }, _dispatcher);
    }

    private async Task CheckForUpdateAsync()
    {
        var release = await Updater.CheckAsync();
        if (release == null || release.Version == _notifiedVersion) return;
        _notifiedVersion = release.Version;
        Notify("Update available", $"QuickClip {release.Version.Display} is ready to install. Click to see what's new.");
        _updateBalloonShown = true;
    }

    /// <summary>Re-reads the shortcut from settings. Returns false if it's taken.</summary>
    public bool RegisterHotkey()
    {
        if (_window == null) return false;
        UnregisterHotKey(_window.Handle, HotkeyId);
        var s = AppSettings.Current;
        HotkeyActive = s.HotkeyKey != 0 && RegisterHotKey(_window.Handle, HotkeyId, (uint)s.HotkeyModifiers | 0x4000 /* MOD_NOREPEAT */, (uint)s.HotkeyKey);
        if (!HotkeyActive && s.HotkeyKey != 0)
            Log.Write($"Couldn't register the shortcut {new Hotkey(s.HotkeyModifiers, s.HotkeyKey)} (in use by another app?)");
        UpdateTooltip();
        return HotkeyActive;
    }

    /// <summary>Lets the settings page capture key presses without triggering a save.</summary>
    public void SuspendHotkey()
    {
        if (_window != null) UnregisterHotKey(_window.Handle, HotkeyId);
        HotkeyActive = false;
    }

    public void Notify(string title, string text)
    {
        _updateBalloonShown = false;
        var data = NewIconData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void OnClipSaved(SavedClip clip)
    {
        if (AppSettings.Current.ClipSound) PlaySound("SystemAsterisk", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
        string length = RecordingPresets.DurationLabel(clip.Seconds);
        Notify("Clip saved", clip.Folder == "Desktop" ? $"Last {length}" : $"{clip.Folder} · last {length}");
    }

    private void UpdateTooltip()
    {
        if (!_iconAdded) return;
        var r = Recorder.Instance;
        string state = r.IsRunning
            ? $"Recording · {new Hotkey(AppSettings.Current.HotkeyModifiers, AppSettings.Current.HotkeyKey)} saves a clip"
            : r.Error != null ? "Not recording: " + r.Error : "Recording is off";
        var data = NewIconData();
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        data.szTip = Truncate("QuickClip — " + state, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddTrayIcon()
    {
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _icon;
        data.szTip = "QuickClip";
        _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4: event in LOWORD(lParam)
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        UpdateTooltip();
    }

    private NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window?.Handle ?? IntPtr.Zero,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _ = Recorder.Instance.SaveClipAsync();
            handled = true;
        }
        else if (msg == TrayCallback)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case 0x0202: // WM_LBUTTONUP
                    OpenRequested?.Invoke();
                    break;
                case 0x0405: // NIN_BALLOONUSERCLICK
                    if (_updateBalloonShown) UpdateRequested?.Invoke();
                    else OpenRequested?.Invoke();
                    break;
                case 0x007B: // WM_CONTEXTMENU
                case 0x0205: // WM_RBUTTONUP
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated)
        {
            AddTrayIcon(); // Explorer restarted
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var recorder = Recorder.Instance;
        var s = AppSettings.Current;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Open QuickClip", () => OpenRequested?.Invoke(), bold: true));
        menu.Items.Add(Item($"Save clip ({new Hotkey(s.HotkeyModifiers, s.HotkeyKey)})", () => _ = recorder.SaveClipAsync(), enabled: recorder.IsRunning));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(s.RecordingEnabled ? "Turn recording off" : "Turn recording on", () =>
        {
            s.RecordingEnabled = !s.RecordingEnabled;
            s.Save();
            _ = recorder.ApplySettingsAsync();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", () => QuitRequested?.Invoke()));
        if (_window != null) SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
        menu.IsOpen = true;
    }

    private static MenuItem Item(string text, Action action, bool bold = false, bool enabled = true)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _updateTimer?.Stop();
        if (_window == null) return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.Dispose();
        _window = null;
    }

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_INFO = 0x1, NIIF_NOSOUND = 0x10;
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_ALIAS = 0x10000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
