using System.Diagnostics;
using QuickClip.Updates;

namespace QuickClip.Tests;

public sealed class UpdaterTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Theory]
    [InlineData("0.3.0-beta", 0, 3, 0, "beta")]
    [InlineData("v0.3-beta", 0, 3, 0, "beta")]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.4.0-beta.2+abc123", 0, 4, 0, "beta.2")]
    public void Parses_versions(string text, int major, int minor, int patch, string? pre) =>
        Assert.Equal(new AppVersion(major, minor, patch, pre), V(text));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x")]
    public void Rejects_non_versions(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.3.0-beta", "0.3.0")]          // a release beats its beta
    [InlineData("0.3.0", "0.3.1-beta")]          // a newer beta beats the older release
    [InlineData("0.3.0-beta", "0.3.0-beta.2")]
    [InlineData("0.3.0-beta.2", "0.3.0-beta.10")] // numbers compare as numbers
    [InlineData("0.3.0-alpha", "0.3.0-beta")]
    [InlineData("0.9.9", "1.0.0")]
    public void Orders_versions(string lower, string higher)
    {
        Assert.True(V(higher) > V(lower));
        Assert.True(V(lower) < V(higher));
    }

    [Fact]
    public void Display_is_short()
    {
        Assert.Equal("0.3 beta", V("0.3.0-beta").Display);
        Assert.Equal("1.2.1", V("1.2.1").Display);
        Assert.Equal("2.0 rc 1", V("2.0.0-rc.1").Display);
    }

    [Fact]
    public void Current_version_comes_from_the_project()
    {
        Assert.Equal(V("0.3.3-beta"), AppVersion.Current);
    }

    private const string Releases = """
        [
          { "tag_name": "v0.5.0-beta", "name": "Draft", "draft": true, "prerelease": true, "html_url": "https://x/draft", "assets": [] },
          { "tag_name": "v0.4.0-beta", "name": "QuickClip 0.4 beta", "draft": false, "prerelease": true, "html_url": "https://x/0.4b",
            "assets": [
              { "name": "notes.txt", "browser_download_url": "https://x/notes", "size": 1 },
              { "name": "QuickClip-0.4.0-beta-win-x64.zip", "browser_download_url": "https://x/0.4b.zip", "size": 123,
                "digest": "sha256:ABCDEF" },
              { "name": "QuickClip-0.4.0-beta-setup.exe", "browser_download_url": "https://x/0.4b-setup.exe", "size": 99 } ] },
          { "tag_name": "v0.3.1", "name": "", "draft": false, "prerelease": false, "html_url": "https://x/0.3.1", "assets": [] },
          { "tag_name": "v0.3.0-beta", "name": "QuickClip 0.3 beta", "draft": false, "prerelease": true, "html_url": "https://x/0.3b", "assets": [] },
          { "tag_name": "nightly", "name": "Nightly", "draft": false, "prerelease": true, "html_url": "https://x/n", "assets": [] }
        ]
        """;

    [Fact]
    public void Beta_users_get_the_newest_release_including_betas()
    {
        var update = Updater.PickUpdate(Releases, V("0.3.0-beta"));
        Assert.NotNull(update);
        Assert.Equal(V("0.4.0-beta"), update!.Version);
        Assert.Equal(new ReleaseAsset("https://x/0.4b.zip", 123, "ABCDEF"), update.Portable);
        Assert.Equal(new ReleaseAsset("https://x/0.4b-setup.exe", 99, null), update.Setup);
        Assert.Equal("QuickClip 0.4 beta", update.Name);
    }

    [Fact]
    public void Stable_users_only_get_stable_releases()
    {
        var update = Updater.PickUpdate(Releases, V("0.3.0"));
        Assert.Equal(V("0.3.1"), update!.Version);
        Assert.Null(update.Setup); // nothing attached: the app sends people to the release page
        Assert.Null(update.Portable);
        Assert.Equal("v0.3.1", update.Name);
    }

    [Fact]
    public void Nothing_newer_means_no_update()
    {
        Assert.Null(Updater.PickUpdate(Releases, V("0.4.0-beta")));
        Assert.Null(Updater.PickUpdate("[]", V("0.3.0-beta")));
    }

    [Fact]
    public async Task Apply_script_copies_the_new_files_over_the_old_ones()
    {
        string root = Path.Combine(Path.GetTempPath(), "quickclip-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        string source = Path.Combine(root, "new"), target = Path.Combine(root, "app");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "ffmpeg"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "QuickClip.exe"), "new exe");
            File.WriteAllText(Path.Combine(source, "ffmpeg", "ffmpeg.exe"), "new ffmpeg");
            File.WriteAllText(Path.Combine(target, "QuickClip.exe"), "old exe");
            File.WriteAllText(Path.Combine(target, "settings-like.txt"), "kept");
            string script = Path.Combine(root, "apply.ps1");
            File.WriteAllText(script, Updater.ApplyScript);

            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProcessId", "999999",
                         "-Source", source, "-Target", target, "-Restart", "" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(target, "QuickClip.exe")));
            Assert.Equal("new ffmpeg", File.ReadAllText(Path.Combine(target, "ffmpeg", "ffmpeg.exe")));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "settings-like.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
