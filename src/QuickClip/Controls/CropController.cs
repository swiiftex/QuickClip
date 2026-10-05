using System.Globalization;
using System.Text;
using System.Windows;

namespace QuickClip.Controls;

/// <summary>
/// Crop rectangle editing on top of the mpv video. Geometry lives in source pixels; the overlay is drawn by
/// mpv itself as an ASS drawing (so it sits exactly on the video), and mouse input arrives from <see cref="VideoHost"/>.
/// </summary>
internal sealed class CropController
{
    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW, New }

    private const double MinSize = 16;
    private const double Grab = 9;

    private Rect _rect;
    private Rect _dragStartRect;
    private Point _dragStart;
    private Handle _drag = Handle.None;

    public double SourceWidth { get; private set; }
    public double SourceHeight { get; private set; }
    public bool Enabled { get; private set; }
    /// <summary>Width / height, or null for free-form.</summary>
    public double? AspectRatio { get; private set; }

    // Where the video sits inside the OSD (device pixels), from mpv's osd-dimensions.
    private double _osdW, _osdH, _vx, _vy, _vw, _vh;

    /// <summary>Raised whenever the overlay must be redrawn and/or the rectangle changed.</summary>
    public event Action? Changed;

    public Rect Rect => _rect;

    public bool IsFullFrame =>
        Math.Round(_rect.X) <= 0 && Math.Round(_rect.Y) <= 0 &&
        Math.Round(_rect.Width) >= SourceWidth - 1 && Math.Round(_rect.Height) >= SourceHeight - 1;

    public void Reset(double sourceWidth, double sourceHeight)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        _rect = new Rect(0, 0, sourceWidth, sourceHeight);
        Enabled = false;
        AspectRatio = null;
        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled && SourceWidth > 0;
        Changed?.Invoke();
    }

    public void SetOsd(double w, double h, double ml, double mt, double mr, double mb)
    {
        _osdW = w; _osdH = h;
        _vx = ml; _vy = mt;
        _vw = Math.Max(1, w - ml - mr);
        _vh = Math.Max(1, h - mt - mb);
        Changed?.Invoke();
    }

    /// <summary>Sets the aspect lock and fits the largest such rectangle around the current centre.</summary>
    public void SetAspect(double? ratio)
    {
        AspectRatio = ratio;
        if (ratio is double r && SourceWidth > 0)
        {
            double cx = _rect.X + _rect.Width / 2, cy = _rect.Y + _rect.Height / 2;
            double w = SourceWidth, h = w / r;
            if (h > SourceHeight) { h = SourceHeight; w = h * r; }
            // Keep roughly the current size if it already fits
            double scale = Math.Min(1, Math.Max(_rect.Width / w, _rect.Height / h));
            if (_rect.Width >= SourceWidth - 1 && _rect.Height >= SourceHeight - 1) scale = 1;
            w *= scale; h *= scale;
            _rect = new Rect(Math.Clamp(cx - w / 2, 0, SourceWidth - w), Math.Clamp(cy - h / 2, 0, SourceHeight - h), w, h);
        }
        Changed?.Invoke();
    }

    public void SetRect(double x, double y, double w, double h)
    {
        w = Math.Clamp(w, MinSize, SourceWidth);
        h = Math.Clamp(h, MinSize, SourceHeight);
        x = Math.Clamp(x, 0, SourceWidth - w);
        y = Math.Clamp(y, 0, SourceHeight - h);
        _rect = new Rect(x, y, w, h);
        Changed?.Invoke();
    }

    /// <summary>The crop FFmpeg should apply: even sizes/offsets, or null if nothing is cropped.</summary>
    public (int X, int Y, int W, int H)? ExportRect()
    {
        if (!Enabled || IsFullFrame) return null;
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        int w = Math.Min(Even(_rect.Width), (int)SourceWidth / 2 * 2);
        int h = Math.Min(Even(_rect.Height), (int)SourceHeight / 2 * 2);
        int x = Math.Clamp((int)Math.Round(_rect.X / 2) * 2, 0, (int)SourceWidth - w);
        int y = Math.Clamp((int)Math.Round(_rect.Y / 2) * 2, 0, (int)SourceHeight - h);
        return (x, y, w, h);
    }

    // ---- mapping ---------------------------------------------------------------------

    private double Sx => _vw / Math.Max(1, SourceWidth);
    private double Sy => _vh / Math.Max(1, SourceHeight);

    private Rect ToScreen(Rect r) => new(_vx + r.X * Sx, _vy + r.Y * Sy, r.Width * Sx, r.Height * Sy);

    private Point ToSource(double x, double y) =>
        new(Math.Clamp((x - _vx) / Sx, 0, SourceWidth), Math.Clamp((y - _vy) / Sy, 0, SourceHeight));

    // ---- mouse -----------------------------------------------------------------------

    private Handle HitTest(double x, double y)
    {
        var r = ToScreen(_rect);
        bool nearL = Math.Abs(x - r.Left) <= Grab, nearR = Math.Abs(x - r.Right) <= Grab;
        bool nearT = Math.Abs(y - r.Top) <= Grab, nearB = Math.Abs(y - r.Bottom) <= Grab;
        bool inX = x >= r.Left - Grab && x <= r.Right + Grab, inY = y >= r.Top - Grab && y <= r.Bottom + Grab;
        if (nearT && nearL) return Handle.NW;
        if (nearT && nearR) return Handle.NE;
        if (nearB && nearL) return Handle.SW;
        if (nearB && nearR) return Handle.SE;
        if (nearT && inX) return Handle.N;
        if (nearB && inX) return Handle.S;
        if (nearL && inY) return Handle.W;
        if (nearR && inY) return Handle.E;
        if (r.Contains(x, y)) return Handle.Move;
        return Handle.New;
    }

    public int CursorAt(int x, int y)
    {
        if (!Enabled) return VideoHost.IDC_HAND;
        return (_drag != Handle.None ? _drag : HitTest(x, y)) switch
        {
            Handle.NW or Handle.SE => VideoHost.IDC_SIZENWSE,
            Handle.NE or Handle.SW => VideoHost.IDC_SIZENESW,
            Handle.N or Handle.S => VideoHost.IDC_SIZENS,
            Handle.E or Handle.W => VideoHost.IDC_SIZEWE,
            Handle.Move => VideoHost.IDC_SIZEALL,
            _ => VideoHost.IDC_CROSS,
        };
    }

    public void MouseDown(int x, int y)
    {
        if (!Enabled) return;
        _drag = HitTest(x, y);
        _dragStart = ToSource(x, y);
        _dragStartRect = _rect;
    }

    public void MouseMove(int x, int y)
    {
        if (!Enabled || _drag == Handle.None) return;
        var p = ToSource(x, y);
        switch (_drag)
        {
            case Handle.Move:
                double nx = Math.Clamp(_dragStartRect.X + p.X - _dragStart.X, 0, SourceWidth - _rect.Width);
                double ny = Math.Clamp(_dragStartRect.Y + p.Y - _dragStart.Y, 0, SourceHeight - _rect.Height);
                _rect = new Rect(nx, ny, _rect.Width, _rect.Height);
                break;
            case Handle.New:
                if (Math.Abs(p.X - _dragStart.X) < 3 && Math.Abs(p.Y - _dragStart.Y) < 3) return;
                _rect = Corner(_dragStart, p, Math.Sign(p.X - _dragStart.X), Math.Sign(p.Y - _dragStart.Y));
                break;
            case Handle.NW: _rect = Corner(_dragStartRect.BottomRight, p, -1, -1); break;
            case Handle.NE: _rect = Corner(_dragStartRect.BottomLeft, p, 1, -1); break;
            case Handle.SW: _rect = Corner(_dragStartRect.TopRight, p, -1, 1); break;
            case Handle.SE: _rect = Corner(_dragStartRect.TopLeft, p, 1, 1); break;
            case Handle.E: _rect = Edge(p.X - _dragStartRect.Left, horizontal: true, anchorMax: false); break;
            case Handle.W: _rect = Edge(_dragStartRect.Right - p.X, horizontal: true, anchorMax: true); break;
            case Handle.S: _rect = Edge(p.Y - _dragStartRect.Top, horizontal: false, anchorMax: false); break;
            case Handle.N: _rect = Edge(_dragStartRect.Bottom - p.Y, horizontal: false, anchorMax: true); break;
        }
        Changed?.Invoke();
    }

    public void MouseUp()
    {
        _drag = Handle.None;
        Changed?.Invoke();
    }

    /// <summary>Rectangle spanned from a fixed anchor towards the pointer, honouring the aspect lock and bounds.</summary>
    private Rect Corner(Point anchor, Point p, int dirX, int dirY)
    {
        if (dirX == 0) dirX = 1;
        if (dirY == 0) dirY = 1;
        double maxW = dirX > 0 ? SourceWidth - anchor.X : anchor.X;
        double maxH = dirY > 0 ? SourceHeight - anchor.Y : anchor.Y;
        double w = Math.Clamp((p.X - anchor.X) * dirX, MinSize, Math.Max(MinSize, maxW));
        double h = Math.Clamp((p.Y - anchor.Y) * dirY, MinSize, Math.Max(MinSize, maxH));
        if (AspectRatio is double r)
        {
            if (w / h > r) w = h * r; else h = w / r;
            if (w > maxW) { w = maxW; h = w / r; }
            if (h > maxH) { h = maxH; w = h * r; }
        }
        double x = dirX > 0 ? anchor.X : anchor.X - w;
        double y = dirY > 0 ? anchor.Y : anchor.Y - h;
        return new Rect(Math.Max(0, x), Math.Max(0, y), w, h);
    }

    /// <summary>Resize from one edge; with an aspect lock the other dimension grows around the centre.</summary>
    private Rect Edge(double size, bool horizontal, bool anchorMax)
    {
        var s = _dragStartRect;
        double limit = horizontal
            ? (anchorMax ? s.Right : SourceWidth - s.Left)
            : (anchorMax ? s.Bottom : SourceHeight - s.Top);
        size = Math.Clamp(size, MinSize, Math.Max(MinSize, limit));

        double w = horizontal ? size : s.Width;
        double h = horizontal ? s.Height : size;
        if (AspectRatio is double r)
        {
            if (horizontal) h = w / r; else w = h * r;
            if (h > SourceHeight) { h = SourceHeight; w = h * r; }
            if (w > SourceWidth) { w = SourceWidth; h = w / r; }
        }
        double x = horizontal ? (anchorMax ? s.Right - w : s.Left) : Math.Clamp(s.X + s.Width / 2 - w / 2, 0, SourceWidth - w);
        double y = horizontal ? Math.Clamp(s.Y + s.Height / 2 - h / 2, 0, SourceHeight - h) : (anchorMax ? s.Bottom - h : s.Top);
        return new Rect(x, y, w, h);
    }

    // ---- drawing ---------------------------------------------------------------------

    /// <summary>ASS events for mpv's osd-overlay, in OSD pixel coordinates. Null when nothing should be shown.</summary>
    public (string Ass, int ResX, int ResY)? BuildOverlay()
    {
        if (!Enabled || _osdW <= 0 || _osdH <= 0) return null;
        var r = ToScreen(_rect);
        double vl = _vx, vt = _vy, vr = _vx + _vw, vb = _vy + _vh;
        var sb = new StringBuilder();

        // Darken everything outside the crop
        const string shade = @"{\an7\pos(0,0)\bord0\shad0\1c&H000000&\1a&H50&\p1}";
        sb.Append(shade).Append(Box(vl, vt, vr, r.Top)).Append(' ')
          .Append(Box(vl, r.Bottom, vr, vb)).Append(' ')
          .Append(Box(vl, r.Top, r.Left, r.Bottom)).Append(' ')
          .Append(Box(r.Right, r.Top, vr, r.Bottom)).Append("\n");

        // Rule-of-thirds guides
        sb.Append(@"{\an7\pos(0,0)\bord0\shad0\1c&HFFFFFF&\1a&HB0&\p1}");
        for (int i = 1; i <= 2; i++)
        {
            double gx = r.Left + r.Width * i / 3, gy = r.Top + r.Height * i / 3;
            sb.Append(Box(gx - 0.5, r.Top, gx + 0.5, r.Bottom)).Append(' ');
            sb.Append(Box(r.Left, gy - 0.5, r.Right, gy + 0.5)).Append(' ');
        }
        sb.Append('\n');

        // Border
        const double b = 2;
        sb.Append(@"{\an7\pos(0,0)\bord0\shad0\1c&HFFFFFF&\1a&H00&\p1}")
          .Append(Box(r.Left - b, r.Top - b, r.Right + b, r.Top)).Append(' ')
          .Append(Box(r.Left - b, r.Bottom, r.Right + b, r.Bottom + b)).Append(' ')
          .Append(Box(r.Left - b, r.Top, r.Left, r.Bottom)).Append(' ')
          .Append(Box(r.Right, r.Top, r.Right + b, r.Bottom)).Append("\n");

        // Handles
        const double hs = 5;
        sb.Append(@"{\an7\pos(0,0)\bord1\shad0\1c&HFFFFFF&\3c&H000000&\1a&H00&\p1}");
        double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        foreach (var (px, py) in new[] { (r.Left, r.Top), (cx, r.Top), (r.Right, r.Top), (r.Right, cy), (r.Right, r.Bottom), (cx, r.Bottom), (r.Left, r.Bottom), (r.Left, cy) })
            sb.Append(Box(px - hs, py - hs, px + hs, py + hs)).Append(' ');
        sb.Append('\n');

        // Size label
        var er = ExportRect();
        string label = er is { } e ? $"{e.W} × {e.H}" : $"{(int)SourceWidth} × {(int)SourceHeight}";
        double ly = r.Top - 8 > vt + 20 ? r.Top - 8 : r.Top + 26;
        sb.Append(FormattableString.Invariant($@"{{\an1\pos({r.Left + 2:0},{ly:0})\fnSegoe UI\fs18\bord2\shad0\1c&HFFFFFF&\3c&H000000&}}{label}"));

        return (sb.ToString(), (int)Math.Round(_osdW), (int)Math.Round(_osdH));
    }

    private static string Box(double x0, double y0, double x1, double y1)
    {
        if (x1 <= x0 || y1 <= y0) return "";
        return string.Create(CultureInfo.InvariantCulture, $"m {x0:0.#} {y0:0.#} l {x1:0.#} {y0:0.#} {x1:0.#} {y1:0.#} {x0:0.#} {y1:0.#}");
    }
}
