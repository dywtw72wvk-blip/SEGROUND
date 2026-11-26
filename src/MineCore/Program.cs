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
//   pickup    while the hold is filling (HaulAt) or the carrier is full, asks
//             the base's Depot for a hauler (HAUL broadcast); a hauler that
//             comes gets the [Haul] port and is loaded from the hold up to the
//             mass it says it can still lift, then told to leave
//   pages     text status on cockpit screens / LCDs tagged [FS:Mine], [FS:Map]
//             (deposits), [FS:Miners], [FS:Cargo] (add :n for a surface index)
//   menu      arrow-key command menu on [FS:Command]: menu up / down / left /
//             right / apply (the carrier's hotbar has them on slots 1-5)
//
// Commands (toolbar / button panel):
//   mine <ore[,ore...]|any>   send the miners after those ores (Iron, Nickel, ...)
//   mine GPS:name:x:y:z:      add a deposit by hand and mine it
//   mine off                  send no more trips (miners out finish their load)
//   recall [miner|all]        everyone home now
//   haul [off]                ask for a pickup now (off: stop asking)
//   survey                    read the ore detectors now and log what is there
//   deposits clear            forget all deposits
//   mintwr <x>                lift guard (default 1.4)
//   menu <up|down|left|right|apply>
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

readonly List<IMyThrust> thr = new List<IMyThrust>();
readonly List<IMyCargoContainer> holds = new List<IMyCargoContainer>();
readonly List<IMyTerminalBlock> holdB = new List<IMyTerminalBlock>();   // the hold, for ore moves
readonly List<IMyShipConnector> haulConns = new List<IMyShipConnector>();
readonly List<IMyOreDetector> dets = new List<IMyOreDetector>();
readonly List<IMyTextSurface>[] pages = { new List<IMyTextSurface>(), new List<IMyTextSurface>(), new List<IMyTextSurface>(), new List<IMyTextSurface>() };
readonly string[] pageTags = { "FS:Mine", "FS:Map", "FS:Miners", "FS:Cargo" };
readonly List<IMyTextSurface> menuSurf = new List<IMyTextSurface>();
readonly List<string> oreOpts = new List<string>();
int menuItem, menuOpt;
string menuMsg = "";
double menuMsgT = -1e9;
bool flip;

// deposits
class Dep { public int Id, Next, Miss; public string Ore = ""; public Vector3D P, Up; public double Vol, Seen; public bool Gone, Manual; }
readonly List<Dep> deps = new List<Dep>();
readonly List<Vector3D> grid = new List<Vector3D>();   // shaft grid offsets (cells), centre first
readonly Dictionary<string, double> oreAmt = new Dictionary<string, double>();
readonly Dictionary<string, int> found = new Dictionary<string, int>();   // new deposits this survey, by ore

// --- config
int shaftN;
string ores = "ANY", srcName = "none";
double minTwr, minerDry, minerLoad, minerM3, launchBatt, launchInterval, shaftSpacing, digBelow, maxDepth, detRange, mergeDist, haulAt;
bool mining, fullLogged, wasBoot;

// --- persistent
int depCtr;

// --- runtime
double nextSurvey, nextUnload, nextLaunch, nextPage, nextOre, nextHaul, nextVis;
string guardWhy = "";
double liftKg, massKg, holdFree, holdMax, twrNow;
int unloading;
// pickup: the hauler visiting (its address), the port it got, what it can lift, what it has been given
long visitor, visitPort;
double visitT, visitKg, loadedKg, loadProgT, oreKg;
bool wantHaul, forceHaul, loadedSent;

public Program()
{
    net = new Net(IGC);
    uni = IGC.UnicastListener;
    HubRole = "MINER";
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
    SaveReg(ini);
    ini.Set("hub", "deps", depCtr); ini.Set("hub", "mining", mining); ini.Set("hub", "ores", ores);
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
            if (LoadReg(ini, s)) continue;
            if (s.Length > 1 && s[0] == 'o' && long.TryParse(s.Substring(1), out addr))
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


void LoadConfig()
{
    HubConfig(SEC, 2);                               // the Fleet hub defaults to net 1: separate networks
    haulAt = cfg.D("HaulAt", 0.6);                   // ask the base for a pickup once the hold is this full
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
    cfg.Save();
    HubListen();

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
    dockConns.Clear(); thr.Clear(); holds.Clear(); holdB.Clear(); haulConns.Clear(); dets.Clear();
    foreach (var l in pages) l.Clear();
    menuSurf.Clear();
    refCtl = null;
    IMyShipController any = null, main = null;
    bool antennaOk = false;
    foreach (var b in tmp)
    {
        var c = b as IMyShipConnector; if (c != null) { if (b.CustomName.Contains(dockTag)) dockConns.Add(c); else if (b.CustomName.Contains("[Haul]")) haulConns.Add(c); continue; }
        var t = b as IMyThrust; if (t != null) { thr.Add(t); continue; }
        var h = b as IMyCargoContainer; if (h != null) { holds.Add(h); holdB.Add(h); continue; }
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
        if (p != null && HasTag(b.CustomName, "FS:Command"))
        {
            int idx = TagIndex(b.CustomName, "FS:Command");
            if (idx < p.SurfaceCount) menuSurf.Add(p.GetSurface(idx));
        }
        if (p != null && b.CustomName.Contains("[FS:"))
            for (int i = 0; i < pageTags.Length; i++)
            {
                if (!HasTag(b.CustomName, pageTags[i])) continue;
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
    if (T >= nextHaul) { Haul(); nextHaul = T + 1; }
    if (autoProv && T >= nextProv) { Provision(false); nextProv = T + 10; }
    if (T >= nextPage) { Pages(); nextPage = T + 1; }
    if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
    UpdateFrequency f = moving || perfMode == "HIGH" ? UpdateFrequency.Update1 : UpdateFrequency.Update10;
    if (Runtime.UpdateFrequency != f) Runtime.UpdateFrequency = f;
}












bool IsMiner(Drone d) { return (d.Role ?? "").ToUpper() == "MINER"; }

// ---------------------------------------------------------------- tasking (2 Hz)
void Tasking()
{
    TaskRegistry();
    if (bootLocked && !wasBoot) { wasBoot = true; nextLaunch = T + 5; }   // let pre-docked miners report in first
    Guard();
    Dispatch();
}

void OnDock(Drone d) { ClearJob(d); }

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
    unloading = 0;
    foreach (var pad in docks.List)
    {
        if (pad.Closed || pad.Status != MyShipConnectorStatus.Connected || pad.OtherConnector == null) continue;
        bool left;
        MoveOre(Docked(pad, true), holdB, 6, 1e12, out left);
        if (left) unloading++;
    }
}

// ---------------------------------------------------------------- pickup (1 Hz)
// Ask the base for a hauler while the hold is filling or the carrier is full (until it is nearly
// empty again); load the ship on a [Haul] port from the hold, up to what it says it can lift.
void Haul()
{
    double fill = holdMax > 0 ? 1 - holdFree / holdMax : 0;
    oreKg = 0; foreach (var kv in oreAmt) oreKg += kv.Value;
    if (forceHaul || fill >= haulAt || (mining && guardWhy.Length > 0)) wantHaul = true;
    else if (fill < 0.05) wantHaul = false;
    if (wantHaul && visitor == 0 && oreKg > 1000) net.SendHaul(M.Translation, holdMax - holdFree, oreKg, Me.CubeGrid.CustomName);
    if (visitor != 0 && T - visitT > 900) { Log("Hauler visit timed out"); visitor = 0; visitKg = 0; }   // nobody visiting: nothing is loaded
    foreach (var p in haulConns)
    {
        if (p.Closed || p.Status != MyShipConnectorStatus.Connected || p.OtherConnector == null) continue;
        bool left;
        double kg = MoveOre(holdB, Docked(p, false), 8, Math.Max(0, visitKg - loadedKg), out left);
        if (kg > 0) { loadedKg += kg; loadProgT = T; }
        // done: it has what it can lift, the hold is empty, or nothing has moved for 15 s (its cargo is full)
        bool done = visitKg - loadedKg < 500 || !left || T - loadProgT > 15;
        if (visitor != 0 && done && T >= nextVis)
        {
            net.SendVisit(visitor, Proto.V_LOADED, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0);
            nextVis = T + 3;
            if (!loadedSent) { loadedSent = true; Log("Hauler loaded: " + (int)(loadedKg / 1000) + " t"); }
        }
    }
}

// a hauler asking for a port, or saying it has left
void OnUnicast(MyIGCMessage m)
{
    int code; double kg;
    if (!net.ReadVReq(m, out code, out kg)) return;
    if (code == Proto.R_LEFT) { if (m.Source == visitor) { Log("Hauler away with " + (int)(loadedKg / 1000) + " t"); visitor = 0; visitKg = 0; forceHaul = false; } return; }
    if (code != Proto.R_DOCK) return;
    if (visitor != 0 && visitor != m.Source && T - visitT < 900) { net.SendVisit(m.Source, Proto.V_WAIT, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0); return; }
    if (oreKg < 1000 && visitor != m.Source) { net.SendVisit(m.Source, Proto.V_NONE, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0); return; }
    IMyShipConnector port = null;
    foreach (var c in haulConns) if (!c.Closed && c.IsFunctional && (c.Status != MyShipConnectorStatus.Connected || c.EntityId == visitPort)) { port = c; break; }
    if (port == null) { net.SendVisit(m.Source, Proto.V_WAIT, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0); return; }
    if (visitor != m.Source) { visitor = m.Source; loadedKg = 0; loadedSent = false; Log("Hauler inbound, can lift " + (int)(kg / 1000) + " t"); }
    visitKg = kg; visitT = T; visitPort = port.EntityId; loadProgT = T;
    MatrixD cm = port.WorldMatrix;
    net.SendVisit(m.Source, Proto.V_DOCK, HubFrame.ToLocal(M, cm.Translation), HubFrame.DirToLocal(M, cm.Forward), HubFrame.DirToLocal(M, cm.Up), port.EntityId);
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
        case "haul":
            forceHaul = a1.ToLower() != "off"; if (!forceHaul) wantHaul = false;
            Log(forceHaul ? "Pickup requested" : "Pickup requests off");
            break;
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
        case "menu": Menu(a1.ToLower()); break;
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



// ---------------------------------------------------------------- pages & echo

string PickupText()
{
    if (visitor != 0) return "Pickup: hauler " + (loadedSent ? "loaded " : "here ") + (int)(loadedKg / 1000) + "/" + (int)(visitKg / 1000) + " t";
    return wantHaul ? "Pickup: requested" : "Pickup: -";
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
    DrawMenu();
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
            sb.Append(PickupText()).Append('\n');
            foreach (var kv in oreAmt) sb.Append(Pad(kv.Key, 10)).Append((int)(kv.Value / 1000)).Append(" t\n");
        }
        string txt = sb.ToString();
        foreach (var s in pages[i]) { s.FontSize = Math.Max(0.3f, s.SurfaceSize.X / (28 * 19.5f)); s.WriteText(txt); }
    }
}


void Overview(StringBuilder e, int evN)
{
    int live = 0; foreach (var p in deps) if (!p.Gone) live++;
    e.Append("MINE CORE  net ").Append(netId).Append('\n');
    e.Append(Job()).Append('\n');
    e.Append("Miners ").Append(out_).Append(" out, ").Append(docked_).Append(" docked\n");
    e.Append("Deposits ").Append(live).Append(" (").Append(srcName).Append(")\n");
    e.Append("Hold ").Append(Util.Pct((float)(holdMax > 0 ? 1 - holdFree / holdMax : 0))).Append("  lift ").Append(Util.Num(twrNow)).Append(" g\n");
    e.Append("Loads left ").Append(LoadsLeft()).Append(unloading > 0 ? "  UNLOADING" : "").Append('\n');
    e.Append(PickupText()).Append('\n');
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - evN); i--) e.Append(Util.Age((int)(T - evT[i]))).Append(' ').Append(evText[i]).Append('\n');
}

void BuildEcho()
{
    var e = new StringBuilder();
    Overview(e, 0);
    e.Append("Pads ").Append(docks.Free()).Append('/').Append(docks.List.Count).Append(" free   holds ").Append(holds.Count).Append("   detectors ").Append(dets.Count).Append("   haul ports ").Append(haulConns.Count).Append('\n');
    e.Append(perf.Text()).Append('\n').Append(warn);
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - 6); i--) e.Append(Util.Age((int)(T - evT[i]))).Append("  ").Append(evText[i]).Append('\n');
    echo = e.ToString();
}

// ---------------------------------------------------------------- command menu ([FS:Command])
// up/down = item, left/right = value, apply = run it. Same look as the Fleet carrier's Defense menu.
static readonly string[] MenuNames = { "MINE", "STOP MINING", "SURVEY", "RECALL ALL", "LIFT GUARD", "CLEAR DEPOSITS" };
static readonly string[] Ores = { "ANY", "IRON", "NICKEL", "SILICON", "COBALT", "MAGNESIUM", "SILVER", "GOLD", "PLATINUM", "URANIUM", "ICE" };
static readonly string[] Guards = { "1.2", "1.3", "1.4", "1.5", "1.6", "1.8", "2.0" };
static readonly Color C_BG = new Color(4, 10, 18), C_PANEL = new Color(14, 30, 46), C_ACC = new Color(0, 170, 255), C_TXT = new Color(220, 235, 245),
                      C_DIM = new Color(110, 130, 150), C_SEL = new Color(0, 64, 104), C_GOOD = new Color(60, 220, 110), C_WARN = new Color(255, 190, 40),
                      C_BAD = new Color(255, 70, 60), C_ORANGE = new Color(255, 130, 40);

// ore choices: ANY plus the ores in the deposit list (all ores before the first survey)
void MenuOres()
{
    oreOpts.Clear(); oreOpts.Add("ANY");
    foreach (var p in deps) { string o = p.Ore.ToUpper(); if (!p.Gone && !p.Manual && !oreOpts.Contains(o)) oreOpts.Add(o); }
    if (oreOpts.Count == 1) for (int i = 1; i < Ores.Length; i++) oreOpts.Add(Ores[i]);
}

int MenuCount(int i) { return i == 0 ? oreOpts.Count : i == 4 ? Guards.Length : 1; }
string MenuOpt(int i, int o) { return i == 0 ? oreOpts[o] : i == 4 ? Guards[o] : "GO"; }

// the option matching what the carrier is doing now, so the selector starts there
int MenuNow(int i)
{
    if (i == 0) return Math.Max(0, oreOpts.IndexOf(ores));
    if (i != 4) return 0;
    int best = 0;
    for (int k = 0; k < Guards.Length; k++) { double g; Util.ParseD(Guards[k], out g); double b; Util.ParseD(Guards[best], out b); if (Math.Abs(g - minTwr) < Math.Abs(b - minTwr)) best = k; }
    return best;
}

void Menu(string a)
{
    MenuOres();
    int n = MenuNames.Length;
    if (a == "up") { menuItem = (menuItem + n - 1) % n; menuOpt = MenuNow(menuItem); }
    else if (a == "down") { menuItem = (menuItem + 1) % n; menuOpt = MenuNow(menuItem); }
    else if (a == "left" || a == "right") { int m = MenuCount(menuItem); menuOpt = (menuOpt % m + (a == "left" ? m - 1 : 1)) % m; }
    else if (a == "apply" || a == "select")
    {
        menuOpt = Math.Min(menuOpt, MenuCount(menuItem) - 1);
        string c = menuItem == 0 ? "mine " + oreOpts[menuOpt] : menuItem == 1 ? "mine off" : menuItem == 2 ? "survey"
                 : menuItem == 3 ? "recall all" : menuItem == 4 ? "mintwr " + Guards[menuOpt] : "deposits clear";
        Command(c);
        menuMsg = "sent: " + c; menuMsgT = T;
    }
    else { Log("menu: up | down | left | right | apply"); return; }
    DrawMenu();
}

static void Box(ref MySpriteDrawFrame f, float x, float y, float w, float h, Color c)
{
    f.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(x + w / 2, y + h / 2), new Vector2(w, h), c));
}

static void Txt(ref MySpriteDrawFrame f, string s, float x, float y, float scale, Color c, TextAlignment al = TextAlignment.LEFT)
{
    var sp = MySprite.CreateText(s, "White", c, scale, al);
    sp.Position = new Vector2(x, y);
    f.Add(sp);
}

void DrawMenu()
{
    if (menuSurf.Count == 0) return;
    if (oreOpts.Count == 0) MenuOres();
    flip = !flip;
    bool full = mining && guardWhy.Length > 0, fresh = T - menuMsgT < 4;
    string badge = full ? "FULL" : mining ? "MINING" : "IDLE";
    Color bc = full ? C_ORANGE : mining ? C_GOOD : C_DIM;
    string stat = "Hold " + Util.Pct((float)(holdMax > 0 ? 1 - holdFree / holdMax : 0)) + "   Lift " + Util.Num(twrNow) + "g   Loads " + LoadsLeft();
    string last = fresh ? menuMsg : evText.Count > 0 ? evText[evText.Count - 1] : "";
    foreach (var s in menuSurf)
    {
        if (s.ContentType != ContentType.SCRIPT) s.ContentType = ContentType.SCRIPT;
        if (s.Script != "") s.Script = "";
        if (s.ScriptBackgroundColor != C_BG) s.ScriptBackgroundColor = C_BG;
        Vector2 sz = s.SurfaceSize, o = (s.TextureSize - sz) / 2f;
        float W = sz.X, H = sz.Y, k = Math.Min(W / 340, H / 252), x0 = o.X, y0 = o.Y;
        var f = s.DrawFrame();
        if (flip) f.Add(new MySprite());
        Box(ref f, x0, y0, W, H, C_BG);
        Box(ref f, x0, y0, W, 38 * k, C_PANEL);
        Box(ref f, x0, y0 + 36 * k, W, 2 * k, C_ACC);
        Txt(ref f, "MINING", x0 + 8 * k, y0 + 5 * k, 0.75f * k, C_ACC);
        float bw = 90 * k;
        Box(ref f, x0 + W - bw - 6 * k, y0 + 7 * k, bw, 24 * k, bc);
        Txt(ref f, badge, x0 + W - bw / 2 - 6 * k, y0 + 9 * k, 0.55f * k, C_BG, TextAlignment.CENTER);
        float rowH = 28 * k, y = y0 + 42 * k, ts = 0.62f * k;
        for (int i = 0; i < MenuNames.Length; i++, y += rowH)
        {
            bool sel = i == menuItem, act = MenuCount(i) == 1;
            if (sel) Box(ref f, x0 + 2 * k, y, W - 4 * k, rowH - 2 * k, C_SEL);
            Txt(ref f, MenuNames[i], x0 + 10 * k, y + 2 * k, ts, i == 5 ? C_BAD : act ? C_WARN : C_ORANGE);
            string val = act ? (sel ? "[ APPLY ]" : "")
                       : sel ? "<  " + MenuOpt(i, Math.Min(menuOpt, MenuCount(i) - 1)) + "  >"
                       : i == 0 ? (mining ? ores : "OFF") : Util.Num(minTwr) + " g";
            Txt(ref f, val, x0 + W * 0.66f, y + 2 * k, ts, sel ? C_ACC : i == 0 && mining ? C_GOOD : C_DIM, TextAlignment.CENTER);
        }
        float fy = y0 + H - 44 * k;
        Box(ref f, x0, fy - 4 * k, W, 2 * k, C_PANEL);
        Txt(ref f, stat, x0 + 8 * k, fy, 0.5f * k, full ? C_ORANGE : C_TXT);
        Txt(ref f, last, x0 + 8 * k, fy + 20 * k, 0.45f * k, fresh ? C_GOOD : C_DIM);
        f.Dispose();
    }
}
