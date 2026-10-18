using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class SharePlanTests
{
    private static MediaInfo Clip(string codec, int width, int height, double fps, double seconds, double sizeMB, int tracks = 3)
    {
        var info = new MediaInfo
        {
            Path = @"C:\clips\clip.mp4",
            Duration = seconds,
            Size = (long)(sizeMB * 1e6),
            Video = new VideoStreamInfo { Index = 0, Codec = codec, Width = width, Height = height, Fps = fps },
        };
        for (int i = 0; i < tracks; i++)
            info.Audio.Add(new AudioStreamInfo { Index = i + 1, Ordinal = i, Codec = "aac", Channels = 2, Title = $"Track {i + 1}" });
        return info;
    }

    private static ExportRequest Plan(MediaInfo clip) => ShareCopy.Plan(clip, @"C:\out.mp4", "h264_amf");

    [Fact]
    public void A_small_h264_clip_keeps_its_video_and_only_has_its_audio_mixed()
    {
        var r = Plan(Clip("h264", 1920, 1080, 60, 20, 12));
        Assert.Equal(VideoMode.Copy, r.VideoMode);
        Assert.True(r.MergeAudio);
        Assert.Equal(3, r.Tracks.Count);
        Assert.Equal(Containers.Mp4, r.Container);
    }

    [Fact]
    public void A_long_recording_is_compressed_to_30_MB_at_a_smaller_size()
    {
        // A minute of 1440p60 AV1, as QuickClip records it.
        var r = Plan(Clip("av1", 2560, 1440, 60, 60, 290));
        Assert.Equal(VideoMode.Encode, r.VideoMode);
        Assert.Equal("size", r.Quality);
        Assert.Equal(30, r.TargetSizeMB);
        Assert.Equal("h264_amf", r.EncoderId);
        Assert.Equal(0, r.Fps);              // already 60 fps
        Assert.Equal(720, r.MaxShortSide);   // ~3.5 Mbit/s is too little for 1080p60
        Assert.True(r.MergeAudio);
    }

    [Fact]
    public void A_short_clip_keeps_more_of_its_picture()
    {
        var r = Plan(Clip("av1", 2560, 1440, 60, 20, 100));
        Assert.Equal(1080, r.MaxShortSide);
    }

    [Fact]
    public void High_frame_rates_are_capped_at_60()
    {
        var r = Plan(Clip("h264", 1920, 1080, 144, 10, 8));
        Assert.Equal(VideoMode.Encode, r.VideoMode); // small enough, but too many frames to copy
        Assert.Equal(60, r.Fps);
    }

    [Fact]
    public void A_small_clip_in_another_codec_is_not_blown_up_to_30_MB()
    {
        var r = Plan(Clip("av1", 1920, 1080, 60, 10, 4));
        Assert.Equal(VideoMode.Encode, r.VideoMode); // H.264 plays everywhere
        Assert.Equal(8, r.TargetSizeMB, 3);
    }

    [Theory]
    [InlineData(2560, 1440, 60, 20_000_000, 0)]      // plenty of bits: keep the size
    [InlineData(2560, 1440, 60, 3_500_000, 720)]
    [InlineData(1920, 1080, 30, 3_500_000, 0)]
    [InlineData(1280, 720, 60, 100_000, 360)]       // never smaller than 360p
    [InlineData(640, 360, 30, 100_000, 0)]          // already that small
    public void The_picture_shrinks_only_when_the_bitrate_is_too_low_for_it(int w, int h, double fps, double bps, int expected) =>
        Assert.Equal(expected, ShareCopy.ShortSideFor(w, h, fps, bps));

    [Fact]
    public void Sharing_prefers_a_gpu_h264_encoder()
    {
        Assert.Equal("h264_amf", ShareCopy.Encoder(new HashSet<string> { "libx264", "av1_amf", "h264_amf" }));
        Assert.Equal("libx264", ShareCopy.Encoder(new HashSet<string> { "libx264", "av1_amf" }));
    }
}

[Collection("media")]
public sealed class ShareTests(MediaFixture media)
{
    private sealed class Attempts : IProgress<(int Attempt, double Progress)>
    {
        public int Last;
        public void Report((int Attempt, double Progress) value) => Last = Math.Max(Last, value.Attempt);
    }

    [Fact]
    public async Task Sharing_a_small_clip_mixes_its_tracks_and_leaves_the_original_alone()
    {
        var before = new FileInfo(media.TwoTracks);
        (long size, DateTime written) original = (before.Length, before.LastWriteTimeUtc);
        var clip = await MediaInfo.ProbeAsync(media.TwoTracks);
        string output = media.Out("share-small.mp4");

        long size = await ShareCopy.CreateAsync(clip, output, "libx264", new Attempts(), CancellationToken.None);

        var shared = await MediaInfo.ProbeAsync(output);
        Assert.Equal(size, shared.Size);
        Assert.Equal("h264", shared.Video!.Codec);
        var track = Assert.Single(shared.Audio);
        Assert.Equal("Mic + Desktop Audio", track.DisplayName);
        Assert.True(await MediaFixture.BandLevel(output, 0, MediaFixture.MicHz) > -40);
        Assert.True(await MediaFixture.BandLevel(output, 0, MediaFixture.DesktopHz) > -40);
        Assert.Equal(clip.Duration, shared.Duration, 0);

        var after = new FileInfo(media.TwoTracks);
        Assert.Equal(original, (after.Length, after.LastWriteTimeUtc));
    }

    [Fact]
    public async Task Sharing_compresses_until_the_copy_fits_the_limit()
    {
        var clip = await MediaInfo.ProbeAsync(media.TwoTracks);
        string output = media.Out("share-tight.mp4");
        const double limitMB = 0.3; // far smaller than the clip, as 30 MB is for a long recording

        long size = await ShareCopy.CreateAsync(clip, output, "libx264", new Attempts(), CancellationToken.None, limitMB);

        Assert.True(size <= limitMB * 1e6, $"{size} bytes");
        var shared = await MediaInfo.ProbeAsync(output);
        Assert.Equal("h264", shared.Video!.Codec);
        Assert.Single(shared.Audio);
        Assert.Equal(clip.Duration, shared.Duration, 0);
    }
}
