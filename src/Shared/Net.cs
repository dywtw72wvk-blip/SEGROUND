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
    public string TNav = "", THello = "", TSts = "", TOrd = "", TCmd = "", TSnap = "", TFac = "", TFacSt = "", TCon = "", TDes = "",
                  THaul = "", TVis = "", TVReq = "";

    public Net(IMyIntergridCommunicationSystem igc) { IGC = igc; }

    public void Configure(int netId, string key)
    {
        NetId = netId;
        KeyHash = Util.Hash(key ?? "");
        string p = "FS" + netId + "/";
        TNav = p + "nav"; THello = p + "hello"; TSts = p + "sts"; TOrd = p + "ord";
        TCmd = p + "cmd"; TSnap = p + "snap"; TFac = p + "fac"; TFacSt = p + "facst"; TCon = p + "con"; TDes = p + "des";
        THaul = p + "haul"; TVis = p + "vis"; TVReq = p + "vreq";
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
    // boxC/boxH: hull bounding box in the hub frame (centre, half extents) for close avoidance
    public void SendNav(Vector3D pos, Vector3D vel, Vector3D fwd, Vector3D up, Vector3D ang,
                        Vector3D koc, double kor, int mode, int alert, double phase, double rate, Vector3D boxC, Vector3D boxH)
    {
        IGC.SendBroadcastMessage(TNav,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>, MyTuple<Vector3D, Vector3D>>(
                Hdr(Proto.M_NAV, 0),
                new MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>(pos, vel, fwd, up, ang),
                new MyTuple<Vector3D, double, int, int, double, double>(koc, kor, mode, alert, phase, rate),
                new MyTuple<Vector3D, Vector3D>(boxC, boxH)));
    }

    public bool ReadNav(MyIGCMessage m, HubFrame f)
    {
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>, MyTuple<Vector3D, Vector3D>>)) return false;
        var n = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, Vector3D>, MyTuple<Vector3D, double, int, int, double, double>, MyTuple<Vector3D, Vector3D>>)m.Data;
        if (!Ok(n.Item1, Proto.M_NAV)) return false;
        f.Pos = n.Item2.Item1; f.Vel = n.Item2.Item2; f.Fwd = n.Item2.Item3; f.Up = n.Item2.Item4; f.AngVel = n.Item2.Item5;
        f.KoC = n.Item3.Item1; f.KoR = n.Item3.Item2; f.Mode = n.Item3.Item3; f.Alert = n.Item3.Item4;
        f.Phase = n.Item3.Item5; f.Rate = n.Item3.Item6;
        f.BoxC = n.Item4.Item1; f.BoxH = n.Item4.Item2;
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

    // ---------------- CONTACT (drone -> its hub, unicast) ----------------
    // what a drone's turrets are locked on: (entity id, type, size m) (pos, vel)
    public bool SendContact(long hub, long id, int type, double size, Vector3D pos, Vector3D vel)
    {
        return IGC.SendUnicastMessage(hub, TCon,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, int, double>, MyTuple<Vector3D, Vector3D>>(
                Hdr(Proto.M_CON, 0), new MyTuple<long, int, double>(id, type, size), new MyTuple<Vector3D, Vector3D>(pos, vel)));
    }

    public bool ReadContact(MyIGCMessage m, out long id, out int type, out double size, out Vector3D pos, out Vector3D vel)
    {
        id = 0; type = 0; size = 0; pos = vel = Vector3D.Zero;
        if (m.Tag != TCon || !(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, int, double>, MyTuple<Vector3D, Vector3D>>)) return false;
        var c = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, int, double>, MyTuple<Vector3D, Vector3D>>)m.Data;
        if (!Ok(c.Item1, Proto.M_CON)) return false;
        id = c.Item2.Item1; type = c.Item2.Item2; size = c.Item2.Item3; pos = c.Item3.Item1; vel = c.Item3.Item2;
        return true;
    }

    // ---------------- DESIGNATION (anyone on the net -> hubs, broadcast) ----------------
    // a target for search & destroy from a designator / AWACS script: (entity id or 0, size m) (pos, vel)
    public void SendDesignation(long id, double size, Vector3D pos, Vector3D vel)
    {
        IGC.SendBroadcastMessage(TDes,
            new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, double>, MyTuple<Vector3D, Vector3D>>(
                Hdr(Proto.M_DES, 0), new MyTuple<long, double>(id, size), new MyTuple<Vector3D, Vector3D>(pos, vel)));
    }

    public bool ReadDesignation(MyIGCMessage m, out long id, out double size, out Vector3D pos, out Vector3D vel)
    {
        id = 0; size = 0; pos = vel = Vector3D.Zero;
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, double>, MyTuple<Vector3D, Vector3D>>)) return false;
        var d = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<long, double>, MyTuple<Vector3D, Vector3D>>)m.Data;
        if (!Ok(d.Item1, Proto.M_DES)) return false;
        id = d.Item2.Item1; size = d.Item2.Item2; pos = d.Item3.Item1; vel = d.Item3.Item2;
        if (id == 0) id = -2;   // anonymous designation: one shared slot
        return true;
    }

    // ---------------- HAUL (carrier -> depots, broadcast): (position, m3 waiting, kg waiting, carrier name) ----------------
    public void SendHaul(Vector3D pos, double m3, double kg, string name)
    {
        IGC.SendBroadcastMessage(THaul, new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, double, double, string>>(
            Hdr(Proto.M_HAUL, 0), new MyTuple<Vector3D, double, double, string>(pos, m3, kg, name ?? "")));
    }

    public bool ReadHaul(MyIGCMessage m, out Vector3D pos, out double m3, out double kg, out string name)
    {
        pos = Vector3D.Zero; m3 = kg = 0; name = "";
        if (!(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, double, double, string>>)) return false;
        var h = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<Vector3D, double, double, string>>)m.Data;
        if (!Ok(h.Item1, Proto.M_HAUL)) return false;
        pos = h.Item2.Item1; m3 = h.Item2.Item2; kg = h.Item2.Item3; name = h.Item2.Item4;
        return true;
    }

    // ---------------- VISIT (carrier -> visiting hauler, unicast): (code, a, b, c, id) ----------------
    public bool SendVisit(long to, int code, Vector3D a, Vector3D b, Vector3D c, long id)
    {
        return IGC.SendUnicastMessage(to, TVis, new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long>>(
            Hdr(Proto.M_VIS, 0), new MyTuple<int, Vector3D, Vector3D, Vector3D, long>(code, a, b, c, id)));
    }

    public bool ReadVisit(MyIGCMessage m, out int code, out Vector3D a, out Vector3D b, out Vector3D c, out long id)
    {
        code = 0; a = b = c = Vector3D.Zero; id = 0;
        if (m.Tag != TVis || !(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long>>)) return false;
        var v = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, Vector3D, Vector3D, Vector3D, long>>)m.Data;
        if (!Ok(v.Item1, Proto.M_VIS)) return false;
        code = v.Item2.Item1; a = v.Item2.Item2; b = v.Item2.Item3; c = v.Item2.Item4; id = v.Item2.Item5;
        return true;
    }

    // ---------------- VISIT REQUEST (hauler -> carrier, unicast): (code, kg it can still lift) ----------------
    public bool SendVReq(long to, int code, double kg)
    {
        return IGC.SendUnicastMessage(to, TVReq, new MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, double>>(
            Hdr(Proto.M_VREQ, 0), new MyTuple<int, double>(code, kg)));
    }

    public bool ReadVReq(MyIGCMessage m, out int code, out double kg)
    {
        code = 0; kg = 0;
        if (m.Tag != TVReq || !(m.Data is MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, double>>)) return false;
        var v = (MyTuple<MyTuple<int, int, int, int, long>, MyTuple<int, double>>)m.Data;
        if (!Ok(v.Item1, Proto.M_VREQ)) return false;
        code = v.Item2.Item1; kg = v.Item2.Item2;
        return true;
    }
}
