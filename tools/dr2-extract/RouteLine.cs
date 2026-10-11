using System.Numerics;

namespace Dr2Extract;

/// <summary>
/// Route centreline parameterised by the progress-track distance (<c>gates[].distance</c>): position, heading and signed
/// curvature at a distance, linearly interpolated between gate crossings.
/// </summary>
internal sealed class RouteLine
{
    private readonly float[] _s;
    private readonly Vector3[] _p;

    public RouteLine(ProgressTrack progress)
    {
        _s = progress.Gates.Select(g => g.Distance).ToArray();
        _p = progress.Gates.Select(g => g.Crossing).ToArray();
        for (var i = 1; i < _s.Length; i++)
        {
            if (_s[i] < _s[i - 1])
            {
                throw new InvalidDataException($"gate {i} distance {_s[i]} < gate {i - 1} distance {_s[i - 1]}");
            }
        }
    }

    public float Min => _s[0];

    public float Max => _s[^1];

    public Vector3 At(float s)
    {
        if (s <= _s[0])
        {
            return _p[0];
        }

        if (s >= _s[^1])
        {
            return _p[^1];
        }

        var i = Array.BinarySearch(_s, s);
        if (i >= 0)
        {
            return _p[i];
        }

        i = ~i;
        var t = (s - _s[i - 1]) / Math.Max(_s[i] - _s[i - 1], 1e-6f);
        return Vector3.Lerp(_p[i - 1], _p[i], t);
    }

    /// <summary>Horizontal unit heading at <paramref name="s"/> (central difference over ±<paramref name="h"/> metres).</summary>
    public Vector3 Heading(float s, float h = 5)
    {
        var d = At(s + h) - At(s - h);
        d.Y = 0;
        return d.LengthSquared() > 0 ? Vector3.Normalize(d) : Vector3.UnitX;
    }

    /// <summary>
    /// Signed horizontal curvature (1/m) at <paramref name="s"/>; positive = turning towards cross(f, +Y), i.e. to the
    /// driver's right if the frame is right-handed.
    /// </summary>
    public float Curvature(float s, float h = 8)
    {
        var a = Heading(s - h, h);
        var b = Heading(s + h, h);
        var turn = -Vector3.Cross(a, b).Y;
        return MathF.Atan2(turn, Vector3.Dot(a, b)) / (2 * h);
    }

    /// <summary>Distance of maximum |curvature| in [from, to] and the curvature there.</summary>
    public (float S, float K) PeakCurvature(float from, float to, float step = 2)
    {
        var best = (S: from, K: 0f);
        for (var s = from; s <= to; s += step)
        {
            var k = Curvature(s);
            if (MathF.Abs(k) > MathF.Abs(best.K))
            {
                best = (s, k);
            }
        }

        return best;
    }
}
