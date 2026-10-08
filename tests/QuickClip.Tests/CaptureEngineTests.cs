using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Tests;

/// <summary>
/// Records the main monitor for a few seconds through the native engine and checks the saved clip's streams (never
/// what they show). Needs a GPU encoder; on machines without one the tests pass without doing anything.
/// </summary>
[Collection("media")]
public sealed class CaptureEngineTests(MediaFixture media)
{
    [Fact]
    public async Task Records_and_saves_a_clip_with_named_audio_tracks()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        if (encoder == null) return;
        var monitor = Monitors.List().First(m => m.IsPrimary);

        // The 16:9 center (all of a 16:9 monitor), scaled down: Windows Graphics Capture crops and scales.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, 720);
        string? error = CaptureEngine.Start(monitor.DeviceName, area, 30, w, h, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, w, h, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: true, splitMusic: true, mic: true, micDeviceId: null);
        Assert.Null(error);
        string clip = media.Out("engine-clip.mp4");
        try
        {
            // Keyframes can be irregular for a moment after the encoder starts; keep that out of the saved 2 s.
            await Task.Delay(3000);
            // Switch Desktop to recording app by app (as with a chat and a music app running) mid-capture.
            uint self = (uint)Environment.ProcessId;
            var explorer = ProcessScan.Snapshot().FirstOrDefault(p => p.ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));
            CaptureEngine.SetAudioProcesses(new AudioRouting.Desktop(0, [self]), [], explorer.Pid != 0 ? [explorer.Pid] : []);
            await Task.Delay(2000);
            var stats = CaptureEngine.GetStats();
            Assert.Equal(1, stats.Running);
            Assert.True(stats.BufferBytes > 0);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 2, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
        }

        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal(encoder.Family, info.Video!.Codec);
        Assert.Equal((w, h), (info.Video.Width, info.Video.Height));
        Assert.InRange(info.Duration, 1.9, 3.2); // starts on the keyframe before the 2 s mark
        Assert.Equal(["Desktop", "Chat", "Music", "Mic"], info.Audio.Select(a => a.DisplayName));
        File.Delete(clip);
    }

    [Fact]
    public async Task Records_the_center_of_the_monitor_at_full_size()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        if (encoder == null) return;
        var monitor = Monitors.List().First(m => m.IsPrimary);

        // Unscaled, so Desktop Duplication copies just this rectangle. 4:3 crops any widescreen monitor.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "4:3");
        string? error = CaptureEngine.Start(monitor.DeviceName, area, 30, 0, 0, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mic: false, micDeviceId: null);
        Assert.Null(error);
        string clip = media.Out("engine-center.mp4");
        try
        {
            await Task.Delay(2500);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 1, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
        }

        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal((area.Width, area.Height), (info.Video!.Width, info.Video.Height));
        File.Delete(clip);
    }
}
