using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickClip.Tests;

/// <summary>Generates small test recordings once per run: a "mic" tone on track 1 and a "desktop" tone on track 2.</summary>
public sealed class MediaFixture : IAsyncLifetime
{
    public const int MicHz = 300;
    public const int DesktopHz = 2000;
    public const int ThirdHz = 5000;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "quickclip-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string TwoTracks => Path.Combine(Dir, "two-tracks.mp4");
    public string ThreeTracks => Path.Combine(Dir, "three-tracks.mkv");

    public async Task InitializeAsync()
    {
        Assert.True(Deps.FFmpeg != null, "ffmpeg.exe not found next to the tests; run scripts/get-deps.ps1 first.");
        Directory.CreateDirectory(Dir);

        await Run(
        [
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-f", "lavfi", "-i", $"sine=frequency={MicHz}:sample_rate=48000",
            "-f", "lavfi", "-i", $"sine=frequency={DesktopHz}:sample_rate=48000",
            "-t", "6", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ac:a:1", "2",
            // MP4 keeps track names in the handler name (like OBS recordings)
            "-metadata:s:a:0", "handler_name=Mic", "-metadata:s:a:1", "handler_name=Desktop Audio", TwoTracks,
        ]);
        await Run(
        [
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
            "-f", "lavfi", "-i", $"sine=frequency={MicHz}:sample_rate=48000",
            "-f", "lavfi", "-i", $"sine=frequency={DesktopHz}:sample_rate=44100",
            "-f", "lavfi", "-i", $"sine=frequency={ThirdHz}:sample_rate=48000",
            "-t", "4", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "libopus",
            "-metadata:s:a:0", "title=Mic", "-metadata:s:a:1", "title=Game", "-metadata:s:a:2", "title=Discord", ThreeTracks,
        ]);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    public string Out(string name) => Path.Combine(Dir, name);

    public static async Task Run(IEnumerable<string> args)
    {
        var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg!, ["-hide_banner", "-nostdin", "-y", "-v", "error", .. args]);
        Assert.True(code == 0, err);
    }

    /// <summary>Mean level (dB) of one audio stream after a narrow band-pass around <paramref name="hz"/>.</summary>
    public static async Task<double> BandLevel(string file, int audioOrdinal, int hz, double fromSeconds = 0)
    {
        var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg!,
        [
            "-hide_banner", "-nostdin", "-ss", Fmt.Num(fromSeconds), "-i", file, "-map", $"0:a:{audioOrdinal}",
            "-af", $"bandpass=f={hz}:width_type=h:w=60,volumedetect", "-f", "null", "-",
        ]);
        Assert.True(code == 0, err);
        var m = Regex.Match(err, @"mean_volume:\s*(-?[\d.]+|-inf) dB");
        Assert.True(m.Success, "volumedetect gave no result:\n" + err);
        return m.Groups[1].Value == "-inf" ? -200 : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

[CollectionDefinition("media")]
public sealed class MediaCollection : ICollectionFixture<MediaFixture>;
