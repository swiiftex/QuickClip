using QuickClip.Media;

namespace QuickClip.Recording;

public enum RecordingQuality { Low, Medium, High, Ultra, Indistinguishable }

/// <summary>Bitrates, sizes and memory estimates for the replay buffer.</summary>
internal static class RecordingPresets
{
    public const int AudioKbps = 160;

    public static readonly int[] BufferSteps = [10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300];
    public static readonly int[] FrameRates = [30, 60, 90, 120, 144, 165, 240];
    public static readonly int[] Resolutions = [0, 2160, 1440, 1080, 900, 720]; // short side; 0 = native

    public static string Label(RecordingQuality q) => q switch
    {
        RecordingQuality.Low => "Low",
        RecordingQuality.Medium => "Medium",
        RecordingQuality.High => "High",
        RecordingQuality.Ultra => "Ultra",
        _ => "Indistinguishable",
    };

    /// <summary>
    /// Video bitrate for a quality preset. Anchored on AV1 at 1080p60 and scaled sub-linearly with the pixel
    /// rate (bigger frames compress better per pixel); HEVC and H.264 need more bits for the same quality.
    /// </summary>
    public static int VideoKbps(RecordingQuality quality, int width, int height, int fps, string family)
    {
        double av1Mbps1080p60 = quality switch
        {
            RecordingQuality.Low => 6,
            RecordingQuality.Medium => 10,
            RecordingQuality.High => 16,
            RecordingQuality.Ultra => 25,
            _ => 40,
        };
        double pixelRate = (double)Math.Max(width, 16) * Math.Max(height, 16) * Math.Max(fps, 1);
        double scale = Math.Pow(pixelRate / (1920.0 * 1080 * 60), 0.75);
        double codec = family switch { "av1" => 1.0, "hevc" => 1.25, _ => 1.6 };
        return (int)Math.Clamp(av1Mbps1080p60 * scale * codec * 1000, 1500, 250_000);
    }

    /// <summary>Output size for a short-side limit, keeping the monitor's aspect ratio (even numbers).</summary>
    public static (int Width, int Height) OutputSize(int monitorWidth, int monitorHeight, int shortSide)
    {
        int shortest = Math.Min(monitorWidth, monitorHeight);
        if (shortSide <= 0 || shortSide >= shortest) return (monitorWidth, monitorHeight);
        double s = shortSide / (double)shortest;
        return ((int)Math.Round(monitorWidth * s / 2) * 2, (int)Math.Round(monitorHeight * s / 2) * 2);
    }

    /// <summary>Approximate RAM held by the buffer: the encoded streams plus a couple of seconds of slack.</summary>
    public static long EstimateRamBytes(int videoKbps, int audioTracks, int bufferSeconds)
    {
        double bytesPerSecond = (videoKbps + audioTracks * AudioKbps) * 1000.0 / 8;
        return (long)(bytesPerSecond * (bufferSeconds + 3) * 1.05) + 16L * 1024 * 1024;
    }

    public static string DurationLabel(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        _ when seconds % 60 == 0 => seconds == 60 ? "1 minute" : $"{seconds / 60} minutes",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };

    /// <summary>GPU encoders usable for recording, newest codec first.</summary>
    public static readonly string[] RecordingEncoderOrder =
        ["av1_nvenc", "av1_amf", "av1_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv", "h264_nvenc", "h264_amf", "h264_qsv"];

    public static VideoEncoder? DefaultEncoder(IReadOnlySet<string> available) =>
        RecordingEncoderOrder.Where(available.Contains).Select(VideoEncoders.Find).FirstOrDefault(e => e != null);
}
