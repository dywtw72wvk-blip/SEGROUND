// ============================================================================
// Hub Core: formation / station patterns in the hub frame
// (+X right, +Y up, +Z back). Offsets are relative to the keep-out centre.
// R = keep-out radius, s = spacing.
// ============================================================================
static class Formation
{
    public static readonly string[] Names = { "WEDGE", "LINE", "COLUMN", "RING", "SPHERE" };

    public static bool Valid(string f) { return Array.IndexOf(Names, f) >= 0; }

    public static string Next(string f)
    {
        int i = Array.IndexOf(Names, f);
        return Names[(i + 1) % Names.Length];
    }

    public static Vector3D Offset(string f, int i, int n, double s, double R)
    {
        int r = i / 2 + 1;
        double side = (i % 2 == 0) ? -1 : 1;
        switch (f)
        {
            case "LINE":      // abreast, alternating left/right outside the hull
                return new Vector3D(side * (R + s * (i / 2 + 0.5)), 0, 0);
            case "COLUMN":    // trailing behind
                return new Vector3D(0, 0, R + s * (i + 1));
            case "RING":      // horizontal ring, first slot dead ahead
                {
                    double a = 2 * Math.PI * i / Math.Max(1, n), rr = R + s;
                    return new Vector3D(rr * Math.Sin(a), 0, -rr * Math.Cos(a));
                }
            case "SPHERE":    // Fibonacci sphere: even all-round screen
                {
                    double rr = R + s, k = (i + 0.5) / Math.Max(1, n);
                    double y = 1 - 2 * k, rad = Math.Sqrt(Math.Max(0, 1 - y * y)), phi = i * 2.399963229728653;
                    return new Vector3D(Math.Cos(phi) * rad * rr, y * rr, Math.Sin(phi) * rad * rr);
                }
            default:          // WEDGE: V trailing from the stern
                return new Vector3D(side * r * s, 0, R + r * s * 0.8);
        }
    }

    // Facing for base stations: look outward (horizontal)
    public static Vector3D Outward(Vector3D off)
    {
        var h = new Vector3D(off.X, 0, off.Z);
        return h.LengthSquared() > 1e-6 ? Util.Norm(h) : new Vector3D(0, 0, -1);
    }
}
