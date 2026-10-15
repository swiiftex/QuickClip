using System.Globalization;

namespace QuickClip.Media;

/// <summary>
/// A sound or song added to a clip as another audio track, starting anywhere on the clip's timeline. The export and
/// the preview place it with the same filters.
/// </summary>
internal static class AddedSound
{
    private const int Rate = 48000;

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Filters (for one of the sound's audio streams) that give the part of the clip's timeline from
    /// <paramref name="windowStart"/> for <paramref name="windowLength"/> seconds, with the sound starting at
    /// <paramref name="offset"/> on that timeline (a negative offset cuts off its beginning) and silence wherever it
    /// isn't playing. Stereo, 48 kHz, exactly the window's length.
    /// </summary>
    public static string Placement(double offset, double windowStart, double windowLength)
    {
        double shift = offset - windowStart; // where the sound starts in the window
        var chain = new List<string> { $"aformat=sample_fmts=fltp:sample_rates={Rate}:channel_layouts=stereo", "asetpts=PTS-STARTPTS" };
        if (shift > 0.0005) chain.Add($"adelay=delays={Math.Round(shift * Rate)}S:all=1");
        else if (shift < -0.0005) chain.AddRange([$"atrim=start={F(-shift)}", "asetpts=PTS-STARTPTS"]);
        chain.Add($"apad=whole_dur={F(windowLength)}");
        chain.Add($"atrim=end={F(windowLength)}");
        return string.Join(",", chain);
    }

    /// <summary>
    /// Writes the sound placed on a clip's whole timeline to <paramref name="output"/> (FLAC) for the preview, which
    /// plays it as one more track of the clip, so it stays in place when seeking.
    /// </summary>
    public static async Task RenderPreviewAsync(string sound, int streamIndex, double offset, double clipDuration, string output,
        CancellationToken ct)
    {
        if (Deps.FFmpeg == null) throw new InvalidOperationException("ffmpeg.exe was not found.");
        bool ok = false;
        try
        {
            var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg,
            [
                "-hide_banner", "-nostdin", "-y", "-v", "error", "-i", sound, "-map", $"0:{streamIndex}",
                "-af", Placement(offset, 0, clipDuration), "-c:a", "flac", "-compression_level", "0", output,
            ], ct);
            if (code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"FFmpeg exited with code {code}." : err.Trim());
            ok = true;
        }
        finally
        {
            if (!ok) try { File.Delete(output); } catch { }
        }
    }
}
