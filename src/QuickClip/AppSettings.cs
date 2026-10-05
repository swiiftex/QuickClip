using System.Text.Json;

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

    public string? EncoderCacheKey { get; set; }
    public List<string>? EncoderCache { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickClip", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
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
