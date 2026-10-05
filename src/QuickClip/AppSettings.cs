using System.Text.Json;
using QuickClip.Recording;

namespace QuickClip;

/// <summary>User preferences persisted to %APPDATA%\QuickClip\settings.json.</summary>
internal sealed class AppSettings
{
    public string Format { get; set; } = "source";
    public string VideoMode { get; set; } = "encode";
    public string Encoder { get; set; } = "";
    public string Quality { get; set; } = "high";
    public double TargetSizeMB { get; set; } = 25;
    public int Resolution { get; set; }
    public double Fps { get; set; }
    public int AudioBitrate { get; set; } = 192;
    public bool MergeAudio { get; set; }
    public bool Loop { get; set; } = true;
    public string? LastFolder { get; set; }

    // Recording (instant replay)
    public bool RecordingEnabled { get; set; } = true;
    public string RecordMonitor { get; set; } = "";             // GDI device name; empty = main monitor
    public int RecordFps { get; set; } = 60;
    public int RecordResolution { get; set; }                   // short side; 0 = native
    public RecordingQuality RecordQuality { get; set; } = RecordingQuality.High;
    public string RecordEncoder { get; set; } = "";             // empty = newest GPU codec available
    public int BufferSeconds { get; set; } = 60;
    public bool CaptureCursor { get; set; } = true;
    public bool SplitChatAudio { get; set; } = true;
    public bool RecordMic { get; set; } = true;
    public string MicDeviceId { get; set; } = "";               // empty = Windows default
    public int HotkeyModifiers { get; set; } = 1;               // MOD_ALT
    public int HotkeyKey { get; set; } = 0x79;                  // F10
    public bool ClipSound { get; set; } = true;
    public string ClipsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "QuickClip");

    // Window behaviour
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;

    public string? EncoderCacheKey { get; set; }
    public List<string>? EncoderCache { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickClip", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    /// <summary>Debug runs that point QuickClip at test folders must not overwrite the real settings.</summary>
    internal static bool SaveDisabled { get; set; }

    public void Save()
    {
        if (SaveDisabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (JsonOptions)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }
}
