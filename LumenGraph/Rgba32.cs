using System.Runtime.InteropServices;

namespace LumenGraph;

/// <summary>
/// Lightweight 32-bit RGBA color. Blittable, no GC pressure.
/// Stored in parallel arrays alongside graph node/edge data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Rgba32(byte r, byte g, byte b, byte a = 255)
{
    public readonly byte R = r;
    public readonly byte G = g;
    public readonly byte B = b;
    public readonly byte A = a;

    /// <summary>Lerp between two colors by t in [0,1].</summary>
    public static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Rgba32(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    /// <summary>Lerp through three colors: a→b→c where t in [0,1], midpoint at 0.5.</summary>
    public static Rgba32 Lerp3(Rgba32 a, Rgba32 mid, Rgba32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.5f)
            return Lerp(a, mid, t * 2f);
        return Lerp(mid, b, (t - 0.5f) * 2f);
    }
}
