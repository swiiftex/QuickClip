using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class AudioRoutingTests
{
    private const uint Explorer = 100, Discord = 200, DiscordVoice = 201, Spotify = 300, Steam = 400, Game = 401,
        Chrome = 500, ChromeAudio = 501, QuickClip = 600, Mumble = 700;

    // Explorer started Discord, Spotify, Steam, Chrome and QuickClip; Steam started the game.
    private static readonly List<ProcessScan.Entry> Processes =
    [
        new(Explorer, 4, "explorer.exe"),
        new(Discord, Explorer, "Discord.exe"), new(DiscordVoice, Discord, "Discord.exe"),
        new(Spotify, Explorer, "Spotify.exe"),
        new(Steam, Explorer, "steam.exe"), new(Game, Steam, "cs2.exe"),
        new(Chrome, Explorer, "chrome.exe"), new(ChromeAudio, Chrome, "chrome.exe"),
        new(QuickClip, Explorer, "QuickClip.exe"),
        new(Mumble, Explorer, "mumble.exe"),
    ];

    private static readonly HashSet<uint> WithAudio = [Explorer, DiscordVoice, Spotify, Steam, Game, ChromeAudio, QuickClip, Mumble];

    private static AudioRouting.Desktop Plan(params uint[] separated) =>
        AudioRouting.Plan(Processes, separated, () => WithAudio, QuickClip);

    [Fact]
    public void Without_separate_apps_desktop_is_everything_but_QuickClip()
    {
        var desktop = Plan();
        Assert.Equal(QuickClip, desktop.Exclude);
        Assert.Empty(desktop.Include);
    }

    [Fact]
    public void One_separate_app_is_left_out_of_desktop()
    {
        var desktop = AudioRouting.Plan(Processes, [Spotify], () => throw new InvalidOperationException("not needed"), QuickClip);
        Assert.Equal(Spotify, desktop.Exclude);
        Assert.Empty(desktop.Include);
    }

    [Fact]
    public void With_chat_and_music_desktop_records_every_other_app()
    {
        var desktop = Plan(Discord, Spotify);
        Assert.Equal(0u, desktop.Exclude);
        // Steam brings the game with it; Chrome's audio process is recorded on its own. Explorer launched Discord
        // and Spotify, so recording it would bring them back. QuickClip is never recorded.
        Assert.Equal([Steam, ChromeAudio, Mumble], desktop.Include);
    }

    [Fact]
    public void Two_chat_apps_are_both_left_out()
    {
        var desktop = Plan(Discord, Mumble);
        Assert.Equal([Spotify, Steam, ChromeAudio], desktop.Include);
    }

    [Fact]
    public void Reused_parent_ids_neither_loop_nor_lose_apps()
    {
        // Each looks like the other's parent (an id reused after a process exited).
        List<ProcessScan.Entry> processes = [new(10, 11, "a.exe"), new(11, 10, "b.exe"), new(20, 4, "c.exe"), new(30, 4, "d.exe")];
        var desktop = AudioRouting.Plan(processes, [20, 30], () => new HashSet<uint> { 10, 11 }, 99);
        Assert.Equal([10u, 11u], desktop.Include);
    }

    [Fact]
    public void Music_apps_are_found_by_their_root_process()
    {
        List<ProcessScan.Entry> processes =
        [
            new(1, 4, "explorer.exe"), new(2, 1, "Spotify.exe"), new(3, 2, "Spotify.exe"), new(4, 2, "Spotify.exe"),
            new(5, 1, "TIDAL.exe"), new(6, 1, "Amazon Music.exe"), new(7, 1, "Discord.exe"),
        ];
        Assert.Equal([new RunningApp(2, "Spotify"), new RunningApp(5, "TIDAL"), new RunningApp(6, "Amazon Music")], MusicApps.Find(processes));
        Assert.Equal([new RunningApp(7, "Discord")], ChatApps.Find(processes));
    }
}
