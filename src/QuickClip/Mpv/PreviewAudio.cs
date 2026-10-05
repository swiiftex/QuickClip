namespace QuickClip.Mpv;

/// <summary>
/// Plays every audio track at once, each at its own volume.
/// lavfi-complex merges the tracks into one multichannel stream (two channels per track); the "qc" audio
/// filter splits that back per track, applies a named volume filter to each and mixes them.
/// Volumes change instantly through af-command, but mpv rebuilds the filter graph from its definition on
/// every seek, so the definition is also rewritten (<see cref="Sync"/>) to keep the volumes.
/// </summary>
internal sealed class PreviewAudio(MpvPlayer player)
{
    public const string FilterLabel = "qc";

    private string? _appliedFilter;

    public static (string LavfiComplex, string AudioFilter) Build(int trackCount, Func<int, double> volume)
    {
        int n = trackCount;
        if (n <= 0) return ("", "");
        if (n == 1) return ("", $"@{FilterLabel}:lavfi=[volume@t1={Fmt.Num(volume(0))}]");

        var inputs = Enumerable.Range(1, n).Select(i => $"[aid{i}]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[qa{i}]");
        string complex = string.Join(";", inputs) + ";" + string.Concat(Enumerable.Range(1, n).Select(i => $"[qa{i}]")) + $"amerge=inputs={n}[ao]";

        var graph = new List<string> { "asplit=" + n + string.Concat(Enumerable.Range(1, n).Select(i => $"[s{i}]")) };
        for (int i = 1; i <= n; i++)
            graph.Add($"[s{i}]pan=stereo|c0=c{2 * (i - 1)}|c1=c{2 * (i - 1) + 1},volume@t{i}={Fmt.Num(volume(i - 1))}[x{i}]");
        graph.Add(string.Concat(Enumerable.Range(1, n).Select(i => $"[x{i}]")) + $"amix=inputs={n}:normalize=0:dropout_transition=0");
        return (complex, $"@{FilterLabel}:lavfi=[{string.Join(";", graph)}]");
    }

    /// <summary>Sets up the graph for a file with <paramref name="trackCount"/> audio tracks. Call before loading it.</summary>
    public void Configure(int trackCount, Func<int, double> volume)
    {
        var (complex, filter) = Build(trackCount, volume);
        player.SetProperty("lavfi-complex", complex);
        player.SetProperty("af", filter);
        player.SetProperty("aid", trackCount >= 1 ? "1" : "no");
        _appliedFilter = filter;
    }

    /// <summary>Changes the volumes of the running graph without interrupting playback.</summary>
    public void SetLive(int trackCount, Func<int, double> volume)
    {
        // Fails harmlessly while the graph doesn't exist yet (before audio starts flowing).
        for (int i = 0; i < trackCount; i++)
            player.TryCommand("af-command", FilterLabel, "volume", Fmt.Num(volume(i)), $"volume@t{i + 1}");
    }

    /// <summary>Rewrites the filter definition if the volumes changed since it was last applied.</summary>
    public void Sync(int trackCount, Func<int, double> volume)
    {
        var (_, filter) = Build(trackCount, volume);
        if (filter == _appliedFilter) return;
        player.SetProperty("af", filter);
        _appliedFilter = filter;
    }
}
