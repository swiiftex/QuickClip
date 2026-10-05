using System.Globalization;
using System.Text.Json;

namespace QuickClip.Media;

internal sealed class VideoStreamInfo
{
    public int Index { get; init; }
    public string Codec { get; init; } = "";
    /// <summary>Display-oriented size (already swapped for 90/270 degree rotation).</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
}

public sealed class AudioStreamInfo
{
    public int Index { get; init; }
    public int Ordinal { get; init; }
    public string Codec { get; init; } = "";
    public int Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int SampleRate { get; init; }
    public long BitRate { get; init; }
    public string? Title { get; init; }
    public string? Language { get; init; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Title) ? Title! : $"Track {Ordinal + 1}";

    public string Description
    {
        get
        {
            string ch = Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{Channels} ch" };
            var parts = new List<string> { Codec.ToUpperInvariant(), ch };
            if (SampleRate > 0) parts.Add($"{SampleRate / 1000.0:0.#} kHz");
            if (!string.IsNullOrEmpty(Language) && Language != "und") parts.Add(Language!);
            return string.Join(" · ", parts);
        }
    }
}

internal sealed class MediaInfo
{
    public required string Path { get; init; }
    public double Duration { get; set; }
    public string FormatName { get; init; } = "";
    public long Size { get; init; }
    public VideoStreamInfo? Video { get; init; }
    public List<AudioStreamInfo> Audio { get; } = [];

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Video != null)
            {
                parts.Add($"{Video.Width}×{Video.Height}");
                if (Video.Fps > 0) parts.Add($"{Video.Fps:0.##} fps");
                parts.Add(Video.Codec.ToUpperInvariant());
            }
            else parts.Add("Audio only");
            parts.Add(Fmt.Time(Duration, millis: false));
            parts.Add(Fmt.Size(Size));
            if (Audio.Count > 1) parts.Add($"{Audio.Count} audio tracks");
            return string.Join("  ·  ", parts);
        }
    }

    // Generic handler names muxers write when no real track name was given.
    private static readonly string[] GenericHandlers =
    [
        "SoundHandler", "VideoHandler", "Core Media Audio", "Core Media Video", "Mainconcept MP4 Sound Media Handler",
        "ISO Media file produced by Google Inc.", "Apple Sound Media Handler", "Sound Media Handler", "GPAC ISO Audio Handler",
        "L-SMASH Audio Handler", "Stereo", "Mono",
    ];

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (Deps.FFprobe == null) throw new InvalidOperationException("ffprobe.exe was not found.");
        var (code, stdout, stderr) = await Proc.RunAsync(Deps.FFprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffprobe could not read this file." : stderr.Trim());

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;

        VideoStreamInfo? video = null;
        var audio = new List<AudioStreamInfo>();
        double streamDuration = 0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type") ?? "";
                double d = Dbl(s, "duration");
                if (d > streamDuration) streamDuration = d;

                if (type == "video" && video == null && !IsAttachedPicture(s))
                {
                    int w = Int(s, "width"), h = Int(s, "height");
                    int rot = Rotation(s);
                    if (Math.Abs(rot) % 180 == 90) (w, h) = (h, w);
                    double fps = Rate(Str(s, "avg_frame_rate"));
                    if (fps <= 0 || fps > 1000) fps = Rate(Str(s, "r_frame_rate"));
                    video = new VideoStreamInfo
                    {
                        Index = Int(s, "index"),
                        Codec = Str(s, "codec_name") ?? "?",
                        Width = w,
                        Height = h,
                        Fps = fps,
                    };
                }
                else if (type == "audio")
                {
                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    string? title = Tag(tags, "title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        string? handler = Tag(tags, "handler_name")?.Trim();
                        if (!string.IsNullOrEmpty(handler) && !GenericHandlers.Contains(handler, StringComparer.OrdinalIgnoreCase))
                            title = handler;
                    }
                    audio.Add(new AudioStreamInfo
                    {
                        Index = Int(s, "index"),
                        Ordinal = audio.Count,
                        Codec = Str(s, "codec_name") ?? "?",
                        Channels = Int(s, "channels"),
                        ChannelLayout = Str(s, "channel_layout"),
                        SampleRate = Int(s, "sample_rate"),
                        BitRate = (long)Dbl(s, "bit_rate"),
                        Title = title,
                        Language = Tag(tags, "language"),
                    });
                }
            }
        }

        double duration = format.ValueKind == JsonValueKind.Object ? Dbl(format, "duration") : 0;
        if (duration <= 0) duration = streamDuration;

        var info = new MediaInfo
        {
            Path = path,
            Duration = duration,
            FormatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") ?? "" : "",
            Size = new FileInfo(path).Length,
            Video = video,
        };
        info.Audio.AddRange(audio);
        if (info.Video == null && info.Audio.Count == 0)
            throw new InvalidOperationException("This file has no video or audio streams.");
        return info;
    }

    private static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var sd))
            foreach (var item in sd.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r))
                    return (int)Math.Round(r.ValueKind == JsonValueKind.Number ? r.GetDouble() : double.Parse(r.GetString() ?? "0", CultureInfo.InvariantCulture));
        if (s.TryGetProperty("tags", out var tags) && int.TryParse(Tag(tags, "rotate"), out int rot))
            return rot;
        return 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0
            : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0
            : 0;

    private static string? Tag(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in tags.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString();
        return null;
    }

    private static double Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
