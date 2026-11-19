using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenPedalHub;

/// <summary>
/// Response-curve graph with live position marker and IN / OUT level bars.
/// When the pedal uses a custom curve, its points can be dragged here.
/// </summary>
public class CurveView : FrameworkElement
{
    static DependencyProperty Reg<T>(string name, T def, bool twoWay = false) => DependencyProperty.Register(
        name, typeof(T), typeof(CurveView), new FrameworkPropertyMetadata(def,
            FrameworkPropertyMetadataOptions.AffectsRender | (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0)));

    public static readonly DependencyProperty LowProperty = Reg(nameof(Low), 0.0);
    public static readonly DependencyProperty HighProperty = Reg(nameof(High), 0.0);
    public static readonly DependencyProperty GammaProperty = Reg(nameof(Gamma), 1.0);
    public static readonly DependencyProperty InputProperty = Reg(nameof(Input), 0.0);
    public static readonly DependencyProperty AccentProperty = Reg(nameof(Accent), Colors.White);
    public static readonly DependencyProperty Accent2Property = Reg(nameof(Accent2), Colors.White);
    public static readonly DependencyProperty PointsProperty = Reg<IReadOnlyList<Point>?>(nameof(Points), null, twoWay: true);

    public double Low { get => (double)GetValue(LowProperty); set => SetValue(LowProperty, value); }
    public double High { get => (double)GetValue(HighProperty); set => SetValue(HighProperty, value); }
    public double Gamma { get => (double)GetValue(GammaProperty); set => SetValue(GammaProperty, value); }
    public double Input { get => (double)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Accent2 { get => (Color)GetValue(Accent2Property); set => SetValue(Accent2Property, value); }
    /// <summary>Custom curve points, or null when the curve comes from gamma.</summary>
    public IReadOnlyList<Point>? Points { get => (IReadOnlyList<Point>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    static readonly Typeface Face = new("Segoe UI");
    static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public CurveView()
    {
        Theme.Changed += InvalidateVisual;
    }

    // ---- Geometry shared by drawing and mouse handling ----

    const double BarsWidth = 62, AxisHeight = 22, Pad = 14, Radius = 14;

    Rect Plot => new(0.5, 0.5, Math.Max(10, ActualWidth - BarsWidth), Math.Max(10, ActualHeight - AxisHeight));
    Rect Inner { get { var p = Plot; return new Rect(p.Left + Pad, p.Top + Pad, Math.Max(1, p.Width - 2 * Pad), Math.Max(1, p.Height - 2 * Pad)); } }
    double Span => Math.Max(0.01, 1 - Low / 100 - High / 100);

    /// <summary>Where a curve point (in the space between the dead zones) sits on screen.</summary>
    Point ToScreen(Point p)
    {
        var inner = Inner;
        return new Point(inner.Left + (Low / 100 + p.X * Span) * inner.Width, inner.Bottom - p.Y * inner.Height);
    }

    Point FromScreen(Point s)
    {
        var inner = Inner;
        return new Point(((s.X - inner.Left) / inner.Width - Low / 100) / Span, (inner.Bottom - s.Y) / inner.Height);
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 120 || ActualHeight < 80) return;
        Rect plot = Plot, inner = Inner;
        double X(double v) => inner.Left + v * inner.Width;
        double Y(double v) => inner.Bottom - v * inner.Height;

        Color accent = Accent, accent2 = Accent2;
        var points = Points;
        double input = Math.Clamp(Input / 100, 0, 1);
        double output = PedalCurve.Shape(input, Low, High, Gamma, points);

        dc.DrawRoundedRectangle(Theme.Brush("PlotBg"), new Pen(Theme.Brush("PlotEdge"), 1), plot, Radius, Radius);
        dc.PushClip(new RectangleGeometry(plot, Radius, Radius));

        var gridPen = new Pen(Theme.Brush("PlotGrid"), 1);
        for (int i = 0; i <= 4; i++)
        {
            double gx = Math.Round(X(i / 4.0)) + 0.5, gy = Math.Round(Y(i / 4.0)) + 0.5;
            dc.DrawLine(gridPen, new Point(gx, plot.Top), new Point(gx, plot.Bottom));
            dc.DrawLine(gridPen, new Point(plot.Left, gy), new Point(plot.Right, gy));
        }

        // Dead zones: shaded bands where pedal movement changes nothing.
        var edgePen = new Pen(new SolidColorBrush(Alpha(accent2, 0xA0)), 1) { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) };
        double lo = Low / 100, hi = High / 100;
        var dead = Theme.Brush("PlotDead");
        if (lo > 0.0005)
        {
            dc.DrawRectangle(dead, null, new Rect(plot.Left, plot.Top, X(lo) - plot.Left, plot.Height));
            dc.DrawLine(edgePen, new Point(X(lo), plot.Top), new Point(X(lo), plot.Bottom));
        }
        if (hi > 0.0005)
        {
            dc.DrawRectangle(dead, null, new Rect(X(1 - hi), plot.Top, plot.Right - X(1 - hi), plot.Height));
            dc.DrawLine(edgePen, new Point(X(1 - hi), plot.Top), new Point(X(1 - hi), plot.Bottom));
        }

        dc.DrawLine(new Pen(Theme.Brush("PlotRef"), 1) { DashStyle = new DashStyle(new[] { 3.0, 4.0 }, 0) },
            new Point(X(0), Y(0)), new Point(X(1), Y(1)));

        // Curve: soft fill underneath, wide faint strokes for glow, then the bright line.
        const int steps = 96;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            ac.BeginFigure(new Point(X(0), Y(0)), true, true);
            for (int i = 0; i <= steps; i++)
            {
                double x = i / (double)steps;
                var p = new Point(X(x), Y(PedalCurve.Shape(x, Low, High, Gamma, points)));
                if (i == 0) lc.BeginFigure(p, false, false); else lc.LineTo(p, true, true);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(X(1), Y(0)), false, false);
        }
        dc.DrawGeometry(new LinearGradientBrush(Alpha(accent2, 0x50), Alpha(accent, 0x00), 90), null, area);

        // Absolute mapping so the gradient runs across the plot even when the curve is a flat line.
        Brush Stroke(double opacity) => new LinearGradientBrush(accent, accent2, new Point(inner.Left, 0), new Point(inner.Right, 0))
            { MappingMode = BrushMappingMode.Absolute, Opacity = opacity };
        Pen Round(Brush b, double width) => new(b, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, Round(Stroke(0.10), 16), line);
        dc.DrawGeometry(null, Round(Stroke(0.22), 8), line);
        dc.DrawGeometry(null, Round(Stroke(1), 3), line);

        // Live position: guides to both axes, then a glowing marker.
        var at = new Point(X(input), Y(output));
        Color here = Mix(accent, accent2, input);
        var guide = new Pen(new SolidColorBrush(Alpha(here, 0x80)), 1) { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) };
        dc.DrawLine(guide, at, new Point(at.X, plot.Bottom));
        dc.DrawLine(guide, at, new Point(plot.Right, at.Y));
        dc.DrawEllipse(new RadialGradientBrush(Alpha(here, 0x90), Alpha(here, 0x00)), null, at, 22, 22);
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(here), 3), at, 5.5, 5.5);

        // Draggable points of a custom curve.
        if (points is not null)
        {
            for (int i = 0; i < points.Count; i++)
            {
                var s = ToScreen(points[i]);
                bool hot = i == _drag || i == _hover;
                var col = Mix(accent, accent2, points[i].X);
                if (hot) dc.DrawEllipse(new SolidColorBrush(Alpha(col, 0x50)), null, s, 14, 14);
                dc.DrawRectangle(new SolidColorBrush(col), new Pen(Brushes.White, 2), new Rect(s.X - 6, s.Y - 6, 12, 12));
            }
        }
        dc.Pop();

        // Axis captions.
        double capY = plot.Bottom + 7;
        Text(dc, "0", plot.Left + Pad, capY, TextAlignment.Left);
        Text(dc, "PEDAL TRAVEL", plot.Left + plot.Width / 2, capY, TextAlignment.Center);
        Text(dc, "100", plot.Right - Pad, capY, TextAlignment.Right);

        // Level bars.
        const double barW = 14;
        double bx = plot.Right + 16;
        var barBrush = new LinearGradientBrush(accent, accent2, 90);
        Bar(dc, new Rect(bx, plot.Top, barW, plot.Height), input, barBrush, 0.45);
        Bar(dc, new Rect(bx + barW + 12, plot.Top, barW, plot.Height), output, barBrush, 1);
        Text(dc, "IN", bx + barW / 2, capY, TextAlignment.Center);
        Text(dc, "OUT", bx + barW + 12 + barW / 2, capY, TextAlignment.Center);
    }

    static void Bar(DrawingContext dc, Rect track, double value, Brush brush, double opacity)
    {
        double r = track.Width / 2;
        dc.DrawRoundedRectangle(Theme.Brush("Groove"), null, track, r, r);
        if (value <= 0.001) return;
        // Clip a full-height gradient so the colour at a given height stays put as the level moves.
        double fh = Math.Max(track.Width, value * track.Height);
        dc.PushClip(new RectangleGeometry(new Rect(track.Left, track.Bottom - fh, track.Width, fh), r, r));
        dc.PushOpacity(opacity);
        dc.DrawRectangle(brush, null, track);
        dc.Pop();
        dc.Pop();
    }

    void Text(DrawingContext dc, string s, double x, double y, TextAlignment align)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Theme.Brush("Muted"),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double ox = align == TextAlignment.Center ? ft.Width / 2 : align == TextAlignment.Right ? ft.Width : 0;
        dc.DrawText(ft, new Point(x - ox, y));
    }

    // ---- Editing a custom curve ----

    int _drag = -1, _hover = -1;
    const double Gap = 0.03;   // points keep at least this much apart, left to right

    int HandleAt(Point mouse)
    {
        if (Points is not { } pts) return -1;
        for (int i = 0; i < pts.Count; i++)
            if ((ToScreen(pts[i]) - mouse).Length <= 13) return i;
        return -1;
    }

    /// <summary>Keeps a point between its neighbours, so the curve always runs left to right and never downhill.</summary>
    static Point Constrain(Point[] pts, int i, Point want)
    {
        double minX = (i > 0 ? pts[i - 1].X : 0) + Gap, maxX = (i < pts.Length - 1 ? pts[i + 1].X : 1) - Gap;
        double minY = i > 0 ? pts[i - 1].Y : 0, maxY = i < pts.Length - 1 ? pts[i + 1].Y : 1;
        return new Point(Math.Clamp(want.X, minX, Math.Max(minX, maxX)), Math.Clamp(want.Y, minY, Math.Max(minY, maxY)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Points is not { } pts) return;
        var mouse = e.GetPosition(this);
        int hit = HandleAt(mouse);
        if (hit < 0 && e.ClickCount == 2 && pts.Count < PedalCurve.MaxPoints && Plot.Contains(mouse))
        {
            // Double-click on empty graph: add a point there.
            var want = FromScreen(mouse);
            int at = pts.Count(p => p.X < want.X);
            var list = pts.ToList();
            list.Insert(at, want);
            var next = list.ToArray();
            double lo = (at > 0 ? next[at - 1].X : 0) + Gap, hi = (at < next.Length - 1 ? next[at + 1].X : 1) - Gap;
            if (lo > hi) return;   // no room between its neighbours
            next[at] = Constrain(next, at, want);
            SetCurrentValue(PointsProperty, next);
            hit = at;
        }
        if (hit < 0) return;
        _drag = hit;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var mouse = e.GetPosition(this);
        if (_drag >= 0 && Points is { } pts && _drag < pts.Count)
        {
            var next = pts.ToArray();
            next[_drag] = Constrain(next, _drag, FromScreen(mouse));
            SetCurrentValue(PointsProperty, next);
            return;
        }
        int hover = HandleAt(mouse);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover >= 0 ? Cursors.SizeAll : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover < 0 || _drag >= 0) return;
        _hover = -1;
        Cursor = null;
        InvalidateVisual();
    }

    // Right-click a point to remove it (the last one stays, so the curve remains custom).
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (Points is not { Count: > 1 } pts) return;
        int hit = HandleAt(e.GetPosition(this));
        if (hit < 0) return;
        _hover = -1;
        SetCurrentValue(PointsProperty, pts.Where((_, i) => i != hit).ToArray());
        e.Handled = true;
    }
}
