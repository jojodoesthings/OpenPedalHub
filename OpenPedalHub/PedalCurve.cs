using System.Windows;

namespace OpenPedalHub;

/// <summary>The response-curve maths. The on-screen graph and the output to the game share this.</summary>
public static class PedalCurve
{
    public const int MaxPoints = 8;

    /// <param name="x">Pedal travel, 0..1 (0 = at rest).</param>
    /// <param name="lowPct">Dead zone at rest, percent of travel.</param>
    /// <param name="highPct">Dead zone at full press, percent of travel.</param>
    /// <param name="gamma">1 = linear, above 1 = gentler start, below 1 = sharper start. Ignored when points are given.</param>
    /// <param name="points">Optional custom curve: points between (0,0) and (1,1), left to right, in the space between the dead zones.</param>
    /// <returns>Value sent to the game, 0..1.</returns>
    public static double Shape(double x, double lowPct, double highPct, double gamma, IReadOnlyList<Point>? points = null)
    {
        double lo = lowPct / 100, hi = highPct / 100;
        double t = Math.Clamp((x - lo) / Math.Max(0.01, 1 - lo - hi), 0, 1);
        return points is { Count: > 0 } ? Custom(points, t) : Math.Pow(t, gamma);
    }

    /// <summary>
    /// A smooth curve through the points that never doubles back (monotone cubic interpolation),
    /// so more pedal can never mean less output.
    /// </summary>
    static double Custom(IReadOnlyList<Point> points, double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        int count = Math.Min(points.Count, MaxPoints), n = count + 2;
        Span<double> xs = stackalloc double[n], ys = stackalloc double[n], slope = stackalloc double[n], tangent = stackalloc double[n];
        xs[0] = 0; ys[0] = 0;
        for (int i = 0; i < count; i++) { xs[i + 1] = points[i].X; ys[i + 1] = points[i].Y; }
        xs[n - 1] = 1; ys[n - 1] = 1;

        for (int k = 0; k < n - 1; k++) slope[k] = (ys[k + 1] - ys[k]) / Math.Max(xs[k + 1] - xs[k], 1e-6);
        tangent[0] = slope[0];
        tangent[n - 1] = slope[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            if (slope[k - 1] * slope[k] <= 0) { tangent[k] = 0; continue; }
            double h0 = xs[k] - xs[k - 1], h1 = xs[k + 1] - xs[k];
            double w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
            tangent[k] = (w1 + w2) / (w1 / slope[k - 1] + w2 / slope[k]);
        }

        int seg = 0;
        while (seg < n - 2 && t > xs[seg + 1]) seg++;
        double h = Math.Max(xs[seg + 1] - xs[seg], 1e-6), s = (t - xs[seg]) / h, s2 = s * s, s3 = s2 * s;
        double y = (2 * s3 - 3 * s2 + 1) * ys[seg] + (s3 - 2 * s2 + s) * h * tangent[seg]
                 + (-2 * s3 + 3 * s2) * ys[seg + 1] + (s3 - s2) * h * tangent[seg + 1];
        return Math.Clamp(y, 0, 1);
    }
}
