using QuickClip.Updates;

namespace QuickClip.Tests;

public sealed class ReleaseNotesTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Fact]
    public void Shows_whats_new_only_after_an_update()
    {
        Assert.Equal((true, V("0.5.0")), ReleaseNotes.Check("0.5.0", true, V("0.6.0")));
        Assert.Equal((false, (AppVersion?)null), ReleaseNotes.Check("0.6.0", true, V("0.6.0")));    // seen already
        Assert.Equal((false, (AppVersion?)null), ReleaseNotes.Check("0.7.0", true, V("0.6.0")));    // went back a version
        Assert.Equal((false, (AppVersion?)null), ReleaseNotes.Check(null, false, V("0.6.0")));      // a fresh install
        Assert.Equal((true, (AppVersion?)null), ReleaseNotes.Check(null, true, V("0.6.0")));        // from before this existed
        Assert.Equal((true, V("0.6.0")), ReleaseNotes.Check("0.6.0", true, V("0.7.0-nightly.1")));
    }

    private const string Releases = """
        [
          { "tag_name": "v0.7.0-nightly.2", "name": "QuickClip 0.7 nightly 2", "prerelease": true, "draft": false, "body": "n2", "html_url": "https://x/n2" },
          { "tag_name": "v0.7.0-nightly.1", "name": "QuickClip 0.7 nightly 1", "prerelease": true, "draft": false, "body": "n1", "html_url": "https://x/n1" },
          { "tag_name": "v0.6.0", "name": "QuickClip 0.6", "prerelease": false, "draft": false, "body": "six", "html_url": "https://x/6" },
          { "tag_name": "v0.5.0", "name": "QuickClip 0.5", "prerelease": false, "draft": false, "body": "five", "html_url": "https://x/5" },
          { "tag_name": "v0.8.0", "name": "Draft", "prerelease": false, "draft": true, "body": "draft", "html_url": "https://x/d" }
        ]
        """;

    [Fact]
    public void Shows_every_release_since_the_last_version_seen_newest_first()
    {
        Assert.Equal(["n2", "n1", "six"], ReleaseNotes.Select(Releases, V("0.5.0"), V("0.7.0-nightly.2")).Select(n => n.Body));
        Assert.Equal(["six"], ReleaseNotes.Select(Releases, V("0.5.0"), V("0.6.0")).Select(n => n.Body));
        Assert.Equal(["n1"], ReleaseNotes.Select(Releases, null, V("0.7.0-nightly.1")).Select(n => n.Body)); // just this one
        Assert.Empty(ReleaseNotes.Select(Releases, null, V("0.9.0")));                                      // never released
    }

    [Fact]
    public void A_stable_version_doesnt_list_the_nightly_builds_before_it()
    {
        string json = Releases.Replace("""{ "tag_name": "v0.8.0", "name": "Draft", "prerelease": false, "draft": true""",
                                       """{ "tag_name": "v0.7.0", "name": "QuickClip 0.7", "prerelease": false, "draft": false""");
        Assert.Equal(["draft"], ReleaseNotes.Select(json, V("0.6.0"), V("0.7.0")).Select(n => n.Body));
    }

    [Fact]
    public void Reads_the_markdown_the_release_notes_use()
    {
        var blocks = ReleaseNotes.Parse("""
            QuickClip 0.6 adds **instant sharing**.

            ## New

            - **Share**: copies `Videos\QuickClip Shared` and more,
              continued on the next line.
              - A nested point with a [link](https://example.com/page).
            * Another point.
            """);

        Assert.Equal([NoteBlockKind.Paragraph, NoteBlockKind.Heading, NoteBlockKind.Bullet, NoteBlockKind.Bullet, NoteBlockKind.Bullet],
            blocks.Select(b => b.Kind));
        Assert.Equal(2, blocks[1].Level);
        Assert.Equal([new NoteSpan(NoteSpanKind.Text, "QuickClip 0.6 adds "), new NoteSpan(NoteSpanKind.Bold, "instant sharing"), new NoteSpan(NoteSpanKind.Text, ".")],
            blocks[0].Spans);
        Assert.Equal([new NoteSpan(NoteSpanKind.Bold, "Share"), new NoteSpan(NoteSpanKind.Text, ": copies "),
                      new NoteSpan(NoteSpanKind.Code, @"Videos\QuickClip Shared"), new NoteSpan(NoteSpanKind.Text, " and more, continued on the next line.")],
            blocks[2].Spans);
        Assert.Equal(1, blocks[3].Level);
        Assert.Contains(new NoteSpan(NoteSpanKind.Link, "link", "https://example.com/page"), blocks[3].Spans);
        Assert.Equal(0, blocks[4].Level);
    }
}
