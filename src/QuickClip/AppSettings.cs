using System.Text.Json;
using System.Text.Json.Serialization;
using QuickClip.Recording;

namespace QuickClip;

/// <summary>User preferences persisted to %APPDATA%\QuickClip\settings.json.</summary>
internal sealed class AppSettings
{
    // The editor's encoder, quality, size and frame rate start at "same as original" for every clip.
    public string Format { get; set; } = "source";
    public string Encoder { get; set; } = "";                    // the last one picked, used for the original's codec
    public double TargetSizeMB { get; set; } = 25;
    public int AudioBitrate { get; set; } = 192;
    public bool MergeAudio { get; set; }                         // no longer remembered: the editor always starts unmerged
    public bool Loop { get; set; } = true;
    public string? LastFolder { get; set; }
    public string? SoundFolder { get; set; }                     // where the editor last added a sound from

    // Recording (instant replay)
    public bool RecordingEnabled { get; set; } = true;
    public string RecordMonitor { get; set; } = "";             // GDI device name; empty = main monitor
    public string RecordAspect { get; set; } = "";              // "16:9" records the monitor's 16:9 center; empty = all of it
    public int RecordFps { get; set; } = 60;
    public int RecordResolution { get; set; }                   // short side; 0 = native
    public RecordingQuality RecordQuality { get; set; } = RecordingQuality.High;
    public string RecordEncoder { get; set; } = "";             // empty = newest GPU codec available
    public int BufferSeconds { get; set; } = 60;
    public bool CaptureCursor { get; set; } = true;
    public bool PreferGameWindow { get; set; }                   // record a running game by its window (the aspect ratio is then for the desktop)
    public bool SplitChatAudio { get; set; } = true;
    public bool SplitMusicAudio { get; set; } = true;
    public bool RecordMic { get; set; } = true;
    public List<string> MicDeviceIds { get; set; } = [""];      // empty id = Windows default
    public bool SeparateMicTracks { get; set; }                 // a track per microphone instead of one shared Mic track

    /// <summary>The single microphone of settings from before several could be recorded, moved into <see cref="MicDeviceIds"/>
    /// when loaded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MicDeviceId { get; set; }
    public int HotkeyModifiers { get; set; } = 1;               // MOD_ALT
    public int HotkeyKey { get; set; } = 0x79;                  // F10
    public bool ClipSound { get; set; } = true;
    public double ShareSizeMB { get; set; } = 30;                // shared copies are made to fit under this
    public bool ShareMotionBlur { get; set; } = true;             // sharing a 120 fps clip blends it into 60 fps motion blur
    public string ClipsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "QuickClip");

    // Window behaviour
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;
    public string? UpdateChannel { get; set; }
    public string? SeenVersion { get; set; }                      // the version whose "what's new" was last shown

    /// <summary>Read from an existing settings file (QuickClip has run here before).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadedFromFile { get; private set; }                   // "stable" or "nightly"; unset = the kind of build this is

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
            if (File.Exists(FilePath) && Parse(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.LoadedFromFile = true;
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    internal static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        if (settings.MicDeviceId != null)
        {
            settings.MicDeviceIds = [settings.MicDeviceId];
            settings.MicDeviceId = null;
        }
        return settings;
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Debug runs that point QuickClip at test folders must not overwrite the real settings.</summary>
    internal static bool SaveDisabled { get; set; }

    public void Save()
    {
        if (SaveDisabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (JsonOptions)
                File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }
}
