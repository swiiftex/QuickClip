using System.Globalization;
using System.Runtime.InteropServices;
using static QuickClip.Mpv.MpvNative;

namespace QuickClip.Mpv;

/// <summary>
/// Thin libmpv wrapper. mpv renders into a child window of <c>wid</c>; events are raised on a
/// background thread, so subscribers must marshal to the UI thread themselves.
/// </summary>
internal sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private readonly Thread _eventThread;
    private volatile bool _disposing;

    /// <summary>Raised on the mpv event thread. Value is double, bool, long, string or null.</summary>
    public event Action<string, object?>? PropertyChanged;

    /// <summary>Raised on the mpv event thread for lifecycle events (file-loaded, end-file, ...).</summary>
    public event Action<MpvEventId, int>? EventReceived;

    /// <param name="wid">Window to render into, or zero for no embedding.</param>
    /// <param name="overrides">Options applied after the defaults (used by tests for headless playback).</param>
    public MpvPlayer(IntPtr wid, IReadOnlyDictionary<string, string>? overrides = null)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create failed.");

        // mpv wants the HWND as an unsigned 32-bit value.
        if (wid != IntPtr.Zero)
            SetOption("wid", ((uint)wid.ToInt64()).ToString(CultureInfo.InvariantCulture));
        SetOption("vo", "gpu-next");
        SetOption("hwdec", "auto-safe");
        SetOption("force-window", "yes");
        SetOption("idle", "yes");
        SetOption("keep-open", "always");
        SetOption("pause", "yes");
        SetOption("hr-seek", "yes");
        SetOption("hr-seek-framedrop", "no");
        SetOption("audio-display", "no");
        SetOption("osc", "no");
        SetOption("osd-bar", "no");
        SetOption("osd-on-seek", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("input-cursor", "no");
        SetOption("cursor-autohide", "no");
        SetOption("drag-and-drop", "no");
        SetOption("load-scripts", "no");
        SetOption("ytdl", "no");
        SetOption("background-color", "#000000");
        SetOption("demuxer-max-back-bytes", "200MiB");
        if (overrides != null)
            foreach (var (name, value) in overrides)
                SetOption(name, value);

        Check(mpv_initialize(_ctx), "mpv_initialize");
        mpv_request_log_messages(_ctx, "warn");

        Observe("time-pos", MpvFormat.Double);
        Observe("duration", MpvFormat.Double);
        Observe("pause", MpvFormat.Flag);
        Observe("eof-reached", MpvFormat.Flag);
        Observe("idle-active", MpvFormat.Flag);
        Observe("osd-dimensions", MpvFormat.None);
        Observe("video-out-params", MpvFormat.None);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();
    }

    public void SetOption(string name, string value)
    {
        int r = mpv_set_option_string(_ctx, name, value);
        if (r < 0) Log.Write($"mpv option {name}={value} failed: {ErrorString(r)}");
    }

    public bool SetProperty(string name, string value)
    {
        if (_ctx == IntPtr.Zero) return false;
        int r = mpv_set_property_string(_ctx, name, value);
        if (r < 0) Log.Write($"mpv set {name}={Truncate(value)} failed: {ErrorString(r)}");
        return r >= 0;
    }

    public bool SetProperty(string name, double value) => SetProperty(name, value.ToString("0.######", CultureInfo.InvariantCulture));

    public bool SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");

    public double? GetDouble(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        return mpv_get_property(_ctx, name, MpvFormat.Double, out double v) >= 0 ? v : null;
    }

    public string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        IntPtr p = mpv_get_property_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { mpv_free(p); }
    }

    /// <summary>Runs a command synchronously. Keep these short; the UI thread waits for mpv's core.</summary>
    public bool Command(params string[] args) => RunCommand(args, async: false);

    /// <summary>Like <see cref="Command"/>, but an expected failure isn't logged.</summary>
    public bool TryCommand(params string[] args) => RunCommand(args, async: false, logFailure: false);

    /// <summary>Queues a command without waiting for it to finish.</summary>
    public bool CommandAsync(params string[] args) => RunCommand(args, async: true);

    private bool RunCommand(string[] args, bool async, bool logFailure = true)
    {
        if (_ctx == IntPtr.Zero) return false;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            int r = async ? mpv_command_async(_ctx, 0, ptrs) : mpv_command(_ctx, ptrs);
            if (r < 0 && logFailure) Log.Write($"mpv command '{args[0]}' failed: {ErrorString(r)}");
            return r >= 0;
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    private void Observe(string name, MpvFormat format) => mpv_observe_property(_ctx, 0, name, format);

    private void EventLoop()
    {
        while (!_disposing)
        {
            IntPtr evPtr = mpv_wait_event(_ctx, -1);
            if (_disposing) break;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            try
            {
                switch (ev.EventId)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.PropertyChange:
                        HandlePropertyChange(ev);
                        break;
                    case MpvEventId.LogMessage:
                        var msg = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                        Log.Write($"mpv [{Marshal.PtrToStringUTF8(msg.Prefix)}] {Marshal.PtrToStringUTF8(msg.Text)?.TrimEnd()}");
                        break;
                    case MpvEventId.EndFile:
                        var end = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        EventReceived?.Invoke(ev.EventId, end.Reason == 4 ? end.Error : 0); // 4 = MPV_END_FILE_REASON_ERROR
                        break;
                    case MpvEventId.CommandReply:
                        if (ev.Error < 0) Log.Write($"mpv async command failed: {ErrorString(ev.Error)}");
                        break;
                    default:
                        EventReceived?.Invoke(ev.EventId, ev.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("mpv event handler failed: " + ex);
            }
        }
    }

    private void HandlePropertyChange(MpvEvent ev)
    {
        var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
        string name = Marshal.PtrToStringUTF8(prop.Name) ?? "";
        object? value = prop.Format switch
        {
            MpvFormat.Double => Marshal.PtrToStructure<double>(prop.Data),
            MpvFormat.Flag => Marshal.ReadInt32(prop.Data) != 0,
            MpvFormat.Int64 => Marshal.ReadInt64(prop.Data),
            MpvFormat.String => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)),
            _ => null,
        };
        PropertyChanged?.Invoke(name, value);
    }

    private static void Check(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"{what} failed: {ErrorString(r)}");
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "..." : s;

    /// <summary>
    /// Shuts mpv down. Call this off the UI thread: mpv destroys its child window during teardown,
    /// which needs the parent's thread to keep pumping messages.
    /// </summary>
    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        _disposing = true;
        mpv_wakeup(_ctx);
        _eventThread.Join(2000);
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        mpv_terminate_destroy(ctx);
    }
}
