using System.Windows.Media;
using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Tests;

/// <summary>
/// Records the main monitor for a few seconds through the native engine and checks the saved clip's streams (never
/// what they show). Needs a GPU encoder; on machines without one the tests pass without doing anything.
/// </summary>
[Collection("media")]
public sealed class CaptureEngineTests(MediaFixture media, Xunit.Abstractions.ITestOutputHelper output)
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
            bufferSeconds: 10, cursor: true, splitChat: true, splitMusic: true, mics: [new("", "Mic")], separateMics: false);
        Assert.Null(error);
        string clip = media.Out("engine-clip.mp4");
        try
        {
            // Keyframes can be irregular for a moment after the encoder starts; keep that out of the saved 2 s.
            await Task.Delay(3000);
            // Switch Desktop to recording app by app (as with a chat and a music app running) mid-capture.
            uint self = (uint)Environment.ProcessId;
            var explorer = ProcessScan.Snapshot().FirstOrDefault(p => p.ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));
            CaptureEngine.SetAudioProcesses(new AudioRouting.Plan(0, [self], [], explorer.Pid != 0 ? [explorer.Pid] : []));
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

    [Theory]
    [InlineData(true, new[] { "Desktop", "Mic (Yeti)", "Mic (Headset)" })]
    [InlineData(false, new[] { "Desktop", "Mic" })]
    public async Task Several_microphones_record_on_their_own_tracks_or_one_shared_track(bool separate, string[] tracks)
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return; // no GPU encoder, or no screen (a disconnected remote session)

        // The second microphone isn't there, so its track stays silent the way an unplugged one's does.
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "16:9", 30, 0, 0, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false,
            mics: [new("", "Mic (Yeti)"), new(@"{0.0.1.00000000}.{not-a-real-microphone}", "Mic (Headset)")], separateMics: separate));
        string clip = media.Out($"engine-mics-{separate}.mp4");
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
        Assert.Equal(tracks, info.Audio.Select(a => a.DisplayName));
        Assert.All(info.Audio, a => Assert.Equal("aac", a.Codec));
        File.Delete(clip);
    }

    /// <summary>
    /// With a chat and a music app, Desktop is everything but the chat app, less the music app's sound subtracted
    /// sample for sample. Here this test is the "music app", playing noise at -80 dB (too quiet to hear), and a
    /// sleeping process stands in for the chat app: the noise has to be on Music and gone from Desktop.
    /// </summary>
    [Fact]
    public async Task Desktop_takes_an_apps_sound_out_by_subtracting_it()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return;

        using var chat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        uint self = (uint)Environment.ProcessId;
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "16:9", 30, 0, 0, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: true, mics: [], separateMics: false));
        string clip = media.Out("engine-subtract.mp4");
        long? offset = null;
        try
        {
            CaptureEngine.SetAudioProcesses(new AudioRouting.Plan((uint)chat.Id, [], [], [self]) { Subtract = [self] });
            CaptureEngine.TestPlayNoise(19000, 1e-4f);
            for (int i = 0; i < 150 && offset == null; i++)
            {
                await Task.Delay(100);
                offset = CaptureEngine.TestSubtractOffset(self);
            }
            await Task.Delay(3500);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 3, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
            try { chat.Kill(); } catch { }
        }
        Assert.NotNull(offset);

        // How much of Music is in Desktop, at the best match within ±50 ms (each track is timed by its own capture).
        // Changes from sample to sample are compared, which the noise is full of and most real sound isn't.
        var desktop = Changes(await Pcm(clip, 0));
        var music = Changes(await Pcm(clip, 1));
        File.Delete(clip);
        int from = 2400, length = Math.Min(desktop.Length, music.Length) - 2 * from;
        double musicEnergy = 0, desktopEnergy = 0, best = 0;
        for (int i = from; i < from + length; i++)
        {
            musicEnergy += music[i] * music[i];
            desktopEnergy += desktop[i] * desktop[i];
        }
        Assert.True(musicEnergy > 0, "the noise wasn't recorded on Music");
        for (int lag = -from; lag <= from; lag++)
        {
            double dot = 0;
            for (int i = from; i < from + length; i++) dot += desktop[i + lag] * music[i];
            best = Math.Max(best, Math.Abs(dot / musicEnergy));
        }
        // Other sound playing on this PC is in Desktop too, and blurs the measurement by about its level against the
        // app's over the square root of the samples compared; allow three times that.
        double blur = 3 * Math.Sqrt(desktopEnergy / musicEnergy / length);
        output.WriteLine($"found at offset {offset}; {best:P2} of the app's sound left on Desktop (give or take {blur:P2}); " +
                         $"Desktop's other sound {10 * Math.Log10(desktopEnergy / musicEnergy):0.0} dB against the app's");
        Assert.True(best < 0.05 + blur, $"{best:P1} of the app's sound is still on Desktop");
    }

    private async Task<float[]> Pcm(string clip, int track)
    {
        string raw = media.Out($"track{track}.f32");
        await MediaFixture.Run(["-i", clip, "-map", $"0:a:{track}", "-ac", "1", "-ar", "48000", "-f", "f32le", raw]);
        var bytes = await File.ReadAllBytesAsync(raw);
        File.Delete(raw);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    private static double[] Changes(float[] s)
    {
        var d = new double[Math.Max(0, s.Length - 1)];
        for (int i = 0; i < d.Length; i++) d[i] = s[i + 1] - s[i];
        return d;
    }

    /// <summary>
    /// The graphics card's encoder through Windows, used when the card's own route can't be: FFmpeg's NVENC needs NVIDIA
    /// driver 610, which GTX 900 and 10 series cards never get. Runs for each one this PC's card offers.
    /// </summary>
    [Theory]
    [InlineData("h264_mf")]
    [InlineData("hevc_mf")]
    [InlineData("av1_mf")]
    public async Task Records_through_the_graphics_cards_encoder_in_Windows(string encoderId)
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (monitor == null || !(await VideoEncoders.GetAvailableAsync()).Contains(encoderId)) return;

        var encoder = VideoEncoders.Find(encoderId)!;
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "16:9", 30, 0, 0, encoderId,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 30, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mics: [], separateMics: false));
        string clip = media.Out($"engine-{encoderId}.mp4");
        try
        {
            await Task.Delay(4000);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 2, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
        }

        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal(encoder.Family, info.Video!.Codec);
        Assert.Equal((area.Width, area.Height), (info.Video.Width, info.Video.Height));
        Assert.InRange(info.Duration, 1.9, 3.2); // starts on the keyframe before the 2 s mark
        File.Delete(clip);
    }

    /// <summary>
    /// Above 60 fps the engine records through Windows Graphics Capture: Desktop Duplication tops out around 75 fps of
    /// 120 on some PCs. A tiny window flickering on every screen refresh keeps the picture changing, as a game would.
    /// </summary>
    [Fact]
    public async Task Records_120_fps_at_full_speed()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return;

        var area = RecordingPresets.Area(monitor.Width, monitor.Height, "16:9");
        using var flicker = TestWindow.Show(monitor.Left + area.X, monitor.Top + area.Y, 3, 3, Colors.Black, Colors.DimGray, topmost: true);
        string? error = CaptureEngine.Start(monitor.DeviceName, "16:9", 120, 0, 0, encoder.Id,
            RecordingPresets.VideoKbps(RecordingQuality.Low, area.Width, area.Height, 120, encoder.Family), RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mics: [], separateMics: false);
        Assert.Null(error);
        string clip = media.Out("engine-120.mp4");
        try
        {
            await Task.Delay(5000);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 3, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
        }

        var (code, stdout, err) = await Proc.RunAsync(Deps.FFprobe!,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", clip]);
        Assert.True(code == 0, err);
        var pts = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.Parse(l.Trim(), System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        double fps = (pts.Count - 1) / (pts[^1] - pts[0]);
        Assert.True(fps > 110, $"{pts.Count} frames over {pts[^1] - pts[0]:0.00} s is {fps:0.0} fps");
        File.Delete(clip);
    }

    /// <summary>
    /// Recording a game's window records just it: a window on top of it (an overlay) isn't in the picture. Checked by
    /// colour alone: the "game" is blue, the window over a quarter of it red.
    /// </summary>
    [Fact]
    public async Task A_game_window_is_recorded_without_the_windows_on_top_of_it()
    {
        if (!CaptureEngine.IsAvailable) return;
        CaptureEngine.Initialize();
        var encoder = RecordingPresets.DefaultEncoder(await VideoEncoders.GetAvailableAsync());
        var monitor = Monitors.List().FirstOrDefault(m => m.IsPrimary);
        if (encoder == null || monitor == null) return;

        using var game = TestWindow.Show(monitor.Left + 120, monitor.Top + 120, 320, 240, Colors.Blue, Colors.DarkBlue, topmost: false);
        using var overlay = TestWindow.Show(monitor.Left + 200, monitor.Top + 180, 160, 120, Colors.Red, Colors.Red, topmost: true);
        Assert.Null(CaptureEngine.Start(monitor.DeviceName, "", 30, 0, 0, encoder.Id, 2000, RecordingPresets.AudioKbps,
            bufferSeconds: 10, cursor: false, splitChat: false, splitMusic: false, mics: [], separateMics: false));
        string clip = media.Out("engine-window.mp4");
        try
        {
            CaptureEngine.SetCaptureWindow(game.Handle, 320, 240, 2000);
            await Task.Delay(3500);
            Assert.Null(await Task.Run(() => CaptureEngine.Save(clip, 2, "Test")));
        }
        finally
        {
            CaptureEngine.Stop();
        }

        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal((320, 240), (info.Video!.Width, info.Video.Height));
        // Blues carry a lot of U (Cb) and little V (Cr); red carries lots of V. A quarter of red would lift V past 140.
        var (u, v) = await AverageChroma(clip);
        Assert.True(u > 170 && v < 135, $"average U {u:0}, V {v:0}");
        File.Delete(clip);
    }

    private static async Task<(double U, double V)> AverageChroma(string clip)
    {
        var (code, _, err) = await Proc.RunAsync(Deps.FFmpeg!,
            ["-hide_banner", "-nostdin", "-i", clip, "-vf", "signalstats,metadata=mode=print", "-f", "null", "-"]);
        Assert.True(code == 0, err);
        double Average(string key) => System.Text.RegularExpressions.Regex.Matches(err, $@"lavfi\.signalstats\.{key}=([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Average();
        return (Average("UAVG"), Average("VAVG"));
    }

    /// <summary>A borderless window that alternates between two colours on every screen refresh, so it always has new frames.</summary>
    private sealed class TestWindow : IDisposable
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private TestWindow(System.Windows.Threading.Dispatcher dispatcher, IntPtr handle) => (_dispatcher, Handle) = (dispatcher, handle);

        public IntPtr Handle { get; }

        public static TestWindow Show(int x, int y, int width, int height, Color a, Color b, bool topmost)
        {
            var ready = new TaskCompletionSource<TestWindow>();
            var thread = new Thread(() =>
            {
                var brushes = new[] { new SolidColorBrush(a), new SolidColorBrush(b) };
                var window = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false,
                    Topmost = topmost, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = x, Top = y, Width = width, Height = height, Background = brushes[0],
                };
                int n = 0;
                CompositionTarget.Rendering += (_, _) => window.Background = brushes[n++ & 1];
                window.Show();
                ready.SetResult(new TestWindow(System.Windows.Threading.Dispatcher.CurrentDispatcher, new System.Windows.Interop.WindowInteropHelper(window).Handle));
                System.Windows.Threading.Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return ready.Task.GetAwaiter().GetResult();
        }

        public void Dispose() => _dispatcher.InvokeShutdown();
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
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mics: [], separateMics: false));
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
            bufferSeconds: 10, cursor: true, splitChat: false, splitMusic: false, mics: [], separateMics: false);
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
            bufferSeconds: 30, cursor: true, splitChat: false, splitMusic: false, mics: [], separateMics: false));
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
