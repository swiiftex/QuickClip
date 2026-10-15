using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickClip.Controls;

/// <summary>
/// Draws one audio track's waveform aligned with the timeline. Clicking seeks; an added sound sits in a block that can
/// be dragged along the timeline.
/// </summary>
public sealed class WaveformLane : FrameworkElement
{
    private static readonly Brush LaneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    private static readonly Brush DisabledBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x10)));
    private static readonly Pen CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen PlayheadPen = Freeze(new Pen(TimelineBar.PlayheadBrush, 1.5));
    private static readonly Pen HandlePen = Freeze(new Pen(TimelineBar.HandleBrush, 2));
    private static readonly Typeface LabelFace = new("Segoe UI");
    private const double SnapPixels = 7;

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(AudioTrack), typeof(WaveformLane), new PropertyMetadata(null, (d, e) => ((WaveformLane)d).OnTrackChanged((AudioTrack?)e.OldValue, (AudioTrack?)e.NewValue)));

    private Geometry? _cache;
    private bool _dirty = true;
    private bool _dragging;
    private bool _moving;   // dragging an added sound along the timeline
    private double _grab;   // seconds between the sound's start and the point it was grabbed by

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
            old.Moved -= OnTrackSettingsChanged;
            old.WaveformUpdated -= Invalidate;
            old.Timeline.Changed -= Invalidate;
            old.Timeline.PositionChanged -= InvalidateVisual;
        }
        if (now != null)
        {
            now.Changed += OnTrackSettingsChanged;
            now.Moved += OnTrackSettingsChanged;
            now.WaveformUpdated += Invalidate;
            now.Timeline.Changed += Invalidate;
            now.Timeline.PositionChanged += InvalidateVisual;
        }
        ContextMenu = now is { IsSound: true } ? SoundMenu(now) : null;
        Invalidate();
    }

    private static ContextMenu SoundMenu(AudioTrack track)
    {
        var toPlayhead = new MenuItem { Header = "Start at the playhead" };
        toPlayhead.Click += (_, _) => track.MoveTo(track.Timeline.Position);
        var remove = new MenuItem { Header = "Remove sound" };
        remove.Click += (_, _) => track.Remove();
        return new ContextMenu { Items = { toPlayhead, remove } };
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

        // An added sound's block: where it plays in the clip.
        Rect block = Rect.Empty;
        if (track.IsSound)
        {
            double xs = Math.Max(0, s.TimeToX(track.Offset, w)), xe = Math.Min(w, s.TimeToX(track.Offset + track.SoundDuration, w));
            if (xe > xs)
            {
                block = new Rect(xs, 1, xe - xs, h - 2);
                var c = track.Color;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(_moving ? (byte)0x48 : (byte)0x2C, c.R, c.G, c.B)),
                    new Pen(new SolidColorBrush(Color.FromArgb(0xB0, c.R, c.G, c.B)), 1), block, 4, 4);
            }
        }

        dc.DrawLine(CenterPen, new Point(0, h / 2), new Point(w, h / 2));
        if (_cache != null)
            dc.DrawGeometry(track.Include ? track.Brush : DisabledBrush, null, _cache);

        if (!block.IsEmpty && block.Width > 36)
        {
            string start = track.Offset >= 0 ? Fmt.Time(track.Offset) : "−" + Fmt.Time(-track.Offset);
            var text = new FormattedText($"♪ {track.Name}  ·  {start}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelFace, 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, block.Width - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawRoundedRectangle(LabelBackBrush, null, new Rect(block.X + 3, 3, text.Width + 8, text.Height + 2), 3, 3);
            dc.DrawText(text, new Point(block.X + 7, 4));
        }

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
        double offset = track.IsSound ? track.Offset : 0; // the waveform is in the sound's own time
        int columns = (int)Math.Ceiling(w);
        double gain = track.Volume;
        double mid = h / 2, half = h / 2 - 2;
        var top = new Point[columns];
        int first = -1, last = -1;

        for (int x = 0; x < columns; x++)
        {
            double t0 = s.ViewStart + x / w * s.ViewSpan - offset;
            double t1 = s.ViewStart + (x + 1) / w * s.ViewSpan - offset;
            if (t1 <= 0) continue; // before an added sound starts
            int b0 = (int)(Math.Max(0, t0) * Media.WaveformData.PeaksPerSecond);
            int b1 = Math.Max(b0 + 1, (int)(t1 * Media.WaveformData.PeaksPerSecond));
            if (b0 >= count) break;
            b1 = Math.Min(b1, count);
            float max = 0;
            for (int b = b0; b < b1; b++)
                if (peaks[b] > max) max = peaks[b];
            // sqrt makes quiet speech readable without flattening loud parts
            double a = Math.Sqrt(Math.Min(1, max * gain)) * half;
            top[x] = new Point(x, mid - Math.Max(a, 0.5));
            if (first < 0) first = x;
            last = x;
        }
        if (last < 0) return null;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(first, mid), true, true);
            for (int x = first; x <= last; x++) ctx.LineTo(top[x], false, false);
            for (int x = last; x >= first; x--) ctx.LineTo(new Point(x, 2 * mid - top[x].Y), false, false);
        }
        g.Freeze();
        return g;
    }

    // ---- Mouse: drag an added sound by its block; anywhere else seeks ------------------------------------

    private bool OnSound(AudioTrack track, double x) =>
        track.IsSound && x >= track.Timeline.TimeToX(track.Offset, ActualWidth) - 2
                      && x <= track.Timeline.TimeToX(track.Offset + track.SoundDuration, ActualWidth) + 2;

    /// <summary>Timeline time at <paramref name="x"/>, past either end too (a sound can be dragged partly out of the clip).</summary>
    private double TimeAt(TimelineState s, double x) => s.ViewStart + x / Math.Max(1, ActualWidth) * s.ViewSpan;

    /// <summary>Lines the sound's start or end up with the clip's ends, the in and out points or the playhead when close.</summary>
    private double Snap(AudioTrack track, double start)
    {
        var s = track.Timeline;
        double tolerance = SnapPixels / Math.Max(1, ActualWidth) * s.ViewSpan;
        double best = start, bestDistance = tolerance;
        foreach (double target in new[] { 0, s.Duration, s.In, s.Out, s.Position })
        {
            foreach (double candidate in new[] { target, target - track.SoundDuration })
            {
                double distance = Math.Abs(candidate - start);
                if (distance <= bestDistance) (best, bestDistance) = (candidate, distance);
            }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var track = Track;
        if (track == null) return;
        double x = e.GetPosition(this).X;
        CaptureMouse();
        e.Handled = true;
        if (OnSound(track, x))
        {
            _moving = true;
            _grab = TimeAt(track.Timeline, x) - track.Offset;
            Invalidate();
            return;
        }
        _dragging = true;
        SeekRequested?.Invoke(track.Timeline.XToTime(x, ActualWidth), false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var track = Track;
        if (track == null) return;
        double x = e.GetPosition(this).X;
        if (_moving)
            track.MoveTo(Snap(track, TimeAt(track.Timeline, x) - _grab));
        else if (_dragging)
            SeekRequested?.Invoke(track.Timeline.XToTime(x, ActualWidth), false);
        else
            Cursor = OnSound(track, x) ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_moving)
        {
            ReleaseMouseCapture(); // ends the move in OnLostMouseCapture
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (Track is { } track)
            SeekRequested?.Invoke(track.Timeline.XToTime(e.GetPosition(this).X, ActualWidth), true);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_moving)
        {
            _moving = false;
            Invalidate();
        }
        else if (_dragging && Track is { } track)
        {
            _dragging = false;
            SeekRequested?.Invoke(track.Timeline.Position, true);
        }
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) => TimelineBar.HandleWheel(this, Track?.Timeline, e);

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
