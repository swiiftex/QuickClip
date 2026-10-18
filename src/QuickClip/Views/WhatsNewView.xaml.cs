using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuickClip.Updates;

namespace QuickClip.Views;

/// <summary>The release notes of the versions QuickClip was just updated through, shown over the window.</summary>
public partial class WhatsNewView : UserControl
{
    private string _page = Updater.ReleasesPage;

    /// <summary>The user is done with it.</summary>
    public event Action? Closed;

    public WhatsNewView() => InitializeComponent();

    public void ShowLoading(AppVersion current)
    {
        TitleText.Text = $"What's new in QuickClip {current.Display}";
        SubtitleText.Text = "Loading the release notes…";
        Notes.Children.Clear();
        _page = Updater.ReleasesPage;
    }

    /// <param name="after">The version updated from, if known.</param>
    public void Show(AppVersion current, AppVersion? after, IReadOnlyList<ReleaseNote> notes, string? error)
    {
        TitleText.Text = $"What's new in QuickClip {current.Display}";
        SubtitleText.Text = error != null ? $"The release notes couldn't be loaded ({error}). They're on the release page."
            : notes.Count == 0 ? "This version has no release notes."
            : after != null ? $"Updated from {after.Display}." + (notes.Count > 1 ? $" Here's everything in the {notes.Count} releases since." : "")
            : "";
        _page = notes.FirstOrDefault()?.Url ?? Updater.ReleasesPage;
        Notes.Children.Clear();
        foreach (var note in notes)
        {
            if (notes.Count > 1)
                Notes.Children.Add(new TextBlock { Text = note.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, Notes.Children.Count == 0 ? 6 : 22, 0, 2) });
            foreach (var block in ReleaseNotes.Parse(note.Body))
                Notes.Children.Add(Element(block));
        }
    }

    private FrameworkElement Element(NoteBlock block)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        foreach (var span in block.Spans) text.Inlines.Add(Inline(span));
        switch (block.Kind)
        {
            case NoteBlockKind.Heading:
                text.FontSize = block.Level <= 2 ? 15 : 13.5;
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, 14, 0, 4);
                return text;
            case NoteBlockKind.Bullet:
                var row = new DockPanel { Margin = new Thickness(4 + block.Level * 18, 3, 0, 3) };
                var dot = new TextBlock { Text = block.Level == 0 ? "•" : "◦", Width = 16, LineHeight = 20, Foreground = (Brush)FindResource("SubtleText") };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(text);
                return row;
            default:
                text.Margin = new Thickness(0, 4, 0, 6);
                return text;
        }
    }

    private static Inline Inline(NoteSpan span)
    {
        switch (span.Kind)
        {
            case NoteSpanKind.Bold:
                return new Bold(new Run(span.Text));
            case NoteSpanKind.Code:
                return new Run(span.Text) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)) };
            case NoteSpanKind.Link when Uri.TryCreate(span.Url, UriKind.Absolute, out var uri):
                var link = new Hyperlink(new Run(span.Text)) { NavigateUri = uri, ToolTip = uri.ToString() };
                link.RequestNavigate += (_, e) => Open(e.Uri.ToString());
                return link;
            default:
                return new Run(span.Text);
        }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Opening a link failed: " + ex.Message); }
    }

    private void Page_Click(object sender, RoutedEventArgs e) => Open(_page);

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
