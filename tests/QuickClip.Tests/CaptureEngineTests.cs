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
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return; // no GPU encoder, or no screen (a disconnected remote session)

        // The 16:9 center (all of a 16:9 monitor) at its own size: Desktop Duplication, which sends frames at a steady
        // rate even when the screen doesn't change, so the clip's length doesn't depend on what's on screen.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        var (w, h) = (area.Width, area.Height);
        string? error = CaptureEngine.Start(monitor.DeviceName, "16:9", 30, 0, 0, encoder.Id,
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
    public async Task Scaled_recordings_crop_before_scaling()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return; // no GPU encoder, or no screen (a disconnected remote session)

        // Windows Graphics Capture crops and scales; it only sends frames when the screen changes.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, 720);
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "16:9", 30, w, h, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, w, h, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mic: false, micDeviceId: null));
        string clip = media.Out("engine-scaled.mp4");
        string? error;
        try
        {
            await Task.Delay(4000);
            error = await Task.Run(() => CaptureEngine.Save(clip, 3, "Test"));
        }
        finally
        {
            CaptureEngine.Stop();
        }
        if (error == "Nothing has been recorded yet.") return; // an idle screen gave it nothing to record
        Assert.Null(error);
        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal((w, h), (info.Video!.Width, info.Video.Height));
        File.Delete(clip);
    }

    [Fact]
    public async Task Records_the_center_of_the_monitor_at_full_size()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return; // no GPU encoder, or no screen (a disconnected remote session)

        // Unscaled, so Desktop Duplication copies just this rectangle. 4:3 crops any widescreen monitor.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "4:3");
        string? error = CaptureEngine.Start(monitor.DeviceName, "4:3", 30, 0, 0, encoder.Id,
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

    [Fact]
    public async Task A_stuck_capture_is_replaced_and_the_picture_comes_back()
    {
        if (await RecordAsync(async () =>
        {
            await Task.Delay(2000);
            CaptureEngine.TestFreezeVideo(4000); // hangs like a stuck capture call
            await Task.Delay(11000);
        }, saveSeconds: 12) is not var (frames, error)) return;
        Assert.Null(error);
        // The watchdog notices after 3 s and starts a new capture thread. While the frozen one still holds Desktop
        // Duplication the new one may have to start on Graphics Capture (few frames on an idle screen), then it
        // switches back. Either way the picture is back well before the end, and the frozen thread adds nothing.
        Assert.True(LargestGap(frames) < 10, $"gap of {LargestGap(frames):0.0} s");
        Assert.True(frames[^1] > 11, $"the picture didn't come back (last frame at {frames[^1]:0.0} s of 12)");
    }

    [Fact]
    public async Task Capture_restarts_on_request_without_losing_the_buffer()
    {
        if (await RecordAsync(async () =>
        {
            await Task.Delay(3000);
            CaptureEngine.RestartVideo(); // as after a display change
            await Task.Delay(3000);
        }, saveSeconds: 5) is not var (frames, error)) return;
        Assert.Null(error);
        Assert.InRange(frames[0], 0, 0.5);    // the frames from before the restart are still there
        Assert.True(LargestGap(frames) < 1.5, $"gap of {LargestGap(frames):0.00} s");
    }

    [Fact]
    public async Task A_flat_picture_starts_capture_again()
    {
        long logStart = new FileInfo(Log.FilePath).Length;
        if (await RecordAsync(async () =>
        {
            await Task.Delay(1000);
            CaptureEngine.TestFlatBelow(int.MaxValue); // every keyframe now looks like a black screen
            await Task.Delay(7500);
        }, saveSeconds: 6) is not var (frames, error)) return;
        Assert.Null(error);
        Assert.True(LargestGap(frames) < 1.5, $"gap of {LargestGap(frames):0.00} s");

        await Task.Delay(500); // the engine's log lines reach the file through two queues
        Log.Flush(3000);
        using var reader = new StreamReader(new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        reader.BaseStream.Position = logStart;
        Assert.Contains("the picture has been a flat color for 5 s; starting capture again", reader.ReadToEnd());
    }

    /// <summary>
    /// Records the 16:9 center at 30 fps while <paramref name="during"/> runs, then saves; returns the video frame
    /// times, or null on a machine without a GPU encoder.
    /// </summary>
    private async Task<(List<double> Frames, string? Error)?> RecordAsync(Func<Task> during, int saveSeconds)
    {
        if (!CaptureEngine.IsAvailable) return null;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return null;
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "16:9", 30, 0, 0, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 30, cursor: true, splitChat: false, splitMusic: false, mic: false, micDeviceId: null));
        string clip = media.Out($"engine-{Guid.NewGuid():N}.mp4");
        string? error;
        try
        {
            await during();
            error = await Task.Run(() => CaptureEngine.Save(clip, saveSeconds, "Test"));
        }
        finally
        {
            CaptureEngine.Stop();
        }
        var (_, packets, _) = await Proc.RunAsync(Deps.FFprobe!, ["-v", "error", "-select_streams", "v", "-show_entries", "packet=pts_time", "-of", "csv=p=0", clip]);
        var frames = packets.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.Parse(t.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        File.Delete(clip);
        return (frames, error);
    }

    private static double LargestGap(List<double> frames) => frames.Zip(frames.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
}
