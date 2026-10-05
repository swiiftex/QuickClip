using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickClip.Controls;

/// <summary>Draws one audio track's waveform aligned with the timeline. Clicking seeks.</summary>
public sealed class WaveformLane : FrameworkElement
{
    private static readonly Brush LaneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    private static readonly Brush DisabledBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Pen CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen PlayheadPen = Freeze(new Pen(TimelineBar.PlayheadBrush, 1.5));
    private static readonly Pen HandlePen = Freeze(new Pen(TimelineBar.HandleBrush, 2));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(AudioTrack), typeof(WaveformLane), new PropertyMetadata(null, (d, e) => ((WaveformLane)d).OnTrackChanged((AudioTrack?)e.OldValue, (AudioTrack?)e.NewValue)));

    private Geometry? _cache;
    private bool _dirty = true;
    private bool _dragging;

    /// <summary>Raised when the user clicks/drags in a lane.</summary>
    public static event Action<double, bool>? SeekRequested;

    public AudioTrack? Track
    {
        get => (AudioTrack?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public WaveformLane()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    private void OnTrackChanged(AudioTrack? old, AudioTrack? now)
    {
        if (old != null)
        {
            old.Changed -= OnTrackSettingsChanged;
            old.WaveformUpdated -= Invalidate;
            old.Timeline.Changed -= Invalidate;
            old.Timeline.PositionChanged -= InvalidateVisual;
        }
        if (now != null)
        {
            now.Changed += OnTrackSettingsChanged;
            now.WaveformUpdated += Invalidate;
            now.Timeline.Changed += Invalidate;
            now.Timeline.PositionChanged += InvalidateVisual;
        }
        Invalidate();
    }

    private void OnTrackSettingsChanged(AudioTrack _) => Invalidate();

    private void Invalidate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Invalidate);
            return;
        }
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(0, 0, w, h), 4, 4);
        var track = Track;
        if (track == null || w <= 0 || h <= 0) return;
        var s = track.Timeline;

        if (_dirty)
        {
            _cache = BuildGeometry(track, w, h);
            _dirty = false;
        }
        dc.DrawLine(CenterPen, new Point(0, h / 2), new Point(w, h / 2));
        if (_cache != null)
            dc.DrawGeometry(track.Include ? track.Brush : DisabledBrush, null, _cache);

        double xi = s.TimeToX(s.In, w), xo = s.TimeToX(s.Out, w);
        if (xi > 0) dc.DrawRectangle(TimelineBar.OutsideShade, null, new Rect(0, 0, Math.Min(xi, w), h));
        if (xo < w) dc.DrawRectangle(TimelineBar.OutsideShade, null, new Rect(Math.Max(0, xo), 0, w - Math.Max(0, xo), h));
        if (xi >= 0 && xi <= w) dc.DrawLine(HandlePen, new Point(xi + 1, 0), new Point(xi + 1, h));
        if (xo >= 0 && xo <= w) dc.DrawLine(HandlePen, new Point(xo - 1, 0), new Point(xo - 1, h));

        double xp = Math.Round(s.TimeToX(s.Position, w)) + 0.5;
        if (xp >= 0 && xp <= w) dc.DrawLine(PlayheadPen, new Point(xp, 0), new Point(xp, h));
    }

    private static Geometry? BuildGeometry(AudioTrack track, double w, double h)
    {
        var (peaks, count) = track.Waveform.Snapshot();
        if (count == 0) return null;
        var s = track.Timeline;
        int columns = (int)Math.Ceiling(w);
        double gain = track.Volume;
        double mid = h / 2, half = h / 2 - 2;
        var top = new Point[columns];
        int last = -1;

        for (int x = 0; x < columns; x++)
        {
            double t0 = s.ViewStart + x / w * s.ViewSpan;
            double t1 = s.ViewStart + (x + 1) / w * s.ViewSpan;
            int b0 = (int)(t0 * Media.WaveformData.PeaksPerSecond);
            int b1 = Math.Max(b0 + 1, (int)(t1 * Media.WaveformData.PeaksPerSecond));
            if (b0 >= count) break;
            b1 = Math.Min(b1, count);
            float max = 0;
            for (int b = Math.Max(b0, 0); b < b1; b++)
                if (peaks[b] > max) max = peaks[b];
            // sqrt makes quiet speech readable without flattening loud parts
            double a = Math.Sqrt(Math.Min(1, max * gain)) * half;
            top[x] = new Point(x, mid - Math.Max(a, 0.5));
            last = x;
        }
        if (last < 0) return null;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, mid), true, true);
            for (int x = 0; x <= last; x++) ctx.LineTo(top[x], false, false);
            for (int x = last; x >= 0; x--) ctx.LineTo(new Point(x, 2 * mid - top[x].Y), false, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var track = Track;
        if (track == null) return;
        _dragging = true;
        CaptureMouse();
        SeekRequested?.Invoke(track.Timeline.XToTime(e.GetPosition(this).X, ActualWidth), false);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var track = Track;
        if (!_dragging || track == null) return;
        SeekRequested?.Invoke(track.Timeline.XToTime(e.GetPosition(this).X, ActualWidth), false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var track = Track;
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (track != null)
            SeekRequested?.Invoke(track.Timeline.XToTime(e.GetPosition(this).X, ActualWidth), true);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging && Track is { } track)
        {
            _dragging = false;
            SeekRequested?.Invoke(track.Timeline.Position, true);
        }
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) => TimelineBar.HandleWheel(this, Track?.Timeline, e);

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
