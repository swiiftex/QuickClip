using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QuickClip.Media;

internal enum VideoMode { Encode, Copy }

internal sealed class ExportException(string message) : Exception(message);

/// <summary>Everything needed to produce one output file.</summary>
internal sealed record ExportRequest
{
    public required MediaInfo Media { get; init; }
    public required string OutputPath { get; init; }
    public required Container Container { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    /// <summary>Crop in display-oriented source pixels, or null for the full frame.</summary>
    public (int X, int Y, int W, int H)? Crop { get; init; }
    public required IReadOnlyList<TrackExport> Tracks { get; init; }
    public bool MergeAudio { get; init; }
    public VideoMode VideoMode { get; init; }
    public string EncoderId { get; init; } = "libx264";
    public string Quality { get; init; } = "high";      // original | best | high | good | small | size
    public double TargetSizeMB { get; init; } = 25;
    public int MaxShortSide { get; init; }               // 0 = original
    public double Fps { get; init; }                     // 0 = original
    public int AudioBitrateK { get; init; } = 192;
    /// <summary>Lowering the frame rate blends the dropped frames into motion blur rather than discarding them.</summary>
    public bool MotionBlur { get; init; }

    public double Duration => End - Start;
}

internal sealed record TrackExport(int StreamIndex, string Codec, int Channels, long BitRate, string Name, double Volume)
{
    /// <summary>An added sound's file (its stream is <see cref="StreamIndex"/>), or null for one of the clip's own tracks.</summary>
    public string? SoundPath { get; init; }
    /// <summary>Where an added sound starts on the clip's timeline, in seconds.</summary>
    public double Offset { get; init; }
}

internal static class ExportBuilder
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static List<string> BuildArgs(ExportRequest r)
    {
        if (r.Duration <= 0.01) throw new ExportException("The selection is empty. Set an in point before the out point.");
        var c = r.Container;
        var media = r.Media;

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-nostats", "-progress", "pipe:1" };
        // A copy starts on the keyframe at or before the seek point: aim a hair past the one asked for, so rounding
        // can't land just before it and take the one a second earlier.
        if (r.Start > 0.0005) args.AddRange(["-ss", F(r.VideoMode == VideoMode.Copy ? r.Start + 0.001 : r.Start)]);
        args.AddRange(["-t", F(r.Duration), "-i", media.Path]);

        var graph = new List<string>();
        var outArgs = new List<string>();

        // ---- Video ----------------------------------------------------------------------
        bool wantVideo = (c.HasVideo || c.IsGif) && media.Video != null;
        if (c.IsGif && media.Video == null) throw new ExportException("GIF export needs a video stream.");
        if (wantVideo)
        {
            var v = media.Video!;
            if (r.VideoMode == VideoMode.Copy && !c.IsGif)
            {
                if (r.Crop != null || r.MaxShortSide > 0 || r.Fps > 0)
                    throw new ExportException("Copying the video can't crop it or change its size or frame rate.");
                if (!c.CanCopyVideo(v.Codec))
                    throw new ExportException($"{CodecName(v.Codec)} video can't be copied into {c.Extension} as it is.");
                outArgs.AddRange(["-map", $"0:{v.Index}", "-c:v", "copy"]);
            }
            else
            {
                BuildVideoEncode(r, v, graph, outArgs);
            }
        }

        // ---- Audio ----------------------------------------------------------------------
        List<TrackExport> tracks = c.IsGif ? [] : [.. r.Tracks];
        long audioBits = 0;
        // Added sounds are further inputs, placed on the clip's timeline by filters.
        var input = new int[tracks.Count];
        var soundInputs = new List<string>();
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].SoundPath is { } sound)
            {
                soundInputs.Add(sound);
                input[i] = soundInputs.Count;
            }
        string Source(int i) => input[i] == 0 ? $"[0:{tracks[i].StreamIndex}]"
            : $"[{input[i]}:{tracks[i].StreamIndex}]{AddedSound.Placement(tracks[i].Offset, r.Start, r.Duration)},";

        if (tracks.Count > 0)
        {
            bool merge = tracks.Count > 1 && (r.MergeAudio || c.SingleAudioTrack);
            string? encoder = c.AudioEncoder;
            bool lossless = encoder is "flac" or "alac" || encoder?.StartsWith("pcm_") == true;

            if (merge)
            {
                var labels = new StringBuilder();
                for (int i = 0; i < tracks.Count; i++)
                {
                    graph.Add($"{Source(i)}volume={F(tracks[i].Volume)},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[am{i}]");
                    labels.Append($"[am{i}]");
                }
                graph.Add($"{labels}amix=inputs={tracks.Count}:normalize=0:duration=longest:dropout_transition=0[aout]");
                outArgs.AddRange(["-map", "[aout]"]);
                AddAudioCodec(outArgs, 0, encoder, lossless, r.AudioBitrateK, 2);
                AddTitle(outArgs, 0, string.Join(" + ", tracks.Select(t => t.Name)));
                audioBits += lossless ? 0 : r.AudioBitrateK * 1000L;
            }
            else
            {
                for (int o = 0; o < tracks.Count; o++)
                {
                    var t = tracks[o];
                    bool unchanged = Math.Abs(t.Volume - 1) < 0.001 && t.SoundPath == null;
                    if (unchanged && c.CanCopyAudio(t.Codec))
                    {
                        outArgs.AddRange(["-map", $"0:{t.StreamIndex}", $"-c:a:{o}", "copy"]);
                        audioBits += t.BitRate > 0 ? t.BitRate : 192_000;
                    }
                    else
                    {
                        if (unchanged)
                            outArgs.AddRange(["-map", $"0:{t.StreamIndex}"]);
                        else
                        {
                            graph.Add($"{Source(o)}volume={F(t.Volume)}[a{o}]");
                            outArgs.AddRange(["-map", $"[a{o}]"]);
                        }
                        AddAudioCodec(outArgs, o, encoder, lossless, r.AudioBitrateK, t.SoundPath != null ? 2 : t.Channels);
                        audioBits += lossless ? 0 : r.AudioBitrateK * 1000L;
                    }
                    AddTitle(outArgs, o, t.Name);
                    if (c.SingleAudioTrack) break;
                }
            }
        }

        if (!wantVideo && tracks.Count == 0)
            throw new ExportException("Nothing to export: no video, and every audio track is unchecked.");

        if (r.Quality == "size" && wantVideo && r.VideoMode == VideoMode.Encode && !c.IsGif)
            ApplyTargetSize(r, outArgs, audioBits);

        foreach (var sound in soundInputs) args.AddRange(["-i", sound]);
        if (graph.Count > 0) args.AddRange(["-filter_complex", string.Join(";", graph)]);
        args.AddRange(outArgs);
        // Copied audio would start where the seek landed, on the keyframe up to a second before the in point, with
        // an edit list to hide the extra; Windows' player and Discord ignore that and play the sound that much late.
        if (r.Start > 0.0005) args.AddRange(["-copypriorss:a", "0"]);
        args.AddRange(["-map_metadata", "0", "-map_chapters", "-1"]);
        if (r.VideoMode == VideoMode.Copy) args.AddRange(["-avoid_negative_ts", "make_zero"]);
        if (c.FastStart) args.AddRange(["-movflags", "+faststart"]);
        if (c.IsGif) args.AddRange(["-loop", "0"]);
        args.Add(r.OutputPath);
        return args;
    }

    private static void BuildVideoEncode(ExportRequest r, VideoStreamInfo v, List<string> graph, List<string> outArgs)
    {
        var c = r.Container;
        var chain = new List<string>();
        int w = v.Width, h = v.Height;
        if (r.Crop is { } crop)
        {
            chain.Add($"crop={crop.W}:{crop.H}:{crop.X}:{crop.Y}");
            (w, h) = (crop.W, crop.H);
        }

        int maxShort = r.MaxShortSide;
        double fps = r.Fps;
        if (c.IsGif)
        {
            if (maxShort == 0) maxShort = 480;
            if (fps == 0) fps = 15;
        }
        bool lowerFps = fps > 0 && (v.Fps <= 0 || fps < v.Fps - 0.01);
        bool blur = lowerFps && r.MotionBlur && !c.IsGif && v.IsBt709 && MotionBlur.Helps(v.Fps, fps);
        if (lowerFps && !blur)
            chain.Add($"fps={F(fps)}");
        bool scaled = maxShort > 0 && Math.Min(w, h) > maxShort;
        if (scaled)
            chain.Add(w >= h ? $"scale=-2:{maxShort}:flags=lanczos" : $"scale={maxShort}:-2:flags=lanczos");
        // After scaling: blending in linear light is the costly part, so it works on as few pixels as possible.
        if (blur)
            chain.Add(MotionBlur.Filter(v.Fps, fps, fullRange: v.ColorRange == "pc"));

        if (c.IsGif)
        {
            chain.Add("split[g0][g1]");
            graph.Add($"[0:{v.Index}]{string.Join(",", chain)}");
            graph.Add("[g0]palettegen=stats_mode=diff[gp]");
            graph.Add("[g1][gp]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[vout]");
            outArgs.AddRange(["-map", "[vout]"]);
            return;
        }

        chain.Add("format=yuv420p");
        graph.Add($"[0:{v.Index}]{string.Join(",", chain)}[vout]");
        outArgs.AddRange(["-map", "[vout]"]);

        var enc = VideoEncoders.Find(r.EncoderId);
        if (enc == null || !c.VideoFamilies.Contains(enc.Family))
        {
            if (c.FallbackVideoEncoder != null)
            {
                outArgs.AddRange(["-c:v", c.FallbackVideoEncoder]);
                if (r.Quality != "size")
                    outArgs.AddRange(["-q:v", c.FallbackVideoEncoder == "libtheora" ? "8" : "2"]);
                return;
            }
            enc = VideoEncoders.Find("libx264")!;
        }

        AddEncoder(outArgs, enc, r.Quality, appleTag: c.Key is "mp4" or "mov" or "m4v");
        if (r.Quality == "original")
        {
            if (scaled) (w, h) = w >= h ? (w * maxShort / h, maxShort) : (maxShort, h * maxShort / w);
            double outFps = lowerFps ? fps : v.Fps > 0 ? v.Fps : 30;
            AddOriginalBitrate(outArgs, enc, v, (double)w * h * outFps);
        }
    }

    /// <summary>
    /// "Same as original" quality when the video has to be re-encoded: about the original's bitrate for the picture
    /// that's left (after cropping, resizing and a lower frame rate), allowing for what each codec gets from a bit.
    /// </summary>
    private static void AddOriginalBitrate(List<string> outArgs, VideoEncoder enc, VideoStreamInfo v, double outPixelRate)
    {
        double srcPixelRate = (double)v.Width * v.Height * (v.Fps > 0 ? v.Fps : 30);
        double bps = v.BitRate > 0 ? v.BitRate : srcPixelRate * 0.1; // unknown: a high-quality H.264 rate
        // Fewer pixels need fewer bits, though not in proportion: each one carries more of the detail.
        bps *= Math.Pow(Math.Min(1, outPixelRate / srcPixelRate), 0.8) * Efficiency(v.Codec) / Efficiency(enc.Family);
        long kbps = Math.Clamp((long)(bps / 1000), 500, 500_000);
        outArgs.AddRange(["-b:v", $"{kbps}k"]);
        if (enc.Id != "libsvtav1") // SVT-AV1 rejects a VBV cap in VBR mode
            outArgs.AddRange(["-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"]);
    }

    /// <summary>Bits a codec needs for the same picture, relative to H.264.</summary>
    private static double Efficiency(string codec) => codec switch { "av1" => 0.6, "hevc" or "vp9" => 0.7, _ => 1.0 };

    public static string CodecName(string codec) => codec switch
    {
        "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "vp8" => "VP8", "prores" => "ProRes",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>
    /// Why the video has to be re-encoded rather than copied as it is, or null when a copy gives what's asked for.
    /// <paramref name="encoderChosen"/>: an encoder was picked rather than keeping the original's codec.
    /// <paramref name="keyframes"/>: when the video's keyframes are (null while unknown), as a copy starts on one.
    /// </summary>
    public static string? ReencodeReason(ExportRequest r, bool encoderChosen, IReadOnlyList<double>? keyframes)
    {
        var v = r.Media.Video;
        var c = r.Container;
        if (v == null || !(c.HasVideo || c.IsGif)) return null;
        if (c.IsGif) return "it's made into a GIF";
        if (r.Crop != null) return "the video is cropped";
        if (r.MaxShortSide > 0 && Math.Min(v.Width, v.Height) > r.MaxShortSide) return "the resolution changes";
        if (r.Fps > 0 && (v.Fps <= 0 || r.Fps < v.Fps - 0.01)) return "the frame rate changes";
        if (r.Quality != "original") return "a quality is chosen";
        if (encoderChosen) return "an encoder is chosen";
        if (!c.CanCopyVideo(v.Codec)) return $"{c.Extension.TrimStart('.').ToUpperInvariant()} can't hold {CodecName(v.Codec)} video as it is";
        if (r.Start > 0.0005 && (keyframes == null || KeyframeAt(keyframes, r.Start, v.Fps) == null))
            return "the clip starts between keyframes, and a copy would have to start on one";
        return null;
    }

    /// <summary>The keyframe within half a frame of <paramref name="time"/>, if there is one.</summary>
    public static double? KeyframeAt(IReadOnlyList<double> keyframes, double time, double fps)
    {
        double tolerance = 0.5 / (fps > 0 ? fps : 30);
        foreach (double k in keyframes)
            if (Math.Abs(k - time) <= tolerance) return k;
        return null;
    }

    /// <summary>Encoder and rate-control arguments for a quality setting ("size" and "original" leave the bitrate to the caller).</summary>
    public static void AddEncoder(List<string> outArgs, VideoEncoder enc, string quality, bool appleTag)
    {
        int q = quality switch { "best" => 16, "good" => 24, "small" => 28, _ => 20 };
        bool size = quality is "size" or "original"; // the caller sets the bitrate
        outArgs.AddRange(["-c:v", enc.Id]);
        switch (enc.Id)
        {
            case "libx264":
                outArgs.AddRange(["-preset", "medium"]);
                if (!size) outArgs.AddRange(["-crf", $"{q}"]);
                break;
            case "libx265":
                outArgs.AddRange(["-preset", "medium", "-x265-params", "log-level=error"]);
                if (!size) outArgs.AddRange(["-crf", $"{q + 4}"]);
                break;
            case "h264_nvenc":
            case "hevc_nvenc":
            case "av1_nvenc":
                outArgs.AddRange(["-preset", "p5", "-tune", "hq", "-rc", "vbr"]);
                if (!size)
                {
                    int cq = enc.Id switch { "h264_nvenc" => q + 3, "hevc_nvenc" => q + 5, _ => q + 12 };
                    outArgs.AddRange(["-cq", $"{cq}", "-b:v", "0"]);
                }
                break;
            case "av1_amf":
                // AMF's AV1 quantiser runs 0-255 rather than 0-51.
                outArgs.AddRange(["-quality", "quality"]);
                if (!size) outArgs.AddRange(["-rc", "cqp", "-qp_i", $"{(q + 5) * 4}", "-qp_p", $"{(q + 7) * 4}"]);
                else outArgs.AddRange(["-rc", "vbr_peak"]);
                break;
            case "h264_amf":
            case "hevc_amf":
                outArgs.AddRange(["-quality", "quality"]);
                if (!size) outArgs.AddRange(["-rc", "cqp", "-qp_i", $"{q}", "-qp_p", $"{q + 2}"]);
                else outArgs.AddRange(["-rc", "vbr_peak"]);
                if (!size && enc.Id == "h264_amf") outArgs.AddRange(["-qp_b", $"{q + 4}"]);
                break;
            case "h264_qsv":
            case "hevc_qsv":
                outArgs.AddRange(["-preset", "medium"]);
                if (!size) outArgs.AddRange(["-global_quality", $"{q + 2}"]);
                break;
            case "libsvtav1":
                outArgs.AddRange(["-preset", "8"]);
                if (!size) outArgs.AddRange(["-crf", $"{q + 10}"]);
                break;
            case "libvpx-vp9":
                outArgs.AddRange(["-deadline", "good", "-cpu-used", "4", "-row-mt", "1"]);
                if (!size) outArgs.AddRange(["-crf", $"{q + 12}", "-b:v", "0"]);
                break;
        }
        if (appleTag && enc.Family == "hevc") outArgs.AddRange(["-tag:v", "hvc1"]);
    }

    private static void ApplyTargetSize(ExportRequest r, List<string> outArgs, long audioBitsPerSecond)
    {
        // Single-pass rate control overshoots a little, especially on short clips; aim below the limit.
        double totalBits = r.TargetSizeMB * 1_000_000 * 8 * 0.9;
        double videoBps = totalBits / r.Duration - audioBitsPerSecond;
        if (videoBps < 50_000)
            throw new ExportException($"{r.TargetSizeMB:0.#} MB is too small for a {Fmt.Time(r.Duration, false)} clip. Raise the target size, lower the audio bitrate or shorten the selection.");
        long kbps = (long)(videoBps / 1000);
        outArgs.AddRange(["-b:v", $"{kbps}k"]);
        if (r.EncoderId != "libsvtav1") // SVT-AV1 rejects a VBV cap in VBR mode
            outArgs.AddRange(["-maxrate", $"{kbps}k", "-bufsize", $"{kbps}k"]);
    }

    private static void AddAudioCodec(List<string> outArgs, int o, string? encoder, bool lossless, int bitrateK, int channels)
    {
        if (encoder == null) return; // muxer default
        outArgs.AddRange([$"-c:a:{o}", encoder]);
        if (!lossless) outArgs.AddRange([$"-b:a:{o}", $"{bitrateK}k"]);
        if (encoder == "libopus" && channels > 2) outArgs.AddRange([$"-ac:a:{o}", "2"]);
    }

    private static void AddTitle(List<string> outArgs, int o, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        outArgs.AddRange([$"-metadata:s:a:{o}", $"title={name}", $"-metadata:s:a:{o}", $"handler_name={name}"]);
    }
}

internal static class ExportRunner
{
    /// <summary>Runs FFmpeg, reporting progress 0..1. Throws on failure; deletes the partial output on failure or cancel.</summary>
    public static async Task RunAsync(IReadOnlyList<string> args, string outputPath, double duration, IProgress<double> progress, CancellationToken ct)
    {
        if (Deps.FFmpeg == null) throw new ExportException("ffmpeg.exe was not found.");
        Log.Write("ffmpeg " + Proc.Quote(args));

        var errors = new StringBuilder();
        using var p = new Process { StartInfo = Proc.StartInfo(Deps.FFmpeg, args) };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        p.Start();
        p.BeginErrorReadLine();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        bool ok = false;
        try
        {
            using (ct.Register(() => Proc.KillQuietly(p)))
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan(12), out long us) && duration > 0)
                        progress.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
                }
                await p.WaitForExitAsync(CancellationToken.None);
            }
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
            {
                string msg;
                lock (errors) msg = errors.ToString().Trim();
                Log.Write("ffmpeg failed: " + msg);
                throw new ExportException(string.IsNullOrEmpty(msg) ? $"FFmpeg exited with code {p.ExitCode}." : LastLines(msg, 8));
            }
            ok = true;
            progress.Report(1);
        }
        finally
        {
            if (!ok)
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static string LastLines(string s, int n)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - n)));
    }
}
