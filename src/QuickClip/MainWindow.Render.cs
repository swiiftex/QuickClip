#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Gallery;
using QuickClip.Recording;

namespace QuickClip;

// Debug-only: renders each page of this window to PNG files (no screen capture) for checking the layout.
public partial class MainWindow
{
    internal async Task RenderPagesAsync(string dir, string? editorFile)
    {
        Directory.CreateDirectory(dir);
        var report = new System.Text.StringBuilder();
        await Task.Delay(5000);
        SaveVisual(Path.Combine(dir, "gallery.png"));

        if (Gallery.PlayFirstForRender())
        {
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "player.png"));
            _player!.SaveVideoForRender(Path.Combine(dir, "player-video.png"));
            report.AppendLine($"player title={Title} {_player.DebugState}");
            // The full-screen layout, without taking over the screen.
            ApplyFullScreenLayout(true);
            await Task.Delay(1000);
            SaveVisual(Path.Combine(dir, "player-full.png"));
            await Task.Delay(3500);
            SaveVisual(Path.Combine(dir, "player-full-idle.png"));
            report.AppendLine($"player after 4.5 s full screen: controls={_player.ControlBar.Visibility} {_player.DebugState}");
            ApplyFullScreenLayout(false);
            ShowPage(Gallery);
        }

        var prompt = Views.TextPrompt.ShowForRender(this, "New project", "Montage", "Create");
        await Task.Delay(800);
        SaveVisual(prompt, Path.Combine(dir, "new-project-prompt.png"));
        prompt.Close();

        // Projects: tick two clips, add them to a new project, add music, preview across the join, reorder, export.
        if (Gallery.SelectForRender(2) is { Count: 2 } picked)
        {
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "gallery-selected.png"));
            string clips = AppSettings.Current.ClipsFolder;
            var project = ProjectLibrary.Create(clips, "Render test");
            await Gallery.AddSelectionForRender(project.Folder);
            var pane = Gallery.ProjectForRender;
            if (Directory.GetFiles(clips, "*.wav").FirstOrDefault() is { } song) await pane.SetMusicForRender(song);
            await Task.Delay(4000);
            SaveVisual(Path.Combine(dir, "project.png"));
            report.AppendLine($"project after adding {picked.Count}: {pane.DebugState}");
            pane.SeekForRender(pane.FirstClipDuration - 1.5);
            await Task.Delay(500);
            pane.PlayForRender();
            await Task.Delay(3500);
            report.AppendLine($"project 3.5 s after playing from 1.5 s before the join: {pane.DebugState}");
            pane.SaveVideoForRender(Path.Combine(dir, "project-video.png"));
            pane.MoveForRender(1, 0);
            report.AppendLine($"after moving the second clip first: {string.Join(", ", ProjectLibrary.Load(project.Folder).Clips.Select(Path.GetFileName))}");
            pane.ExportForRender();
            for (int i = 0; i < 1200 && pane.Exporting; i++) await Task.Delay(100);
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "montage-editor.png"));
            report.AppendLine($"after export: page={(EditorNav.IsChecked == true ? "editor" : "other")} title={Title}");
            if (_editor?.CurrentFile is { } montage)
            {
                var info = await Media.MediaInfo.ProbeAsync(montage);
                report.AppendLine($"montage {Path.GetFileName(montage)}: {info.Video?.Width}x{info.Video?.Height} {info.Duration:0.00}s " +
                                  $"tracks=[{string.Join(", ", info.Audio.Select(a => a.DisplayName))}]");
            }
            ShowPage(Gallery);
        }

        ShowSettings();
        await Updates.Updater.CheckAsync();
        await Task.Delay(3000);
        SaveVisual(Path.Combine(dir, "settings-1.png"));
        _settings!.SelectAspectForRender("16:9");
        await Task.Delay(500);
        SaveVisual(Path.Combine(dir, "settings-16x9.png"));
        _settings.SetGameWindowForRender(true);
        await Task.Delay(500);
        SaveVisual(Path.Combine(dir, "settings-game-window.png"));
        _settings.SetGameWindowForRender(false);
        if (FindChild<ScrollViewer>(Pages.Children.OfType<Views.SettingsView>().First()) is { } scroller)
        {
            scroller.ScrollToEnd();
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "settings-2.png"));
        }

        // What's new after an update from 0.4.2: the real notes of every release since, from GitHub.
        await ShowWhatsNewAsync(new Updates.AppVersion(0, 4, 2, null));
        await Task.Delay(800);
        SaveVisual(Path.Combine(dir, "whats-new.png"));
        report.AppendLine($"what's new: visible={WhatsNew.Visibility}");
        WhatsNew.Visibility = Visibility.Collapsed;

        if (editorFile != null)
        {
            await OpenInEditorAsync(editorFile);
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "editor.png"));
            report.AppendLine($"editor frame rates: {_editor?.FpsChoicesForRender}");
            _editor?.ExpandOptionsForRender();
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "editor-options.png"));

            // Save over the clip (trimmed), then undo that.
            if (_editor != null && Path.GetFileName(editorFile).StartsWith("undo-test", StringComparison.OrdinalIgnoreCase))
            {
                double before = (await Media.MediaInfo.ProbeAsync(editorFile)).Duration;
                await _editor.SaveOverForRender(2);
                await Task.Delay(1500);
                var edited = await Media.MediaInfo.ProbeAsync(editorFile);
                report.AppendLine($"undo: {before:0.00}s before, {edited.Duration:0.00}s after saving over; can undo={_editor.CanUndo}; status={_editor.StatusForRender}");
                SaveVisual(Path.Combine(dir, "editor-saved-over.png"));
                await _editor.UndoForRender();
                await Task.Delay(1500);
                var restored = await Media.MediaInfo.ProbeAsync(editorFile);
                report.AppendLine($"undo: {restored.Duration:0.00}s after undo; can undo={_editor.CanUndo}; status={_editor.StatusForRender}; " +
                                  $"leftovers=[{string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(editorFile)!).Select(Path.GetFileName))}]");
                SaveVisual(Path.Combine(dir, "editor-undone.png"));
            }

            // Add a sound next to the clip, play across it, then move it.
            if (_editor != null && Directory.GetFiles(Path.GetDirectoryName(editorFile)!, "*.wav").FirstOrDefault() is { } sound)
            {
                await _editor.AddSoundForRender(sound, 1.0);
                await Task.Delay(2500);
                report.AppendLine($"editor after adding a sound at 1 s: {_editor.DebugState}");
                SaveVisual(Path.Combine(dir, "editor-sound.png"));
                _editor.SeekForRender(0.5);
                await Task.Delay(300);
                _editor.PlayForRender();
                await Task.Delay(2000);
                report.AppendLine($"editor 2 s after playing from 0.5 s: {_editor.DebugState}");
                _editor.PlayForRender();
                _editor.MoveSoundForRender(2.5);
                await Task.Delay(2500);
                report.AppendLine($"editor after moving the sound to 2.5 s: {_editor.DebugState}");
                SaveVisual(Path.Combine(dir, "editor-sound-moved.png"));
            }
            double width = Width;
            Width = MinWidth;
            await Task.Delay(1000);
            SaveVisual(Path.Combine(dir, "editor-narrow.png"));
            Width = width;
        }

        report.AppendLine($"content {((FrameworkElement)Content).ActualWidth}x{((FrameworkElement)Content).ActualHeight}, drawn bounds {VisualTreeHelper.GetDescendantBounds((Visual)Content)}");
        foreach (UIElement page in Pages.Children)
            report.AppendLine($"  {page.GetType().Name} {page.Visibility} bounds {VisualTreeHelper.GetDescendantBounds(page)}");
        var r = Recorder.Instance;
        var stats = r.Stats();
        report.AppendLine($"recorder running={r.IsRunning} error={r.Error} summary={r.State.Summary} chat=[{string.Join(",", r.State.ChatApps)}] music=[{string.Join(",", r.State.MusicApps)}]");
        report.AppendLine($"stats {stats.Width}x{stats.Height} fps={stats.Fps} buffered={stats.BufferSeconds:0.0}s bytes={stats.BufferBytes} dropped={stats.DroppedFrames}");
        report.AppendLine($"hotkey active={App.Host?.HotkeyActive} status={RecStatus.Text}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
    }

    private void SaveVisual(string path) => SaveVisual(this, path);

    private static void SaveVisual(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            // Map exactly the window's area, even if something hangs off its edge.
            var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area }, null, area);
        }
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
#endif
