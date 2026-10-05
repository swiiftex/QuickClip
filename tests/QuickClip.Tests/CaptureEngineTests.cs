using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Tests;

/// <summary>
/// Records the main monitor for a few seconds through the native engine and checks the saved clip's streams.
/// Needs a GPU encoder; on machines without one the test passes without doing anything.
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

        var (w, h) = RecordingPresets.OutputSize(monitor.Width, monitor.Height, 720);
        string? error = CaptureEngine.Start(monitor.DeviceName, 30, w, h, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, w, h, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: true, mic: true, micDeviceId: null);
        Assert.Null(error);
        string clip = media.Out("engine-clip.mp4");
        try
        {
            await Task.Delay(4000);
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
        Assert.Equal(h, info.Video.Height);
        Assert.InRange(info.Duration, 1.9, 3.2); // starts on the keyframe before the 2 s mark
        Assert.Equal(["Desktop", "Chat", "Mic"], info.Audio.Select(a => a.DisplayName));
        File.Delete(clip);
    }
}
