using QuickClip.Gallery;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class RecordingPresetTests
{
    [Fact]
    public void Quality_presets_rise_monotonically()
    {
        var rates = Enum.GetValues<RecordingQuality>().Select(q => RecordingPresets.VideoKbps(q, 2560, 1440, 60, "av1")).ToList();
        Assert.Equal(rates.OrderBy(r => r), rates);
        Assert.True(rates.Distinct().Count() == rates.Count);
    }

    [Fact]
    public void Bitrate_scales_with_pixels_frame_rate_and_codec()
    {
        int av1_1080 = RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 60, "av1");
        Assert.Equal(16000, av1_1080);
        Assert.True(RecordingPresets.VideoKbps(RecordingQuality.High, 2560, 1440, 60, "av1") > av1_1080);
        Assert.True(RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 120, "av1") > av1_1080);
        Assert.True(RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 60, "h264") > RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 60, "hevc"));
    }

    [Theory]
    [InlineData(2560, 1440, 0, 2560, 1440)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(5120, 1440, 1080, 3840, 1080)]
    [InlineData(1080, 1920, 720, 720, 1280)]   // portrait monitor
    [InlineData(1920, 1080, 1440, 1920, 1080)] // never upscale
    public void Output_size_keeps_aspect_and_even_dimensions(int w, int h, int shortSide, int ew, int eh) =>
        Assert.Equal((ew, eh), RecordingPresets.OutputSize(w, h, shortSide));

    [Theory]
    [InlineData(5120, 1440, "16:9", 1280, 0, 2560, 1440)]   // super ultrawide (32:9): the middle half
    [InlineData(3440, 1440, "16:9", 440, 0, 2560, 1440)]    // ultrawide
    [InlineData(5120, 1440, "21:9", 880, 0, 3360, 1440)]
    [InlineData(1920, 1200, "16:9", 0, 60, 1920, 1080)]     // 16:10: bars off the top and bottom
    [InlineData(1440, 2560, "16:9", 0, 874, 1440, 810)]     // portrait
    [InlineData(2560, 1440, "16:9", 0, 0, 2560, 1440)]      // already 16:9
    [InlineData(5120, 1440, "", 0, 0, 5120, 1440)]          // whole monitor
    [InlineData(5120, 1440, "wide", 0, 0, 5120, 1440)]      // not an aspect ratio
    public void Recorded_area_is_the_centered_aspect_ratio(int mw, int mh, string aspect, int x, int y, int w, int h) =>
        Assert.Equal(new RecordingArea(x, y, w, h), RecordingPresets.Area(mw, mh, aspect));

    [Fact]
    public void Area_labels_and_output_sizes_follow_the_area()
    {
        Assert.Equal("16:9 center (2560×1440)", RecordingPresets.AreaLabel("16:9", RecordingPresets.Area(5120, 1440, "16:9"), 5120, 1440));
        Assert.Equal("16:9 (the whole monitor)", RecordingPresets.AreaLabel("16:9", RecordingPresets.Area(2560, 1440, "16:9"), 2560, 1440));
        Assert.Equal("Whole monitor (5120×1440)", RecordingPresets.AreaLabel("", RecordingPresets.Area(5120, 1440, ""), 5120, 1440));
        var area = RecordingPresets.Area(5120, 1440, "16:9");
        Assert.Equal((1920, 1080), RecordingPresets.OutputSize(area.Width, area.Height, 1080));
    }

    [Fact]
    public void Ram_estimate_tracks_bitrate_and_length()
    {
        long minute = RecordingPresets.EstimateRamBytes(16000, 3, 60);
        long fiveMinutes = RecordingPresets.EstimateRamBytes(16000, 3, 300);
        // ~16.5 Mbps for 63 s ≈ 130 MB, plus fixed slack
        Assert.InRange(minute, 120_000_000, 170_000_000);
        Assert.True(fiveMinutes > minute * 4);
    }

    [Fact]
    public void Default_encoder_prefers_newest_gpu_codec()
    {
        Assert.Equal("av1_amf", RecordingPresets.DefaultEncoder(new HashSet<string> { "h264_amf", "hevc_amf", "av1_amf", "libx264" })?.Id);
        Assert.Equal("hevc_nvenc", RecordingPresets.DefaultEncoder(new HashSet<string> { "h264_nvenc", "hevc_nvenc" })?.Id);
        Assert.Null(RecordingPresets.DefaultEncoder(new HashSet<string> { "libx264" }));
    }

    [Theory]
    [InlineData(10, "10 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(300, "5 minutes")]
    public void Duration_labels(int seconds, string text) => Assert.Equal(text, RecordingPresets.DurationLabel(seconds));
}

public sealed class GalleryLogicTests
{
    [Theory]
    [InlineData(20, "just now")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(3 * 86400, "3 days ago")]
    [InlineData(15 * 86400, "2 weeks ago")]
    [InlineData(70 * 86400, "2 months ago")]
    [InlineData(800 * 86400, "2 years ago")]
    public void Time_ago(int secondsAgo, string text)
    {
        var now = new DateTime(2026, 10, 5, 12, 0, 0);
        Assert.Equal(text, TimeAgo.Format(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void Game_folder_comes_from_the_store_install_path()
    {
        var none = new HashSet<string>();
        Assert.Equal("Counter-Strike Global Offensive",
            GameDetector.Classify(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", false, none));
        Assert.Equal("VALORANT", GameDetector.Classify(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", false, none));
        Assert.Equal("Fortnite", GameDetector.Classify(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", false, none));
    }

    [Fact]
    public void Browsers_chat_and_launchers_are_never_games()
    {
        var none = new HashSet<string>();
        Assert.Null(GameDetector.Classify(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true, none));
        Assert.Null(GameDetector.Classify(@"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe", true, none));
        Assert.Null(GameDetector.Classify(@"C:\Program Files (x86)\Steam\steam.exe", true, none));
        Assert.Null(GameDetector.Classify(@"C:\Tools\notatool.exe", false, none)); // not fullscreen, not known, not in a store folder
    }

    [Fact]
    public void Windows_known_games_and_fullscreen_apps_count()
    {
        // No store folder and no version info: fullscreen or Windows' game list makes it a game, named after the exe.
        Assert.Equal("game", GameDetector.Classify(@"C:\Odd\Place\game.exe", true, new HashSet<string>()));
        Assert.Equal("game", GameDetector.Classify(@"C:\Odd\Place\game.exe", false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Odd\Place\game.exe" }));
    }

    [Theory]
    [InlineData("Halo: Infinite", "Halo Infinite")]
    [InlineData("  What?  ", "What")]
    [InlineData("a/b\\c", "a b c")]
    public void Folder_names_are_safe(string input, string expected) => Assert.Equal(expected, GameDetector.SanitizeFolderName(input));

    [Fact]
    public void Hotkey_text()
    {
        Assert.Equal("Alt + F10", new Hotkey(Hotkey.Alt, 0x79).ToString());
        Assert.Equal("Ctrl + Shift + S", new Hotkey(Hotkey.Control | Hotkey.Shift, 0x53).ToString());
        Assert.Equal("None", new Hotkey(0, 0).ToString());
    }
}

[Collection("media")]
public sealed class Mp4InfoTests(MediaFixture media)
{
    [Fact]
    public void Reads_duration_without_ffprobe()
    {
        double? d = Mp4Info.TryReadDuration(media.TwoTracks);
        Assert.NotNull(d);
        Assert.Equal(6, d!.Value, 1);
        Assert.Null(Mp4Info.TryReadDuration(media.ThreeTracks)); // MKV: not an MP4
    }
}
