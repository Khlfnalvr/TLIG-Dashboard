using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace TLIGDashboard.Views;

/// <summary>
/// Course-learning analytics dashboard (client experience). The completion gauge
/// and the small progress rings are vector arcs drawn in code so the fill ratio
/// is data-driven rather than hand-authored path data. Values shown are
/// placeholder samples that mirror the design reference.
/// </summary>
public sealed partial class LearningAnalyticPage : Page
{
    private LocalizationManager Lang => App.Lang;

    public LearningAnalyticPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Semicircular completion gauge (0..1 of the 180° sweep).
        DrawGauge(0.80);

        // Small progress rings — fraction of a full circle, starting at 12 o'clock.
        DrawRing(RingEnroll,    0.85);
        DrawRing(RingCompleted, 0.18);
        DrawRing(RingHours,     0.70);
        DrawRing(RingLive,      0.70);
        DrawRing(RingQuiz,      0.667);
        DrawRing(RingAssign,    0.667);
    }

    // ── Gauge: top half-circle, fills from the left end ───────────────────
    private void DrawGauge(double fraction)
    {
        const double cx = 82, cy = 82, r = 66;
        GaugeTrack.Data = ArcGeometry(cx, cy, r, 180, 0, SweepDirection.Clockwise, isLargeArc: false);
        double end = 180 - Clamp01(fraction) * 180;
        GaugeValue.Data = ArcGeometry(cx, cy, r, 180, end, SweepDirection.Clockwise, isLargeArc: false);
    }

    // ── Ring: full-circle track + clockwise value arc from the top ────────
    private static void DrawRing(Path target, double fraction)
    {
        const double cx = 27, cy = 27, r = 21;
        double sweep = Clamp01(fraction) * 360.0;
        if (sweep >= 359.9) sweep = 359.9; // keep the arc non-degenerate
        double end = 90 - sweep;
        target.Data = ArcGeometry(cx, cy, r, 90, end, SweepDirection.Clockwise, isLargeArc: sweep > 180);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static Geometry ArcGeometry(double cx, double cy, double r,
                                        double startDeg, double endDeg,
                                        SweepDirection dir, bool isLargeArc)
    {
        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(cx, cy, r, startDeg),
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(cx, cy, r, endDeg),
            Size = new Size(r, r),
            SweepDirection = dir,
            IsLargeArc = isLargeArc,
            RotationAngle = 0
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // Angle in degrees: 0° = 3 o'clock, 90° = 12 o'clock (screen y is flipped).
    private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
    }
}
