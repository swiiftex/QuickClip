using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace QuickClip.Shell;

/// <summary>
/// Copies a file to the clipboard the way Explorer's Copy does: the shell's own data object for it, which QuickClip
/// keeps serving (apps can ask it for the file's contents as well as its path) until something else is copied or
/// QuickClip quits.
/// </summary>
internal static class ShellClipboard
{
    private const int ClipboardBusy = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN
    private static ComDataObject? _current;

    /// <summary>Call on the UI thread (OLE's clipboard needs an STA thread that pumps messages).</summary>
    public static void CopyFile(string path)
    {
        OleInitialize(IntPtr.Zero); // WPF only does this once something uses OLE; already done is fine
        var shellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        var dataObjectHandler = new Guid("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4"); // BHID_DataObject
        var dataObject = new Guid("0000010e-0000-0000-c000-000000000046");
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItem, out var item);
        try
        {
            var data = (ComDataObject)item.BindToHandler(IntPtr.Zero, ref dataObjectHandler, ref dataObject);
            SetPreferredDropEffect(data, 1); // DROPEFFECT_COPY: pasting copies the file rather than moving it
            Retry(() => OleSetClipboard(data));
            _current = data;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// Before quitting: if the clipboard still holds QuickClip's file, leaves a copy of it there that doesn't need
    /// QuickClip running (the path and file list; apps then read the file itself).
    /// </summary>
    public static void KeepAfterExit()
    {
        if (_current == null) return;
        try
        {
            if (OleIsCurrentClipboard(_current) == 0) Retry(OleFlushClipboard);
        }
        catch (Exception ex)
        {
            Log.Write("Leaving the shared clip on the clipboard failed: " + ex.Message);
        }
        _current = null;
    }

    // Clipboard watchers (Windows' clipboard history, for one) open the clipboard the moment it changes.
    private static void Retry(Func<int> call)
    {
        for (int attempt = 1; ; attempt++)
        {
            int hr = call();
            if (hr >= 0) return;
            if (hr != ClipboardBusy || attempt == 20) Marshal.ThrowExceptionForHR(hr);
            Thread.Sleep(50);
        }
    }

    private static void SetPreferredDropEffect(ComDataObject data, int effect)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat("Preferred DropEffect"),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };
        IntPtr memory = GlobalAlloc(0x0002, sizeof(int)); // GMEM_MOVEABLE
        Marshal.WriteInt32(GlobalLock(memory), effect);
        GlobalUnlock(memory);
        var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = memory };
        data.SetData(ref format, ref medium, true); // the data object frees it
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(ComDataObject data);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(ComDataObject data);
    [DllImport("ole32.dll")] private static extern int OleFlushClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
}
