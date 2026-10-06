#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        ShowSettings();
        await Updates.Updater.CheckAsync();
        await Task.Delay(3000);
        SaveVisual(Path.Combine(dir, "settings-1.png"));
        if (FindChild<ScrollViewer>(Pages.Children.OfType<Views.SettingsView>().First()) is { } scroller)
        {
            scroller.ScrollToEnd();
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "settings-2.png"));
        }

        if (editorFile != null)
        {
            await OpenInEditorAsync(editorFile);
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "editor.png"));
            double width = Width;
            Width = MinWidth;
            await Task.Delay(1000);
            SaveVisual(Path.Combine(dir, "editor-narrow.png"));
            Width = width;
        }

        var r = Recorder.Instance;
        var stats = r.Stats();
        report.AppendLine($"recorder running={r.IsRunning} error={r.Error} summary={r.State.Summary} chat=[{string.Join(",", r.State.ChatApps)}]");
        report.AppendLine($"stats {stats.Width}x{stats.Height} fps={stats.Fps} buffered={stats.BufferSeconds:0.0}s bytes={stats.BufferBytes} dropped={stats.DroppedFrames}");
        report.AppendLine($"hotkey active={App.Host?.HotkeyActive} status={RecStatus.Text}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
    }

    private void SaveVisual(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
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
