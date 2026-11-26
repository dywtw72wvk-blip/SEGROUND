// ============================================================================
// FS Mining: hub plumbing shared by Mine Core (carrier) and Depot (base).
// Registry of drones of one role (HubRole: MINER or HAULER, so both hubs can
// share a network without taking each other's drones), reliable orders with
// retries, dock reservations on [Dock] connectors, the reference-frame
// broadcast drones dock by, auto-provisioning of docked drone PBs, ore moves
// between conveyor-linked inventories, and the event log.
// Each program defines: OnUnicast(m) for its own unicast messages, and
// OnDock(d), called when a drone is sent to a dock.
// ============================================================================
Net net;
Cfg cfg;
readonly Perf perf = new Perf();
IMyBroadcastListener helloL, cmdL;
IMyUnicastListener uni;
IMyShipController refCtl;
IMyTerminalBlock refBlk;
string HubRole = "MINER";

readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();
readonly List<IMyShipConnector> dockConns = new List<IMyShipConnector>();
readonly List<IMyProgrammableBlock> pbs = new List<IMyProgrammableBlock>();
readonly List<MyInventoryItem> items = new List<MyInventoryItem>();
readonly Dictionary<long, Drone> reg = new Dictionary<long, Drone>();
readonly List<Drone> sorted = new List<Drone>();
readonly Docks docks = new Docks();
readonly List<string> evText = new List<string>();
readonly List<double> evT = new List<double>();
readonly StringBuilder sb = new StringBuilder();

int netId;
string key = "", dockTag = "", ctlTag = "", perfMode = "NORMAL", noLink = "";
double lostAfter, rtbBatt, noLinkT = -1e9;
bool autoProv, bootLocked;
int epoch, callCtr, out_, docked_;
long ctr;
double T, nextNav, nextTask, nextScan, nextProv, nextEcho;
bool havePos, moving;
Vector3D lastPos, vel, angVel;
MatrixD M = MatrixD.Identity;
string echo = "", warn = "";

static void SetV(MyIni ini, string s, string k, Vector3D v) { ini.Set(s, k + "x", v.X); ini.Set(s, k + "y", v.Y); ini.Set(s, k + "z", v.Z); }
static Vector3D GetV(MyIni ini, string s, string k) { return new Vector3D(ini.Get(s, k + "x").ToDouble(), ini.Get(s, k + "y").ToDouble(), ini.Get(s, k + "z").ToDouble()); }

// registry in Storage: section d<addr>
void SaveReg(MyIni ini)
{
    ini.Set("hub", "epoch", epoch); ini.Set("hub", "ctr", ctr); ini.Set("hub", "calls", callCtr);
    foreach (var d in reg.Values)
    {
        string s = "d" + d.Addr;
        ini.Set(s, "call", d.Call); ini.Set(s, "role", d.Role); ini.Set(s, "grid", d.Grid);
        ini.Set(s, "dir", d.Dir); ini.Set(s, "id", d.Id); ini.Set(s, "conn", d.Conn); ini.Set(s, "dep", d.Dep); ini.Set(s, "shaft", d.Shaft);
        SetV(ini, s, "a", d.A); SetV(ini, s, "b", d.B); SetV(ini, s, "c", d.C);
    }
}

bool LoadReg(MyIni ini, string s)
{
    long addr;
    if (s.Length < 2 || s[0] != 'd' || !long.TryParse(s.Substring(1), out addr)) return false;
    var d = new Drone { Addr = addr, Restored = true };
    d.Call = ini.Get(s, "call").ToString(""); d.Role = ini.Get(s, "role").ToString(""); d.Grid = ini.Get(s, "grid").ToString("");
    d.Dir = ini.Get(s, "dir").ToInt32(); d.Id = ini.Get(s, "id").ToInt64(); d.Conn = ini.Get(s, "conn").ToInt64();
    d.Dep = ini.Get(s, "dep").ToInt32(-1); d.Shaft = ini.Get(s, "shaft").ToInt32(-1);
    d.A = GetV(ini, s, "a"); d.B = GetV(ini, s, "b"); d.C = GetV(ini, s, "c");
    if (d.Conn != 0) docks.Res[d.Conn] = d.Addr;
    reg[addr] = d;
    return true;
}

void HubConfig(string section, int defNet)
{
    cfg = new Cfg(Me, section);
    netId = cfg.I("NetId", defNet);
    key = cfg.S("Key", "change-me");
    dockTag = cfg.S("DockTag", "[Dock]");
    ctlTag = cfg.S("ControllerTag", "");
    lostAfter = cfg.D("LostAfter", 10);
    autoProv = cfg.B("AutoProvision", true);
    rtbBatt = cfg.D("RtbBattery", 0.25);
    perfMode = cfg.S("Perf", "Normal").ToUpper();
}

void HubListen()
{
    net.Configure(netId, key);
    if (helloL != null) IGC.DisableBroadcastListener(helloL);
    if (cmdL != null) IGC.DisableBroadcastListener(cmdL);
    helloL = IGC.RegisterBroadcastListener(net.THello);
    cmdL = IGC.RegisterBroadcastListener(net.TCmd);
}

// exact tags: [FS:Mine] or [FS:Mine:n] (so FS:Mine never matches [FS:Miners])
static bool HasTag(string name, string tag) { return name.Contains("[" + tag + ":") || name.Contains("[" + tag + "]"); }

static int TagIndex(string name, string tag)
{
    int i = name.IndexOf("[" + tag + ":");
    if (i < 0) return 0;
    int j = i + tag.Length + 2, e = name.IndexOf(']', j), v;
    return e > j && int.TryParse(name.Substring(j, e - j), out v) ? v : 0;
}

// ---------------------------------------------------------------- frame
void UpdateFrame(double dt)
{
    M = refBlk.WorldMatrix;
    if (refCtl != null) { var v = refCtl.GetShipVelocities(); vel = v.LinearVelocity; angVel = v.AngularVelocity; }
    else
    {
        Vector3D p = M.Translation;
        if (havePos && dt > 0) vel = (p - lastPos) / dt;
        lastPos = p; havePos = true; angVel = Vector3D.Zero;
    }
    moving = vel.LengthSquared() > 0.25 || angVel.LengthSquared() > 1e-4;
}

void SendNav()
{
    var g = Me.CubeGrid;
    double gs = g.GridSize;
    Vector3D half = new Vector3D(gs / 2), lo = new Vector3D(g.Min) * gs - half, hi = new Vector3D(g.Max) * gs + half;
    Vector3D bmin = new Vector3D(double.MaxValue), bmax = new Vector3D(double.MinValue);
    for (int i = 0; i < 8; i++)
    {
        var c = new Vector3D((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
        var l = HubFrame.ToLocal(M, Vector3D.Transform(c, g.WorldMatrix));
        bmin = Vector3D.Min(bmin, l); bmax = Vector3D.Max(bmax, l);
    }
    net.SendNav(M.Translation, vel, M.Forward, M.Up, angVel, g.WorldVolume.Center, g.WorldVolume.Radius,
                g.IsStatic ? Proto.MODE_BASE : Proto.MODE_CARRIER, Proto.ALERT_GREEN, 0, 0, (bmin + bmax) / 2, (bmax - bmin) / 2);
}

Vector3D UpVec()
{
    Vector3D g = refCtl != null ? refCtl.GetNaturalGravity() : Vector3D.Zero;
    return g.LengthSquared() > 0.01 ? -Util.Norm(g) : M.Up;
}

// ---------------------------------------------------------------- messages & registry
void Receive()
{
    while (helloL.HasPendingMessage)
    {
        var m = helloL.AcceptMessage();
        string role, gname; long caps; double acc, spd;
        if (!net.ReadHello(m, out role, out caps, out acc, out spd, out gname) || (role ?? "").ToUpper() != HubRole) continue;
        var d = GetOrAdd(m.Source, role);
        d.Caps = caps; d.MaxAccel = acc; d.MaxSpeed = spd; d.Grid = gname;
        Register(d);
    }
    while (uni.HasPendingMessage)
    {
        var m = uni.AcceptMessage();
        MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>> s;
        if (!net.ReadStatus(m, out s)) { OnUnicast(m); continue; }
        Drone d;
        if (!reg.TryGetValue(m.Source, out d))
        {
            if ((s.Item2.Item1 ?? "").ToUpper() != HubRole) continue;
            d = GetOrAdd(m.Source, s.Item2.Item1); Register(d);
        }
        bool wasDocked = d.Docked;
        d.Role = s.Item2.Item1; d.State = s.Item2.Item2; d.Task = s.Item2.Item3; d.Ack = s.Item2.Item4; d.DockId = s.Item2.Item5;
        d.Pos = s.Item3.Item1; d.Vel = s.Item3.Item2;
        d.Batt = s.Item4.Item1; d.H2 = s.Item4.Item2; d.Cargo = s.Item4.Item3; d.Integ = s.Item4.Item4; d.Ammo = s.Item4.Item5; d.Flags = s.Item4.Item6;
        d.Seen = T;
        if (d.LostFlag)
        {
            d.LostFlag = false;
            if (d.Restored) { d.Restored = false; Log(d.Call + " reconnected"); Reissue(d); }
            else Log(d.Call + " link restored");
        }
        if (!wasDocked && d.Docked) { Log(d.Call + " docked"); nextProv = T + 1; d.DockedSince = T; }
        if (wasDocked && !d.Docked) Log(d.Call + " undocked");
        if (d.Acked) { d.Tries = 0; d.Unresp = false; }
    }
    while (cmdL.HasPendingMessage)
    {
        var c = cmdL.AcceptMessage().Data as string;
        if (c != null) Command(c);
    }
}

Drone GetOrAdd(long addr, string role)
{
    Drone d;
    if (reg.TryGetValue(addr, out d)) return d;
    callCtr++;
    string r = (role ?? HubRole).ToUpper();
    if (r.Length > 6) r = r.Substring(0, 6);
    d = new Drone { Addr = addr, Role = role, Call = r + "-" + callCtr.ToString("00"), Seen = T, LostFlag = false };
    reg[addr] = d;
    Log("New drone " + d.Call);
    return d;
}

long NextSeq() { ctr++; return ((long)epoch << 32) | (ctr & 0xFFFFFFFFL); }

void Register(Drone d)
{
    net.SendOrder(d.Addr, NextSeq(), Proto.O_REGISTER, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0, d.Call);
    Reissue(d);
}

void Issue(Drone d, int dir, Vector3D a, Vector3D b, Vector3D c, long id)
{
    d.Dir = dir; d.A = a; d.B = b; d.C = c; d.Id = id;
    d.Seq = NextSeq(); d.Tries = 0;
    SendOrd(d);
}

void Reissue(Drone d)
{
    if (d.Dir == Proto.O_NONE) return;
    if (d.Dir == Proto.O_DOCK && d.Conn == 0) { nextTask = T; return; }
    d.Seq = NextSeq(); d.Tries = 0;
    SendOrd(d);
}

void SendOrd(Drone d) { net.SendOrder(d.Addr, d.Seq, d.Dir, d.A, d.B, d.C, d.Id, ""); d.Sent = T; d.Tries++; }

void OrderDock(Drone d)
{
    OnDock(d);
    d.Dir = Proto.O_DOCK; d.Seq = 0;
    var c = docks.Reserve(d);
    if (c != null) SendDock(d, c);
    else Log(d.Call + " waiting for a free dock");
}

void SendDock(Drone d, IMyShipConnector c)
{
    MatrixD cm = c.WorldMatrix;
    Issue(d, Proto.O_DOCK, HubFrame.ToLocal(M, cm.Translation), HubFrame.DirToLocal(M, cm.Forward), HubFrame.DirToLocal(M, cm.Up), c.EntityId);
}

// 2 Hz bookkeeping common to both hubs: boot dock lock, lost drones, order retries, dock
// reservations, low-battery returns, the dock queue
void TaskRegistry()
{
    sorted.Clear();
    foreach (var d in reg.Values) sorted.Add(d);
    sorted.Sort((x, y) => string.Compare(x.Call, y.Call, StringComparison.Ordinal));
    if (!bootLocked && T >= 2)
    {
        // pre-docked blueprints: lock every dock with a drone sitting on it, once
        foreach (var c in docks.List) if (!c.Closed && c.Status == MyShipConnectorStatus.Connectable) c.Connect();
        bootLocked = true;
    }
    out_ = docked_ = 0;
    foreach (var d in sorted)
    {
        if (!d.LostFlag && T - d.Seen > lostAfter) { d.LostFlag = true; Log(d.Call + " LOST"); }
        if (d.LostFlag) { if (d.Conn != 0 && T - d.Seen > 60) docks.Release(d); continue; }
        if (!d.Acked && T - d.Sent > Math.Min(8, 0.75 * (1 << Math.Min(d.Tries, 4))))
        {
            SendOrd(d);
            if (d.Tries > 6 && !d.Unresp) { d.Unresp = true; Log(d.Call + " not acknowledging orders"); }
        }
        if (d.Docked && d.DockId != 0 && docks.Get(d.DockId) != null) docks.Claim(d, d.DockId);
        else if (d.Dir != Proto.O_DOCK && d.Conn != 0 && !d.Docked) docks.Release(d);
        if (d.Docked) docked_++; else out_++;
        if (d.Dir != Proto.O_DOCK && !d.Docked && docks.List.Count > 0 && ((d.Batt >= 0 && d.Batt < rtbBatt) || (d.Flags & Proto.F_WANT_RTB) != 0))
        { Log(d.Call + " returning (battery " + Util.Pct(d.Batt) + ")"); OrderDock(d); }
        if (d.Dir == Proto.O_DOCK && (d.Conn == 0 || docks.Get(d.Conn) == null)) { var c = docks.Reserve(d); if (c != null) SendDock(d, c); }
    }
}

// ore (and ice, stone) from one set of inventories into another, at most `budget` transfers and maxKg
// per call; left = ore is still waiting in the source. Items only move between conveyor-linked
// inventories: a missing link is logged once a minute.
double MoveOre(List<IMyTerminalBlock> from, List<IMyTerminalBlock> to, int budget, double maxKg, out bool left)
{
    double moved = 0;
    left = false;
    foreach (var b in from)
        for (int k = 0; k < b.InventoryCount; k++)
        {
            if (b.Closed) break;
            var src = b.GetInventory(k);
            items.Clear();
            src.GetItems(items, it => it.Type.TypeId == "MyObjectBuilder_Ore");
            foreach (var it in items)
            {
                left = true;
                if (budget <= 0 || moved >= maxKg - 1) return moved;
                foreach (var h in to)
                {
                    if (h.Closed) continue;
                    var dst = h.GetInventory(0);
                    double free = (float)dst.MaxVolume - (float)dst.CurrentVolume;
                    if (free < 0.05) continue;
                    if (!src.IsConnectedTo(dst))
                    {
                        if (T - noLinkT > 60) { noLinkT = T; Log("No conveyor link: " + Util.Clean(b.CustomName) + " to " + Util.Clean(h.CustomName)); }
                        continue;
                    }
                    double amt = Math.Min(Math.Min((float)it.Amount, maxKg - moved), free / 0.00037 * 0.95);   // ~0.37 L per kg of ore
                    budget--;
                    if (amt > 1 && src.TransferItemTo(dst, it, (MyFixedPoint)(float)amt)) { moved += amt; break; }
                    if (budget <= 0) return moved;
                }
            }
        }
    return moved;
}

// cargo containers of the grid docked on a connector (refreshed when the grid changes)
class DockInv { public long Grid; public double T; public readonly List<IMyTerminalBlock> L = new List<IMyTerminalBlock>(); }
readonly Dictionary<long, DockInv> dockInv = new Dictionary<long, DockInv>();

List<IMyTerminalBlock> Docked(IMyShipConnector c, bool drills)
{
    var g = c.OtherConnector.CubeGrid;
    DockInv di;
    if (!dockInv.TryGetValue(c.EntityId, out di)) dockInv[c.EntityId] = di = new DockInv();
    if (di.Grid != g.EntityId || T - di.T > 15)
    {
        di.Grid = g.EntityId; di.T = T; di.L.Clear();
        GridTerminalSystem.GetBlocksOfType(di.L, b => b.CubeGrid == g && b.HasInventory && (b is IMyCargoContainer || (drills && (b is IMyShipDrill || b is IMyShipConnector))));
    }
    return di.L;
}

void ForTargets(string tgt, Action<Drone> act)
{
    bool all = tgt.Equals("all", StringComparison.OrdinalIgnoreCase);
    int n = 0;
    foreach (var d in reg.Values)
    {
        bool hit = all ? !d.LostFlag : d.Call.Equals(tgt, StringComparison.OrdinalIgnoreCase) || d.Grid.Equals(tgt, StringComparison.OrdinalIgnoreCase);
        if (hit) { act(d); n++; }
    }
    if (n == 0) Log(all ? "No drones online" : "No drone matches '" + Util.Clean(tgt) + "'");
}

// Push NetId/Key into docked drone PBs ([Drone] section) and restart their config
void Provision(bool verbose)
{
    pbs.Clear();
    GridTerminalSystem.GetBlocksOfType(pbs, b => !b.IsSameConstructAs(Me) && b.CustomData.Contains("[Drone]"));
    int n = 0;
    foreach (var pb in pbs)
    {
        var ini = new MyIni();
        if (!ini.TryParse(pb.CustomData)) continue;
        if (ini.Get("Drone", "NetId").ToInt32(0) == netId && ini.Get("Drone", "Key").ToString("") == key) continue;
        ini.Set("Drone", "NetId", netId);
        ini.Set("Drone", "Key", key);
        pb.CustomData = ini.ToString();
        pb.TryRun("provision");
        n++;
        Log("Provisioned " + Util.Clean(pb.CubeGrid.CustomName));
    }
    if (verbose && n == 0) Log("Provision: nothing to update (" + pbs.Count + " drone PBs connected)");
}

void Log(string s)
{
    evText.Add(Util.Clean(s)); evT.Add(T);
    if (evText.Count > 8) { evText.RemoveAt(0); evT.RemoveAt(0); }
}

static string Pad(string s, int n) { return s.Length >= n ? s.Substring(0, n - 1) + " " : s + new string(' ', n - s.Length); }
