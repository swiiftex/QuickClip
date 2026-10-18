using System.Globalization;
using System.Text.RegularExpressions;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class MotionBlurFilterTests
{
    [Fact]
    public void Blends_120_fps_into_60_with_each_neighbour_counting_half()
    {
        string f = MotionBlur.Filter(120, 60);
        Assert.Contains("tmix=frames=3:weights='1 2 1'", f);
        Assert.Contains("setpts=PTS-0.008333/TB", f); // back to the middle of the two frames it covers
        Assert.Contains("fps=60", f);
        Assert.Contains("t=linear", f);               // mixed in linear light
        Assert.EndsWith("format=yuv420p", f);
    }

    [Fact]
    public void Higher_frame_rates_blend_more_frames()
    {
        Assert.Contains("tmix=frames=5:weights='1 2 2 2 1'", MotionBlur.Filter(240, 60));
        Assert.Contains("tmix=frames=3:weights='1 2 1'", MotionBlur.Filter(144, 60));
        Assert.True(MotionBlur.Helps(90, 60));
        Assert.False(MotionBlur.Helps(75, 60)); // too few extra frames to be worth it
        Assert.False(MotionBlur.Helps(60, 60));
    }
}

[Collection("media")]
public sealed class MotionBlurTests(MediaFixture media)
{
    /// <summary>120 fps flipping between black and white every frame, tagged as BT.709 like a recording.</summary>
    private async Task<string> FlickerClip()
    {
        string path = media.Out("flicker-120.mp4");
        if (!File.Exists(path))
            await MediaFixture.Run(
            [
                "-f", "lavfi", "-i", "color=c=black:s=320x180:r=120:d=2,format=yuv420p,geq=lum='if(mod(N,2),235,16)':cb=128:cr=128",
                "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-pix_fmt", "yuv420p",
                "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709", "-color_range", "tv", path,
            ]);
        return path;
    }

    private async Task<(MediaInfo Info, List<double> Brightness)> Export(string name, bool blur)
    {
        var src = await MediaInfo.ProbeAsync(await FlickerClip());
        string output = media.Out(name);
        var request = new ExportRequest
        {
            Media = src, OutputPath = output, Container = Containers.Mp4, Start = 0, End = 2, Tracks = [],
            EncoderId = "libx264", Quality = "best", Fps = 60, MotionBlur = blur,
        };
        await ExportRunner.RunAsync(ExportBuilder.BuildArgs(request), output, request.Duration, new Progress<double>(), CancellationToken.None);

        var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg!,
            ["-hide_banner", "-nostdin", "-i", output, "-vf", "signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"]);
        Assert.True(code == 0, err);
        var brightness = Regex.Matches(err, @"lavfi\.signalstats\.YAVG=([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return (await MediaInfo.ProbeAsync(output), brightness);
    }

    [Fact]
    public async Task Blending_flickering_frames_gives_an_even_grey_mixed_in_linear_light()
    {
        var (info, brightness) = await Export("blurred-60.mp4", blur: true);
        Assert.Equal(60, info.Video!.Fps, 1);
        Assert.InRange(brightness.Count, 115, 125);
        // Every frame is half black, half white. Mixed in linear light that's about 170 on the 16-235 scale
        // (mixing the stored values would give 125, darker than the eye sees half the light as).
        Assert.All(brightness.Skip(2), y => Assert.InRange(y, 160, 186));
    }

    [Fact]
    public async Task Without_blending_every_other_frame_is_just_dropped()
    {
        var (info, brightness) = await Export("dropped-60.mp4", blur: false);
        Assert.Equal(60, info.Video!.Fps, 1);
        Assert.All(brightness.Skip(2), y => Assert.True(Math.Abs(y - 125) > 80, $"frame brightness {y}"));
    }
}
