// ============================================================================
// Drone: flight controller
//  - 6-direction thrust allocator (MaxEffectiveThrust -> atmosphere aware)
//  - gravity compensation + velocity feed-forward
//  - braking-distance speed profile
//  - disturbance compensation (measured vs expected acceleration)
//  - gyro aim of any reference block's forward/up
// Requires dampeners OFF while thrusting (override 0 then means "no thrust").
// ============================================================================
class Flight
{
    public IMyShipController Ctl;
    readonly List<IMyThrust>[] grp = new List<IMyThrust>[6];
    readonly List<IMyGyro> gyros = new List<IMyGyro>();
    readonly double[] gMax = new double[6];
    readonly Vector3D[] gDir = new Vector3D[6];

    public Vector3D Pos, Vel, Grav, Dist;
    public double Mass = 1, MaxAccel;
    public bool HasThrust, UseObserver = true, Freeze;
    public double Kv = 2.5, Kp = 0.8, BrakeSafety = 0.6, GyroGain = 3, GyroMax = 3, SpeedCap = 100;
    public float GyroSign = -1;   // config GyroInvert=true flips this (fallback only)

    Vector3D lastVel, lastCmdAccel;
    bool haveLast, thrusting;

    public Flight() { for (int i = 0; i < 6; i++) grp[i] = new List<IMyThrust>(); }

    public void Bind(IMyShipController ctl, List<IMyThrust> thr, List<IMyGyro> gy)
    {
        Ctl = ctl;
        for (int i = 0; i < 6; i++) grp[i].Clear();
        // Group by the block's grid-relative forward (= exhaust direction)
        foreach (var t in thr) grp[(int)t.Orientation.Forward].Add(t);
        gyros.Clear();
        gyros.AddRange(gy);
    }

    public void Sense(double dt)
    {
        Pos = Ctl.CenterOfMass;
        Vel = Ctl.GetShipVelocities().LinearVelocity;
        Grav = Ctl.GetNaturalGravity();
        Mass = Math.Max(1, Ctl.CalculateShipMass().PhysicalMass);

        MatrixD gm = Ctl.CubeGrid.WorldMatrix;
        HasThrust = false; MaxAccel = 0;
        for (int i = 0; i < 6; i++)
        {
            // Thrust force acts opposite to the block's forward (exhaust) direction
            Vector3D v = Base6Directions.GetVector((Base6Directions.Direction)i);
            gDir[i] = -Vector3D.TransformNormal(v, gm);
            double sum = 0;
            foreach (var t in grp[i]) if (!t.Closed && t.IsFunctional && t.Enabled) sum += t.MaxEffectiveThrust;
            gMax[i] = sum;
            if (sum > 0) { HasThrust = true; MaxAccel = Math.Max(MaxAccel, sum / Mass); }
        }

        if (thrusting && haveLast && UseObserver && !Freeze && dt > 0)
        {
            Vector3D err = (Vel - lastVel) / dt - (lastCmdAccel + Grav);
            Dist += (err - Dist) * (1 - Math.Exp(-dt / 0.8));
            double l = Dist.Length();
            if (l > 6) Dist *= 6 / l;
        }
        lastVel = Vel; haveLast = true;
    }

    // Max thrust acceleration achievable along unit vector w (0 if a needed axis has no thrusters)
    public double AccelAlong(Vector3D w)
    {
        double k = double.MaxValue; bool any = false;
        for (int i = 0; i < 6; i++)
        {
            double c = Vector3D.Dot(gDir[i], w);
            if (c < 0.2) continue;
            any = true;
            k = Math.Min(k, gMax[i] / Mass / c);
        }
        return any ? k : 0;
    }

    public void Engage()
    {
        if (Ctl.DampenersOverride) Ctl.DampenersOverride = false;
        thrusting = true; haveLast = false; Dist = Vector3D.Zero;
    }

    public void Release(bool dampeners)
    {
        for (int i = 0; i < 6; i++) foreach (var t in grp[i]) if (t.ThrustOverridePercentage != 0) t.ThrustOverridePercentage = 0;
        foreach (var g in gyros) if (g.GyroOverride) { g.Pitch = 0; g.Yaw = 0; g.Roll = 0; g.GyroOverride = false; }
        if (Ctl != null && Ctl.DampenersOverride != dampeners) Ctl.DampenersOverride = dampeners;
        thrusting = false; haveLast = false; Dist = Vector3D.Zero;
    }

    // accel: desired world acceleration of the ship (gravity is compensated here)
    public void Thrust(Vector3D accel)
    {
        if (Ctl.DampenersOverride) Ctl.DampenersOverride = false;
        Vector3D F = (accel - Grav - (UseObserver ? Dist : Vector3D.Zero)) * Mass;
        Vector3D got = Vector3D.Zero;
        for (int i = 0; i < 6; i++)
        {
            double frac = 0;
            if (gMax[i] > 0)
            {
                double c = Vector3D.Dot(F, gDir[i]);
                if (c > 0) frac = Math.Min(1, c / gMax[i]);
            }
            got += gDir[i] * (frac * gMax[i]);
            float f = (float)frac;
            foreach (var t in grp[i]) if (Math.Abs(t.ThrustOverridePercentage - f) > 0.002f) t.ThrustOverridePercentage = f;
        }
        lastCmdAccel = got / Mass;
    }

    // Fly toward target while matching target velocity (moving hub frame feed-forward)
    public void FlyTo(Vector3D target, Vector3D tVel, double maxSpeed)
    {
        Vector3D err = target - Pos;
        double d = err.Length();
        Vector3D want = tVel;
        if (d > 0.01)
        {
            Vector3D dir = err / d;
            double brake = Math.Max(0.3, (AccelAlong(-dir) + Vector3D.Dot(Grav, -dir)) * BrakeSafety);
            double v = Math.Min(maxSpeed, Math.Min(Math.Sqrt(2 * brake * d), Kp * d));
            want += dir * v;
        }
        double wl = want.Length();
        if (wl > SpeedCap) want *= SpeedCap / wl;
        Thrust((want - Vel) * Kv);
    }

    // Rotate so that r.Forward -> fwd and (optionally) r.Up -> up. r can be any block's matrix.
    public void Aim(MatrixD r, Vector3D fwd, Vector3D up)
    {
        fwd = Util.Norm(fwd);
        if (fwd.LengthSquared() < 0.5) return;
        Vector3D f = r.Forward, u = r.Up;
        Vector3D axis = Vector3D.Cross(f, fwd);
        double s = axis.Length(), c = Vector3D.Dot(f, fwd);
        Vector3D rot;
        if (s < 1e-6) rot = c > 0 ? Vector3D.Zero : u * Math.PI;
        else rot = axis / s * Math.Atan2(s, c);

        if (up.LengthSquared() > 0.01)
        {
            Vector3D upP = up - fwd * Vector3D.Dot(up, fwd);
            if (upP.LengthSquared() > 1e-4)
            {
                upP = Util.Norm(upP);
                Vector3D ra = Vector3D.Cross(u, upP);
                // fade roll in as forward alignment improves
                rot += f * (Math.Atan2(Vector3D.Dot(ra, f), Vector3D.Dot(u, upP)) * Math.Max(0, c));
            }
        }

        Vector3D w = rot * GyroGain;
        double wl = w.Length();
        if (wl > GyroMax) w *= GyroMax / wl;

        foreach (var g in gyros)
        {
            if (g.Closed || !g.IsFunctional) continue;
            Vector3D l = Vector3D.TransformNormal(w, MatrixD.Transpose(g.WorldMatrix));
            // Gyro overrides rotate about -X/-Y/-Z of the gyro block, hence the negation
            float p = GyroSign * (float)l.X, y = GyroSign * (float)l.Y, ro = GyroSign * (float)l.Z;
            if (Math.Abs(g.Pitch - p) > 1e-3f) g.Pitch = p;
            if (Math.Abs(g.Yaw - y) > 1e-3f) g.Yaw = y;
            if (Math.Abs(g.Roll - ro) > 1e-3f) g.Roll = ro;
            if (!g.GyroOverride) g.GyroOverride = true;
        }
    }

    public static double AimError(MatrixD r, Vector3D fwd) { return Util.Angle(r.Forward, fwd); }

    // Docked: thrusters and gyros off so they do not fight the host ship
    public void SetPower(bool on)
    {
        for (int i = 0; i < 6; i++) foreach (var t in grp[i]) if (t.Enabled != on) t.Enabled = on;
        foreach (var g in gyros) if (g.Enabled != on) g.Enabled = on;
    }
}
