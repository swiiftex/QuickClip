using System.Globalization;
using System.Text.RegularExpressions;
using QuickClip.Gallery;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class MontageTimelineTests
{
    private readonly MontageTimeline _timeline = new([10, 5, 20]);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(9.5, 0, 9.5)]
    [InlineData(10, 1, 0)]
    [InlineData(12.5, 1, 2.5)]
    [InlineData(34, 2, 19)]
    [InlineData(99, 2, 20)]   // past the end: the end of the last clip
    [InlineData(-3, 0, 0)]
    public void Montage_time_maps_to_a_clip(double t, int index, double offset)
    {
        var (i, o) = _timeline.Locate(t);
        Assert.Equal(index, i);
        Assert.Equal(offset, o, 6);
    }

    [Fact]
    public void Each_clip_gets_its_slice_of_the_music()
    {
        Assert.Equal(35, _timeline.Total);
        Assert.Equal((10.0, 5.0), _timeline.MusicSlice(1, 120));
        Assert.Equal((15.0, 3.0), _timeline.MusicSlice(2, 18));   // the song ends 3 s into the third clip
        Assert.Null(_timeline.MusicSlice(2, 15));                 // ...or before it
    }

    [Fact]
    public void Preview_mixes_every_track_with_the_music()
    {
        Assert.Equal(("[aid1][aid2][aid3][aid4][aid5]amix=inputs=5:normalize=0:dropout_transition=0[ao]", "auto"), Montage.PreviewMix(4, music: true));
        Assert.Equal(("", "1"), Montage.PreviewMix(1, music: false));
        Assert.Equal(("", "1"), Montage.PreviewMix(0, music: true));
        Assert.Equal(("", "no"), Montage.PreviewMix(0, music: false));
        Assert.Equal(@"edl://%15%C:\a,b\clip.mp4,10,5", Montage.EdlSlice(@"C:\a,b\clip.mp4", 10, 5)); // %bytes% protects the comma
    }
}

/// <summary>Joins the fixture recordings (different sizes, different audio tracks) and a song, then listens to the result.</summary>
[Collection("media")]
public sealed class MontageExportTests(MediaFixture media)
{
    [Fact]
    public async Task Clips_are_joined_with_tracks_matched_by_name_and_the_music_on_its_own()
    {
        string song = media.Out("song.wav");
        await MediaFixture.Run(["-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=44100", "-t", "30", song]);
        var first = await MediaInfo.ProbeAsync(media.TwoTracks);    // 640x360, 6 s: Mic 300 Hz, Desktop Audio 2000 Hz
        var second = await MediaInfo.ProbeAsync(media.ThreeTracks); // 320x240, 4 s: Mic 300 Hz, Game 2000 Hz, Discord 5000 Hz
        var music = await MediaInfo.ProbeAsync(song);
        string output = media.Out("montage.mp4");

        await Montage.ExportAsync([first, second], music, VideoEncoders.Find("libx264")!, output, new Progress<double>(), CancellationToken.None);

        var info = await MediaInfo.ProbeAsync(output);
        Assert.Equal((640, 360), (info.Video!.Width, info.Video.Height));
        Assert.InRange(info.Duration, 9.8, 10.4);
        Assert.Equal(["Mic", "Desktop Audio", "Game", "Discord", "Soundtrack"], info.Audio.Select(a => a.DisplayName));

        // Mic plays throughout; Desktop Audio only in the first clip, Game and Discord only in the second.
        Assert.True(await Level(output, 0, 300, 1, 4) > -40);
        Assert.True(await Level(output, 0, 300, 7, 2) > -40);
        Assert.True(await Level(output, 1, 2000, 1, 4) > -40);
        Assert.True(await Level(output, 1, 2000, 7, 2) < -70);
        Assert.True(await Level(output, 2, 2000, 1, 4) < -70);
        Assert.True(await Level(output, 2, 2000, 7, 2) > -40);
        Assert.True(await Level(output, 3, 5000, 7, 2) > -40);
        // The song plays under everything and fades out over the last 2 seconds.
        double full = await Level(output, 4, 1000, 2, 4), fading = await Level(output, 4, 1000, 9.3, 0.6);
        Assert.True(full > -40);
        Assert.True(fading < full - 6, $"music at the end {fading} dB vs {full} dB");
        File.Delete(output);
    }

    [Fact]
    public void Graph_fills_missing_tracks_with_silence_and_letterboxes_other_sizes()
    {
        var a = new MediaInfo { Path = "a.mp4", Duration = 5, Video = new VideoStreamInfo { Index = 0, Width = 2560, Height = 1440, Fps = 60 } };
        a.Audio.Add(new AudioStreamInfo { Index = 1, Ordinal = 0, Title = "Desktop" });
        var b = new MediaInfo { Path = "b.mp4", Duration = 3, Video = new VideoStreamInfo { Index = 0, Width = 5120, Height = 1440, Fps = 60 } };
        b.Audio.Add(new AudioStreamInfo { Index = 1, Ordinal = 0, Title = "Desktop" });
        b.Audio.Add(new AudioStreamInfo { Index = 2, Ordinal = 1, Title = "Mic" });

        var (args, graph) = Montage.Build([a, b], null, VideoEncoders.Find("libx264")!, "out.mp4", "graph.txt");

        Assert.Contains("[1:0]scale=2560:1440:force_original_aspect_ratio=decrease", graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=5,asetpts=PTS-STARTPTS[a0_1]", graph);
        Assert.Contains("concat=n=2:v=1:a=2[vout][aout0][aout1]", graph);
        Assert.Equal(["-/filter_complex", "graph.txt"], args.SkipWhile(x => x != "-/filter_complex").Take(2));
        Assert.DoesNotContain("[music]", graph);
    }

    private static async Task<double> Level(string file, int track, int hz, double from, double length)
    {
        var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg!,
        [
            "-hide_banner", "-nostdin", "-ss", Fmt.Num(from), "-t", Fmt.Num(length), "-i", file, "-map", $"0:a:{track}",
            "-af", $"bandpass=f={hz}:width_type=h:w=60,volumedetect", "-f", "null", "-",
        ]);
        Assert.True(code == 0, err);
        var m = Regex.Match(err, @"mean_volume:\s*(-?[\d.]+|-inf) dB");
        return m.Groups[1].Value == "-inf" ? -200 : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

[Collection("media")]
public sealed class ProjectLibraryTests(MediaFixture media) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quickclip-projects-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Clip(string folder, string name, string source)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.Copy(source, path);
        return path;
    }

    [Fact]
    public async Task Projects_keep_their_clips_order_and_music()
    {
        string a = Clip("Game", "a.mp4", media.TwoTracks), b = Clip("Game", "b.mp4", media.TwoTracks), c = Clip("Desktop", "c.mkv", media.ThreeTracks);
        var project = ProjectLibrary.Create(_root, "Frag: movie");
        Assert.Equal("Frag movie", project.Name);
        Assert.Equal(Path.Combine(_root, "Projects", "Frag movie"), project.Folder);

        Assert.Equal(3, await ProjectLibrary.AddClipsAsync(project, [a, b, c]));
        Assert.Equal(0, await ProjectLibrary.AddClipsAsync(project, [a]));            // already in it
        Assert.Equal(["a.mp4", "b.mp4", "c.mkv"], project.Clips.Select(Path.GetFileName));

        // Reorder and add music; it all comes back on load.
        project.Clips.Reverse();
        ProjectLibrary.Save(project);
        string song = Path.Combine(_root, "song.wav");
        await MediaFixture.Run(["-f", "lavfi", "-i", "sine=frequency=1000", "-t", "1", song]);
        await ProjectLibrary.SetMusicAsync(project, song);
        var loaded = ProjectLibrary.Load(project.Folder);
        Assert.Equal(["c.mkv", "b.mp4", "a.mp4"], loaded.Clips.Select(Path.GetFileName));
        Assert.Equal(Path.Combine(project.Folder, "Music", "song.wav"), loaded.Music);

        // A clip put in the folder by hand joins at the end; the project's copies outlive the gallery's.
        File.Copy(media.TwoTracks, Path.Combine(project.Folder, "z.mp4"));
        File.Delete(a);
        loaded = ProjectLibrary.Load(project.Folder);
        Assert.Equal(["c.mkv", "b.mp4", "a.mp4", "z.mp4"], loaded.Clips.Select(Path.GetFileName));
        Assert.Equal(new FileInfo(media.TwoTracks).Length, new FileInfo(loaded.Clips[2]).Length);

        // The gallery doesn't list project clips as more clips.
        Assert.Equal(["b.mp4", "c.mkv"], ClipLibrary.Scan(_root).Select(x => Path.GetFileName(x.Path)).Order());
        Assert.Equal([("Frag movie", 4)], ProjectLibrary.List(_root).Select(p => (p.Name, p.ClipCount)));

        var renamed = ProjectLibrary.Rename(loaded, "Season 3");
        Assert.Equal("Season 3", renamed.Name);
        Assert.Equal(["c.mkv", "b.mp4", "a.mp4", "z.mp4"], renamed.Clips.Select(Path.GetFileName));
        Assert.Equal(Path.Combine(renamed.Folder, "Music", "song.wav"), renamed.Music);
    }

    [Fact]
    public async Task Copying_out_never_overwrites()
    {
        string a = Clip("Game", "a.mp4", media.TwoTracks);
        string target = Path.Combine(_root, "Out");
        Directory.CreateDirectory(target);
        await ProjectLibrary.CopyToFolderAsync([a], target);
        await ProjectLibrary.CopyToFolderAsync([a], target);
        Assert.Equal(["a (2).mp4", "a.mp4"], Directory.GetFiles(target).Select(Path.GetFileName).Order());
    }
}
