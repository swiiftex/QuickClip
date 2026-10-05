using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickClip.Controls;

/// <summary>
/// Native child window that mpv renders into. mpv creates its own window inside this one; we disable
/// that window so Windows routes its mouse input here instead (disabled children forward to the parent).
/// Coordinates in the mouse events are device pixels relative to this window.
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "QuickClipVideoHost";
    private static WndProcDelegate? _defProc;
    private static bool _classRegistered;

    public event Action<int, int>? VideoMouseDown;
    public event Action<int, int>? VideoMouseMove;
    public event Action<int, int>? VideoMouseUp;

    /// <summary>Asked on WM_SETCURSOR with the cursor position; return a Win32 IDC_* id or 0 for the arrow.</summary>
    public Func<int, int, int>? CursorQuery { get; set; }

    public IntPtr Hwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        Hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the video window.");
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    /// <summary>Disables mpv's child window(s) so their mouse input reaches us.</summary>
    public void DisableChildWindows()
    {
        if (Hwnd == IntPtr.Zero) return;
        EnumChildWindows(Hwnd, (child, _) =>
        {
            if (IsWindowEnabled(child)) EnableWindow(child, false);
            return true;
        }, IntPtr.Zero);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                SetCapture(hwnd);
                VideoMouseDown?.Invoke(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                VideoMouseMove?.Invoke(x, y);
                ApplyCursor(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                ReleaseCapture();
                VideoMouseUp?.Invoke(x, y);
                handled = true;
                return IntPtr.Zero;
            case WM_SETCURSOR:
                if (GetCursorPos(out var pt) && ScreenToClient(hwnd, ref pt))
                {
                    ApplyCursor(pt.X, pt.Y);
                    handled = true;
                    return new IntPtr(1);
                }
                break;
            case WM_ERASEBKGND:
                handled = true;
                return new IntPtr(1);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void ApplyCursor(int x, int y)
    {
        int id = CursorQuery?.Invoke(x, y) ?? 0;
        SetCursor(LoadCursor(IntPtr.Zero, id == 0 ? IDC_ARROW : id));
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _defProc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_defProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already exists */)
            throw new InvalidOperationException("RegisterClassEx failed.");
        _classRegistered = true;
    }

    public const int IDC_ARROW = 32512, IDC_CROSS = 32515, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643,
        IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646, IDC_HAND = 32649;

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int BLACK_BRUSH = 4;
    private const int WM_SETCURSOR = 0x0020, WM_ERASEBKGND = 0x0014, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
