using QuickClip.Controls;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class CropTests
{
    private static CropController Make(bool enabled = true)
    {
        var crop = new CropController();
        crop.Reset(1920, 1080);
        crop.SetOsd(960, 540, 0, 0, 0, 0); // video fills the view at half scale
        crop.SetEnabled(enabled);
        return crop;
    }

    [Fact]
    public void Full_frame_or_disabled_means_no_crop()
    {
        Assert.Null(Make().ExportRect());
        var crop = Make(enabled: false);
        crop.SetRect(10, 10, 100, 100);
        Assert.Null(crop.ExportRect());
    }

    [Fact]
    public void Export_rect_uses_even_numbers()
    {
        var crop = Make();
        crop.SetRect(101, 51, 333, 187);
        var r = crop.ExportRect()!.Value;
        Assert.True(r.X % 2 == 0 && r.Y % 2 == 0 && r.W % 2 == 0 && r.H % 2 == 0, r.ToString());
        Assert.InRange(r.W, 332, 334);
    }

    [Fact]
    public void Vertical_aspect_fills_the_height_centred()
    {
        var crop = Make();
        crop.SetAspect(9 / 16.0);
        var r = crop.ExportRect()!.Value;
        Assert.Equal(1080, r.H);
        Assert.InRange(r.W, 606, 608);
        Assert.InRange(r.X, 654, 658);
    }

    [Fact]
    public void Dragging_outside_draws_a_new_rect_in_source_pixels()
    {
        var crop = Make();
        crop.SetRect(800, 400, 200, 200);
        crop.MouseDown(10, 10);      // screen px -> source (20, 20)
        crop.MouseMove(110, 60);     // -> source (220, 120)
        crop.MouseUp();
        Assert.Equal(new System.Windows.Rect(20, 20, 200, 100), crop.Rect);
    }

    [Fact]
    public void Corner_drag_keeps_the_aspect_lock_and_stays_inside_the_frame()
    {
        var crop = Make();
        crop.SetRect(0, 0, 400, 400);
        crop.SetAspect(1);
        crop.MouseDown(200, 200);    // bottom-right handle of the 400x400 source rect
        crop.MouseMove(900, 500);    // far outside, wider than tall
        crop.MouseUp();
        var r = crop.Rect;
        Assert.Equal(r.Width, r.Height, 3);
        Assert.True(r.Bottom <= 1080 && r.Right <= 1920, r.ToString());
    }

    [Fact]
    public void Moving_is_clamped_to_the_frame()
    {
        var crop = Make();
        crop.SetRect(100, 100, 400, 300);
        crop.MouseDown(150, 150);
        crop.MouseMove(2000, 2000);
        crop.MouseUp();
        Assert.Equal(new System.Windows.Rect(1520, 780, 400, 300), crop.Rect);
    }

    [Fact]
    public void Overlay_is_built_only_when_enabled()
    {
        Assert.Null(Make(enabled: false).BuildOverlay());
        var overlay = Make().BuildOverlay();
        Assert.NotNull(overlay);
        Assert.Equal(960, overlay!.Value.ResX);
        Assert.Contains(@"\p1", overlay.Value.Ass);
    }
}

public sealed class TimelineTests
{
    [Fact]
    public void Setting_in_after_out_resets_out_to_the_end()
    {
        var t = new TimelineState();
        t.Reset(60);
        t.SetOut(20);
        t.SetIn(30);
        Assert.Equal(30, t.In);
        Assert.Equal(60, t.Out);
    }

    [Fact]
    public void Setting_out_before_in_resets_in_to_the_start()
    {
        var t = new TimelineState();
        t.Reset(60);
        t.SetIn(30);
        t.SetOut(10);
        Assert.Equal(0, t.In);
        Assert.Equal(10, t.Out);
    }

    [Fact]
    public void Zoom_and_pan_stay_inside_the_file()
    {
        var t = new TimelineState();
        t.Reset(100);
        t.ZoomAt(90, 0.1);
        Assert.Equal(10, t.ViewSpan, 6);
        t.Pan(50);
        Assert.Equal(100, t.ViewEnd, 6);
        t.Fit();
        Assert.Equal(0, t.ViewStart);
        Assert.Equal(100, t.ViewEnd);
    }
}

public sealed class FormatTests
{
    [Theory]
    [InlineData(".MP4", "mp4")]
    [InlineData(".mkv", "mkv")]
    [InlineData(".m4v", "m4v")]
    [InlineData(".ts", "ts")]
    [InlineData(".avi", "avi")]
    [InlineData(".wmv", "asf")]
    [InlineData(".flac", "flac")]
    [InlineData(".xyz", "other")]
    public void Source_extension_maps_to_a_container(string ext, string key) =>
        Assert.Equal(key, Containers.ForExtension(ext, sourceHasVideo: true).Key);

    [Fact]
    public void Copy_rules()
    {
        Assert.True(Containers.Mp4.CanCopyAudio("aac"));
        Assert.False(Containers.WebM.CanCopyAudio("aac"));
        Assert.True(Containers.Mkv.CanCopyAudio("truehd"));
        Assert.True(Containers.Wav.CanCopyAudio("pcm_s24le"));
        Assert.False(Containers.WebM.CanCopyVideo("h264"));
    }

    [Theory]
    [InlineData("1:23.5", 83.5)]
    [InlineData("83.5", 83.5)]
    [InlineData("1:02:03", 3723)]
    public void Time_parsing(string text, double seconds)
    {
        Assert.True(Fmt.TryParseTime(text, out var v));
        Assert.Equal(seconds, v, 6);
    }
}
