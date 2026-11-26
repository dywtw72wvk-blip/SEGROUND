// ============================================================================
// FS MINING — MINE CORE
// Runs on the mining carrier. Same network layer as the Fleet hub (registry,
// reliable orders, dock pads, reference-frame broadcast for docking to a moving
// carrier, auto-provisioning of docked drone PBs), plus:
//   survey    ore deposits from the carrier's ore detectors (OreNet Detector PB
//             Integration "OreNet.Deposits"; OreDetectorBridge "DetectedOres" as
//             a fallback), merged into a deposit list that persists
//   dispatch  each miner gets one shaft at a time: deposits matching the chosen
//             ores, a free deposit before a shared one, an N x N shaft grid per
//             deposit handed out centre-first so no two miners share a shaft
//   unload    ore in a docked miner's cargo, drills and connector is moved into
//             the carrier's hold
//   guard     no miner leaves when its load would not fit in the hold, or would
//             push the carrier's lift below MinTWR (live: the thrusters'
//             effective thrust in the current air, the current gravity, the
//             carrier's mass with docked miners, plus every miner still out
//             coming back full)
//   pages     text status on cockpit screens / LCDs tagged [FS:Mine], [FS:Map]
//             (deposits), [FS:Miners], [FS:Cargo] (add :n for a surface index)
//
// Commands (toolbar / button panel):
//   mine <ore[,ore...]|any>   send the miners after those ores (Iron, Nickel, ...)
//   mine GPS:name:x:y:z:      add a deposit by hand and mine it
//   mine off                  send no more trips (miners out finish their load)
//   recall [miner|all]        everyone home now
//   survey                    read the ore detectors now and log what is there
//   deposits clear            forget all deposits
//   mintwr <x>                lift guard (default 1.4)
//   provision   forget <callsign>   reload
//
// Miner contract (Proto.O_MINE): a = shaft axis point at the deposit's depth,
// b = up (unit) * metres to dig below it, c = (shaft index, shafts, max depth
// below the surface), id = deposit id; all world coordinates. The miner sets
// F_FULL when its cargo is full (the hub sends it to a pad) and F_DONE when the
// shaft is finished with room left (the hub sends the next shaft), and clears
// both when it starts a new order.
// ============================================================================
const string SEC = "Mine";

Net net;
Cfg cfg;
readonly Perf perf = new Perf();
IMyBroadcastListener helloL, cmdL;
IMyUnicastListener uni;
IMyShipController refCtl;
IMyTerminalBlock refBlk;

readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();
readonly List<IMyShipConnector> dockConns = new List<IMyShipConnector>();
readonly List<IMyThrust> thr = new List<IMyThrust>();
readonly List<IMyCargoContainer> holds = new List<IMyCargoContainer>();
readonly List<IMyOreDetector> dets = new List<IMyOreDetector>();
readonly List<IMyProgrammableBlock> pbs = new List<IMyProgrammableBlock>();
readonly List<MyInventoryItem> items = new List<MyInventoryItem>();
readonly Dictionary<long, Drone> reg = new Dictionary<long, Drone>();
readonly List<Drone> sorted = new List<Drone>();
readonly Docks docks = new Docks();
readonly List<string> evText = new List<string>();
readonly List<double> evT = new List<double>();
readonly StringBuilder sb = new StringBuilder();
readonly List<IMyTextSurface>[] pages = { new List<IMyTextSurface>(), new List<IMyTextSurface>(), new List<IMyTextSurface>(), new List<IMyTextSurface>() };
readonly string[] pageTags = { "FS:Mine", "FS:Map", "FS:Miners", "FS:Cargo" };

// deposits
class Dep { public int Id, Next, Miss; public string Ore = ""; public Vector3D P, Up; public double Vol, Seen; public bool Gone, Manual; }
readonly List<Dep> deps = new List<Dep>();
readonly List<Vector3D> grid = new List<Vector3D>();   // shaft grid offsets (cells), centre first
readonly Dictionary<string, double> oreAmt = new Dictionary<string, double>();
readonly Dictionary<string, int> found = new Dictionary<string, int>();   // new deposits this survey, by ore
// a docked miner's inventories (cargo, drills, connectors), per pad, refreshed when the miner changes
class PadInv { public long Grid; public double T; public readonly List<IMyTerminalBlock> L = new List<IMyTerminalBlock>(); }
readonly Dictionary<long, PadInv> padInv = new Dictionary<long, PadInv>();

// --- config
int netId, shaftN;
string key = "", dockTag = "", ctlTag = "", perfMode = "NORMAL", ores = "ANY", srcName = "none";
double lostAfter, minTwr, minerDry, minerLoad, minerM3, launchBatt, launchInterval, shaftSpacing, digBelow, maxDepth, detRange, mergeDist, rtbBatt;
bool autoProv, mining, bootLocked, fullLogged;

// --- persistent
int epoch, callCtr, depCtr;
long ctr;

// --- runtime
double T, nextNav, nextTask, nextScan, nextProv, nextEcho, nextSurvey, nextUnload, nextLaunch, nextPage, nextOre;
bool havePos, moving;
Vector3D lastPos, vel, angVel;
MatrixD M = MatrixD.Identity;
string echo = "", warn = "", guardWhy = "";
double liftKg, massKg, holdFree, holdMax, twrNow;
int out_, docked_, unloading;

public Program()
{
    net = new Net(IGC);
    uni = IGC.UnicastListener;
    LoadConfig();
    Load();
    Scan();
    UpdateFrame(0);
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    Log("Mine Core online, net " + netId);
}

public void Save()
{
    var ini = new MyIni();
    ini.Set("hub", "epoch", epoch); ini.Set("hub", "ctr", ctr); ini.Set("hub", "calls", callCtr); ini.Set("hub", "deps", depCtr);
    ini.Set("hub", "mining", mining); ini.Set("hub", "ores", ores);
    foreach (var d in reg.Values)
    {
        string s = "d" + d.Addr;
        ini.Set(s, "call", d.Call); ini.Set(s, "role", d.Role); ini.Set(s, "grid", d.Grid);
        ini.Set(s, "dir", d.Dir); ini.Set(s, "id", d.Id); ini.Set(s, "conn", d.Conn); ini.Set(s, "dep", d.Dep); ini.Set(s, "shaft", d.Shaft);
        SetV(ini, s, "a", d.A); SetV(ini, s, "b", d.B); SetV(ini, s, "c", d.C);
    }
    foreach (var p in deps)
    {
        string s = "o" + p.Id;
        ini.Set(s, "ore", p.Ore); ini.Set(s, "vol", p.Vol); ini.Set(s, "next", p.Next); ini.Set(s, "gone", p.Gone); ini.Set(s, "man", p.Manual);
        SetV(ini, s, "p", p.P); SetV(ini, s, "u", p.Up);
    }
    Storage = ini.ToString();
}

void Load()
{
    var ini = new MyIni();
    if (!string.IsNullOrEmpty(Storage) && ini.TryParse(Storage))
    {
        epoch = ini.Get("hub", "epoch").ToInt32(); ctr = ini.Get("hub", "ctr").ToInt64(); callCtr = ini.Get("hub", "calls").ToInt32();
        depCtr = ini.Get("hub", "deps").ToInt32(); mining = ini.Get("hub", "mining").ToBoolean(); ores = ini.Get("hub", "ores").ToString("ANY");
        var secs = new List<string>();
        ini.GetSections(secs);
        foreach (var s in secs)
        {
            long addr;
            if (s.Length > 1 && s[0] == 'd' && long.TryParse(s.Substring(1), out addr))
            {
                var d = new Drone { Addr = addr, Restored = true };
                d.Call = ini.Get(s, "call").ToString(""); d.Role = ini.Get(s, "role").ToString(""); d.Grid = ini.Get(s, "grid").ToString("");
                d.Dir = ini.Get(s, "dir").ToInt32(); d.Id = ini.Get(s, "id").ToInt64(); d.Conn = ini.Get(s, "conn").ToInt64();
                d.Dep = ini.Get(s, "dep").ToInt32(-1); d.Shaft = ini.Get(s, "shaft").ToInt32(-1);
                d.A = GetV(ini, s, "a"); d.B = GetV(ini, s, "b"); d.C = GetV(ini, s, "c");
                if (d.Conn != 0) docks.Res[d.Conn] = d.Addr;
                reg[addr] = d;
            }
            else if (s.Length > 1 && s[0] == 'o' && long.TryParse(s.Substring(1), out addr))
            {
                var p = new Dep { Id = (int)addr, Seen = -1e9 };
                p.Ore = ini.Get(s, "ore").ToString(""); p.Vol = ini.Get(s, "vol").ToDouble(); p.Next = ini.Get(s, "next").ToInt32();
                p.Gone = ini.Get(s, "gone").ToBoolean(); p.Manual = ini.Get(s, "man").ToBoolean();
                p.P = GetV(ini, s, "p"); p.Up = GetV(ini, s, "u");
                deps.Add(p);
            }
        }
    }
    if (epoch <= 0) epoch = new Random().Next(1, int.MaxValue);
}

static void SetV(MyIni ini, string s, string k, Vector3D v) { ini.Set(s, k + "x", v.X); ini.Set(s, k + "y", v.Y); ini.Set(s, k + "z", v.Z); }
static Vector3D GetV(MyIni ini, string s, string k) { return new Vector3D(ini.Get(s, k + "x").ToDouble(), ini.Get(s, k + "y").ToDouble(), ini.Get(s, k + "z").ToDouble()); }

void LoadConfig()
{
    cfg = new Cfg(Me, SEC);
    netId = cfg.I("NetId", 2);                       // the Fleet hub defaults to 1: separate networks
    key = cfg.S("Key", "change-me");
    dockTag = cfg.S("DockTag", "[Dock]");
    ctlTag = cfg.S("ControllerTag", "");
    lostAfter = cfg.D("LostAfter", 10);
    autoProv = cfg.B("AutoProvision", true);
    rtbBatt = cfg.D("RtbBattery", 0.25);
    launchBatt = cfg.D("LaunchBattery", 0.7);        // a docked miner leaves for its next trip above this charge
    launchInterval = Math.Max(1, cfg.D("LaunchInterval", 3));
    minTwr = Math.Max(1.05, cfg.D("MinTWR", 1.4));   // lift guard: carrier lift / weight never below this
    minerDry = cfg.D("MinerMass", 40000);            // kg, a miner without cargo (SCAM Min3r 69R: ~40 t)
    minerLoad = cfg.D("MinerLoad", 85000);           // kg of ore a miner brings back full (31 m3 x 2.7 t/m3)
    minerM3 = cfg.D("MinerCargo", 31.25);            // m3 a miner brings back full
    shaftN = Math.Max(1, Math.Min(7, cfg.I("ShaftGrid", 3)));   // N x N shafts per deposit
    shaftSpacing = cfg.D("ShaftSpacing", 5);         // m between shaft centres (SCAM drill head ~5.5 m)
    digBelow = cfg.D("DigBelow", 8);                 // m to dig past the deposit's centre
    maxDepth = cfg.D("MaxDepth", 60);                // m below the surface, hard cap per shaft
    detRange = cfg.D("DetectorRange", 150);          // m, the ore detector's range (vanilla large: 150)
    mergeDist = cfg.D("MergeDistance", 16);          // m: deposit reports of one ore this close are one deposit
    perfMode = cfg.S("Perf", "Normal").ToUpper();
    cfg.Save();

    net.Configure(netId, key);
    if (helloL != null) IGC.DisableBroadcastListener(helloL);
    if (cmdL != null) IGC.DisableBroadcastListener(cmdL);
    helloL = IGC.RegisterBroadcastListener(net.THello);
    cmdL = IGC.RegisterBroadcastListener(net.TCmd);

    // shaft grid: cells of an N x N square, nearest the centre first
    grid.Clear();
    double h = (shaftN - 1) / 2.0;
    for (int i = 0; i < shaftN; i++) for (int j = 0; j < shaftN; j++) grid.Add(new Vector3D(i - h, 0, j - h));
    grid.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
}

void Scan()
{
    tmp.Clear();
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me));
    dockConns.Clear(); thr.Clear(); holds.Clear(); dets.Clear();
    foreach (var l in pages) l.Clear();
    refCtl = null;
    IMyShipController any = null, main = null;
    bool antennaOk = false;
    foreach (var b in tmp)
    {
        var c = b as IMyShipConnector; if (c != null) { if (b.CustomName.Contains(dockTag)) dockConns.Add(c); continue; }
        var t = b as IMyThrust; if (t != null) { thr.Add(t); continue; }
        var h = b as IMyCargoContainer; if (h != null) { holds.Add(h); continue; }
        var o = b as IMyOreDetector; if (o != null) { dets.Add(o); continue; }
        var s = b as IMyShipController;
        if (s != null)
        {
            if (ctlTag.Length > 0 && b.CustomName.Contains(ctlTag)) refCtl = s;
            if (s.IsMainCockpit) main = s;
            if (any == null) any = s;
        }
        var a = b as IMyRadioAntenna; if (a != null && a.IsWorking && a.EnableBroadcasting) antennaOk = true;
        var p = b as IMyTextSurfaceProvider;
        if (p != null && b.CustomName.Contains("[FS:"))
            for (int i = 0; i < pageTags.Length; i++)
            {
                if (!b.CustomName.Contains("[" + pageTags[i])) continue;
                int idx = TagIndex(b.CustomName, pageTags[i]);
                if (idx < p.SurfaceCount) { var sf = p.GetSurface(idx); sf.ContentType = ContentType.TEXT_AND_IMAGE; sf.Font = "Monospace"; pages[i].Add(sf); }
            }
    }
    if (refCtl == null) refCtl = main ?? any;
    refBlk = refCtl != null ? (IMyTerminalBlock)refCtl : Me;
    docks.Bind(dockConns);

    var w = new StringBuilder();
    if (cfg.Error.Length > 0) w.Append(cfg.Error).Append('\n');
    if (refCtl == null) w.Append("WARN: no cockpit/remote: no lift guard, frame uses this PB.\n");
    if (dockConns.Count == 0) w.Append("WARN: no connectors named with " + dockTag + ": no miner pads.\n");
    if (holds.Count == 0) w.Append("WARN: no cargo containers: nowhere to unload.\n");
    if (dets.Count == 0) w.Append("WARN: no ore detector: use mine GPS:...\n");
    if (!antennaOk) w.Append("WARN: no working broadcasting antenna: miners cannot hear the carrier.\n");
    if (key == "change-me") w.Append("NOTE: change Key in Custom Data before multiplayer.\n");
    warn = w.ToString();
}

static int TagIndex(string name, string tag)
{
    int i = name.IndexOf("[" + tag);
    int j = i + tag.Length + 1;
    if (i >= 0 && j < name.Length && name[j] == ':')
    {
        int e = name.IndexOf(']', j);
        int v;
        if (e > j + 1 && int.TryParse(name.Substring(j + 1, e - j - 1), out v)) return v;
    }
    return 0;
}

public void Main(string arg, UpdateType src)
{
    double dt = Runtime.TimeSinceLastRun.TotalSeconds;
    T += dt;
    perf.Tick(Runtime.LastRunTimeMs, T);
    if (!string.IsNullOrEmpty(arg)) Command(arg.Trim());
    if ((src & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0) Tick(dt);
    Echo(echo);
}

void Tick(double dt)
{
    if (T >= nextScan) { Scan(); nextScan = T + 15; }
    UpdateFrame(dt);
    Receive();
    if (T >= nextNav) { SendNav(); nextNav = T + (moving ? (perfMode == "LOW" ? 0.16 : 0.1) : 1); }
    if (T >= nextTask) { Tasking(); nextTask = T + 0.5; }
    if (T >= nextSurvey) { Survey(false); nextSurvey = T + 4; }
    if (T >= nextUnload) { Unload(); nextUnload = T + 0.5; }
    if (autoProv && T >= nextProv) { Provision(false); nextProv = T + 10; }
    if (T >= nextPage) { Pages(); nextPage = T + 1; }
    if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
    UpdateFrequency f = moving || perfMode == "HIGH" ? UpdateFrequency.Update1 : UpdateFrequency.Update10;
    if (Runtime.UpdateFrequency != f) Runtime.UpdateFrequency = f;
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
    net.SendNav(M.Translation, vel, M.Forward, M.Up, angVel, g.WorldVolume.Center, g.WorldVolume.Radius, Proto.MODE_CARRIER, Proto.ALERT_GREEN, 0, 0,
                (bmin + bmax) / 2, (bmax - bmin) / 2);
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
        if (!net.ReadHello(m, out role, out caps, out acc, out spd, out gname)) continue;
        var d = GetOrAdd(m.Source, role);
        d.Caps = caps; d.MaxAccel = acc; d.MaxSpeed = spd; d.Grid = gname;
        Register(d);
    }
    while (uni.HasPendingMessage)
    {
        var m = uni.AcceptMessage();
        MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>> s;
        if (!net.ReadStatus(m, out s)) continue;
        Drone d;
        if (!reg.TryGetValue(m.Source, out d)) { d = GetOrAdd(m.Source, s.Item2.Item1); Register(d); }
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
    string r = (role ?? "MINER").ToUpper();
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
    ClearJob(d);
    d.Dir = Proto.O_DOCK; d.Seq = 0;
    var c = docks.Reserve(d);
    if (c != null) SendDock(d, c);
    else Log(d.Call + " waiting for a free pad");
}

void SendDock(Drone d, IMyShipConnector c)
{
    MatrixD cm = c.WorldMatrix;
    Issue(d, Proto.O_DOCK, HubFrame.ToLocal(M, cm.Translation), HubFrame.DirToLocal(M, cm.Forward), HubFrame.DirToLocal(M, cm.Up), c.EntityId);
}

bool IsMiner(Drone d) { return (d.Role ?? "").ToUpper() == "MINER"; }

// ---------------------------------------------------------------- tasking (2 Hz)
void Tasking()
{
    sorted.Clear();
    foreach (var d in reg.Values) sorted.Add(d);
    sorted.Sort((x, y) => string.Compare(x.Call, y.Call, StringComparison.Ordinal));
    if (!bootLocked && T >= 2)
    {
        // pre-docked blueprints: lock every pad with a miner sitting on it, once
        foreach (var c in docks.List) if (!c.Closed && c.Status == MyShipConnectorStatus.Connectable) c.Connect();
        bootLocked = true; nextLaunch = T + 5;
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
    Guard();
    Dispatch();
}

// ---------------------------------------------------------------- lift & hold guard
// liftKg: what the thrusters pointing up can hold at MinTWR right now (effective thrust in this air)
void Guard()
{
    Vector3D g = refCtl != null ? refCtl.GetNaturalGravity() : Vector3D.Zero;
    double gl = g.Length();
    massKg = refCtl != null ? refCtl.CalculateShipMass().PhysicalMass : 0;
    double f = 0;
    if (gl > 0.1)
    {
        Vector3D up = -g / gl;
        foreach (var t in thr) if (!t.Closed && t.IsWorking) f += t.MaxEffectiveThrust * Math.Max(0, Vector3D.Dot(t.WorldMatrix.Backward, up));
    }
    liftKg = gl > 0.1 ? f / (gl * minTwr) : 1e12;
    twrNow = gl > 0.1 && massKg > 0 ? f / (massKg * gl) : 99;
    holdFree = holdMax = 0;
    foreach (var h in holds)
    {
        if (h.Closed) continue;
        var inv = h.GetInventory(0);
        holdMax += (double)(float)inv.MaxVolume; holdFree += (double)((float)inv.MaxVolume - (float)inv.CurrentVolume);
    }
    Room();
}

int Airborne() { int n = 0; foreach (var d in sorted) if (IsMiner(d) && !d.LostFlag && !d.Docked && (d.Dir == Proto.O_MINE || d.Dir == Proto.O_DOCK)) n++; return n; }

// can one more miner go out? (everyone out comes back full; docked miners' mass is already in massKg)
bool Room()
{
    int n = Airborne();
    if (massKg + n * (minerDry + minerLoad) + minerLoad > liftKg) { guardWhy = "LIFT: carrier would drop below " + Util.Num(minTwr) + " g"; return false; }
    if (holdFree < (n + 1) * minerM3) { guardWhy = "HOLD FULL"; return false; }
    guardWhy = "";
    return true;
}

int LoadsLeft()
{
    int n = Airborne();
    double byLift = (liftKg - massKg - n * (minerDry + minerLoad)) / Math.Max(1, minerLoad), byHold = holdFree / Math.Max(0.1, minerM3) - n;
    return (int)Math.Max(0, Math.Min(byLift, byHold));
}

// ---------------------------------------------------------------- survey
void Survey(bool verbose)
{
    int seen = 0;
    found.Clear();
    foreach (var det in dets)
    {
        if (det.Closed || !det.IsWorking) continue;
        try
        {
            var p = det.GetProperty("OreNet.Deposits");
            var l = p == null ? null : p.As<IReadOnlyList<MyTuple<string, Vector3D, float>>>();
            if (l != null)
            {
                srcName = "OreNet";
                foreach (var e in l.GetValue(det)) { Found(e.Item1, e.Item2, e.Item3); seen++; }
                continue;
            }
            var q = det.GetProperty("DetectedOres");
            var ql = q == null ? null : q.As<string>();
            if (ql != null)
            {
                srcName = "OreDetectorBridge";
                foreach (var r in (ql.GetValue(det) ?? "").Split(';'))
                {
                    var f = r.Split('|');
                    double x, y, z;
                    if (f.Length == 4 && Util.ParseD(f[1], out x) && Util.ParseD(f[2], out y) && Util.ParseD(f[3], out z)) { Found(f[0], new Vector3D(x, y, z), 0); seen++; }
                }
            }
        }
        catch { }
    }
    // a deposit well inside a working detector's range that stops being reported is mined out
    foreach (var p in deps)
    {
        if (p.Gone || p.Manual || p.Seen == T) continue;
        bool inRange = false;
        foreach (var det in dets) if (!det.Closed && det.IsWorking && (det.GetPosition() - p.P).Length() < detRange * 0.8) inRange = true;
        if (inRange && srcName != "none" && ++p.Miss >= 5) { p.Gone = true; Log(p.Ore + " deposit " + p.Id + " mined out"); }
    }
    if (found.Count > 0)
    {
        var f = new StringBuilder();
        foreach (var kv in found) f.Append(f.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
        Log("Found " + f + " (" + srcName + ")");
    }
    if (verbose)
    {
        int live = 0; foreach (var p in deps) if (!p.Gone) live++;
        Log("Survey (" + srcName + "): " + seen + " reports, " + live + " deposits");
    }
}

void Found(string ore, Vector3D pos, double vol)
{
    if (string.IsNullOrEmpty(ore) || ore == "Stone") return;
    foreach (var p in deps)
        if (p.Ore == ore && (p.P - pos).Length() < mergeDist)
        {
            if (vol > 0) p.Vol = vol;
            p.Seen = T; p.Miss = 0;
            return;
        }
    if (deps.Count >= 48)
    {
        // full: drop the farthest deposit nobody is mining
        Dep far = null; double fd = -1;
        foreach (var p in deps) { double dd = (p.P - M.Translation).Length() + (p.Gone ? 1e6 : 0); if (MinersOn(p) == 0 && dd > fd) { fd = dd; far = p; } }
        if (far == null) return;
        deps.Remove(far);
    }
    deps.Add(new Dep { Id = ++depCtr, Ore = ore, P = pos, Vol = vol, Seen = T });
    int c; found.TryGetValue(ore, out c); found[ore] = c + 1;
}

int MinersOn(Dep p) { int n = 0; foreach (var d in reg.Values) if (d.Dir == Proto.O_MINE && d.Dep == p.Id && !d.LostFlag) n++; return n; }

bool Wanted(Dep p)
{
    if (p.Gone || p.Next >= grid.Count) return false;
    if (ores == "ANY") return p.Ore != "Ice" || p.Manual;
    foreach (var o in ores.Split(',')) if (o.Length > 0 && p.Ore.ToUpper().StartsWith(o)) return true;
    return p.Manual;
}

// ---------------------------------------------------------------- dispatch
void Dispatch()
{
    foreach (var d in sorted)
    {
        if (!IsMiner(d) || d.LostFlag) continue;
        if (d.Dir == Proto.O_MINE)
        {
            if ((d.Flags & Proto.F_FULL) != 0) { Log(d.Call + " full: returning"); OrderDock(d); continue; }
            if ((d.Flags & Proto.F_DONE) != 0 && d.Acked)
            {
                if (!mining || !Assign(d)) { Log(d.Call + " shaft done: returning"); OrderDock(d); }
            }
            continue;
        }
        if (!mining || !bootLocked || !d.Docked || T < nextLaunch) continue;
        if (d.Cargo > 0.02 || d.Batt < launchBatt) continue;     // still unloading / charging
        if (!Room())
        {
            if (!fullLogged) { fullLogged = true; Log("CARRIER FULL (" + guardWhy + "): no more trips"); }
            continue;
        }
        fullLogged = false;
        if (Assign(d)) nextLaunch = T + launchInterval;
        else { mining = false; Log("No deposits left for " + ores + ": mining stopped"); }
    }
}

// next shaft for this miner: a deposit nobody mines before a shared one, then the biggest, then the nearest
bool Assign(Drone d)
{
    ClearJob(d);
    Dep best = null; double bs = double.MaxValue;
    foreach (var p in deps)
    {
        if (!Wanted(p)) continue;
        double s = MinersOn(p) * 1e7 - p.Vol + (p.P - M.Translation).Length();
        if (s < bs) { bs = s; best = p; }
    }
    if (best == null) return false;
    if (best.Up.LengthSquared() < 0.5) best.Up = UpVec();
    Vector3D e1 = Util.Perp(best.Up), e2 = Vector3D.Cross(best.Up, e1), g = grid[best.Next];
    Vector3D a = best.P + (e1 * g.X + e2 * g.Z) * shaftSpacing;
    d.Dep = best.Id; d.Shaft = best.Next++;
    Issue(d, Proto.O_MINE, a, best.Up * digBelow, new Vector3D(d.Shaft, grid.Count, maxDepth), best.Id);
    Log(d.Call + " -> " + best.Ore + " #" + best.Id + " shaft " + (d.Shaft + 1) + "/" + grid.Count + ", " + Util.Dist((a - M.Translation).Length()));
    return true;
}

void ClearJob(Drone d) { d.Dep = -1; d.Shaft = -1; }

// ---------------------------------------------------------------- unload (2 Hz)
// ore from a docked miner's cargo, drills and connectors into the carrier's hold, a few stacks per run
void Unload()
{
    int budget = 6;
    unloading = 0;
    foreach (var pad in docks.List)
    {
        if (budget <= 0) break;
        if (pad.Closed || pad.Status != MyShipConnectorStatus.Connected || pad.OtherConnector == null) continue;
        var g = pad.OtherConnector.CubeGrid;
        PadInv pi;
        if (!padInv.TryGetValue(pad.EntityId, out pi)) padInv[pad.EntityId] = pi = new PadInv();
        if (pi.Grid != g.EntityId || T - pi.T > 15)
        {
            pi.Grid = g.EntityId; pi.T = T; pi.L.Clear();
            GridTerminalSystem.GetBlocksOfType(pi.L, b => b.CubeGrid == g && b.HasInventory && (b is IMyCargoContainer || b is IMyShipDrill || b is IMyShipConnector));
        }
        bool any = false;
        foreach (var b in pi.L)
            for (int k = 0; k < b.InventoryCount && budget > 0; k++)
            {
                var src = b.GetInventory(k);
                items.Clear();
                src.GetItems(items, it => it.Type.TypeId == "MyObjectBuilder_Ore");
                foreach (var it in items)
                {
                    if (budget <= 0) break;
                    any = true;
                    foreach (var h in holds)
                    {
                        var dst = h.GetInventory(0);
                        if (h.Closed || (float)dst.MaxVolume - (float)dst.CurrentVolume < 0.05f) continue;
                        budget--;
                        if (src.TransferItemTo(dst, it)) break;
                    }
                }
            }
        if (any) unloading++;
    }
}

// ---------------------------------------------------------------- commands
void Command(string raw)
{
    string[] t = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (t.Length == 0) return;
    string v = t[0].ToLower(), a1 = t.Length > 1 ? t[1] : "";
    switch (v)
    {
        case "mine":
            {
                Vector3D p; string nm;
                if (Util.ParseGps(raw, out p, out nm))
                {
                    var n = new Dep { Id = ++depCtr, Ore = nm.Length > 0 ? nm : "GPS", P = p, Seen = T, Manual = true, Vol = 1e6 };
                    deps.Add(n);
                    ores = ores.Length == 0 ? "ANY" : ores; mining = true; fullLogged = false;
                    Log("Deposit " + n.Id + " added by hand: " + Util.Dist((p - M.Translation).Length()) + " away");
                    break;
                }
                if (a1.ToLower() == "off" || a1.ToLower() == "stop") { mining = false; Log("Mining off: no more trips (miners out finish their load)"); break; }
                if (a1.Length > 0) ores = a1.ToUpper();
                mining = true; fullLogged = false; nextLaunch = Math.Max(nextLaunch, T);
                int n2 = 0; foreach (var p2 in deps) if (Wanted(p2)) n2++;
                Log("MINE " + ores + ": " + n2 + " deposit(s) to work" + (n2 == 0 ? " (survey first, or mine GPS:...)" : ""));
            }
            break;
        case "recall": case "dock":
            {
                string tg = a1.Length > 0 ? a1 : "all";
                if (tg == "all") mining = false;
                ForTargets(tg, OrderDock);
                Log("RECALL " + tg);
            }
            break;
        case "survey": Survey(true); ListDeposits(); break;
        case "deposits":
            if (a1.ToLower() == "clear") { deps.Clear(); foreach (var d in reg.Values) if (d.Dir != Proto.O_MINE) ClearJob(d); Log("Deposits cleared"); }
            else ListDeposits();
            break;
        case "mintwr":
            {
                double x;
                if (Util.ParseD(a1, out x) && x >= 1.05) { minTwr = x; cfg.Set("MinTWR", Util.Num(x)); Log("Lift guard " + Util.Num(x) + " g"); }
                else Log("mintwr: number >= 1.05");
            }
            break;
        case "provision": Provision(true); break;
        case "forget":
            {
                Drone hit = null;
                foreach (var d in reg.Values) if (d.Call.Equals(a1, StringComparison.OrdinalIgnoreCase)) hit = d;
                if (hit == null) { Log("forget: no drone " + a1); break; }
                docks.Release(hit); reg.Remove(hit.Addr); Log("Forgot " + hit.Call);
            }
            break;
        case "refresh": case "reload": LoadConfig(); Scan(); Log("Config reloaded"); break;
        default: Log("Unknown command: " + Util.Clean(raw)); break;
    }
}

void ListDeposits()
{
    int n = 0;
    foreach (var p in deps) if (!p.Gone && n++ < 4) Log(p.Ore + " #" + p.Id + " " + (p.Vol > 0 && !p.Manual ? (int)p.Vol + "m3 " : "") + Util.Dist((p.P - M.Translation).Length()) + " " + Util.Gps(p.Ore + p.Id, p.P));
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
    if (n == 0) Log(all ? "No miners online" : "No miner matches '" + Util.Clean(tgt) + "'");
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

// ---------------------------------------------------------------- pages & echo
void Log(string s)
{
    evText.Add(Util.Clean(s)); evT.Add(T);
    if (evText.Count > 8) { evText.RemoveAt(0); evT.RemoveAt(0); }
}

string Job() { return mining ? (guardWhy.Length > 0 ? "FULL: " + guardWhy : "MINING " + ores) : "IDLE"; }

// ore in the hold by type, every few seconds
void CountOre()
{
    oreAmt.Clear();
    foreach (var h in holds)
    {
        if (h.Closed) continue;
        items.Clear();
        h.GetInventory(0).GetItems(items);
        foreach (var it in items)
        {
            double a; oreAmt.TryGetValue(it.Type.SubtypeId, out a);
            oreAmt[it.Type.SubtypeId] = a + (double)(float)it.Amount;
        }
    }
}

void Pages()
{
    if (T >= nextOre) { CountOre(); nextOre = T + 5; }
    for (int i = 0; i < pages.Length; i++)
    {
        if (pages[i].Count == 0) continue;
        sb.Clear();
        if (i == 0) Overview(sb, 4);
        else if (i == 1)
        {
            sb.Append("DEPOSITS  ").Append(srcName).Append('\n');
            int n = 0;
            foreach (var p in deps)
            {
                if (p.Gone || n++ >= 12) continue;
                sb.Append(Pad(p.Ore, 8)).Append(Pad(p.Manual ? "GPS" : ((int)p.Vol) + "m3", 7)).Append(Pad(Util.Dist((p.P - M.Translation).Length()), 7))
                  .Append(p.Next).Append('/').Append(grid.Count).Append(MinersOn(p) > 0 ? " *" + MinersOn(p) : "").Append('\n');
            }
            if (n == 0) sb.Append("none: run 'survey'\nor 'mine GPS:...'\n");
        }
        else if (i == 2)
        {
            sb.Append("MINERS  out ").Append(out_).Append("  docked ").Append(docked_).Append('\n');
            foreach (var d in sorted)
            {
                sb.Append(Pad(d.Call, 9)).Append(Pad(d.LostFlag ? "LOST" : Proto.StateName(d.State), 9)).Append(Pad(Util.Pct(d.Cargo), 5)).Append(Util.Pct(d.Batt));
                if (d.Dir == Proto.O_MINE) sb.Append(" #").Append(d.Dep).Append('/').Append(d.Shaft + 1);
                sb.Append('\n');
            }
            if (sorted.Count == 0) sb.Append("none registered\n");
        }
        else
        {
            sb.Append("CARGO\nHold ").Append(Util.Pct((float)(holdMax > 0 ? 1 - holdFree / holdMax : 0))).Append("  ").Append((int)(holdMax - holdFree)).Append('/').Append((int)holdMax).Append(" m3\n");
            sb.Append("Lift ").Append(Util.Num(twrNow)).Append(" g  guard ").Append(Util.Num(minTwr)).Append('\n');
            sb.Append("Mass ").Append((int)(massKg / 1000)).Append(" t  max ").Append((int)(liftKg / 1000)).Append(" t\n");
            sb.Append("Loads left ").Append(LoadsLeft()).Append(unloading > 0 ? "  UNLOADING" : "").Append('\n');
            foreach (var kv in oreAmt) sb.Append(Pad(kv.Key, 10)).Append((int)(kv.Value / 1000)).Append(" t\n");
        }
        string txt = sb.ToString();
        foreach (var s in pages[i]) { s.FontSize = Math.Max(0.3f, s.SurfaceSize.X / (28 * 19.5f)); s.WriteText(txt); }
    }
}

static string Pad(string s, int n) { return s.Length >= n ? s.Substring(0, n - 1) + " " : s + new string(' ', n - s.Length); }

void Overview(StringBuilder e, int evN)
{
    int live = 0; foreach (var p in deps) if (!p.Gone) live++;
    e.Append("MINE CORE  net ").Append(netId).Append('\n');
    e.Append(Job()).Append('\n');
    e.Append("Miners ").Append(out_).Append(" out, ").Append(docked_).Append(" docked\n");
    e.Append("Deposits ").Append(live).Append(" (").Append(srcName).Append(")\n");
    e.Append("Hold ").Append(Util.Pct((float)(holdMax > 0 ? 1 - holdFree / holdMax : 0))).Append("  lift ").Append(Util.Num(twrNow)).Append(" g\n");
    e.Append("Loads left ").Append(LoadsLeft()).Append(unloading > 0 ? "  UNLOADING" : "").Append('\n');
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - evN); i--) e.Append(Util.Age((int)(T - evT[i]))).Append(' ').Append(evText[i]).Append('\n');
}

void BuildEcho()
{
    var e = new StringBuilder();
    Overview(e, 0);
    e.Append("Pads ").Append(docks.Free()).Append('/').Append(docks.List.Count).Append(" free   holds ").Append(holds.Count).Append("   detectors ").Append(dets.Count).Append('\n');
    e.Append(perf.Text()).Append('\n').Append(warn);
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - 6); i--) e.Append(Util.Age((int)(T - evT[i]))).Append("  ").Append(evText[i]).Append('\n');
    echo = e.ToString();
}
