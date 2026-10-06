using System.Runtime.InteropServices;

namespace QuickClip.Shell;

/// <summary>
/// QuickClip's entry in the Windows volume mixer: this process's audio session on the default output device.
/// The editor's preview volume is this level, so the slider and the mixer always agree.
/// </summary>
internal static class AppVolume
{
    // Tags our own changes so the change notification can tell them from the mixer's.
    private static readonly Guid OwnChange = Guid.NewGuid();
    private static string? _deviceId;
    private static ISimpleAudioVolume? _volume;
    private static IAudioSessionControl? _control;
    private static SessionEvents? _events;
    private static volatile bool _disconnected;

    /// <summary>
    /// Raised on a background thread when the level or mute is changed outside QuickClip (e.g. in the volume
    /// mixer) or the session goes away; call <see cref="Refresh"/> and <see cref="Read"/> to pick it up.
    /// </summary>
    public static event Action? ChangedElsewhere;

    /// <summary>The level (0 to 1) and mute state, or null when there's no output device.</summary>
    public static (float Level, bool Muted)? Read()
    {
        if (Session() is not { } volume) return null;
        try
        {
            volume.GetMasterVolume(out float level);
            volume.GetMute(out bool muted);
            return (level, muted);
        }
        catch (Exception ex)
        {
            Forget("Reading the app volume failed: " + ex.Message);
            return null;
        }
    }

    public static void SetLevel(float level)
    {
        try
        {
            var context = OwnChange;
            Session()?.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context);
        }
        catch (Exception ex)
        {
            Forget("Setting the app volume failed: " + ex.Message);
        }
    }

    public static void SetMuted(bool muted)
    {
        try
        {
            var context = OwnChange;
            Session()?.SetMute(muted, ref context);
        }
        catch (Exception ex)
        {
            Forget("Muting the app failed: " + ex.Message);
        }
    }

    /// <summary>Follows a change of default output device, or reopens a session that was disconnected.</summary>
    public static void Refresh()
    {
        try
        {
            using var device = DefaultDevice();
            if (_disconnected || device?.Id != _deviceId) Forget(null);
        }
        catch (Exception ex)
        {
            Forget("Checking the output device failed: " + ex.Message);
        }
    }

    private static ISimpleAudioVolume? Session()
    {
        if (_volume != null) return _volume;
        try
        {
            if (OpenSession() is not { } session) return null;
            var events = new SessionEvents();
            session.Control.RegisterAudioSessionNotification(events);
            (_deviceId, _control, _volume, _events) = (session.DeviceId, session.Control, session.Volume, events);
            return session.Volume;
        }
        catch (Exception ex)
        {
            Log.Write("Opening the app's audio session failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>This process's default audio session on the default output device, or null when there's no device.</summary>
    internal static (string? DeviceId, IAudioSessionControl Control, ISimpleAudioVolume Volume)? OpenSession()
    {
        using var device = DefaultDevice();
        if (device == null) return null;
        var iid = typeof(IAudioSessionManager).GUID;
        device.Com.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o);
        var manager = (IAudioSessionManager)o;
        // A null session GUID is the process's default session, the one mpv's output joins.
        manager.GetAudioSessionControl(IntPtr.Zero, 0, out var control);
        manager.GetSimpleAudioVolume(IntPtr.Zero, 0, out var volume);
        return (device.Id, control, volume);
    }

    private static void Forget(string? why)
    {
        if (why != null) Log.Write(why);
        try
        {
            if (_control != null && _events != null) _control.UnregisterAudioSessionNotification(_events);
        }
        catch { }
        (_deviceId, _control, _volume, _events) = (null, null, null, null);
        _disconnected = false;
    }

    private sealed class Device(IMMDevice com) : IDisposable
    {
        public IMMDevice Com { get; } = com;
        public string? Id
        {
            get
            {
                if (Com.GetId(out IntPtr p) < 0) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        public void Dispose() => Marshal.ReleaseComObject(Com);
    }

    private static Device? DefaultDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            // Fails with E_NOTFOUND when no output device is connected.
            return enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) < 0 ? null : new Device(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private sealed class SessionEvents : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(IntPtr name, IntPtr context) => 0;
        public int OnIconPathChanged(IntPtr path, IntPtr context) => 0;
        public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, IntPtr context) => 0;
        public int OnGroupingParamChanged(IntPtr param, IntPtr context) => 0;
        public int OnStateChanged(int state) => 0;

        public int OnSimpleVolumeChanged(float level, int muted, IntPtr context)
        {
            if (context == IntPtr.Zero || Marshal.PtrToStructure<Guid>(context) != OwnChange) ChangedElsewhere?.Invoke();
            return 0;
        }

        public int OnSessionDisconnected(int reason)
        {
            _disconnected = true;
            ChangedElsewhere?.Invoke();
            return 0;
        }
    }

    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const uint ClsctxAll = 0x17;
}

// ---- Core Audio (mmdeviceapi.h, audiopolicy.h) -------------------------------------------------

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr id);
}

[ComImport, Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager
{
    void GetAudioSessionControl(IntPtr sessionGuid, uint crossProcess, out IAudioSessionControl control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint crossProcess, out ISimpleAudioVolume volume);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}

[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(IntPtr name, IntPtr context);
    [PreserveSig] int OnIconPathChanged(IntPtr path, IntPtr context);
    [PreserveSig] int OnSimpleVolumeChanged(float level, int muted, IntPtr context);
    [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, IntPtr context);
    [PreserveSig] int OnGroupingParamChanged(IntPtr param, IntPtr context);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}
