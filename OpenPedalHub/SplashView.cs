using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenPedalHub;

/// <summary>
/// Startup animation: ribbons of green light spiral in, a silver orb forms, three ring segments
/// light up (one per pedal, in each pedal's colour), then a pulse of light hands over to the app.
/// </summary>
public class SplashView : FrameworkElement
{
    /// <summary>Stretches the whole animation: 1 = 3 seconds. Set before Start, e.g. to fit a startup sound.</summary>
    public double Scale { get; set; } = 1;
    public double Duration => 3.0 * Scale;   // seconds

    readonly Stopwatch _clock = new();
    bool _running;

    public event Action? Finished;

    public void Start()
    {
        _running = true;
        _clock.Restart();
        CompositionTarget.Rendering += Tick;
    }

    /// <summary>Jump to the end (used when the user clicks to skip).</summary>
    public void Finish()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= Tick;
        Finished?.Invoke();
    }

    void Tick(object? sender, EventArgs e)
    {
        if (_clock.Elapsed.TotalSeconds >= Duration) Finish();
        else InvalidateVisual();
    }

    static readonly Color Green = Color.FromRgb(0x74, 0xC2, 0x2A), GreenDeep = Color.FromRgb(0x3F, 0x8F, 0x0E);
    static readonly Color[] PedalColors = [Color.FromRgb(0x4F, 0xAE, 0xF5), Color.FromRgb(0xF4, 0x63, 0x4F), Color.FromRgb(0x8A, 0xD0, 0x3A)];
    static readonly Typeface Light = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

    static double Clamp01(double v) => Math.Clamp(v, 0, 1);
    static double Smooth(double p) => p * p * (3 - 2 * p);
    static double OutBack(double p) { const double c = 1.70158; double q = p - 1; return 1 + (c + 1) * q * q * q + c * q * q; }
    static Color Alpha(Color c, double a) => Color.FromArgb((byte)(Clamp01(a) * 255), c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Geometry Arc(Point c, double radius, double startDeg, double sweepDeg)
    {
        Point At(double deg) => new(c.X + radius * Math.Cos(deg * Math.PI / 180), c.Y + radius * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            // Two halves so sweeps over 180 degrees stay unambiguous.
            ctx.ArcTo(At(startDeg + sweepDeg / 2), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
        }
        return g;
    }

    static Pen Round(Color color, double width) =>
        new(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        double t = _clock.Elapsed.TotalSeconds / Scale;   // all timings below are on the 3-second scale
        var c = new Point(w / 2, h / 2 - 30);

        // Backdrop: the app's silver, with a soft light behind the orb.
        bool dark = Theme.IsDark;
        dc.DrawRectangle(Theme.Brush("WindowBg"), null, new Rect(0, 0, w, h));
        double bloom = Clamp01(t / 1.2);
        dc.DrawEllipse(new RadialGradientBrush(Alpha(Colors.White, (dark ? 0.10 : 0.95) * bloom), Alpha(Colors.White, 0)), null, c, 520, 420);
        dc.DrawEllipse(new RadialGradientBrush(Alpha(Green, 0.22 * bloom), Alpha(Green, 0)), null, c, 380, 300);

        // 1. Ribbons of light spiral inward.
        for (int i = 0; i < 6; i++)
        {
            double p = (t - i * 0.07) / 1.15;
            if (p <= 0 || p >= 1) continue;
            double e = Smooth(p), fade = Math.Sin(Math.PI * p);
            double radius = 560 + (58 - 560) * e;
            double start = i * 60 + e * 520;
            double sweep = 30 + 150 * fade;
            double width = 22 + (3 - 22) * e;
            var col = i % 2 == 0 ? Green : GreenDeep;
            dc.DrawGeometry(null, Round(Alpha(col, 0.16 * fade), width * 2.4), Arc(c, radius, start, sweep));
            dc.DrawGeometry(null, Round(Alpha(col, 0.85 * fade), width), Arc(c, radius, start, sweep));
            dc.DrawGeometry(null, Round(Alpha(Colors.White, 0.7 * fade), width * 0.25), Arc(c, radius, start + sweep * 0.5, sweep * 0.45));
        }

        // 2. The orb forms.
        double grow = Clamp01((t - 0.75) / 0.55);
        if (grow > 0)
        {
            double R = 68 * OutBack(grow);
            dc.DrawEllipse(new RadialGradientBrush(Alpha(Colors.Black, 0.28 * grow), Alpha(Colors.Black, 0)), null,
                new Point(c.X, c.Y + R * 1.25), R * 1.3, R * 0.28);
            var body = new RadialGradientBrush { GradientOrigin = new Point(0.36, 0.28), Center = new Point(0.5, 0.5), RadiusX = 0.62, RadiusY = 0.62 };
            body.GradientStops.Add(new GradientStop(Colors.White, 0));
            body.GradientStops.Add(new GradientStop(Color.FromRgb(0xDD, 0xE2, 0xE6), 0.55));
            body.GradientStops.Add(new GradientStop(Color.FromRgb(0x8F, 0x99, 0xA1), 1));
            dc.DrawEllipse(body, new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0x86, 0x8E)), 1.2), c, R, R);

            // Groove the ring of light sits in.
            double ringR = R * 0.60, ringW = R * 0.17;
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(Color.FromRgb(0x4A, 0x52, 0x59), 0.55)), ringW), c, ringR, ringR);

            // 3. Three segments light up, one per pedal, then settle to green.
            double toGreen = Clamp01((t - 2.05) / 0.3);
            for (int k = 0; k < 3; k++)
            {
                double on = Clamp01((t - (1.3 + k * 0.22)) / 0.18);
                if (on <= 0) continue;
                var col = Mix(PedalColors[k], Green, toGreen);
                var seg = Arc(c, ringR, -90 + k * 120 + 9, 102);
                dc.DrawGeometry(null, Round(Alpha(col, 0.28 * on), ringW * 1.7), seg);
                dc.DrawGeometry(null, Round(Alpha(col, on), ringW * 0.9), seg);
                dc.DrawGeometry(null, Round(Alpha(Colors.White, 0.55 * on), ringW * 0.25), seg);
            }

            // Gloss across the top of the sphere.
            dc.PushClip(new EllipseGeometry(c, R, R));
            dc.DrawEllipse(new LinearGradientBrush(Alpha(Colors.White, 0.75), Alpha(Colors.White, 0.05), 90), null,
                new Point(c.X, c.Y - R * 0.62), R * 0.92, R * 0.52);
            dc.Pop();

            // 4. Pulse of light.
            double pulse = Clamp01((t - 2.05) / 0.65);
            if (pulse > 0 && pulse < 1)
            {
                double pr = R * (1 + Smooth(pulse) * 7);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(Green, 0.7 * (1 - pulse))), 2 + 10 * (1 - pulse)), c, pr, pr);
                dc.DrawEllipse(new RadialGradientBrush(Alpha(Colors.White, 0.55 * (1 - pulse)), Alpha(Colors.White, 0)), null, c, pr, pr);
            }
        }

        // Title.
        double show = Clamp01((t - 1.55) / 0.5);
        if (show > 0)
        {
            var title = new FormattedText("OpenPedalHub", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Light, 38,
                new SolidColorBrush(Alpha(dark ? Color.FromRgb(0xEE, 0xF1, 0xF3) : Color.FromRgb(0x26, 0x2B, 0x2F), show)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double ty = c.Y + 120 + (1 - Smooth(show)) * 10;
            dc.DrawText(title, new Point(c.X - title.Width / 2, ty));
            double line = 150 * Smooth(show);
            dc.DrawLine(new Pen(new LinearGradientBrush(Alpha(Green, 0), Alpha(Green, show), 0), 2),
                new Point(c.X - line, ty + 58), new Point(c.X, ty + 58));
            dc.DrawLine(new Pen(new LinearGradientBrush(Alpha(Green, show), Alpha(Green, 0), 0), 2),
                new Point(c.X, ty + 58), new Point(c.X + line, ty + 58));
        }
    }
}
