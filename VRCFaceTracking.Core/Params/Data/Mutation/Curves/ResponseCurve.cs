using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace VRCFaceTracking.Core.Params.Data.Mutation.Curves;

/// <summary>
/// A single draggable point on a response curve. X is the incoming tracking value,
/// Y is what it gets remapped to. Both are normalized 0..1.
/// </summary>
public class CurveControlPoint
{
    public float X;
    public float Y;

    public CurveControlPoint() { }

    public CurveControlPoint(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// An editable input/output response curve for one tracking channel. Defaults to a straight
/// 1:1 line, so a freshly created curve changes nothing. Interpolation is monotone cubic
/// (Fritsch-Carlson), which stays smooth between points but never overshoots them - so the
/// output can't wiggle outside the range the user actually drew.
/// </summary>
/// <remarks>
/// Thread safety: the UI edits <see cref="Points"/> while the tracking thread calls
/// <see cref="Evaluate"/> ~100 times a second. Evaluate therefore never reads the point list
/// directly - it reads immutable snapshot arrays that are rebuilt (under lock) only after an
/// edit marks the curve dirty. That keeps the hot path allocation- and lock-free while making
/// structural edits safe.
/// </remarks>
public class ResponseCurve
{
    /// <summary> Whether this channel's curve is applied at all. </summary>
    public bool Enabled;

    /// <summary> Control points, kept sorted by X. Always contains at least the two endpoints. </summary>
    public List<CurveControlPoint> Points = new();

    [JsonIgnore] private readonly object _sync = new();
    [JsonIgnore] private volatile bool _dirty = true;

    // Immutable snapshots consumed by Evaluate
    [JsonIgnore] private float[] _sx = Array.Empty<float>();
    [JsonIgnore] private float[] _sy = Array.Empty<float>();
    [JsonIgnore] private float[] _tangents = Array.Empty<float>();

    public static ResponseCurve Identity() => new()
    {
        Points = new List<CurveControlPoint> { new(0f, 0f), new(1f, 1f) }
    };

    /// <summary>
    /// Marks the curve as changed so the next evaluation picks up new point positions.
    /// Call after moving, adding or removing points.
    /// </summary>
    public void Invalidate() => _dirty = true;

    /// <summary>
    /// Repairs a curve loaded from disk (or newly created): guarantees at least two points,
    /// sorted, in range, with endpoints pinned to X=0 and X=1.
    /// </summary>
    public void EnsureValid()
    {
        lock (_sync)
        {
            Points ??= new List<CurveControlPoint>();
            Points.RemoveAll(p => p == null || float.IsNaN(p.X) || float.IsNaN(p.Y));

            foreach (var p in Points)
            {
                p.X = Math.Clamp(p.X, 0f, 1f);
                p.Y = Math.Clamp(p.Y, 0f, 1f);
            }

            if (Points.Count < 2)
            {
                Points = new List<CurveControlPoint> { new(0f, 0f), new(1f, 1f) };
            }
            else
            {
                Points.Sort((a, b) => a.X.CompareTo(b.X));
                Points[0].X = 0f;
                Points[^1].X = 1f;
            }
        }

        _dirty = true;
    }

    /// <summary>
    /// Adds a point at the given position and returns it. Points are kept sorted.
    /// </summary>
    public CurveControlPoint AddPoint(float x, float y)
    {
        var point = new CurveControlPoint(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f));

        lock (_sync)
        {
            Points.Add(point);
            Points.Sort((a, b) => a.X.CompareTo(b.X));
        }

        _dirty = true;
        return point;
    }

    /// <summary>
    /// Removes a point. Endpoints can't be removed, so a curve always spans the full range.
    /// </summary>
    public bool RemovePoint(CurveControlPoint point)
    {
        lock (_sync)
        {
            var index = Points.IndexOf(point);
            if (index <= 0 || index >= Points.Count - 1)
            {
                return false;
            }

            Points.RemoveAt(index);
        }

        _dirty = true;
        return true;
    }

    public void Reset()
    {
        lock (_sync)
        {
            Points = new List<CurveControlPoint> { new(0f, 0f), new(1f, 1f) };
        }

        _dirty = true;
    }

    /// <summary>
    /// True when the curve is still an unmodified straight 1:1 line.
    /// </summary>
    [JsonIgnore]
    public bool IsIdentity
    {
        get
        {
            lock (_sync)
            {
                return Points.Count == 2 &&
                       Math.Abs(Points[0].Y) < 0.0005f &&
                       Math.Abs(Points[1].Y - 1f) < 0.0005f;
            }
        }
    }

    /// <summary>
    /// Maps an incoming normalized value through the curve. Safe to call from the tracking
    /// thread while the UI is editing.
    /// </summary>
    public float Evaluate(float x)
    {
        if (float.IsNaN(x) || float.IsInfinity(x))
        {
            return x;
        }

        if (_dirty)
        {
            RebuildSnapshot();
        }

        // Take local references so a concurrent rebuild can't swap arrays mid-evaluation
        var sx = _sx;
        var sy = _sy;
        var tangents = _tangents;

        if (sx.Length == 0 || sx.Length != sy.Length || sx.Length != tangents.Length)
        {
            return x;
        }
        if (sx.Length == 1)
        {
            return Math.Clamp(sy[0], 0f, 1f);
        }

        x = Math.Clamp(x, 0f, 1f);

        if (x <= sx[0])
        {
            return sy[0];
        }
        if (x >= sx[^1])
        {
            return sy[^1];
        }

        // Find the segment containing x
        var i = 0;
        for (var k = 0; k < sx.Length - 1; k++)
        {
            if (x >= sx[k] && x <= sx[k + 1])
            {
                i = k;
                break;
            }
        }

        var x0 = sx[i];
        var x1 = sx[i + 1];
        var h = x1 - x0;
        if (h <= 0f)
        {
            return sy[i + 1];
        }

        var y0 = sy[i];
        var y1 = sy[i + 1];
        var t = (x - x0) / h;
        var t2 = t * t;
        var t3 = t2 * t;

        // Cubic Hermite basis
        var h00 = 2f * t3 - 3f * t2 + 1f;
        var h10 = t3 - 2f * t2 + t;
        var h01 = -2f * t3 + 3f * t2;
        var h11 = t3 - t2;

        var y = h00 * y0 + h10 * h * tangents[i] + h01 * y1 + h11 * h * tangents[i + 1];
        return Math.Clamp(y, 0f, 1f);
    }

    /// <summary>
    /// Copies the point list into flat arrays and computes Fritsch-Carlson monotone tangents:
    /// keeps the curve smooth while preventing it from overshooting the user's points.
    /// </summary>
    private void RebuildSnapshot()
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return; // another thread already rebuilt it
            }

            var points = Points;
            var n = points?.Count ?? 0;
            if (n == 0)
            {
                _sx = Array.Empty<float>();
                _sy = Array.Empty<float>();
                _tangents = Array.Empty<float>();
                _dirty = false;
                return;
            }

            var sx = new float[n];
            var sy = new float[n];
            for (var i = 0; i < n; i++)
            {
                sx[i] = points![i].X;
                sy[i] = points[i].Y;
            }

            var tangents = new float[n];
            if (n > 1)
            {
                var secants = new float[n - 1];
                for (var i = 0; i < n - 1; i++)
                {
                    var dx = sx[i + 1] - sx[i];
                    secants[i] = dx > 0f ? (sy[i + 1] - sy[i]) / dx : 0f;
                }

                tangents[0] = secants[0];
                tangents[n - 1] = secants[n - 2];

                for (var i = 1; i < n - 1; i++)
                {
                    tangents[i] = secants[i - 1] * secants[i] <= 0f
                        ? 0f // local extremum - flatten to stay monotone
                        : (secants[i - 1] + secants[i]) / 2f;
                }

                // Limit tangents so no segment overshoots
                for (var i = 0; i < n - 1; i++)
                {
                    if (secants[i] == 0f)
                    {
                        tangents[i] = 0f;
                        tangents[i + 1] = 0f;
                        continue;
                    }

                    var a = tangents[i] / secants[i];
                    var b = tangents[i + 1] / secants[i];
                    var s = a * a + b * b;
                    if (s > 9f)
                    {
                        var scale = 3f / MathF.Sqrt(s);
                        tangents[i] = scale * a * secants[i];
                        tangents[i + 1] = scale * b * secants[i];
                    }
                }
            }

            _sx = sx;
            _sy = sy;
            _tangents = tangents;
            _dirty = false;
        }
    }

    public ResponseCurve Clone()
    {
        lock (_sync)
        {
            return new ResponseCurve
            {
                Enabled = Enabled,
                Points = Points.Select(p => new CurveControlPoint(p.X, p.Y)).ToList()
            };
        }
    }
}
