using QuickClip.Media;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class ExportTests(MediaFixture media)
{
    private static TrackExport Track(AudioStreamInfo a, double volume) =>
        new(a.Index, a.Codec, a.Channels, a.BitRate, a.DisplayName, volume);

    private async Task<(MediaInfo Source, MediaInfo Output)> Export(string outName, Container container,
        Func<MediaInfo, IReadOnlyList<TrackExport>> tracks, Func<ExportRequest, ExportRequest>? tweak = null,
        double start = 1, double end = 4, string? source = null)
    {
        var src = await MediaInfo.ProbeAsync(source ?? media.TwoTracks);
        string output = media.Out(outName);
        var request = new ExportRequest
        {
            Media = src,
            OutputPath = output,
            Container = container,
            Start = start,
            End = end,
            Tracks = tracks(src),
            EncoderId = "libx264",
        };
        if (tweak != null) request = tweak(request);
        var args = ExportBuilder.BuildArgs(request);
        await ExportRunner.RunAsync(args, output, request.Duration, new Progress<double>(), CancellationToken.None);
        Assert.True(File.Exists(output));
        return (src, await MediaInfo.ProbeAsync(output));
    }

    [Fact]
    public async Task Probe_reads_track_names_and_video()
    {
        var info = await MediaInfo.ProbeAsync(media.TwoTracks);
        Assert.Equal(640, info.Video!.Width);
        Assert.Equal(360, info.Video.Height);
        Assert.Equal(30, info.Video.Fps, 1);
        Assert.Equal(["Mic", "Desktop Audio"], info.Audio.Select(a => a.DisplayName));
        Assert.Equal([1, 2], info.Audio.Select(a => a.Channels));
        Assert.Equal(6, info.Duration, 1);
    }

    [Fact]
    public async Task Reencode_trims_crops_and_applies_per_track_volume()
    {
        var (src, outInfo) = await Export("trim-crop.mp4", Containers.Mp4,
            s => [Track(s.Audio[0], 1), Track(s.Audio[1], 0.5)],
            r => r with { Crop = (100, 50, 320, 180) });

        Assert.Equal(3, outInfo.Duration, 1);
        Assert.Equal(320, outInfo.Video!.Width);
        Assert.Equal(180, outInfo.Video.Height);
        Assert.Equal(["Mic", "Desktop Audio"], outInfo.Audio.Select(a => a.DisplayName));

        double micBefore = await MediaFixture.BandLevel(src.Path, 0, MediaFixture.MicHz);
        double micAfter = await MediaFixture.BandLevel(outInfo.Path, 0, MediaFixture.MicHz);
        double deskBefore = await MediaFixture.BandLevel(src.Path, 1, MediaFixture.DesktopHz);
        double deskAfter = await MediaFixture.BandLevel(outInfo.Path, 1, MediaFixture.DesktopHz);
        Assert.InRange(micAfter - micBefore, -1.5, 1.5);
        Assert.InRange(deskAfter - deskBefore, -7.5, -4.5); // 50% = -6 dB
    }

    [Fact]
    public async Task Merge_mixes_all_tracks_into_one()
    {
        var (_, outInfo) = await Export("merged.mp4", Containers.Mp4,
            s => [Track(s.Audio[0], 1), Track(s.Audio[1], 1)],
            r => r with { MergeAudio = true });

        var a = Assert.Single(outInfo.Audio);
        Assert.Equal("Mic + Desktop Audio", a.DisplayName);
        Assert.True(await MediaFixture.BandLevel(outInfo.Path, 0, MediaFixture.MicHz) > -40);
        Assert.True(await MediaFixture.BandLevel(outInfo.Path, 0, MediaFixture.DesktopHz) > -40);
    }

    [Fact]
    public async Task Merge_with_a_muted_track_leaves_it_out()
    {
        var (_, outInfo) = await Export("merged-muted.mp4", Containers.Mp4,
            s => [Track(s.Audio[0], 1), Track(s.Audio[1], 0)],
            r => r with { MergeAudio = true });

        double mic = await MediaFixture.BandLevel(outInfo.Path, 0, MediaFixture.MicHz);
        double desk = await MediaFixture.BandLevel(outInfo.Path, 0, MediaFixture.DesktopHz);
        Assert.True(mic - desk > 30, $"mic {mic} dB vs desktop {desk} dB");
    }

    [Fact]
    public async Task Unchecked_track_is_dropped()
    {
        var (_, outInfo) = await Export("one-track.mp4", Containers.Mp4, s => [Track(s.Audio[1], 1)]);
        Assert.Equal("Desktop Audio", Assert.Single(outInfo.Audio).DisplayName);
    }

    [Fact]
    public async Task Stream_copy_keeps_the_codec_and_cuts_near_the_selection()
    {
        var (_, outInfo) = await Export("copy.mp4", Containers.Mp4,
            s => [Track(s.Audio[0], 1), Track(s.Audio[1], 1)],
            r => r with { VideoMode = VideoMode.Copy });

        Assert.Equal("h264", outInfo.Video!.Codec);
        Assert.InRange(outInfo.Duration, 2.9, 4.2); // keyframes every second
        Assert.Equal(2, outInfo.Audio.Count);
    }

    [Fact]
    public async Task Copied_audio_starts_at_the_in_point_not_the_keyframe_before_it()
    {
        // Players that ignore MP4 edit lists (Windows' player, Discord) would play audio from before the in point, that
        // much late. The source has a keyframe every second, so 1.5 s is half a second past one.
        var (_, outInfo) = await Export("copied-audio.mp4", Containers.Mp4, s => [Track(s.Audio[0], 1)], start: 1.5, end: 4);
        Assert.Equal(0.0, await FirstPacketTime(outInfo.Path, "a:0"), 0.03);
    }

    [Fact]
    public async Task Keyframes_are_listed_from_the_start_of_the_file()
    {
        var keys = await MediaInfo.KeyframesAsync(await MediaInfo.ProbeAsync(media.TwoTracks));
        Assert.Equal([0.0, 1, 2, 3, 4, 5], keys.Select(k => Math.Round(k, 2)));
    }

    [Fact]
    public async Task Video_is_copied_unless_something_needs_it_re_encoded()
    {
        var src = await MediaInfo.ProbeAsync(media.TwoTracks);
        var keys = await MediaInfo.KeyframesAsync(src);
        var r = new ExportRequest
        {
            Media = src, OutputPath = media.Out("x.mp4"), Container = Containers.Mp4, Start = 0, End = 4, Tracks = [], Quality = "original",
        };
        Assert.Null(ExportBuilder.ReencodeReason(r, false, keys));
        Assert.Null(ExportBuilder.ReencodeReason(r with { Start = 2 }, false, keys));            // on a keyframe
        Assert.Null(ExportBuilder.ReencodeReason(r with { Container = Containers.Mkv }, false, keys));
        Assert.Null(ExportBuilder.ReencodeReason(r with { MaxShortSide = 1080, Fps = 60 }, false, keys)); // no smaller or slower
        Assert.Contains("keyframe", ExportBuilder.ReencodeReason(r with { Start = 2.5 }, false, keys));
        Assert.Contains("keyframe", ExportBuilder.ReencodeReason(r with { Start = 2 }, false, null)); // not known yet
        Assert.Contains("cropped", ExportBuilder.ReencodeReason(r with { Crop = (0, 0, 320, 180) }, false, keys));
        Assert.Contains("resolution", ExportBuilder.ReencodeReason(r with { MaxShortSide = 240 }, false, keys));
        Assert.Contains("frame rate", ExportBuilder.ReencodeReason(r with { Fps = 15 }, false, keys));
        Assert.Contains("quality", ExportBuilder.ReencodeReason(r with { Quality = "high" }, false, keys));
        Assert.Contains("encoder", ExportBuilder.ReencodeReason(r, true, keys));
        Assert.Contains("WEBM", ExportBuilder.ReencodeReason(r with { Container = Containers.WebM }, false, keys));
    }

    [Fact]
    public async Task A_copy_from_a_keyframe_starts_exactly_there()
    {
        var (_, outInfo) = await Export("copy-key.mp4", Containers.Mp4, s => [Track(s.Audio[0], 1)],
            r => r with { VideoMode = VideoMode.Copy }, start: 2, end: 4);
        Assert.Equal(2, outInfo.Duration, 0.05);
        Assert.Equal(0.0, await FirstPacketTime(outInfo.Path, "v:0"), 0.01);
        Assert.Equal(0.0, await FirstPacketTime(outInfo.Path, "a:0"), 0.03);
    }

    [Fact]
    public async Task Same_as_original_quality_keeps_about_the_original_bitrate()
    {
        var (src, outInfo) = await Export("original-quality.mp4", Containers.Mp4, s => [Track(s.Audio[0], 1)],
            r => r with { Quality = "original" }, start: 0, end: 6);
        double target = Math.Max(src.Video!.BitRate, 500_000);
        Assert.InRange(outInfo.Video!.BitRate, target * 0.6, target * 1.4);
    }

    private static async Task<double> FirstPacketTime(string path, string stream)
    {
        var (_, packets, _) = await Proc.RunAsync(Deps.FFprobe!,
            ["-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time", "-of", "csv=p=0", "-read_intervals", "%+#1", path]);
        return double.Parse(packets.Trim().Split('\n')[0].Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Stream_copy_refuses_to_crop()
    {
        var src = await MediaInfo.ProbeAsync(media.TwoTracks);
        var request = new ExportRequest
        {
            Media = src, OutputPath = media.Out("x.mp4"), Container = Containers.Mp4, Start = 0, End = 2,
            Tracks = [], VideoMode = VideoMode.Copy, Crop = (0, 0, 320, 180),
        };
        Assert.Throws<ExportException>(() => ExportBuilder.BuildArgs(request));
    }

    [Fact]
    public async Task Gif_has_no_audio_and_respects_the_size_cap()
    {
        var (_, outInfo) = await Export("clip.gif", Containers.Gif, s => [Track(s.Audio[0], 1)],
            r => r with { MaxShortSide = 240 });
        Assert.Equal("gif", outInfo.Video!.Codec);
        Assert.Equal(240, outInfo.Video.Height);
        Assert.Empty(outInfo.Audio);
    }

    [Fact]
    public async Task Audio_only_format_merges_tracks()
    {
        var (_, outInfo) = await Export("clip.mp3", Containers.Mp3, s => [Track(s.Audio[0], 1), Track(s.Audio[1], 1)]);
        Assert.Null(outInfo.Video);
        Assert.Equal("mp3", Assert.Single(outInfo.Audio).Codec);
    }

    [Fact]
    public async Task WebM_uses_vp9_and_opus()
    {
        var (_, outInfo) = await Export("clip.webm", Containers.WebM, s => [Track(s.Audio[0], 1)],
            r => r with { EncoderId = "libvpx-vp9" });
        Assert.Equal("vp9", outInfo.Video!.Codec);
        Assert.Equal("opus", Assert.Single(outInfo.Audio).Codec);
    }

    [Fact]
    public async Task Target_size_stays_under_the_limit()
    {
        var (_, outInfo) = await Export("small.mp4", Containers.Mp4, s => [Track(s.Audio[0], 1)],
            r => r with { Quality = "size", TargetSizeMB = 0.4, AudioBitrateK = 64 });
        Assert.True(outInfo.Size <= 0.4 * 1_000_000 * 1.05, $"{outInfo.Size} bytes");
    }

    [Fact]
    public async Task Same_container_copies_opus_tracks_from_mkv()
    {
        var (_, outInfo) = await Export("three.mkv", Containers.ForExtension(".mkv", true),
            s => s.Audio.Select(a => Track(a, 1)).ToList(), start: 0.5, end: 3, source: media.ThreeTracks);
        Assert.Equal(["Mic", "Game", "Discord"], outInfo.Audio.Select(a => a.DisplayName));
        Assert.All(outInfo.Audio, a => Assert.Equal("opus", a.Codec));
    }

    /// <summary>A recording laid out like QuickClip's: Desktop, Chat, Music and Mic tracks, named in the MP4 handler.</summary>
    private async Task<string> RecorderLikeClip()
    {
        string path = media.Out("recorder-like.mp4");
        if (File.Exists(path)) return path;
        await MediaFixture.Run(
        [
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60",
            "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=700:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=1100:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=1500:sample_rate=48000",
            "-t", "5", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-ac", "2",
            "-metadata:s:a:0", "handler_name=Desktop", "-metadata:s:a:1", "handler_name=Chat",
            "-metadata:s:a:2", "handler_name=Music", "-metadata:s:a:3", "handler_name=Mic", path,
        ]);
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trimming_a_recording_keeps_all_four_named_tracks(bool copyVideo)
    {
        var (_, outInfo) = await Export($"trimmed-{copyVideo}.mp4", Containers.Mp4, s => s.Audio.Select(a => Track(a, 1)).ToList(),
            r => r with { VideoMode = copyVideo ? VideoMode.Copy : VideoMode.Encode }, start: 1, end: 3, source: await RecorderLikeClip());

        Assert.Equal(["Desktop", "Chat", "Music", "Mic"], outInfo.Audio.Select(a => a.DisplayName));
        Assert.All(outInfo.Audio, a => Assert.Equal(2, a.Channels));
        Assert.InRange(outInfo.Duration, 1.9, 3.1);
    }

    public static TheoryData<string> Encoders => [.. VideoEncoders.All.Where(e => e.Export).Select(e => e.Id)];

    /// <summary>Runs every encoder this machine supports (GPU ones only if the hardware is present).</summary>
    [Theory]
    [MemberData(nameof(Encoders))]
    public async Task Every_available_encoder_produces_its_codec(string encoderId)
    {
        var available = await Proc.RunAsync(Deps.FFmpeg!,
        [
            "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.2",
            "-frames:v", "3", "-pix_fmt", "yuv420p", "-c:v", encoderId, "-f", "null", "-",
        ]);
        if (available.ExitCode != 0) return; // not supported here

        var enc = VideoEncoders.Find(encoderId)!;
        var container = enc.Family == "vp9" ? Containers.WebM : Containers.Mkv;
        foreach (var quality in new[] { "high", "size" })
        {
            var (_, outInfo) = await Export($"enc-{encoderId}-{quality}{container.Extension}", container, s => [Track(s.Audio[0], 1)],
                r => r with { EncoderId = encoderId, Quality = quality, TargetSizeMB = 1 });
            Assert.Equal(enc.Family, outInfo.Video!.Codec);
        }
    }

}
