using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class AudioRoutingTests
{
    private const uint Explorer = 100, Discord = 200, DiscordVoice = 201, Spotify = 300, Steam = 400, Game = 401,
        Chrome = 500, ChromeAudio = 501, QuickClip = 600, Mumble = 700, Brave = 800, BraveAudio = 801;

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

    // As above, and a link in Discord opened Brave, which is playing a video.
    private static readonly List<ProcessScan.Entry> WithBrave = [.. Processes, new(Brave, Discord, "brave.exe"), new(BraveAudio, Brave, "brave.exe")];
    private static readonly HashSet<uint> WithBraveAudio = [.. WithAudio, BraveAudio];

    private static readonly Dictionary<uint, string> Paths = new()
    {
        [Discord] = @"C:\Users\a\AppData\Local\Discord\app-1.0.9213\Discord.exe",
        [DiscordVoice] = @"C:\Users\a\AppData\Local\Discord\app-1.0.9213\Discord.exe",
        [Spotify] = @"C:\Users\a\AppData\Roaming\Spotify\Spotify.exe",
        [Brave] = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        [BraveAudio] = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    };

    private static AudioRouting.Plan Plan(uint[] chat, uint[] music, List<ProcessScan.Entry>? processes = null, HashSet<uint>? audio = null) =>
        AudioRouting.Make(processes ?? Processes, chat, music, () => audio ?? WithAudio, pid => Paths.GetValueOrDefault(pid), QuickClip);

    [Fact]
    public void Without_separate_apps_desktop_is_everything_but_QuickClip()
    {
        var plan = Plan([], []);
        Assert.Equal(QuickClip, plan.Exclude);
        Assert.Empty(plan.Desktop);
        Assert.Empty(plan.Chat);
        Assert.Empty(plan.Music);
    }

    [Fact]
    public void One_separate_app_is_left_out_of_desktop()
    {
        var plan = AudioRouting.Make(Processes, [], [Spotify], () => throw new InvalidOperationException("not needed"),
            _ => throw new InvalidOperationException("not needed"), QuickClip);
        Assert.Equal(Spotify, plan.Exclude);
        Assert.Empty(plan.Desktop);
        Assert.Equal([Spotify], plan.Music);
    }

    [Fact]
    public void With_chat_and_music_desktop_records_every_other_app()
    {
        var plan = Plan([Discord], [Spotify]);
        Assert.Equal(0u, plan.Exclude);
        // Steam brings the game with it; Chrome's audio process is recorded on its own. Explorer launched Discord
        // and Spotify, so recording it would bring them back. QuickClip is never recorded.
        Assert.Equal([Steam, ChromeAudio, Mumble], plan.Desktop);
        Assert.Equal([Explorer], plan.Missing);
        Assert.Equal([Discord], plan.Chat);
        Assert.Equal([Spotify], plan.Music);
    }

    [Fact]
    public void A_browser_opened_from_Discord_is_on_desktop_not_chat()
    {
        // Desktop is everything but Discord, and Brave on top; Chat is Discord's processes without Brave.
        var plan = Plan([Discord], [], WithBrave, WithBraveAudio);
        Assert.Equal(Discord, plan.Exclude);
        Assert.Equal([Brave], plan.Desktop);
        Assert.Equal([DiscordVoice], plan.Chat);
    }

    [Fact]
    public void A_browser_opened_from_Discord_is_on_desktop_with_a_music_app_too()
    {
        var plan = Plan([Discord], [Spotify], WithBrave, WithBraveAudio);
        Assert.Equal(0u, plan.Exclude);
        Assert.Equal([Steam, ChromeAudio, Mumble, BraveAudio], plan.Desktop);
        Assert.Equal([DiscordVoice], plan.Chat);
        Assert.Equal([Spotify], plan.Music);
    }

    [Fact]
    public void A_music_app_that_plays_from_the_process_that_opened_a_browser_keeps_its_sound()
    {
        // Spotify's own process plays and started Brave: recording Spotify brings Brave along, which beats losing Spotify.
        List<ProcessScan.Entry> processes =
        [
            new(Explorer, 4, "explorer.exe"), new(Spotify, Explorer, "Spotify.exe"),
            new(Brave, Spotify, "brave.exe"), new(BraveAudio, Brave, "brave.exe"),
        ];
        var plan = Plan([], [Spotify], processes, [Spotify, BraveAudio]);
        Assert.Equal(Spotify, plan.Exclude);
        Assert.Equal([Brave], plan.Desktop);
        Assert.Equal([Spotify], plan.Music);
    }

    [Fact]
    public void Helpers_from_the_apps_folder_and_web_views_are_part_of_it()
    {
        const uint Teams = 900, WebView = 901, WebViewChild = 902, Zoom = 1000, CptHost = 1001, AomHost = 1002;
        List<ProcessScan.Entry> processes =
        [
            new(Explorer, 4, "explorer.exe"),
            new(Teams, Explorer, "ms-teams.exe"), new(WebView, Teams, "msedgewebview2.exe"), new(WebViewChild, WebView, "msedgewebview2.exe"),
            new(Zoom, Explorer, "Zoom.exe"), new(CptHost, Zoom, "CptHost.exe"), new(AomHost, CptHost, "aomhost64.exe"),
        ];
        Dictionary<uint, string> paths = new()
        {
            [Teams] = @"C:\Program Files\WindowsApps\MSTeams_x64\ms-teams.exe",
            [WebView] = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe",
            [Zoom] = @"C:\Users\a\AppData\Roaming\Zoom\bin\Zoom.exe",
            [CptHost] = @"C:\Users\a\AppData\Roaming\Zoom\bin\CptHost.exe",
            [AomHost] = @"C:\Users\a\AppData\Roaming\Zoom\bin\aomhost\aomhost64.exe",
        };
        var plan = AudioRouting.Make(processes, [Teams, Zoom], [], () => new HashSet<uint> { WebViewChild, AomHost },
            pid => paths.GetValueOrDefault(pid), QuickClip);
        Assert.Equal([Teams, Zoom], plan.Chat);
        Assert.Empty(plan.Desktop);
    }

    [Fact]
    public void A_program_whose_folder_cant_be_read_counts_as_another_app()
    {
        const uint Other = 1000;
        List<ProcessScan.Entry> processes = [new(Explorer, 4, "explorer.exe"), new(Discord, Explorer, "Discord.exe"), new(Other, Discord, "game.exe")];
        var plan = Plan([Discord], [], processes, [Other]);
        Assert.Equal([Other], plan.Desktop);
        Assert.Equal([Discord], plan.Chat);
    }

    [Fact]
    public void An_app_that_opened_the_music_app_is_named_as_one_desktop_cant_record()
    {
        // Spotify opened from a link in Chrome: Chrome itself can't be recorded without Spotify, but its audio process can.
        List<ProcessScan.Entry> processes =
        [
            new(Explorer, 4, "explorer.exe"), new(Chrome, Explorer, "chrome.exe"), new(ChromeAudio, Chrome, "chrome.exe"),
            new(Spotify, Chrome, "Spotify.exe"), new(Discord, Explorer, "Discord.exe"),
        ];
        var plan = Plan([Discord], [Spotify], processes, [Chrome, ChromeAudio, Spotify]);
        Assert.Equal([ChromeAudio], plan.Desktop);
        Assert.Equal([Chrome], plan.Missing);
    }

    [Fact]
    public void Two_chat_apps_are_both_left_out()
    {
        var plan = Plan([Discord, Mumble], []);
        Assert.Equal([Spotify, Steam, ChromeAudio], plan.Desktop);
        Assert.Equal([Discord, Mumble], plan.Chat);
    }

    [Fact]
    public void Reused_parent_ids_neither_loop_nor_lose_apps()
    {
        // Each looks like the other's parent (an id reused after a process exited).
        List<ProcessScan.Entry> processes = [new(10, 11, "a.exe"), new(11, 10, "b.exe"), new(20, 4, "c.exe"), new(30, 4, "d.exe")];
        var plan = AudioRouting.Make(processes, [20], [30], () => new HashSet<uint> { 10, 11 }, _ => null, 99);
        Assert.Equal([10u, 11u], plan.Desktop);
    }

    [Fact]
    public void Apps_that_had_audio_are_remembered_while_they_run()
    {
        var known = new Dictionary<uint, string>();
        List<ProcessScan.Entry> processes = [new(1, 4, "brave.exe"), new(2, 4, "game.exe")];
        AudioRouting.Remember(known, processes, new HashSet<uint> { 1 });
        AudioRouting.Remember(known, processes, new HashSet<uint>()); // Brave closed its sound output
        Assert.Equal([1u], known.Keys);
        AudioRouting.Remember(known, [new(1, 4, "notepad.exe"), new(2, 4, "game.exe")], new HashSet<uint>()); // its id reused
        Assert.Empty(known);
        AudioRouting.Remember(known, processes, new HashSet<uint> { 2 });
        AudioRouting.Remember(known, [], new HashSet<uint>()); // the game exited
        Assert.Empty(known);
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
