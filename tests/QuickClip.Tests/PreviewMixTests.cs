using QuickClip.Mpv;

namespace QuickClip.Tests;

/// <summary>
/// Plays the test files through libmpv with QuickClip's preview filter graph, records what would be heard
/// to a WAV file, and checks that each track is audible only at the volume it was given.
/// </summary>
[Collection("media")]
public sealed class PreviewMixTests(MediaFixture media)
{
    private sealed class Session : IAsyncDisposable
    {
        public MpvPlayer Player { get; }
        public PreviewAudio Audio { get; }
        public TaskCompletionSource Loaded { get; } = new();
        public TaskCompletionSource Ended { get; } = new();

        public Session(string wav)
        {
            Assert.True(Deps.HasLibMpv, "libmpv-2.dll not found next to the tests.");
            Player = new MpvPlayer(IntPtr.Zero, new Dictionary<string, string>
            {
                ["vo"] = "null",
                ["force-window"] = "no",
                ["keep-open"] = "no",
                ["ao"] = "pcm",
                ["ao-pcm-file"] = wav,
                ["ao-pcm-waveheader"] = "yes",
            });
            Audio = new PreviewAudio(Player);
            Player.EventReceived += (id, _) =>
            {
                if (id == MpvEventId.FileLoaded) Loaded.TrySetResult();
                if (id == MpvEventId.EndFile && Loaded.Task.IsCompleted) Ended.TrySetResult();
            };
        }

        public async Task Load(string file)
        {
            Player.CommandAsync("loadfile", file);
            await Loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async Task PlayToEnd()
        {
            Player.SetProperty("pause", false);
            await Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async ValueTask DisposeAsync() => await Task.Run(Player.Dispose);
    }

    private async Task<string> Record(string file, double[] volumes, string name)
    {
        string wav = media.Out(name);
        await using (var s = new Session(wav))
        {
            s.Audio.Configure(volumes.Length, i => volumes[i]);
            await s.Load(file);
            await s.PlayToEnd();
        }
        Assert.True(new FileInfo(wav).Length > 10_000, "no audio was recorded");
        return wav;
    }

    [Fact]
    public async Task Each_of_two_tracks_plays_at_its_own_volume()
    {
        string micOnly = await Record(media.TwoTracks, [1, 0], "mic-only.wav");
        string deskOnly = await Record(media.TwoTracks, [0, 1], "desk-only.wav");

        double micInMic = await MediaFixture.BandLevel(micOnly, 0, MediaFixture.MicHz);
        double deskInMic = await MediaFixture.BandLevel(micOnly, 0, MediaFixture.DesktopHz);
        double micInDesk = await MediaFixture.BandLevel(deskOnly, 0, MediaFixture.MicHz);
        double deskInDesk = await MediaFixture.BandLevel(deskOnly, 0, MediaFixture.DesktopHz);

        Assert.True(micInMic - deskInMic > 30, $"mic-only: mic {micInMic} dB, desktop {deskInMic} dB");
        Assert.True(deskInDesk - micInDesk > 30, $"desktop-only: mic {micInDesk} dB, desktop {deskInDesk} dB");
    }

    [Fact]
    public async Task Three_tracks_with_different_sample_rates_can_be_isolated()
    {
        string wav = await Record(media.ThreeTracks, [0, 1, 0], "middle-only.wav");
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz);
        double game = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz);
        double discord = await MediaFixture.BandLevel(wav, 0, MediaFixture.ThirdHz);
        Assert.True(game - mic > 30 && game - discord > 30, $"mic {mic}, game {game}, discord {discord} dB");
    }

    [Fact]
    public async Task Live_volume_change_applies_while_playing()
    {
        string wav = media.Out("live-only.wav");
        double[] volumes = [1, 1];
        await using (var s = new Session(wav))
        {
            s.Audio.Configure(2, i => volumes[i]);
            await s.Load(media.TwoTracks);
            s.Player.SetProperty("pause", false);
            for (int i = 0; i < 200 && (s.Player.GetDouble("time-pos") ?? 0) < 1; i++) await Task.Delay(20);
            volumes[0] = 0;
            s.Audio.SetLive(2, i => volumes[i]);
            await s.Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, fromSeconds: 2.5);
        double desk = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, fromSeconds: 2.5);
        Assert.True(desk - mic > 30, $"after live change: mic {mic} dB, desktop {desk} dB");
    }

    [Fact]
    public async Task Live_volume_change_survives_a_seek()
    {
        string wav = media.Out("live-change.wav");
        double[] volumes = [1, 1];
        await using (var s = new Session(wav))
        {
            s.Audio.Configure(2, i => volumes[i]);
            await s.Load(media.TwoTracks);
            s.Player.SetProperty("pause", false);
            for (int i = 0; i < 200 && (s.Player.GetDouble("time-pos") ?? 0) < 1; i++) await Task.Delay(20);

            // What the UI does when the desktop slider goes to 0: live change, then sync, then the user seeks.
            volumes[1] = 0;
            s.Audio.SetLive(2, i => volumes[i]);
            s.Audio.Sync(2, i => volumes[i]);
            s.Player.Command("seek", "0", "absolute+exact");
            await s.Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        // Everything recorded after the first ~1.5 s comes from the replay after the seek.
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, fromSeconds: 2.5);
        double desk = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, fromSeconds: 2.5);
        Assert.True(mic - desk > 30, $"after seek: mic {mic} dB, desktop {desk} dB");
    }
}
