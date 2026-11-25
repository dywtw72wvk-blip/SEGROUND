// ============================================================================
// Shared: utilities (culture-independent parsing, formatting, vector helpers)
// ============================================================================
static class Util
{
    // FNV-1a: stable across machines (string.GetHashCode is not guaranteed to be)
    public static int Hash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (int)h;
        }
    }

    public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

    public static Vector3D Norm(Vector3D v)
    {
        double l = v.Length();
        return l > 1e-9 ? v / l : Vector3D.Zero;
    }

    public static Vector3D Perp(Vector3D v)
    {
        Vector3D p = Vector3D.Cross(v, Vector3D.Up);
        if (p.LengthSquared() < 1e-6) p = Vector3D.Cross(v, Vector3D.Right);
        return Norm(p);
    }

    public static double Angle(Vector3D a, Vector3D b)
    {
        return Math.Acos(Clamp(Vector3D.Dot(Norm(a), Norm(b)), -1, 1));
    }

    // Parses "123", "-45.67". Always '.' as decimal separator, regardless of game locale.
    public static bool ParseD(string s, out double v)
    {
        v = 0;
        if (s == null) return false;
        s = s.Trim();
        if (s.Length == 0) return false;
        int i = 0; bool neg = false;
        if (s[0] == '-' || s[0] == '+') { neg = s[0] == '-'; i++; }
        double ip = 0, fp = 0, scale = 1; bool digits = false, dot = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
            {
                digits = true;
                if (dot) { scale *= 0.1; fp += (c - '0') * scale; }
                else ip = ip * 10 + (c - '0');
            }
            else if (c == '.' && !dot) dot = true;
            else return false;
        }
        if (!digits) return false;
        v = neg ? -(ip + fp) : ip + fp;
        return true;
    }

    // "GPS:Name:X:Y:Z:" (optionally followed by colour) anywhere inside s
    public static bool ParseGps(string s, out Vector3D p, out string name)
    {
        p = Vector3D.Zero; name = "";
        int i = s.IndexOf("GPS:");
        if (i < 0) return false;
        string[] parts = s.Substring(i).Split(':');
        if (parts.Length < 5) return false;
        double x, y, z;
        if (!ParseD(parts[2], out x) || !ParseD(parts[3], out y) || !ParseD(parts[4], out z)) return false;
        name = parts[1];
        p = new Vector3D(x, y, z);
        return true;
    }

    public static string Gps(string name, Vector3D p)
    {
        return "GPS:" + name + ":" + Num(p.X) + ":" + Num(p.Y) + ":" + Num(p.Z) + ":";
    }

    // Integer-based number formatting: immune to decimal-comma locales
    public static string Num(double v)
    {
        long cents = (long)Math.Round(v * 100);
        string sign = cents < 0 ? "-" : "";
        cents = Math.Abs(cents);
        return sign + (cents / 100) + "." + (cents % 100).ToString("00");
    }

    public static string Pct(float f) { return f < 0 ? "--" : ((int)Math.Round(f * 100)) + "%"; }
    public static string PctM(int milli) { return milli < 0 ? "--" : ((milli + 5) / 10) + "%"; }

    public static string Dist(double m)
    {
        if (m < 1000) return ((int)m) + "m";
        if (m < 100000) return ((int)(m / 1000)) + "." + ((int)(m / 100) % 10) + "km";
        return ((int)(m / 1000)) + "km";
    }

    public static string Age(int s)
    {
        if (s < 60) return s + "s";
        if (s < 3600) return (s / 60) + "m";
        return (s / 3600) + "h";
    }

    public static string Clean(string s) { return s == null ? "" : s.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' '); }

    public static int Milli(float f) { return f < 0 ? -1 : (int)Math.Round(f * 1000); }
}

// Rolling runtime average for server-budget monitoring (target <= 0.3 ms avg)
class Perf
{
    public double Avg, Peak;
    double peakT;

    public void Tick(double ms, double now)
    {
        Avg += (ms - Avg) * 0.02;
        if (ms > Peak || now - peakT > 10) { Peak = ms; peakT = now; }
    }

    public string Text()
    {
        return "Runtime avg " + Util.Num(Avg) + " ms (peak " + Util.Num(Peak) + ")" + (Avg > 0.3 ? "  OVER BUDGET" : "");
    }
}
