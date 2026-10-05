namespace QuickClip.Controls;

/// <summary>Shared time model for the ruler and the waveform lanes: duration, playhead, in/out and the zoomed view.</summary>
public sealed class TimelineState
{
    public const double MinSelection = 0.05;
    private const double MinSpan = 0.25;

    public double Duration { get; private set; }
    public double Position { get; private set; }
    public double In { get; private set; }
    public double Out { get; private set; }
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; } = 1;
    public double ViewSpan => ViewEnd - ViewStart;

    /// <summary>View, duration or in/out changed: everything must redraw.</summary>
    public event Action? Changed;
    /// <summary>Only the playhead moved.</summary>
    public event Action? PositionChanged;
    /// <summary>In or out point changed (by the user or programmatically).</summary>
    public event Action? SelectionChanged;

    public void Reset(double duration)
    {
        Duration = Math.Max(duration, 0.001);
        Position = 0;
        In = 0;
        Out = Duration;
        ViewStart = 0;
        ViewEnd = Duration;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        PositionChanged?.Invoke();
    }

    public void UpdateDuration(double duration)
    {
        if (duration <= 0 || Math.Abs(duration - Duration) < 0.0005) return;
        bool outAtEnd = Math.Abs(Out - Duration) < 0.0005;
        bool fullView = ViewStart <= 0 && Math.Abs(ViewEnd - Duration) < 0.0005;
        Duration = duration;
        if (outAtEnd || Out > Duration) Out = Duration;
        if (In > Out) In = 0;
        if (fullView) ViewEnd = Duration;
        ClampView();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void SetPosition(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        if (t == Position) return;
        Position = t;
        PositionChanged?.Invoke();
    }

    public void SetIn(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        if (t > Out - MinSelection) Out = Duration;
        In = Math.Min(t, Out - MinSelection);
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void SetOut(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        if (t < In + MinSelection) In = 0;
        Out = Math.Max(t, In + MinSelection);
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Drag helpers that never push the other handle around.</summary>
    public void DragIn(double t)
    {
        In = Math.Clamp(t, 0, Out - MinSelection);
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void DragOut(double t)
    {
        Out = Math.Clamp(t, In + MinSelection, Duration);
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void ResetSelection()
    {
        In = 0;
        Out = Duration;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void SetView(double start, double end)
    {
        double span = Math.Clamp(end - start, Math.Min(MinSpan, Duration), Duration);
        ViewStart = start;
        ViewEnd = start + span;
        ClampView();
        Changed?.Invoke();
    }

    public void ZoomAt(double anchorTime, double factor)
    {
        double span = Math.Clamp(ViewSpan * factor, Math.Min(MinSpan, Duration), Duration);
        double ratio = ViewSpan > 0 ? (anchorTime - ViewStart) / ViewSpan : 0.5;
        SetView(anchorTime - ratio * span, anchorTime - ratio * span + span);
    }

    public void Pan(double seconds) => SetView(ViewStart + seconds, ViewEnd + seconds);

    public void Fit() => SetView(0, Duration);

    /// <summary>Pages the view so the playhead stays visible while playing.</summary>
    public void Follow(double t)
    {
        if (ViewSpan >= Duration - 0.001) return;
        if (t > ViewEnd || t < ViewStart)
            SetView(t - ViewSpan * 0.05, t + ViewSpan * 0.95);
    }

    private void ClampView()
    {
        double span = ViewEnd - ViewStart;
        if (ViewStart < 0) { ViewStart = 0; ViewEnd = span; }
        if (ViewEnd > Duration) { ViewEnd = Duration; ViewStart = Math.Max(0, Duration - span); }
    }

    public double TimeToX(double t, double width) => ViewSpan <= 0 ? 0 : (t - ViewStart) / ViewSpan * width;

    public double XToTime(double x, double width) => width <= 0 ? 0 : Math.Clamp(ViewStart + x / width * ViewSpan, 0, Duration);
}
