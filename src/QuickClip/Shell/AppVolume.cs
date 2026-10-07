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
        device.Com.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out object o);
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
            return enumerator.GetDefaultAudioEndpoint(CoreAudio.DataFlowRender, CoreAudio.RoleMultimedia, out var device) < 0 ? null : new Device(device);
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
}
