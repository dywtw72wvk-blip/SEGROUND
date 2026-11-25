// ============================================================================
// Shared: hub reference frame. The hub always broadcasts its live frame; a
// base simply has zero velocity. Drone orders are expressed in this frame.
// Local axes follow SE convention: +X right, +Y up, +Z backward (forward = -Z).
// ============================================================================
class HubFrame
{
    public Vector3D Pos, Vel, Fwd = Vector3D.Forward, Up = Vector3D.Up, AngVel, KoC;
    public double KoR, Phase, Rate;
    public int Mode, Alert;
    public double T = -1e9;     // local time the frame was received
    public bool Valid;

    // Frame extrapolated to 'now' (translation only; rotation is covered by VelAt feed-forward)
    public MatrixD Matrix(double now)
    {
        double dt = Util.Clamp(now - T, 0, 2);
        return MatrixD.CreateWorld(Pos + Vel * dt, Fwd, Up);
    }

    public Vector3D KeepOut(double now)
    {
        return KoC + Vel * Util.Clamp(now - T, 0, 2);
    }

    // Shared patrol rotation angle, extrapolated
    public double PhaseAt(double now) { return Phase + Rate * Util.Clamp(now - T, 0, 2); }

    // Rotate a hub-local vector about the local +Y (up) axis, right-handed
    public static Vector3D RotY(Vector3D v, double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new Vector3D(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    // Velocity of a point rigidly attached to the hub
    public Vector3D VelAt(Vector3D world, MatrixD m)
    {
        return Vel + Vector3D.Cross(AngVel, world - m.Translation);
    }

    public static Vector3D ToWorld(MatrixD m, Vector3D local) { return m.Translation + Vector3D.TransformNormal(local, m); }
    public static Vector3D ToLocal(MatrixD m, Vector3D world) { return Vector3D.TransformNormal(world - m.Translation, MatrixD.Transpose(m)); }
    public static Vector3D DirToWorld(MatrixD m, Vector3D d) { return Vector3D.TransformNormal(d, m); }
    public static Vector3D DirToLocal(MatrixD m, Vector3D d) { return Vector3D.TransformNormal(d, MatrixD.Transpose(m)); }
}
