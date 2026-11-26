// ============================================================================
// Shared: IGC network layer. All wire formats live here so hub and drones can
// never disagree on tuple layouts. Payloads use MyTuple/Vector3D/primitives only
// (no string-encoded numbers, so no locale bugs).
//
// Header: (version, netId, keyHash, msgType, seq)
// seq for orders = (hubEpoch << 32) | counter   -> drones detect hub restarts
// ============================================================================
class Net
{
    public readonly IMyIntergridCommunicationSystem IGC;
    public int NetId, KeyHash;
    public string TNav = "", THello = "", TSts = "", TOrd = "", TCmd = "", TSnap = "", TFac = "", TFacSt = "";

    public Net(IMyIntergridCommunicationSystem igc) { IGC = igc; }

    public void Configure(int netId, string key)
    {
        NetId = netId;
        KeyHash = Util.Hash(key ?? "");
        string p = "FS" + netId + "/";
        TNav = p + "nav"; THello = p + "hello"; TSts = p + "sts"; TOrd = p + "ord";
        TCmd = p + "cmd"; TSnap = p + "snap"; TFac = p + "fac"; TFacSt = p + "facst";
    }

    MyTuple<int, int, int, int, long> Hdr(int type, long seq)
    {
        return new MyTuple<int, int, int, int, long>(Proto.Version, NetId, KeyHash, type, seq);
    }

    bool Ok(MyTuple<int, int, int, int, long> h, int type)
    {
        return h.Item1 == Proto.Version && h.Item2 == NetId && h.Item3 == KeyHash && h.Item4 == type;
    }

    // ---------------- NAV (hub -> all, broadcast) ----------------
    // phase/rate: shared patrol rotation (radians, rad/s) so all drones orbit in step
    public void SendNav(Vector3D pos, Vector3D vel, Vector3D fwd, Vector3D up, Vector3D ang,
                        Vector3D koc, double kor, int mode, int alert, double phase, double rate)
    {
        IGC.SendBroadcastMessage(TNav,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>>(
                Hdr(Proto.M_NAV, 0),
                new MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>(pos, vel, fwd, up, ang),
                new MyTuple<Vector3D, double, int, int, double, double>(koc, kor, mode, alert, phase, rate)));
    }

    public bool ReadNav(MyIGCMessage m, HubFrame f)
    {
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>>)) return false;
        var n = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>>)m.Data;
        if (!Ok(n.Item1, Proto.M_NAV)) return false;
        f.Pos = n.Item2.Item1; f.Vel = n.Item2.Item2; f.Fwd = n.Item2.Item3; f.Up = n.Item2.Item4; f.AngVel = n.Item2.Item5;
        f.KoC = n.Item3.Item1; f.KoR = n.Item3.Item2; f.Mode = n.Item3.Item3; f.Alert = n.Item3.Item4;
        f.Phase = n.Item3.Item5; f.Rate = n.Item3.Item6;
        f.Valid = true;
        return true;
    }

    // ---------------- HELLO (drone -> hubs, broadcast) ----------------
    public void SendHello(string role, long caps, double maxAccel, double maxSpeed, string grid)
    {
        IGC.SendBroadcastMessage(THello,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, long, double, double, string>>(
                Hdr(Proto.M_HELLO, 0),
                new MyTuple<string, long, double, double, string>(role, caps, maxAccel, maxSpeed, grid)));
    }

    public bool ReadHello(MyIGCMessage m, out string role, out long caps, out double maxAccel, out double maxSpeed, out string grid)
    {
        role = grid = ""; caps = 0; maxAccel = maxSpeed = 0;
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, long, double, double, string>>)) return false;
        var h = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, long, double, double, string>>)m.Data;
        if (!Ok(h.Item1, Proto.M_HELLO)) return false;
        role = h.Item2.Item1; caps = h.Item2.Item2; maxAccel = h.Item2.Item3; maxSpeed = h.Item2.Item4; grid = h.Item2.Item5;
        return true;
    }

    // ---------------- STATUS (drone -> its hub, unicast) ----------------
    // (role, state, task, ackSeq, dockId) (pos, vel) (battery, h2, cargo, integrity, ammo, flags)
    public bool SendStatus(long hub, string role, int state, int task, long ack, long dockId,
                           Vector3D pos, Vector3D vel, float batt, float h2, float cargo, float integ, float ammo, int flags)
    {
        return IGC.SendUnicastMessage(hub, TSts,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>>(
                Hdr(Proto.M_STS, 0),
                new MyTuple<string, int, int, long, long>(role, state, task, ack, dockId),
                new MyTuple<Vector3D, Vector3D>(pos, vel),
                new MyTuple<float, float, float, float, float, int>(batt, h2, cargo, integ, ammo, flags)));
    }

    public bool ReadStatus(MyIGCMessage m, out MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>> s)
    {
        s = default(MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>>);
        if (m.Tag != TSts) return false;
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>>)) return false;
        s = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>>)m.Data;
        return Ok(s.Item1, Proto.M_STS);
    }

    // ---------------- ORDER (hub -> drone, unicast) ----------------
    // (code, a, b, c, id, text)
    public bool SendOrder(long to, long seq, int code, Vector3D a, Vector3D b, Vector3D c, long id, string text)
    {
        return IGC.SendUnicastMessage(to, TOrd,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long, string>>(
                Hdr(Proto.M_ORD, seq),
                new MyTuple<int, Vector3D, Vector3D, Vector3D, long, string>(code, a, b, c, id, text ?? "")));
    }

    public bool ReadOrder(MyIGCMessage m, out long seq, out int code, out Vector3D a, out Vector3D b, out Vector3D c, out long id, out string text)
    {
        seq = 0; code = 0; a = b = c = Vector3D.Zero; id = 0; text = "";
        if (m.Tag != TOrd) return false;
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long, string>>)) return false;
        var o = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long, string>>)m.Data;
        if (!Ok(o.Item1, Proto.M_ORD)) return false;
        seq = o.Item1.Item5; code = o.Item2.Item1; a = o.Item2.Item2; b = o.Item2.Item3; c = o.Item2.Item4;
        id = o.Item2.Item5; text = o.Item2.Item6;
        return true;
    }
}
