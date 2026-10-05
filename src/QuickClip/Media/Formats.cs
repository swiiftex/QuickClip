namespace QuickClip.Media;

/// <summary>An output container and what it can hold.</summary>
internal sealed record Container(
    string Key,
    string Extension,
    string Label,
    bool HasVideo,
    string[] VideoFamilies,          // encoder families we may produce (h264, hevc, av1, vp9)
    string? FallbackVideoEncoder,    // used when none of our families fit (e.g. wmv2 for .wmv)
    string? AudioEncoder,            // null = let FFmpeg pick the muxer default
    string[] AudioCopy,              // audio codecs that can be stream-copied ("*" = anything)
    string[] VideoCopy,              // video codecs that can be stream-copied ("*" = anything)
    bool SingleAudioTrack = false,
    bool IsGif = false,
    bool FastStart = false)
{
    public bool IsAudioOnly => !HasVideo && !IsGif;

    public bool CanCopyAudio(string codec) =>
        AudioCopy.Contains("*") || AudioCopy.Contains(codec, StringComparer.OrdinalIgnoreCase) ||
        (codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) && AudioCopy.Contains("pcm"));

    public bool CanCopyVideo(string codec) =>
        VideoCopy.Contains("*") || VideoCopy.Contains(codec, StringComparer.OrdinalIgnoreCase);
}

internal static class Containers
{
    private static readonly string[] Mp4Audio = ["aac", "mp3", "ac3", "eac3", "alac", "opus", "flac"];
    private static readonly string[] Mp4Video = ["h264", "hevc", "av1", "vp9", "mpeg4", "mjpeg"];
    private static readonly string[] TsAudio = ["aac", "mp3", "mp2", "ac3", "eac3", "opus"];

    public static readonly Container Mp4 = new("mp4", ".mp4", "MP4 (H.264 / HEVC / AV1)", true, ["h264", "hevc", "av1"], null, "aac", Mp4Audio, Mp4Video, FastStart: true);
    public static readonly Container Mkv = new("mkv", ".mkv", "MKV (Matroska)", true, ["h264", "hevc", "av1", "vp9"], null, "aac", ["*"], ["*"]);
    public static readonly Container Mov = new("mov", ".mov", "MOV (QuickTime)", true, ["h264", "hevc"], null, "aac", [.. Mp4Audio, "pcm"], ["h264", "hevc", "prores", "mpeg4", "mjpeg"], FastStart: true);
    public static readonly Container WebM = new("webm", ".webm", "WebM (VP9 / AV1)", true, ["vp9", "av1"], null, "libopus", ["opus", "vorbis"], ["vp8", "vp9", "av1"]);
    public static readonly Container Gif = new("gif", ".gif", "GIF (animated, no audio)", false, [], "gif", null, [], [], IsGif: true);
    public static readonly Container Mp3 = new("mp3", ".mp3", "MP3 (audio only)", false, [], null, "libmp3lame", ["mp3"], [], SingleAudioTrack: true);
    public static readonly Container M4a = new("m4a", ".m4a", "M4A / AAC (audio only)", false, [], null, "aac", ["aac", "alac"], [], SingleAudioTrack: true, FastStart: true);
    public static readonly Container Wav = new("wav", ".wav", "WAV (audio only, lossless)", false, [], null, "pcm_s16le", ["pcm"], [], SingleAudioTrack: true);
    public static readonly Container Flac = new("flac", ".flac", "FLAC (audio only, lossless)", false, [], null, "flac", ["flac"], [], SingleAudioTrack: true);
    public static readonly Container Opus = new("opus", ".opus", "Opus (audio only)", false, [], null, "libopus", ["opus"], [], SingleAudioTrack: true);

    /// <summary>Formats offered in the export dropdown.</summary>
    public static readonly Container[] Choices = [Mp4, Mkv, Mov, WebM, Gif, Mp3, M4a, Wav, Flac, Opus];

    /// <summary>The container used for "same as source" and for saving over the original.</summary>
    public static Container ForExtension(string ext, bool sourceHasVideo)
    {
        ext = ext.ToLowerInvariant();
        foreach (var c in Choices)
            if (c.Extension == ext) return c;

        return ext switch
        {
            ".m4v" => Mp4 with { Key = "m4v", Extension = ".m4v" },
            ".3gp" or ".3g2" => Mp4 with { Key = "3gp", Extension = ext, VideoFamilies = ["h264"], AudioCopy = ["aac", "amr_nb", "amr_wb"] },
            ".ts" or ".m2ts" or ".mts" or ".m2t" => new Container("ts", ext, "MPEG-TS", true, ["h264", "hevc"], null, "aac", TsAudio, ["h264", "hevc", "mpeg2video", "mpeg1video"]),
            ".flv" or ".f4v" => new Container("flv", ext, "FLV", true, ["h264"], null, "aac", ["aac", "mp3"], ["h264", "flv1"]),
            ".avi" or ".divx" => new Container("avi", ext, "AVI", true, ["h264"], null, "libmp3lame", ["mp3", "mp2", "ac3", "pcm", "aac"], ["*"]),
            ".wmv" or ".asf" => new Container("asf", ext, "WMV", true, [], "wmv2", "wmav2", ["wmav1", "wmav2", "wmapro", "mp3"], ["wmv1", "wmv2", "wmv3", "vc1", "msmpeg4v3"]),
            ".mpg" or ".mpeg" or ".vob" or ".m2v" => new Container("mpeg", ext, "MPEG-PS", true, [], "mpeg2video", "mp2", ["mp2", "mp3", "ac3", "pcm"], ["mpeg1video", "mpeg2video"]),
            ".ogv" => new Container("ogv", ext, "Ogg video", true, [], "libtheora", "libvorbis", ["vorbis", "opus", "flac"], ["theora", "vp8"]),
            ".mka" => new Container("mka", ext, "Matroska audio", false, [], null, "aac", ["*"], []),
            ".aac" => new Container("aac", ext, "AAC", false, [], null, "aac", ["aac"], [], SingleAudioTrack: true),
            ".ogg" or ".oga" => new Container("ogg", ext, "Ogg", false, [], null, "libvorbis", ["vorbis", "opus", "flac"], [], SingleAudioTrack: true),
            ".wma" => new Container("wma", ext, "WMA", false, [], null, "wmav2", ["wmav1", "wmav2", "wmapro"], [], SingleAudioTrack: true),
            ".aif" or ".aiff" => new Container("aiff", ext, "AIFF", false, [], null, "pcm_s16be", ["pcm"], [], SingleAudioTrack: true),
            ".ac3" => new Container("ac3", ext, "AC-3", false, [], null, "ac3", ["ac3"], [], SingleAudioTrack: true),
            // Anything else: keep the extension and let FFmpeg choose codecs for that muxer.
            _ => new Container("other", ext, ext.TrimStart('.').ToUpperInvariant(), sourceHasVideo, ["h264", "hevc"], null, null, [], []),
        };
    }
}

/// <summary>A video encoder QuickClip knows how to drive.</summary>
internal sealed record VideoEncoder(string Id, string Family, string Label, bool Hardware);

internal static class VideoEncoders
{
    public static readonly VideoEncoder[] All =
    [
        new("h264_nvenc", "h264", "H.264 · NVIDIA GPU (fast)", true),
        new("h264_amf", "h264", "H.264 · AMD GPU (fast)", true),
        new("h264_qsv", "h264", "H.264 · Intel GPU (fast)", true),
        new("libx264", "h264", "H.264 · CPU (x264)", false),
        new("hevc_nvenc", "hevc", "H.265/HEVC · NVIDIA GPU", true),
        new("hevc_amf", "hevc", "H.265/HEVC · AMD GPU", true),
        new("hevc_qsv", "hevc", "H.265/HEVC · Intel GPU", true),
        new("libx265", "hevc", "H.265/HEVC · CPU (x265)", false),
        new("av1_nvenc", "av1", "AV1 · NVIDIA GPU", true),
        new("libsvtav1", "av1", "AV1 · CPU (SVT-AV1)", false),
        new("libvpx-vp9", "vp9", "VP9 · CPU", false),
    ];

    public static VideoEncoder? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HashSet<string>? _available;

    /// <summary>Encoders that are compiled into FFmpeg and (for GPU encoders) actually work on this machine.</summary>
    public static async Task<HashSet<string>> GetAvailableAsync()
    {
        if (_available != null) return _available;
        await Gate.WaitAsync();
        try
        {
            if (_available != null) return _available;
            var result = new HashSet<string>();
            if (Deps.FFmpeg == null) return _available = result;

            var fi = new FileInfo(Deps.FFmpeg);
            string cacheKey = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            var settings = AppSettings.Current;
            if (settings.EncoderCacheKey == cacheKey && settings.EncoderCache is { Count: > 0 })
                return _available = new HashSet<string>(settings.EncoderCache);

            var (_, listing, _) = await Proc.RunAsync(Deps.FFmpeg, ["-hide_banner", "-encoders"]);
            var compiled = new HashSet<string>(listing.Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0].Length == 6)
                .Select(p => p[1]));

            var tests = All.Where(e => compiled.Contains(e.Id)).Select(async e =>
            {
                if (!e.Hardware) return (e.Id, true);
                // GPU encoders are compiled in regardless of hardware; try a tiny encode.
                var (code, _, _) = await Proc.RunAsync(Deps.FFmpeg,
                [
                    "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.2",
                    "-frames:v", "3", "-pix_fmt", "yuv420p", "-c:v", e.Id, "-f", "null", "-",
                ]);
                return (e.Id, code == 0);
            });
            foreach (var (id, ok) in await Task.WhenAll(tests))
                if (ok) result.Add(id);
            foreach (var audio in new[] { "libopus", "libmp3lame", "libvorbis", "libtheora" })
                if (compiled.Contains(audio)) result.Add(audio);

            settings.EncoderCacheKey = cacheKey;
            settings.EncoderCache = [.. result];
            settings.Save();
            return _available = result;
        }
        finally
        {
            Gate.Release();
        }
    }
}
