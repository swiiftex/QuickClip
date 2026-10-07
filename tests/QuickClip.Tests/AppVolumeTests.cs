using QuickClip.Mpv;
using QuickClip.Shell;

namespace QuickClip.Tests;

/// <summary>
/// The editor's preview volume is the test process's own entry in the Windows volume mixer. These tests change
/// that level (never the system volume) and put it back. On a machine without an output device they do nothing.
/// </summary>
[Collection("media")]
public sealed class AppVolumeTests(MediaFixture media)
{
    [Fact]
    public void Level_and_mute_reach_the_mixer_and_mixer_changes_are_reported()
    {
        if (AppVolume.Read() is not { } original || AppVolume.OpenSession() is not { } mixer) return;
        using var reported = new ManualResetEventSlim();
        Action onChange = reported.Set;
        AppVolume.ChangedElsewhere += onChange;
        try
        {
            AppVolume.SetLevel(0.35f);
            mixer.Volume.GetMasterVolume(out float level);
            Assert.Equal(0.35f, level, 0.001f);
            Assert.False(reported.Wait(500), "QuickClip's own change was reported as a mixer change.");

            // Another client of the same session, like the Windows mixer.
            var other = Guid.NewGuid();
            mixer.Volume.SetMasterVolume(0.6f, ref other);
            Assert.True(reported.Wait(3000), "A change made in the mixer was not reported.");
            Assert.Equal(0.6f, AppVolume.Read()!.Value.Level, 0.001f);

            AppVolume.SetMuted(true);
            mixer.Volume.GetMute(out bool muted);
            Assert.True(muted);
            AppVolume.SetMuted(false);
            Assert.False(AppVolume.Read()!.Value.Muted);
        }
        finally
        {
            AppVolume.ChangedElsewhere -= onChange;
            AppVolume.SetLevel(original.Level);
            AppVolume.SetMuted(original.Muted);
        }
    }

    [Fact]
    public void Apps_with_audio_are_listed()
    {
        if (AppVolume.OpenSession() is null) return; // opening our session makes this process one of them
        Assert.Contains((uint)Environment.ProcessId, CoreAudio.SessionProcessIds());
    }

    [Fact]
    public async Task Preview_audio_is_listed_as_QuickClip_in_the_mixer()
    {
        if (AppVolume.OpenSession() is not { } session) return;
        var loaded = new TaskCompletionSource();
        // Real output so mpv joins the session, at zero volume so nothing is heard.
        var player = new MpvPlayer(IntPtr.Zero, new Dictionary<string, string>
        {
            ["vo"] = "null",
            ["force-window"] = "no",
            ["ao"] = "wasapi",
            ["volume"] = "0",
        });
        try
        {
            player.EventReceived += (id, _) => { if (id == MpvEventId.FileLoaded) loaded.TrySetResult(); };
            player.CommandAsync("loadfile", media.TwoTracks);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            player.SetProperty("pause", false);
            await Task.Delay(1500);

            session.Control.GetDisplayName(out string name);
            Assert.Equal("QuickClip", name);
        }
        finally
        {
            await Task.Run(player.Dispose);
        }
    }
}
