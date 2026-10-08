using System.Runtime.InteropServices;

namespace QuickClip.Recording;

/// <summary>P/Invoke surface of QuickClipCapture.dll (src/QuickClip.Capture). Struct layouts mirror Engine.h.</summary>
internal static class CaptureEngine
{
    private const string Dll = "QuickClipCapture.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public IntPtr MonitorDevice;
        public int Fps;
        public int OutWidth;
        public int OutHeight;
        public int CropX, CropY;
        public int CropWidth, CropHeight;
        public IntPtr Encoder;
        public int VideoKbps;
        public int AudioKbps;
        public int BufferSeconds;
        public int CaptureCursor;
        public int SplitChat;
        public int SplitMusic;
        public int MicEnabled;
        public IntPtr MicDeviceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int Running;
        public int Width;
        public int Height;
        public double Fps;
        public long BufferBytes;
        public double BufferSeconds;
        public long DroppedFrames;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Output
    {
        public int Adapter;
        public int OutputIndex;
        public int Left, Top, Right, Bottom;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AudioDevice
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public int IsDefault;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LogCallback(IntPtr message);

    private static readonly LogCallback LogDelegate = message => Log.Write("[capture] " + Marshal.PtrToStringUTF8(message));
    private static bool _initialized;

    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));

    /// <summary>Points the loader at the bundled FFmpeg DLLs the engine links against. Call before any other method.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        SetDllDirectory(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        qc_set_log(LogDelegate);
    }

    public static IReadOnlyList<Output> EnumOutputs()
    {
        var buffer = new Output[16];
        int n = qc_enum_outputs(buffer, buffer.Length);
        return buffer.Take(Math.Min(n, buffer.Length)).ToList();
    }

    public static IReadOnlyList<AudioDevice> EnumMicrophones()
    {
        var buffer = new AudioDevice[32];
        int n = qc_enum_microphones(buffer, buffer.Length);
        return buffer.Take(Math.Min(n, buffer.Length)).ToList();
    }

    /// <param name="area">The part of the monitor to record.</param>
    /// <param name="outWidth">Size to scale the area to, or 0 for its native size.</param>
    public static string? Start(string monitorDevice, RecordingArea area, int fps, int outWidth, int outHeight, string encoder, int videoKbps,
        int audioKbps, int bufferSeconds, bool cursor, bool splitChat, bool splitMusic, bool mic, string? micDeviceId)
    {
        var strings = new List<IntPtr>();
        IntPtr Uni(string? s) { var p = s == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        try
        {
            var config = new Config
            {
                MonitorDevice = Uni(monitorDevice),
                Fps = fps,
                OutWidth = outWidth,
                OutHeight = outHeight,
                CropX = area.X,
                CropY = area.Y,
                CropWidth = area.Width,
                CropHeight = area.Height,
                Encoder = Marshal.StringToHGlobalAnsi(encoder),
                VideoKbps = videoKbps,
                AudioKbps = audioKbps,
                BufferSeconds = bufferSeconds,
                CaptureCursor = cursor ? 1 : 0,
                SplitChat = splitChat ? 1 : 0,
                SplitMusic = splitMusic ? 1 : 0,
                MicEnabled = mic ? 1 : 0,
                MicDeviceId = Uni(micDeviceId),
            };
            strings.Add(config.Encoder);
            var error = new char[1024];
            return qc_start(ref config, error, error.Length) != 0 ? null : new string(error).TrimEnd('\0');
        }
        finally
        {
            foreach (var p in strings) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    public static void Stop() => qc_stop();

    /// <summary>Which apps each track records; see <see cref="AudioRouting"/> for Desktop.</summary>
    public static void SetAudioProcesses(AudioRouting.Desktop desktop, IReadOnlyList<uint> chat, IReadOnlyList<uint> music) =>
        qc_set_audio_processes(desktop.Exclude, [.. desktop.Include], desktop.Include.Count, [.. chat], chat.Count, [.. music], music.Count);

    public static string? Save(string path, int seconds, string title)
    {
        var error = new char[1024];
        return qc_save(path, seconds, title, error, error.Length) != 0 ? null : new string(error).TrimEnd('\0');
    }

    public static Stats GetStats()
    {
        qc_get_stats(out var stats);
        return stats;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport(Dll)] private static extern void qc_set_log(LogCallback callback);
    [DllImport(Dll)] private static extern int qc_enum_outputs([Out] Output[] outputs, int max);
    [DllImport(Dll)] private static extern int qc_enum_microphones([Out] AudioDevice[] devices, int max);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_start(ref Config config, [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern void qc_stop();
    [DllImport(Dll)] private static extern void qc_set_audio_processes(uint desktopExclude, uint[] desktopInclude, int desktopCount,
        uint[] chat, int chatCount, uint[] music, int musicCount);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_save(string path, int seconds, string title, [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern int qc_get_stats(out Stats stats);
}
