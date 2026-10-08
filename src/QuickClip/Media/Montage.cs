using System.Globalization;
using System.Text;

namespace QuickClip.Media;

/// <summary>
/// Joins a project's clips into one video for the editor. Every clip is re-encoded to the first one's size and frame
/// rate (others letterboxed); audio tracks are matched up by name, with silence where a clip lacks one; the music goes
/// on its own track, trimmed to the montage and faded out.
/// </summary>
internal static class Montage
{
    public const string MusicTrackName = "Soundtrack";
    private const double MusicFadeSeconds = 2;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Audio track names across the clips, in order of first appearance ("Mic (2)" for a second "Mic").</summary>
    public static List<string> TrackNames(IReadOnlyList<MediaInfo> clips)
    {
        var names = new List<string>();
        foreach (var clip in clips)
            foreach (var name in Keys(clip))
                if (!names.Contains(name)) names.Add(name);
        return names;
    }

    private static List<string> Keys(MediaInfo clip)
    {
        var keys = new List<string>();
        foreach (var track in clip.Audio)
        {
            string key = track.DisplayName;
            for (int n = 2; keys.Contains(key); n++) key = $"{track.DisplayName} ({n})";
            keys.Add(key);
        }
        return keys;
    }

    /// <summary>The best encoder for the montage: a GPU HEVC or H.264 encoder if there is one.</summary>
    public static VideoEncoder ChooseEncoder(IReadOnlySet<string> available) =>
        new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv", "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }
            .Where(available.Contains).Select(VideoEncoders.Find).OfType<VideoEncoder>().FirstOrDefault()
        ?? VideoEncoders.Find("libx264")!;

    /// <summary>FFmpeg arguments, and the filter graph they read from <paramref name="graphFile"/>.</summary>
    public static (List<string> Args, string Graph) Build(IReadOnlyList<MediaInfo> clips, MediaInfo? music, VideoEncoder encoder,
        string output, string graphFile)
    {
        if (clips.Count == 0) throw new ExportException("The project has no clips.");
        var first = clips.FirstOrDefault(c => c.Video != null)?.Video;
        int width = (first?.Width ?? 1920) & ~1, height = (first?.Height ?? 1080) & ~1;
        double fps = first is { Fps: > 0 } ? Math.Round(first.Fps, 3) : 60;
        var names = TrackNames(clips);
        double total = clips.Sum(c => c.Duration);

        var graph = new List<string>();
        var concatInputs = new StringBuilder();
        for (int i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            string video = clip.Video != null
                ? $"[{i}:{clip.Video.Index}]scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos," +
                  $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1"
                : $"color=c=black:s={width}x{height}:d={F(clip.Duration)}";
            graph.Add($"{video},fps={F(fps)},format=yuv420p,setpts=PTS-STARTPTS[v{i}]");
            concatInputs.Append($"[v{i}]");

            var keys = Keys(clip);
            for (int k = 0; k < names.Count; k++)
            {
                int ordinal = keys.IndexOf(names[k]);
                string source = ordinal >= 0
                    ? $"[{i}:{clip.Audio[ordinal].Index}]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={F(clip.Duration)}";
                graph.Add($"{source},asetpts=PTS-STARTPTS[a{i}_{k}]");
                concatInputs.Append($"[a{i}_{k}]");
            }
        }
        var outputs = new StringBuilder("[vout]");
        for (int k = 0; k < names.Count; k++) outputs.Append($"[aout{k}]");
        graph.Add($"{concatInputs}concat=n={clips.Count}:v=1:a={names.Count}{outputs}");

        if (music?.Audio.Count > 0)
        {
            string fade = music.Duration > total + 0.5 ? $",afade=t=out:st={F(Math.Max(0, total - MusicFadeSeconds))}:d={F(MusicFadeSeconds)}" : "";
            graph.Add($"[{clips.Count}:{music.Audio[0].Index}]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo," +
                      $"atrim=duration={F(total)},asetpts=PTS-STARTPTS{fade}[music]");
        }

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-nostats", "-progress", "pipe:1" };
        foreach (var clip in clips) args.AddRange(["-i", clip.Path]);
        if (music?.Audio.Count > 0) args.AddRange(["-i", music.Path]);
        // From a file: a long project's graph wouldn't fit on a command line.
        args.AddRange(["-/filter_complex", graphFile, "-map", "[vout]"]);
        ExportBuilder.AddEncoder(args, encoder, "best", appleTag: true);

        var tracks = names.Select((n, k) => ($"[aout{k}]", n)).ToList();
        if (music?.Audio.Count > 0) tracks.Add(("[music]", MusicTrackName));
        for (int o = 0; o < tracks.Count; o++)
            args.AddRange(["-map", tracks[o].Item1, $"-c:a:{o}", "aac", $"-b:a:{o}", "256k",
                $"-metadata:s:a:{o}", $"title={tracks[o].Item2}", $"-metadata:s:a:{o}", $"handler_name={tracks[o].Item2}"]);
        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", output]);
        return (args, string.Join(";\n", graph));
    }

    public static async Task ExportAsync(IReadOnlyList<MediaInfo> clips, MediaInfo? music, VideoEncoder encoder, string output,
        IProgress<double> progress, CancellationToken ct)
    {
        string graphFile = Path.Combine(Path.GetTempPath(), $"quickclip-montage-{Guid.NewGuid():N}.txt");
        var (args, graph) = Build(clips, music, encoder, output, graphFile);
        await File.WriteAllTextAsync(graphFile, graph, ct);
        try
        {
            await ExportRunner.RunAsync(args, output, clips.Sum(c => c.Duration), progress, ct);
        }
        finally
        {
            try { File.Delete(graphFile); } catch { }
        }
    }

    // ---- Preview: the clips play one after another, each with its slice of the music -----------------

    /// <summary>An mpv EDL reference to part of a file (it then plays as if it were just that part).</summary>
    public static string EdlSlice(string path, double start, double length) =>
        $"edl://%{Encoding.UTF8.GetByteCount(path)}%{path},{F(start)},{F(length)}";

    /// <summary>
    /// mpv's lavfi-complex mixing a clip's own audio tracks and the music slice added after them (external tracks
    /// come last), and the audio track to select when there's nothing to mix.
    /// </summary>
    public static (string LavfiComplex, string Aid) PreviewMix(int clipTracks, bool music)
    {
        int n = clipTracks + (music ? 1 : 0);
        if (n <= 1) return ("", n == 1 ? "1" : "no");
        return (string.Concat(Enumerable.Range(1, n).Select(i => $"[aid{i}]")) + $"amix=inputs={n}:normalize=0:dropout_transition=0[ao]", "auto");
    }
}

/// <summary>Where each clip sits on the montage's timeline.</summary>
internal sealed class MontageTimeline
{
    private readonly double[] _starts;

    public MontageTimeline(IReadOnlyList<double> durations)
    {
        _starts = new double[durations.Count + 1];
        for (int i = 0; i < durations.Count; i++) _starts[i + 1] = _starts[i] + Math.Max(0, durations[i]);
    }

    public int Count => _starts.Length - 1;
    public double Total => _starts[^1];
    public double Start(int index) => _starts[Math.Clamp(index, 0, Count)];
    public double Duration(int index) => Start(index + 1) - Start(index);

    /// <summary>The clip playing at montage time <paramref name="t"/>, and how far into it.</summary>
    public (int Index, double Offset) Locate(double t)
    {
        if (Count == 0) return (-1, 0);
        t = Math.Clamp(t, 0, Total);
        int index = Count - 1;
        for (int i = 0; i < Count; i++)
            if (t < _starts[i + 1]) { index = i; break; }
        return (index, t - _starts[index]);
    }

    /// <summary>The part of the music under clip <paramref name="index"/>, or null once the music has ended.</summary>
    public (double Start, double Length)? MusicSlice(int index, double musicDuration)
    {
        double start = Start(index);
        if (musicDuration <= start + 0.05) return null;
        return (start, Math.Min(Duration(index), musicDuration - start));
    }
}
