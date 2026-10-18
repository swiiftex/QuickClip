namespace QuickClip.Media;

/// <summary>
/// A copy of a clip made to be posted anywhere: under a size limit (30 MB unless set otherwise), at most 60 fps,
/// H.264 (which everything plays) and every audio track mixed into one. A higher frame rate can be blended into
/// motion blur rather than dropped (<see cref="MotionBlur"/>). The original is left alone.
/// </summary>
internal static class ShareCopy
{
    public const double LimitMB = 30;
    public const double MaxFps = 60;
    public const int AudioKbps = 128;

    // Below about this many bits per pixel, H.264 game footage turns to mush; a smaller picture looks better.
    private const double MinBitsPerPixel = 0.05;
    private static readonly int[] ShortSides = [1440, 1080, 720, 480, 360];

    /// <summary>GPU H.264 encoders first: sharing should be quick.</summary>
    public static string Encoder(IReadOnlySet<string> available) =>
        new[] { "h264_nvenc", "h264_amf", "h264_qsv" }.FirstOrDefault(available.Contains) ?? "libx264";

    /// <summary>How to make the copy. A clip that's already small enough H.264 keeps its video and only has its audio mixed.</summary>
    public static ExportRequest Plan(MediaInfo clip, string output, string encoderId, double limitMB = LimitMB, bool allowCopy = true,
        bool motionBlur = true)
    {
        if (clip.Duration <= 0) throw new ExportException("This clip has no length to share.");
        var request = new ExportRequest
        {
            Media = clip,
            OutputPath = output,
            Container = Containers.Mp4,
            Start = 0,
            End = clip.Duration,
            Tracks = clip.Audio.Select(a => new TrackExport(a.Index, a.Codec, a.Channels, a.BitRate, a.DisplayName, 1)).ToList(),
            MergeAudio = true,
            AudioBitrateK = AudioKbps,
            EncoderId = encoderId,
        };
        var v = clip.Video;
        double sizeMB = clip.Size / 1e6;
        if (v == null || (allowCopy && v.Codec == "h264" && v.Fps <= MaxFps + 0.5 && sizeMB <= limitMB * 0.95))
            return request with { VideoMode = VideoMode.Copy };

        // No bigger than it needs to be: H.264 needs about twice the bits AV1 or HEVC do for the same picture.
        double targetMB = Math.Min(limitMB, sizeMB * (v.Codec == "h264" ? 1 : 2));
        double fps = Math.Min(v.Fps > 0 ? v.Fps : MaxFps, MaxFps);
        double videoBps = targetMB * 1e6 * 8 * 0.9 / clip.Duration - AudioKbps * 1000; // as the export aims for the target
        return request with
        {
            VideoMode = VideoMode.Encode,
            Quality = "size",
            TargetSizeMB = targetMB,
            Fps = v.Fps > MaxFps + 0.01 ? MaxFps : 0,
            MotionBlur = motionBlur,
            MaxShortSide = ShortSideFor(v.Width, v.Height, fps, videoBps),
        };
    }

    /// <summary>
    /// The biggest picture (by its short side) that still gets enough bits per pixel at this bitrate: 0 to keep the
    /// clip's own size, otherwise the short side to scale it down to.
    /// </summary>
    public static int ShortSideFor(int width, int height, double fps, double videoBps)
    {
        int native = Math.Min(width, height);
        if (native <= 0 || fps <= 0) return 0;
        foreach (int side in ShortSides.Where(s => s < native).Prepend(native))
        {
            double scale = side / (double)native;
            if (videoBps / (width * scale * height * scale * fps) >= MinBitsPerPixel) return side == native ? 0 : side;
        }
        return native > ShortSides[^1] ? ShortSides[^1] : 0;
    }

    /// <summary>
    /// Makes the copy at <paramref name="output"/>, compressing again a little harder if the encoder overshot the limit.
    /// Progress is per attempt (the attempt number and 0..1). Returns the copy's size.
    /// </summary>
    public static async Task<long> CreateAsync(MediaInfo clip, string output, string encoderId, IProgress<(int Attempt, double Progress)> progress,
        CancellationToken ct, double limitMB = LimitMB, bool motionBlur = true)
    {
        var request = Plan(clip, output, encoderId, limitMB, motionBlur: motionBlur);
        for (int attempt = 1; ; attempt++)
        {
            int a = attempt;
            Log.Write($"Sharing {clip.Path}: {(request.VideoMode == VideoMode.Copy ? "video copied" : $"{request.TargetSizeMB:0.#} MB, short side {request.MaxShortSide}")}" +
                      $", fps {request.Fps}{(request.MotionBlur && request.Fps > 0 ? " with motion blur" : "")}, {request.Tracks.Count} audio tracks merged, attempt {attempt}");
            await ExportRunner.RunAsync(ExportBuilder.BuildArgs(request), output, request.Duration,
                new Progress<double>(p => progress.Report((a, p))), ct);
            long size = new FileInfo(output).Length;
            if (size <= limitMB * 1e6) return size;
            if (attempt == 3) throw new ExportException($"The clip didn't fit in {limitMB:0} MB.");
            // Aim lower by as much as it overshot.
            double target = (request.VideoMode == VideoMode.Copy ? limitMB : request.TargetSizeMB) * (limitMB * 1e6 / size) * 0.95;
            request = Plan(clip, output, encoderId, target, allowCopy: false, motionBlur: motionBlur);
        }
    }
}
