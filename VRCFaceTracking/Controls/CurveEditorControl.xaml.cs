using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VRCFaceTracking.Core.Params.Data.Mutation.Curves;
using Windows.Foundation;
using Windows.UI;

namespace VRCFaceTracking.Controls;

/// <summary>
/// Interactive response-curve editor. Click empty space to add a point, drag points to reshape,
/// right-click a point to delete it. A live indicator shows where the current tracking value
/// sits on the curve so it can be tuned while the tracker is running.
/// </summary>
public sealed partial class CurveEditorControl : UserControl
{
    private const double HitRadius = 14;
    private const double PointRadius = 5.5;
    private const int CurveSamples = 72;

    // Neon518 palette, matching VRC Parameter Relay's default theme
    private static readonly Color CurveColor = Color.FromArgb(255, 0x31, 0xf2, 0x72);      // accent #31f272
    private static readonly Color PointColor = Color.FromArgb(255, 0x1c, 0xf2, 0x6b);      // switch-on #1cf26b
    private static readonly Color PointStrokeColor = Color.FromArgb(255, 0x04, 0x15, 0x0b); // on-accent #04150b
    private static readonly Color GridColor = Color.FromArgb(60, 0x36, 0xa3, 0x5c);        // edge #36a35c, faded
    private static readonly Color IdentityColor = Color.FromArgb(130, 0x5f, 0x6f, 0x63);   // faint #5f6f63
    private static readonly Color LiveColor = Color.FromArgb(255, 0xff, 0xb4, 0x54);       // amber #ffb454

    private static readonly InputSystemCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private static readonly InputSystemCursor CrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

    private ResponseCurve? _curve;
    private bool _bipolar;
    private CurveControlPoint? _dragging;

    private Line? _liveLine;
    private Ellipse? _liveDot;

    /// <summary> Raised whenever the user changes the curve, so the host can persist it. </summary>
    public event EventHandler? CurveEdited;

    public CurveEditorControl()
    {
        InitializeComponent();
    }

    private double W => PlotCanvas.ActualWidth;
    private double H => PlotCanvas.ActualHeight;
    private bool HasSize => W > 4 && H > 4;

    /// <summary>
    /// Points this editor at a curve. Safe to call repeatedly.
    /// </summary>
    public void Attach(ResponseCurve curve, bool bipolar)
    {
        _curve = curve;
        _bipolar = bipolar;
        _dragging = null;
        Redraw();
    }

    /// <summary>
    /// Moves the live indicator. Cheap enough to call at UI frame rate - it only repositions
    /// two existing shapes instead of redrawing the whole plot.
    /// </summary>
    public void SetLiveValue(float rawValue)
    {
        if (_curve == null || !HasSize || _liveLine == null || _liveDot == null)
        {
            return;
        }

        var input = _bipolar ? (rawValue + 1f) / 2f : rawValue;
        input = Math.Clamp(float.IsNaN(input) ? 0f : input, 0f, 1f);
        var output = _curve.Enabled ? _curve.Evaluate(input) : input;

        var x = input * W;
        _liveLine.X1 = x;
        _liveLine.X2 = x;
        _liveLine.Y1 = 0;
        _liveLine.Y2 = H;

        Canvas.SetLeft(_liveDot, x - PointRadius);
        Canvas.SetTop(_liveDot, (1 - output) * H - PointRadius);
    }

    private Point ToScreen(double curveX, double curveY) => new(curveX * W, (1 - curveY) * H);

    private (float X, float Y) ToCurveSpace(Point screen) =>
        ((float)Math.Clamp(screen.X / W, 0, 1), (float)Math.Clamp(1 - screen.Y / H, 0, 1));

    private void Redraw()
    {
        PlotCanvas.Children.Clear();
        _liveLine = null;
        _liveDot = null;

        if (_curve == null || !HasSize)
        {
            return;
        }

        var gridBrush = new SolidColorBrush(GridColor);
        for (var i = 1; i < 4; i++)
        {
            var f = i / 4.0;
            PlotCanvas.Children.Add(new Line
            {
                X1 = f * W, X2 = f * W, Y1 = 0, Y2 = H,
                Stroke = gridBrush, StrokeThickness = 1
            });
            PlotCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = W, Y1 = f * H, Y2 = f * H,
                Stroke = gridBrush, StrokeThickness = 1
            });
        }

        // Dashed 1:1 reference so it's obvious how far the curve has been bent
        PlotCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = H, X2 = W, Y2 = 0,
            Stroke = new SolidColorBrush(IdentityColor),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 }
        });

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(CurveColor),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round
        };
        for (var i = 0; i <= CurveSamples; i++)
        {
            var x = i / (double)CurveSamples;
            polyline.Points.Add(ToScreen(x, _curve.Evaluate((float)x)));
        }
        PlotCanvas.Children.Add(polyline);

        var pointFill = new SolidColorBrush(PointColor);
        var pointStroke = new SolidColorBrush(PointStrokeColor);
        foreach (var point in _curve.Points)
        {
            var screen = ToScreen(point.X, point.Y);
            var dot = new Ellipse
            {
                Width = PointRadius * 2,
                Height = PointRadius * 2,
                Fill = pointFill,
                Stroke = pointStroke,
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, screen.X - PointRadius);
            Canvas.SetTop(dot, screen.Y - PointRadius);
            PlotCanvas.Children.Add(dot);
        }

        var liveBrush = new SolidColorBrush(LiveColor);
        _liveLine = new Line
        {
            Stroke = liveBrush,
            StrokeThickness = 1,
            Opacity = 0.55,
            IsHitTestVisible = false
        };
        PlotCanvas.Children.Add(_liveLine);

        _liveDot = new Ellipse
        {
            Width = PointRadius * 2,
            Height = PointRadius * 2,
            Fill = liveBrush,
            IsHitTestVisible = false
        };
        PlotCanvas.Children.Add(_liveDot);
    }

    private CurveControlPoint? HitTest(Point position)
    {
        if (_curve == null)
        {
            return null;
        }

        CurveControlPoint? best = null;
        var bestDistance = double.MaxValue;

        foreach (var point in _curve.Points)
        {
            var screen = ToScreen(point.X, point.Y);
            var dx = screen.X - position.X;
            var dy = screen.Y - position.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= HitRadius && distance < bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void PlotCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_curve == null || !HasSize)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(PlotCanvas);

        // Right button is handled by RightTapped (delete), don't start a drag with it
        if (pointerPoint.Properties.IsRightButtonPressed)
        {
            return;
        }

        var position = pointerPoint.Position;
        var hit = HitTest(position);

        if (hit == null)
        {
            var (x, y) = ToCurveSpace(position);
            hit = _curve.AddPoint(x, y);
            Redraw();
            CurveEdited?.Invoke(this, EventArgs.Empty);
        }

        _dragging = hit;
        PlotCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PlotCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_curve == null || !HasSize)
        {
            return;
        }

        var position = e.GetCurrentPoint(PlotCanvas).Position;

        if (_dragging == null)
        {
            // Hover feedback: a hand over draggable points, crosshair elsewhere
            ProtectedCursor = HitTest(position) != null ? HandCursor : CrossCursor;
            return;
        }

        var (x, y) = ToCurveSpace(position);
        var index = _curve.Points.IndexOf(_dragging);

        // Endpoints stay pinned horizontally so the curve always covers the full range;
        // inner points can't cross their neighbours.
        if (index == 0)
        {
            _dragging.X = 0f;
        }
        else if (index == _curve.Points.Count - 1)
        {
            _dragging.X = 1f;
        }
        else
        {
            var lower = _curve.Points[index - 1].X + 0.01f;
            var upper = _curve.Points[index + 1].X - 0.01f;
            _dragging.X = upper <= lower ? _dragging.X : Math.Clamp(x, lower, upper);
        }

        _dragging.Y = y;
        _curve.Invalidate();
        Redraw();
        e.Handled = true;
    }

    private void PlotCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == null)
        {
            return;
        }

        _dragging = null;
        PlotCanvas.ReleasePointerCaptures();
        CurveEdited?.Invoke(this, EventArgs.Empty);
    }

    private void PlotCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_curve == null || !HasSize)
        {
            return;
        }

        var hit = HitTest(e.GetPosition(PlotCanvas));
        if (hit != null && _curve.RemovePoint(hit))
        {
            _dragging = null;
            Redraw();
            CurveEdited?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }
}
