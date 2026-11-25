// ============================================================================
// FLEET SYSTEM — HUB CORE (phase 1)
// Runs on the carrier or base. Registry, Carrier/Base/Auto modes, reference
// frame broadcast, reliable orders, dock manager, formations, provisioning.
//
// Commands (toolbar / button panel / Hub UI):
//   escort|follow|launch <target>   hold <target>      goto <target> [GPS:...]
//   dock|rtb|recall <target>        undock <target>    stop <target>
//   formation <name|next>           spacing <m>        mode <auto|carrier|base|next>
//   autortb <on|off|toggle>         waypoint GPS:...   provision
//   forget <callsign>               refresh
// <target> = all | callsign | drone grid name
// ============================================================================
const string SEC = "Hub";

Net net;
Cfg cfg;
readonly Perf perf = new Perf();
IMyBroadcastListener helloL, cmdL;
IMyUnicastListener uni;
IMyShipController refCtl;
IMyTerminalBlock refBlk;

readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();
readonly List<IMyShipConnector> dockConns = new List<IMyShipConnector>();
readonly List<IMyShipConnector> allConns = new List<IMyShipConnector>();
readonly List<IMyLandingGear> gears = new List<IMyLandingGear>();
readonly List<IMyProgrammableBlock> pbs = new List<IMyProgrammableBlock>();
readonly Dictionary<long, Drone> reg = new Dictionary<long, Drone>();
readonly List<Drone> sorted = new List<Drone>();
readonly Docks docks = new Docks();
readonly List<string> evText = new List<string>();
readonly List<double> evT = new List<double>();
readonly StringBuilder sb = new StringBuilder();

// --- config (Custom Data)
int netId, cfgMode;
string key = "", dockTag = "", ctlTag = "", perfMode = "NORMAL", formC = "WEDGE", formB = "RING";
double spacing, koCfg, rtbBatt, rtbInteg, lostAfter;
bool autoRtb, autoProv, hasWp;
Vector3D wp;

// --- persistent runtime (Storage)
int epoch, callCtr;
long ctr;

// --- runtime
double T, nextNav, nextTask, nextSnap, nextScan, nextProv, nextEcho, nextMode, lastModeT, anchoredFor;
int mode = Proto.MODE_CARRIER;
bool havePos, moving;
Vector3D lastPos, vel, angVel;
MatrixD M = MatrixD.Identity;
string echo = "", warn = "";

public Program()
{
    net = new Net(IGC);
    uni = IGC.UnicastListener;
    LoadConfig();
    Load();
    Scan();
    UpdateFrame(0);
    mode = Me.CubeGrid.IsStatic || cfgMode == Proto.MODE_BASE ? Proto.MODE_BASE : Proto.MODE_CARRIER;
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
    Log("Hub online, mode " + Proto.ModeName(mode));
}

public void Save()
{
    var ini = new MyIni();
    ini.Set("hub", "epoch", epoch); ini.Set("hub", "ctr", ctr); ini.Set("hub", "calls", callCtr);
    foreach (var d in reg.Values)
    {
        string s = "d" + d.Addr;
        ini.Set(s, "call", d.Call); ini.Set(s, "role", d.Role); ini.Set(s, "grid", d.Grid);
        ini.Set(s, "dir", d.Dir); ini.Set(s, "id", d.Id); ini.Set(s, "conn", d.Conn);
        SetV(ini, s, "a", d.A); SetV(ini, s, "b", d.B); SetV(ini, s, "c", d.C);
    }
    Storage = ini.ToString();
}

void Load()
{
    var ini = new MyIni();
    if (!string.IsNullOrEmpty(Storage) && ini.TryParse(Storage))
    {
        epoch = ini.Get("hub", "epoch").ToInt32(); ctr = ini.Get("hub", "ctr").ToInt64(); callCtr = ini.Get("hub", "calls").ToInt32();
        var secs = new List<string>();
        ini.GetSections(secs);
        foreach (var s in secs)
        {
            long addr;
            if (s.Length < 2 || s[0] != 'd' || !long.TryParse(s.Substring(1), out addr)) continue;
            var d = new Drone { Addr = addr, Restored = true };
            d.Call = ini.Get(s, "call").ToString(""); d.Role = ini.Get(s, "role").ToString(""); d.Grid = ini.Get(s, "grid").ToString("");
            d.Dir = ini.Get(s, "dir").ToInt32(); d.Id = ini.Get(s, "id").ToInt64(); d.Conn = ini.Get(s, "conn").ToInt64();
            d.A = GetV(ini, s, "a"); d.B = GetV(ini, s, "b"); d.C = GetV(ini, s, "c");
            if (d.Conn != 0) docks.Res[d.Conn] = d.Addr;
            reg[addr] = d;
        }
    }
    if (epoch <= 0) epoch = new Random().Next(1, int.MaxValue);
}

static void SetV(MyIni ini, string s, string k, Vector3D v) { ini.Set(s, k + "x", v.X); ini.Set(s, k + "y", v.Y); ini.Set(s, k + "z", v.Z); }
static Vector3D GetV(MyIni ini, string s, string k) { return new Vector3D(ini.Get(s, k + "x").ToDouble(), ini.Get(s, k + "y").ToDouble(), ini.Get(s, k + "z").ToDouble()); }

void LoadConfig()
{
    cfg = new Cfg(Me, SEC);
    netId = cfg.I("NetId", 1);
    key = cfg.S("Key", "change-me");
    string m = cfg.S("Mode", "Auto").ToUpper();
    cfgMode = m == "CARRIER" ? Proto.MODE_CARRIER : m == "BASE" ? Proto.MODE_BASE : Proto.MODE_AUTO;
    formC = cfg.S("CarrierFormation", "WEDGE").ToUpper();
    formB = cfg.S("BaseFormation", "RING").ToUpper();
    if (!Formation.Valid(formC)) formC = "WEDGE";
    if (!Formation.Valid(formB)) formB = "RING";
    spacing = Math.Max(5, cfg.D("Spacing", 40));
    koCfg = cfg.D("KeepOutRadius", 0);
    dockTag = cfg.S("DockTag", "[Dock]");
    ctlTag = cfg.S("ControllerTag", "");
    autoRtb = cfg.B("AutoRTB", true);
    rtbBatt = cfg.D("RtbBattery", 0.25);
    rtbInteg = cfg.D("RtbIntegrity", 0.6);
    lostAfter = cfg.D("LostAfter", 10);
    autoProv = cfg.B("AutoProvision", true);
    perfMode = cfg.S("Perf", "Normal").ToUpper();
    string wps = cfg.S("Waypoint", "");
    string nm;
    hasWp = Util.ParseGps(wps, out wp, out nm);
    cfg.Save();

    net.Configure(netId, key);
    if (helloL != null) IGC.DisableBroadcastListener(helloL);
    if (cmdL != null) IGC.DisableBroadcastListener(cmdL);
    helloL = IGC.RegisterBroadcastListener(net.THello);
    cmdL = IGC.RegisterBroadcastListener(net.TCmd);
}

void Scan()
{
    tmp.Clear();
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me));
    dockConns.Clear(); allConns.Clear(); gears.Clear();
    refCtl = null;
    IMyShipController any = null, main = null;
    bool antennaOk = false;
    foreach (var b in tmp)
    {
        var c = b as IMyShipConnector;
        if (c != null) { allConns.Add(c); if (b.CustomName.Contains(dockTag)) dockConns.Add(c); continue; }
        var s = b as IMyShipController;
        if (s != null)
        {
            if (ctlTag.Length > 0 && b.CustomName.Contains(ctlTag)) refCtl = s;
            if (s.IsMainCockpit) main = s;
            if (any == null) any = s;
            continue;
        }
        var g = b as IMyLandingGear; if (g != null) { gears.Add(g); continue; }
        var a = b as IMyRadioAntenna; if (a != null && a.IsWorking && a.EnableBroadcasting) antennaOk = true;
    }
    if (refCtl == null) refCtl = main ?? any;
    refBlk = refCtl != null ? (IMyTerminalBlock)refCtl : Me;
    docks.Bind(dockConns);

    var w = new StringBuilder();
    if (cfg.Error.Length > 0) w.Append(cfg.Error).Append('\n');
    if (refCtl == null) w.Append("WARN: no cockpit/remote: hub frame uses this PB's orientation.\n");
    if (dockConns.Count == 0) w.Append("WARN: no connectors named with " + dockTag + ": docking disabled.\n");
    if (!antennaOk) w.Append("WARN: no working broadcasting antenna: drones cannot hear the hub.\n");
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
    if (T >= nextMode) { ResolveMode(T - lastModeT); lastModeT = T; nextMode = T + 0.25; }
    Receive();
    if (T >= nextNav) { SendNav(); nextNav = T + NavPeriod(); }
    if (T >= nextTask) { Tasking(); nextTask = T + 0.5; }
    if (T >= nextSnap) { SendSnapshot(); nextSnap = T + 0.3; }
    if (autoProv && T >= nextProv) { Provision(false); nextProv = T + 10; }
    if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
    SetFreq();
}

// ---------------------------------------------------------------- frame & mode
void UpdateFrame(double dt)
{
    M = refBlk.WorldMatrix;
    if (refCtl != null)
    {
        var v = refCtl.GetShipVelocities();
        vel = v.LinearVelocity; angVel = v.AngularVelocity;
    }
    else
    {
        Vector3D p = M.Translation;
        if (havePos && dt > 0) vel = (p - lastPos) / dt;
        lastPos = p; havePos = true; angVel = Vector3D.Zero;
    }
    moving = vel.LengthSquared() > 0.25 || angVel.LengthSquared() > 1e-4;
}

void ResolveMode(double dt)
{
    bool st = Me.CubeGrid.IsStatic;
    bool anchoredNow = vel.Length() < 0.1 && (GearLocked() || DockedToStatic());
    anchoredFor = anchoredNow ? anchoredFor + dt : 0;

    int want;
    if (cfgMode == Proto.MODE_CARRIER) want = st ? Proto.MODE_BASE : Proto.MODE_CARRIER;
    else if (cfgMode == Proto.MODE_BASE) want = Proto.MODE_BASE;
    else want = (st || anchoredFor > 30) ? Proto.MODE_BASE : Proto.MODE_CARRIER;

    if (want != mode)
    {
        mode = want;
        Log("Mode -> " + Proto.ModeName(mode) + (cfgMode == Proto.MODE_CARRIER && st ? " (grid is static)" : ""));
        foreach (var d in reg.Values) if (d.Dir == Proto.O_ESCORT) d.SlotOk = false;
        nextTask = T;
    }
}

bool GearLocked() { foreach (var g in gears) if (g.IsLocked) return true; return false; }

bool DockedToStatic()
{
    foreach (var c in allConns)
        if (c.Status == MyShipConnectorStatus.Connected && c.OtherConnector != null && c.OtherConnector.CubeGrid.IsStatic) return true;
    return false;
}

double KeepOutRadius() { return koCfg > 0 ? koCfg : Me.CubeGrid.WorldVolume.Radius; }

double NavPeriod()
{
    if (mode == Proto.MODE_BASE && !moving) return 1.0;
    return perfMode == "HIGH" ? 0.05 : perfMode == "LOW" ? 0.16 : 0.1;
}

void SetFreq()
{
    UpdateFrequency want = perfMode == "HIGH" ? UpdateFrequency.Update1
                         : perfMode == "LOW" ? UpdateFrequency.Update10
                         : (mode == Proto.MODE_CARRIER || moving ? UpdateFrequency.Update1 : UpdateFrequency.Update10);
    if (Runtime.UpdateFrequency != want) Runtime.UpdateFrequency = want;
}

void SendNav()
{
    net.SendNav(M.Translation, vel, M.Forward, M.Up, angVel, Me.CubeGrid.WorldVolume.Center, KeepOutRadius(), mode, 0);
}

// ---------------------------------------------------------------- messages
void Receive()
{
    while (helloL.HasPendingMessage)
    {
        var m = helloL.AcceptMessage();
        string role, grid; long caps; double acc, spd;
        if (!net.ReadHello(m, out role, out caps, out acc, out spd, out grid)) continue;
        var d = GetOrAdd(m.Source, role);
        d.Caps = caps; d.MaxAccel = acc; d.MaxSpeed = spd; d.Grid = grid;
        Register(d);
    }

    while (uni.HasPendingMessage)
    {
        var m = uni.AcceptMessage();
        MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, int>> s;
        if (!net.ReadStatus(m, out s)) continue;

        Drone d;
        if (!reg.TryGetValue(m.Source, out d))
        {
            d = GetOrAdd(m.Source, s.Item2.Item1);
            Register(d);
        }
        bool wasDocked = d.Docked;
        d.Role = s.Item2.Item1; d.State = s.Item2.Item2; d.Task = s.Item2.Item3; d.Ack = s.Item2.Item4; d.DockId = s.Item2.Item5;
        d.Pos = s.Item3.Item1; d.Vel = s.Item3.Item2;
        d.Batt = s.Item4.Item1; d.H2 = s.Item4.Item2; d.Cargo = s.Item4.Item3; d.Integ = s.Item4.Item4; d.Flags = s.Item4.Item5;
        d.Seen = T;

        if (d.LostFlag)
        {
            d.LostFlag = false;
            if (d.Restored) { d.Restored = false; Log(d.Call + " reconnected"); Reissue(d); }
            else Log(d.Call + " link restored");
        }
        if (!wasDocked && d.Docked) { Log(d.Call + " docked"); nextProv = T + 1; }
        if (wasDocked && !d.Docked) Log(d.Call + " undocked");
        if (d.Acked) { d.Tries = 0; d.Unresp = false; }
    }

    while (cmdL.HasPendingMessage)
    {
        var s = cmdL.AcceptMessage().Data as string;
        if (s != null) Command(s);
    }
}

Drone GetOrAdd(long addr, string role)
{
    Drone d;
    if (reg.TryGetValue(addr, out d)) return d;
    d = new Drone { Addr = addr, Role = role, Call = NewCall(role), Seen = T, LostFlag = false };
    reg[addr] = d;
    Log("New drone " + d.Call);
    return d;
}

string NewCall(string role)
{
    callCtr++;
    string r = (role ?? "DRONE").ToUpper();
    if (r.Length > 6) r = r.Substring(0, 6);
    return r + "-" + callCtr.ToString("00");
}

// ---------------------------------------------------------------- orders
long NextSeq() { ctr++; return ((long)epoch << 32) | (ctr & 0xFFFFFFFFL); }

void Register(Drone d)
{
    net.SendOrder(d.Addr, NextSeq(), Proto.O_REGISTER, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0, d.Call);
    Reissue(d);   // directive needs a newer seq than the REGISTER
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
    if (d.Dir == Proto.O_ESCORT && !d.SlotOk) { nextTask = T; return; }
    if (d.Dir == Proto.O_DOCK && d.Conn == 0) { nextTask = T; return; }
    d.Seq = NextSeq(); d.Tries = 0;
    SendOrd(d);
}

void SendOrd(Drone d)
{
    net.SendOrder(d.Addr, d.Seq, d.Dir, d.A, d.B, d.C, d.Id, "");
    d.Sent = T; d.Tries++;
}

void OrderEscort(Drone d) { d.Dir = Proto.O_ESCORT; d.SlotOk = false; d.Seq = 0; nextTask = T; }

void OrderDock(Drone d)
{
    d.Dir = Proto.O_DOCK; d.SlotOk = false; d.Seq = 0;
    var c = docks.Reserve(d);
    if (c != null) SendDock(d, c);
    else Log(d.Call + " waiting for a free dock");
}

void SendDock(Drone d, IMyShipConnector c)
{
    MatrixD cm = c.WorldMatrix;
    Issue(d, Proto.O_DOCK, HubFrame.ToLocal(M, cm.Translation), HubFrame.DirToLocal(M, cm.Forward), HubFrame.DirToLocal(M, cm.Up), c.EntityId);
}

// ---------------------------------------------------------------- tasking (2 Hz)
void Tasking()
{
    sorted.Clear();
    foreach (var d in reg.Values) sorted.Add(d);
    sorted.Sort((x, y) => string.Compare(x.Call, y.Call, StringComparison.Ordinal));

    foreach (var d in sorted)
    {
        // lost detection
        if (!d.LostFlag && T - d.Seen > lostAfter) { d.LostFlag = true; Log(d.Call + " LOST"); }
        if (d.LostFlag)
        {
            if (d.Conn != 0 && T - d.Seen > 60) docks.Release(d);
            continue;
        }

        // retries with backoff
        if (!d.Acked && T - d.Sent > Math.Min(8, 0.75 * (1 << Math.Min(d.Tries, 4))))
        {
            SendOrd(d);
            if (d.Tries > 6 && !d.Unresp) { d.Unresp = true; Log(d.Call + " not acknowledging orders"); }
        }

        // keep reservations in sync with reality
        if (d.Docked && d.DockId != 0 && docks.Get(d.DockId) != null) docks.Claim(d, d.DockId);
        else if (d.Dir != Proto.O_DOCK && d.Conn != 0 && !d.Docked) docks.Release(d);

        // auto RTB
        if (autoRtb && d.Dir != Proto.O_DOCK && !d.Docked && docks.List.Count > 0)
        {
            string why = null;
            if (d.Batt >= 0 && d.Batt < rtbBatt) why = "battery " + Util.Pct(d.Batt);
            else if (d.Integ < rtbInteg) why = "damage, hull " + Util.Pct(d.Integ);
            else if ((d.Flags & Proto.F_WANT_RTB) != 0) why = "drone request";
            if (why != null) { Log(d.Call + " auto-RTB (" + why + ")"); OrderDock(d); }
        }

        // dock queue
        if (d.Dir == Proto.O_DOCK && (d.Conn == 0 || docks.Get(d.Conn) == null))
        {
            var c = docks.Reserve(d);
            if (c != null) SendDock(d, c);
        }
    }

    // formation slots
    string form = mode == Proto.MODE_BASE ? formB : formC;
    double R = KeepOutRadius();
    Vector3D lc = HubFrame.ToLocal(M, Me.CubeGrid.WorldVolume.Center);
    int n = 0;
    foreach (var d in sorted) if (d.Dir == Proto.O_ESCORT && !d.LostFlag) n++;
    int i = 0;
    foreach (var d in sorted)
    {
        if (d.Dir != Proto.O_ESCORT || d.LostFlag) continue;
        Vector3D pat = Formation.Offset(form, i++, n, spacing, R);
        Vector3D off = lc + pat;
        Vector3D face = mode == Proto.MODE_BASE ? Formation.Outward(pat) : new Vector3D(0, 0, -1);
        if (!d.SlotOk || (off - d.A).LengthSquared() > 0.25 || (face - d.B).LengthSquared() > 0.01)
        {
            Issue(d, Proto.O_ESCORT, off, face, Vector3D.Zero, 0);
            d.SlotOk = true;
        }
    }
}

// ---------------------------------------------------------------- commands
void Command(string raw)
{
    string[] t = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (t.Length == 0) return;
    string v = t[0].ToLower();
    string tgt = t.Length > 1 ? t[1] : "all";
    switch (v)
    {
        case "escort": case "follow": case "launch":
            ForTargets(tgt, OrderEscort);
            break;
        case "hold":
            ForTargets(tgt, d => Issue(d, Proto.O_HOLD, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0));
            break;
        case "goto":
            {
                Vector3D p; string nm;
                if (!Util.ParseGps(raw, out p, out nm))
                {
                    if (!hasWp) { Log("goto: give GPS:... or set a waypoint first"); break; }
                    p = wp;
                }
                if (tgt.StartsWith("GPS:")) tgt = "all";
                ForTargets(tgt, d => Issue(d, Proto.O_GOTO, p, Vector3D.Zero, Vector3D.Zero, 0));
            }
            break;
        case "dock": case "rtb": case "recall":
            if (docks.List.Count == 0) { Log("No docks tagged " + dockTag); break; }
            ForTargets(tgt, OrderDock);
            break;
        case "undock":
            ForTargets(tgt, d => Issue(d, Proto.O_UNDOCK, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0));
            break;
        case "stop":
            ForTargets(tgt, d => Issue(d, Proto.O_STOP, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0));
            break;
        case "formation":
            {
                string cur = mode == Proto.MODE_BASE ? formB : formC;
                string f = t.Length > 1 ? t[1].ToUpper() : "NEXT";
                if (f == "NEXT") f = Formation.Next(cur);
                if (!Formation.Valid(f)) { Log("Formations: " + string.Join(", ", Formation.Names)); break; }
                if (mode == Proto.MODE_BASE) { formB = f; cfg.Set("BaseFormation", f); }
                else { formC = f; cfg.Set("CarrierFormation", f); }
                Log(Proto.ModeName(mode) + " formation " + f);
                nextTask = T;
            }
            break;
        case "spacing":
            {
                double s;
                if (t.Length > 1 && Util.ParseD(t[1], out s) && s >= 5) { spacing = s; cfg.Set("Spacing", Util.Num(s)); Log("Spacing " + (int)s + "m"); nextTask = T; }
                else Log("spacing: number >= 5");
            }
            break;
        case "mode":
            {
                string m = tgt.ToUpper();
                int nm = m == "CARRIER" ? 1 : m == "BASE" ? 2 : m == "AUTO" ? 0 : m == "NEXT" || m == "ALL" ? (cfgMode + 1) % 3 : -1;
                if (nm < 0) { Log("mode: auto | carrier | base | next"); break; }
                cfgMode = nm; anchoredFor = 0;
                cfg.Set("Mode", Proto.ModeName(cfgMode));
                Log("Mode setting " + Proto.ModeName(cfgMode));
            }
            break;
        case "autortb":
            {
                string a = tgt.ToLower();
                autoRtb = a == "on" ? true : a == "off" ? false : !autoRtb;
                cfg.Set("AutoRTB", autoRtb ? "true" : "false");
                Log("Auto-RTB " + (autoRtb ? "ON" : "OFF"));
            }
            break;
        case "waypoint":
            {
                Vector3D p; string nm;
                if (Util.ParseGps(raw, out p, out nm)) { wp = p; hasWp = true; cfg.Set("Waypoint", Util.Gps(nm, p)); Log("Waypoint set: " + Util.Clean(nm)); }
                else Log("waypoint: paste a GPS:... string");
            }
            break;
        case "provision":
            Provision(true);
            break;
        case "forget":
            {
                Drone hit = null;
                foreach (var d in reg.Values) if (d.Call.Equals(tgt, StringComparison.OrdinalIgnoreCase)) hit = d;
                if (hit == null) { Log("forget: no drone " + tgt); break; }
                docks.Release(hit); reg.Remove(hit.Addr); Log("Forgot " + hit.Call);
            }
            break;
        case "refresh": case "reload":
            LoadConfig(); Scan(); Log("Config reloaded");
            break;
        default:
            Log("Unknown command: " + Util.Clean(raw));
            break;
    }
}

void ForTargets(string tgt, Action<Drone> act)
{
    bool all = tgt.Equals("all", StringComparison.OrdinalIgnoreCase);
    int n = 0;
    foreach (var d in reg.Values)
    {
        bool hit = all ? !d.LostFlag
                       : d.Call.Equals(tgt, StringComparison.OrdinalIgnoreCase) || d.Grid.Equals(tgt, StringComparison.OrdinalIgnoreCase);
        if (hit) { act(d); n++; }
    }
    if (n == 0) Log(all ? "No drones online" : "No drone matches '" + Util.Clean(tgt) + "'");
}

// Push NetId/Key into connected (docked or merged) drone PBs and restart their config
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

// ---------------------------------------------------------------- UI snapshot & echo
// Integers only, so no locale issues. Format documented in Hub UI.
void SendSnapshot()
{
    sb.Clear();
    int online = 0;
    foreach (var d in sorted) if (!d.LostFlag) online++;
    sb.Append("H|").Append(mode).Append('|').Append(cfgMode).Append('|').Append(mode == Proto.MODE_BASE ? formB : formC)
      .Append('|').Append((int)spacing).Append('|').Append(autoRtb ? 1 : 0).Append('|').Append(docks.Free()).Append('|').Append(docks.List.Count)
      .Append('|').Append(hasWp ? 1 : 0).Append('|').Append((int)(perf.Avg * 1000)).Append('|').Append(online)
      .Append('|').Append(Util.Clean(Me.CubeGrid.CustomName)).Append('\n');
    foreach (var d in sorted)
    {
        Vector3D lp = HubFrame.ToLocal(M, d.Pos);
        sb.Append("D|").Append(d.Call).Append('|').Append(Util.Clean(d.Role)).Append('|').Append(d.State).Append('|').Append(d.Dir)
          .Append('|').Append(Util.Milli(d.Batt)).Append('|').Append(Util.Milli(d.Integ)).Append('|').Append(Util.Milli(d.Cargo)).Append('|').Append(Util.Milli(d.H2))
          .Append('|').Append((long)lp.Length()).Append('|').Append((int)(d.Vel - vel).Length()).Append('|').Append(d.Flags)
          .Append('|').Append(d.LostFlag ? (int)Math.Min(99999, Math.Max(0, T - d.Seen)) : -1).Append('|').Append(d.Unresp ? 1 : 0)
          .Append('|').Append(d.Dir == Proto.O_DOCK && d.Conn == 0 ? 1 : 0).Append('|').Append(d.Acked ? 1 : 0)
          .Append('|').Append((long)lp.X).Append('|').Append((long)lp.Y).Append('|').Append((long)lp.Z).Append('\n');
    }
    for (int i = 0; i < evText.Count; i++) sb.Append("E|").Append((int)(T - evT[i])).Append('|').Append(evText[i]).Append('\n');
    IGC.SendBroadcastMessage(net.TSnap, sb.ToString(), TransmissionDistance.CurrentConstruct);
}

void Log(string s)
{
    evText.Add(Util.Clean(s)); evT.Add(T);
    if (evText.Count > 8) { evText.RemoveAt(0); evT.RemoveAt(0); }
}

void BuildEcho()
{
    int online = 0;
    foreach (var d in reg.Values) if (!d.LostFlag) online++;
    var e = new StringBuilder();
    e.Append("FLEET HUB CORE  net ").Append(netId).Append('\n');
    e.Append("Mode: ").Append(Proto.ModeName(mode)).Append(" (setting ").Append(Proto.ModeName(cfgMode)).Append(")\n");
    e.Append("Drones: ").Append(online).Append(" online / ").Append(reg.Count).Append("   Docks free: ").Append(docks.Free()).Append('/').Append(docks.List.Count).Append('\n');
    e.Append("Formation: ").Append(mode == Proto.MODE_BASE ? formB : formC).Append(' ').Append((int)spacing).Append("m   Auto-RTB: ").Append(autoRtb ? "on" : "off").Append('\n');
    e.Append(perf.Text()).Append('\n');
    e.Append(warn);
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - 5); i--) e.Append(Util.Age((int)(T - evT[i]))).Append("  ").Append(evText[i]).Append('\n');
    echo = e.ToString();
}
