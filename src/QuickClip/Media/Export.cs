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
    public string Quality { get; init; } = "high";      // best | high | good | small | size
    public double TargetSizeMB { get; init; } = 25;
    public int MaxShortSide { get; init; }               // 0 = original
    public double Fps { get; init; }                     // 0 = original
    public int AudioBitrateK { get; init; } = 192;

    public double Duration => End - Start;
}

internal sealed record TrackExport(int StreamIndex, string Codec, int Channels, long BitRate, string Name, double Volume);

internal static class ExportBuilder
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static List<string> BuildArgs(ExportRequest r)
    {
        if (r.Duration <= 0.01) throw new ExportException("The selection is empty. Set an in point before the out point.");
        var c = r.Container;
        var media = r.Media;

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-nostats", "-progress", "pipe:1" };
        if (r.Start > 0.0005) args.AddRange(["-ss", F(r.Start)]);
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
                if (r.Crop != null) throw new ExportException("Stream copy can't crop. Switch Video to \"Re-encode\" to crop.");
                if (r.MaxShortSide > 0 || r.Fps > 0) throw new ExportException("Stream copy can't change resolution or frame rate. Switch Video to \"Re-encode\".");
                if (!c.CanCopyVideo(v.Codec))
                    throw new ExportException($"{v.Codec.ToUpperInvariant()} video can't be stream-copied into {c.Extension}. Switch Video to \"Re-encode\" or pick another format.");
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
                    graph.Add($"[0:{tracks[i].StreamIndex}]volume={F(tracks[i].Volume)},aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[am{i}]");
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
                    bool unchanged = Math.Abs(t.Volume - 1) < 0.001;
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
                            graph.Add($"[0:{t.StreamIndex}]volume={F(t.Volume)}[a{o}]");
                            outArgs.AddRange(["-map", $"[a{o}]"]);
                        }
                        AddAudioCodec(outArgs, o, encoder, lossless, r.AudioBitrateK, t.Channels);
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

        if (graph.Count > 0) args.AddRange(["-filter_complex", string.Join(";", graph)]);
        args.AddRange(outArgs);
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
        if (fps > 0 && (v.Fps <= 0 || fps < v.Fps - 0.01))
            chain.Add($"fps={F(fps)}");
        if (maxShort > 0 && Math.Min(w, h) > maxShort)
            chain.Add(w >= h ? $"scale=-2:{maxShort}:flags=lanczos" : $"scale={maxShort}:-2:flags=lanczos");

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
    }

    /// <summary>Encoder and rate-control arguments for a quality setting ("size" leaves the bitrate to the caller).</summary>
    public static void AddEncoder(List<string> outArgs, VideoEncoder enc, string quality, bool appleTag)
    {
        int q = quality switch { "best" => 16, "good" => 24, "small" => 28, _ => 20 };
        bool size = quality == "size";
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
