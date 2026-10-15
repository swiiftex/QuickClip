using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class SoundPlacementTests
{
    [Fact]
    public void A_sound_after_the_window_start_is_delayed_and_padded_to_the_window()
    {
        string chain = AddedSound.Placement(offset: 3, windowStart: 1, windowLength: 4);
        Assert.Contains("adelay=delays=96000S:all=1", chain); // 2 s at 48 kHz
        Assert.DoesNotContain("atrim=start", chain);
        Assert.EndsWith("apad=whole_dur=4,atrim=end=4", chain);
    }

    [Fact]
    public void A_sound_before_the_window_start_loses_its_beginning()
    {
        string chain = AddedSound.Placement(offset: 0.5, windowStart: 2, windowLength: 3);
        Assert.Contains("atrim=start=1.5,asetpts=PTS-STARTPTS", chain);
        Assert.DoesNotContain("adelay", chain);
    }

    [Fact]
    public void A_sound_at_the_window_start_is_only_padded()
    {
        string chain = AddedSound.Placement(offset: 2, windowStart: 2, windowLength: 3);
        Assert.DoesNotContain("adelay", chain);
        Assert.DoesNotContain("atrim=start", chain);
    }

    [Fact]
    public void Added_sounds_stay_partly_inside_the_clip()
    {
        var timeline = new Controls.TimelineState();
        timeline.Reset(10);
        var info = new AudioStreamInfo { Index = 0, Ordinal = 2, Codec = "mp3", Channels = 2, Title = "Song" };
        var track = AudioTrack.ForSound(@"C:\song.mp3", info, 4, timeline, 3);
        Assert.True(track.IsSound);
        Assert.Equal(3, track.Offset);
        track.MoveTo(-100);
        Assert.Equal(-3.95, track.Offset, 3);
        track.MoveTo(100);
        Assert.Equal(9.95, track.Offset, 3);
        Assert.Equal("Song", track.Name);
    }
}

[Collection("media")]
public sealed class AddedSoundTests(MediaFixture media)
{
    private const int SoundHz = 1000;

    /// <summary>A one-second tone, like a short sound effect.</summary>
    private async Task<string> Sound()
    {
        string path = media.Out("beep.mp3");
        if (!File.Exists(path))
            await MediaFixture.Run(["-f", "lavfi", "-i", $"sine=frequency={SoundHz}:sample_rate=44100", "-t", "1", "-c:a", "libmp3lame", path]);
        return path;
    }

    private static TrackExport Track(AudioStreamInfo a) => new(a.Index, a.Codec, a.Channels, a.BitRate, a.DisplayName, 1);

    private async Task<MediaInfo> Export(string name, double start, double end, double soundOffset, bool merge)
    {
        var src = await MediaInfo.ProbeAsync(media.TwoTracks);
        var sound = await MediaInfo.ProbeAsync(await Sound());
        var s = sound.Audio[0];
        string output = media.Out(name);
        var request = new ExportRequest
        {
            Media = src,
            OutputPath = output,
            Container = Containers.Mp4,
            Start = start,
            End = end,
            Tracks =
            [
                Track(src.Audio[0]), Track(src.Audio[1]),
                new TrackExport(s.Index, s.Codec, s.Channels, s.BitRate, "Beep", 1) { SoundPath = sound.Path, Offset = soundOffset },
            ],
            MergeAudio = merge,
            EncoderId = "libx264",
        };
        await ExportRunner.RunAsync(ExportBuilder.BuildArgs(request), output, request.Duration, new Progress<double>(), CancellationToken.None);
        return await MediaInfo.ProbeAsync(output);
    }

    [Fact]
    public async Task An_added_sound_is_exported_as_its_own_track_where_it_was_placed()
    {
        // The export runs from 1 s to 4 s of the clip; the sound starts at 2 s, so 1 s into the export.
        var output = await Export("with-sound.mp4", start: 1, end: 4, soundOffset: 2, merge: false);

        Assert.Equal(["Mic", "Desktop Audio", "Beep"], output.Audio.Select(a => a.DisplayName));
        Assert.Equal(3, output.Duration, 1);
        Assert.True(await MediaFixture.BandLevel(output.Path, 2, SoundHz, 0, 0.8) < -60, "the sound plays before its start");
        Assert.True(await MediaFixture.BandLevel(output.Path, 2, SoundHz, 1.2, 0.6) > -30, "the sound is missing where it was placed");
        Assert.True(await MediaFixture.BandLevel(output.Path, 2, SoundHz, 2.2, 0.7) < -60, "the sound plays past its end");
        // The clip's own tracks are untouched.
        Assert.True(await MediaFixture.BandLevel(output.Path, 0, MediaFixture.MicHz) > -40);
    }

    [Fact]
    public async Task A_sound_that_starts_before_the_export_is_cut_and_merged()
    {
        // The sound runs from 0.5 s to 1.5 s of the clip; the export starts at 1 s, so only its last half second is in it.
        var output = await Export("merged-sound.mp4", start: 1, end: 4, soundOffset: 0.5, merge: true);

        var track = Assert.Single(output.Audio);
        Assert.Equal("Mic + Desktop Audio + Beep", track.DisplayName);
        // The clip's own tones leak a little through the filter, so compare with the sound playing.
        double during = await MediaFixture.BandLevel(output.Path, 0, SoundHz, 0, 0.4);
        double after = await MediaFixture.BandLevel(output.Path, 0, SoundHz, 0.8, 2);
        Assert.True(during > -30, $"the end of the sound is missing ({during:0.0} dB)");
        Assert.True(during - after > 25, $"the sound plays for too long ({during:0.0} dB, then {after:0.0} dB)");
        Assert.True(await MediaFixture.BandLevel(output.Path, 0, MediaFixture.DesktopHz) > -40);
    }

    [Fact]
    public async Task The_preview_copy_spans_the_clip_with_the_sound_in_place()
    {
        var sound = await MediaInfo.ProbeAsync(await Sound());
        string preview = media.Out("preview.flac");
        await AddedSound.RenderPreviewAsync(sound.Path, sound.Audio[0].Index, offset: 1.5, clipDuration: 6, preview, CancellationToken.None);

        var info = await MediaInfo.ProbeAsync(preview);
        Assert.Equal(6, info.Duration, 1);
        Assert.Equal(2, Assert.Single(info.Audio).Channels);
        Assert.True(await MediaFixture.BandLevel(preview, 0, SoundHz, 0, 1.3) < -60);
        Assert.True(await MediaFixture.BandLevel(preview, 0, SoundHz, 1.7, 0.6) > -30);
        Assert.True(await MediaFixture.BandLevel(preview, 0, SoundHz, 2.7, 3) < -60);
    }
}
