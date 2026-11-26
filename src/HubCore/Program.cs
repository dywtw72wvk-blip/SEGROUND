// ============================================================================
// FLEET SYSTEM — HUB CORE (phase 3)
// Runs on the carrier or base. Registry, Carrier/Base/Auto modes, reference
// frame broadcast, reliable orders, dock manager, formations, provisioning.
// Phase 2 defense: alert levels from the hub's own turrets/sensors and drone
// reports; automatic screen size per alert, launch/recall/charge rotation,
// patrol orbit, engage/disengage of drones' vanilla combat blocks, SAFE switch.
//
// Phase 3 logistics: drone stock target for the Factory PB, [Repair] welders and
// [Rearm] sorters run while drones need them, launch readiness (charge, ammo,
// hull), power-aware charging, carrier jump interlock.
//
// Logistics commands:
//   jump <prep|force|cancel|toggle>   stock <n>   build <n> (forwarded to Factory)
//
// Defense commands:
//   auto <target>   manual <target>   defense <on|off|toggle>
//   alert <auto|green|yellow|red>     safe <on|off|toggle>
//   scramble (launch everything, alert RED)     recall (defense off, all dock)
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
readonly List<IMyLargeTurretBase> hubTurrets = new List<IMyLargeTurretBase>();
readonly List<IMySensorBlock> alertSensors = new List<IMySensorBlock>();
readonly List<MyDetectedEntityInfo> sensed = new List<MyDetectedEntityInfo>();
readonly List<Drone> mOut = new List<Drone>(), mReady = new List<Drone>(), mIdle = new List<Drone>();
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
// defense config
bool defense;
int screenG, screenY, screenR, engageAlert;
double leash, launchBatt, rotateBatt, launchInterval, alertCooldown, patrolBase, patrolCarrier;
string alertTag = "[Alert]";
string[] defenseRoles = { "BUSTER", "DRONE" };
Vector3D wp;

// --- persistent runtime (Storage)
int epoch, callCtr;
int alertOverride = -1;   // -1 = automatic
bool safe;
int jumpState = Proto.JUMP_NONE;
long ctr;

// --- runtime
double T, nextNav, nextTask, nextSnap, nextScan, nextProv, nextEcho, nextMode, lastModeT, anchoredFor;
double nextManage, lastRed = -1e9, phase, rate;
int alert = Proto.ALERT_GREEN;
// phase 3 runtime
readonly List<IMyFunctionalBlock> repairBlocks = new List<IMyFunctionalBlock>(), rearmBlocks = new List<IMyFunctionalBlock>();
readonly List<IMyJumpDrive> jumpDrives = new List<IMyJumpDrive>();
readonly List<IMyBatteryBlock> hubBats = new List<IMyBatteryBlock>(), otherBats = new List<IMyBatteryBlock>();
readonly List<Drone> chargeOrder = new List<Drone>();
IMyBroadcastListener facL;
int targetStock, maxChargeC, maxChargeB, facSt = -1, facProg = -1, facBuilt, facDemand, charging, liveStock;
double launchInteg, launchAmmo, rtbAmmo, repairTimeout, powerReserve, nextFac, nextPower, facT = -1e9;
bool factoryInCarrier, repairOn, rearmOn, powerLimited, havePrev;
float hubReserve = -1;
string repairTag = "[Repair]", rearmTag = "[Rearm]", facMsg = "";
Vector3D prevPos;
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
    ini.Set("hub", "alertOv", alertOverride); ini.Set("hub", "safe", safe); ini.Set("hub", "jump", jumpState);
    foreach (var d in reg.Values)
    {
        string s = "d" + d.Addr;
        ini.Set(s, "call", d.Call); ini.Set(s, "role", d.Role); ini.Set(s, "grid", d.Grid);
        ini.Set(s, "dir", d.Dir); ini.Set(s, "id", d.Id); ini.Set(s, "conn", d.Conn); ini.Set(s, "man", d.Managed);
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
        alertOverride = ini.Get("hub", "alertOv").ToInt32(-1); safe = ini.Get("hub", "safe").ToBoolean();
        jumpState = ini.Get("hub", "jump").ToInt32();
        var secs = new List<string>();
        ini.GetSections(secs);
        foreach (var s in secs)
        {
            long addr;
            if (s.Length < 2 || s[0] != 'd' || !long.TryParse(s.Substring(1), out addr)) continue;
            var d = new Drone { Addr = addr, Restored = true };
            d.Call = ini.Get(s, "call").ToString(""); d.Role = ini.Get(s, "role").ToString(""); d.Grid = ini.Get(s, "grid").ToString("");
            d.Dir = ini.Get(s, "dir").ToInt32(); d.Id = ini.Get(s, "id").ToInt64(); d.Conn = ini.Get(s, "conn").ToInt64();
            d.Managed = ini.Get(s, "man").ToBoolean(IsDefense(d.Role));
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
    defense = cfg.B("Defense", true);
    defenseRoles = cfg.S("DefenseRoles", "Buster,Drone").ToUpper().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    screenG = cfg.I("ScreenGreen", 2);
    screenY = cfg.I("ScreenYellow", 4);
    screenR = cfg.I("ScreenRed", -1);
    string ea = cfg.S("EngageAlert", "RED").ToUpper();
    engageAlert = ea == "YELLOW" ? Proto.ALERT_YELLOW : ea == "NEVER" ? 99 : Proto.ALERT_RED;
    leash = cfg.D("Leash", 600);
    launchBatt = cfg.D("LaunchBattery", 0.8);
    rotateBatt = cfg.D("RotateBattery", 0.4);
    launchInterval = Math.Max(0.5, cfg.D("LaunchInterval", 1.0));
    alertCooldown = cfg.D("AlertCooldown", 60);
    alertTag = cfg.S("AlertSensorTag", "[Alert]");
    patrolBase = cfg.D("BasePatrolSpeed", 8);
    patrolCarrier = cfg.D("CarrierPatrolSpeed", 0);
    targetStock = cfg.I("TargetStock", 0);
    factoryInCarrier = cfg.B("FactoryInCarrier", false);
    repairTag = cfg.S("RepairTag", "[Repair]");
    rearmTag = cfg.S("RearmTag", "[Rearm]");
    launchInteg = cfg.D("LaunchIntegrity", 0.95);
    launchAmmo = cfg.D("LaunchAmmo", 0.5);
    rtbAmmo = cfg.D("RtbAmmo", 0.1);
    repairTimeout = cfg.D("RepairTimeout", 180);
    maxChargeC = cfg.I("CarrierMaxCharging", 2);
    maxChargeB = cfg.I("BaseMaxCharging", 0);
    powerReserve = cfg.D("PowerReserve", 0.3);
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
    if (facL != null) IGC.DisableBroadcastListener(facL);
    facL = IGC.RegisterBroadcastListener(net.TFacSt);
}

void Scan()
{
    tmp.Clear();
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me));
    dockConns.Clear(); allConns.Clear(); gears.Clear(); hubTurrets.Clear(); alertSensors.Clear();
    repairBlocks.Clear(); rearmBlocks.Clear(); jumpDrives.Clear(); hubBats.Clear();
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
        var tu = b as IMyLargeTurretBase; if (tu != null) { hubTurrets.Add(tu); continue; }
        var se = b as IMySensorBlock; if (se != null) { if (b.CustomName.Contains(alertTag)) alertSensors.Add(se); continue; }
        var jd = b as IMyJumpDrive; if (jd != null) { jumpDrives.Add(jd); continue; }
        var bt = b as IMyBatteryBlock; if (bt != null) { hubBats.Add(bt); continue; }
        var fb = b as IMyFunctionalBlock;
        if (fb != null) { if (b.CustomName.Contains(repairTag)) repairBlocks.Add(fb); else if (b.CustomName.Contains(rearmTag)) rearmBlocks.Add(fb); }
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
    double ps = mode == Proto.MODE_BASE ? patrolBase : patrolCarrier;
    rate = ps > 0 ? ps / Math.Max(10, KeepOutRadius() + spacing) : 0;
    phase = (phase + rate * dt) % (2 * Math.PI);
    if (T >= nextMode) { ResolveMode(T - lastModeT); lastModeT = T; nextMode = T + 0.25; }
    Receive();
    if (T >= nextNav) { SendNav(); nextNav = T + NavPeriod(); }
    if (T >= nextTask) { Tasking(); nextTask = T + 0.5; }
    if (T >= nextSnap) { SendSnapshot(); nextSnap = T + 0.3; }
    if (autoProv && T >= nextProv) { Provision(false); nextProv = T + 10; }
    if (T >= nextFac) { FactoryLink(); nextFac = T + 2; }
    if (T >= nextPower) { ManagePower(); nextPower = T + 5; }
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

    // a jump shows up as a >1 km position change between ticks
    if (havePrev && (M.Translation - prevPos).LengthSquared() > 1e6)
    {
        Log("Jump detected");
        if (jumpState != Proto.JUMP_NONE) { jumpState = Proto.JUMP_NONE; SetJumpDrives(true); nextManage = T + 5; Log("Jump complete: defense resumes"); }
    }
    prevPos = M.Translation; havePrev = true;
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
    net.SendNav(M.Translation, vel, M.Forward, M.Up, angVel, Me.CubeGrid.WorldVolume.Center, KeepOutRadius(), mode, alert, phase, rate);
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
        MyTuple<MyTuple<int, int, int, int, long>, MyTuple<string, int, int, long, long>, MyTuple<Vector3D, Vector3D>, MyTuple<float, float, float, float, float, int>> s;
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
        d.Batt = s.Item4.Item1; d.H2 = s.Item4.Item2; d.Cargo = s.Item4.Item3; d.Integ = s.Item4.Item4; d.Ammo = s.Item4.Item5; d.Flags = s.Item4.Item6;
        d.Seen = T;

        if (d.LostFlag)
        {
            d.LostFlag = false;
            if (d.Restored) { d.Restored = false; Log(d.Call + " reconnected"); Reissue(d); }
            else Log(d.Call + " link restored");
        }
        if (!wasDocked && d.Docked) { Log(d.Call + " docked"); nextProv = T + 1; d.DockedSince = T; nextPower = T + 1; }
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
    d = new Drone { Addr = addr, Role = role, Call = NewCall(role), Seen = T, LostFlag = false, Managed = IsDefense(role) };
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
            else if (d.Ammo >= 0 && d.Ammo < rtbAmmo) why = "ammo " + Util.Pct(d.Ammo);
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

    UpdateAlert();
    Logistics();
    Manage();

    // formation slots (c = engage flag, leash, patrol flag)
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
        bool engage = !safe && IsDefense(d.Role) && alert >= engageAlert;
        Vector3D c = new Vector3D(engage ? 1 : 0, leash, rate > 0 ? 1 : 0);
        if (!d.SlotOk || (off - d.A).LengthSquared() > 0.25 || (face - d.B).LengthSquared() > 0.01 || (c - d.C).LengthSquared() > 0.01)
        {
            Issue(d, Proto.O_ESCORT, off, face, c, 0);
            d.SlotOk = true;
        }
    }
}

// ---------------------------------------------------------------- defense
bool IsDefense(string role) { return Array.IndexOf(defenseRoles, (role ?? "").ToUpper()) >= 0; }

Action<Drone> Manual(Action<Drone> act) { return d => { d.Managed = false; act(d); }; }

// GREEN: quiet. YELLOW: [Alert] sensor sees an enemy, or RED in the last AlertCooldown s.
// RED: a hub turret has a target, or a drone reports its turrets have one.
void UpdateAlert()
{
    bool red = false, yellow = false;
    foreach (var t in hubTurrets) if (!t.Closed && t.IsWorking && t.HasTarget) { red = true; break; }
    if (!red) foreach (var d in reg.Values) if (!d.LostFlag && (d.Flags & Proto.F_CONTACT) != 0) { red = true; break; }
    foreach (var s in alertSensors)
    {
        if (yellow || s.Closed || !s.IsWorking) continue;
        sensed.Clear();
        s.DetectedEntities(sensed);
        foreach (var e in sensed) if (e.Relationship == MyRelationsBetweenPlayerAndBlock.Enemies) { yellow = true; break; }
    }
    if (red) lastRed = T;
    int a = red ? Proto.ALERT_RED : (yellow || T - lastRed < alertCooldown) ? Proto.ALERT_YELLOW : Proto.ALERT_GREEN;
    if (alertOverride >= 0) a = alertOverride;
    if (a != alert)
    {
        Log("ALERT " + Proto.AlertName(a) + (alertOverride >= 0 ? " (forced)" : ""));
        alert = a; nextManage = T;
    }
}

// Keeps the right number of managed drones on screen for the alert level,
// launches the best-charged, recalls surplus, rotates low-battery drones out.
// One action per LaunchInterval so departures from the docks are staggered.
void Manage()
{
    if (!defense || jumpState != Proto.JUMP_NONE || T < nextManage) return;
    mOut.Clear(); mReady.Clear(); mIdle.Clear();
    int total = 0;
    foreach (var d in sorted)
    {
        if (!d.Managed || d.LostFlag) continue;
        total++;
        if (d.Dir == Proto.O_ESCORT) mOut.Add(d);
        else if (d.Docked) { if (LaunchReady(d)) mReady.Add(d); }
        else if (d.Dir != Proto.O_DOCK) mIdle.Add(d);
    }
    int target = alert == Proto.ALERT_RED ? screenR : alert == Proto.ALERT_YELLOW ? screenY : screenG;
    if (target < 0 || target > total) target = total;
    double interval = alertOverride == Proto.ALERT_RED ? 0.5 : launchInterval;
    bool canDock = docks.List.Count > 0;

    if (mOut.Count < target)
    {
        Drone pick = mIdle.Count > 0 ? mIdle[0] : Best(mReady, true);
        if (pick != null) { OrderEscort(pick); Log("Launch " + pick.Call + " (" + Proto.AlertName(alert) + ")"); nextManage = T + interval; return; }
    }
    else if (mIdle.Count > 0 && canDock)
    {
        OrderDock(mIdle[0]); nextManage = T + interval; return;
    }
    if (mOut.Count > target && canDock)
    {
        Drone pick = Best(mOut, false);
        OrderDock(pick); Log("Stand down " + pick.Call); nextManage = T + interval; return;
    }
    if (mReady.Count > 0 && canDock)
    {
        Drone low = Best(mOut, false);
        if (low != null && low.Batt >= 0 && low.Batt < rotateBatt)
        {
            Drone fresh = Best(mReady, true);
            OrderEscort(fresh); OrderDock(low);
            Log("Rotate: " + fresh.Call + " out, " + low.Call + " in to charge");
            nextManage = T + interval;
        }
    }
}

// Highest (or lowest) battery drone in a list
static Drone Best(List<Drone> l, bool highest)
{
    Drone b = null;
    foreach (var d in l) if (b == null || (highest ? d.Batt > b.Batt : d.Batt < b.Batt)) b = d;
    return b;
}

// ---------------------------------------------------------------- logistics (phase 3)
bool LaunchReady(Drone d)
{
    if (d.Batt >= 0 && d.Batt < launchBatt) return false;
    bool waited = T - d.DockedSince > repairTimeout;   // never strand a drone that can't be fully fixed
    if (d.Integ < launchInteg && repairBlocks.Count > 0 && !waited) return false;
    if (d.Ammo >= 0 && d.Ammo < launchAmmo && rearmBlocks.Count > 0 && !waited) return false;
    return true;
}

void SetAll(List<IMyFunctionalBlock> l, bool on) { foreach (var b in l) if (!b.Closed && b.Enabled != on) b.Enabled = on; }

void SetJumpDrives(bool on) { foreach (var j in jumpDrives) if (!j.Closed && j.Enabled != on) j.Enabled = on; }

// repair/rearm on demand + jump interlock (2 Hz from Tasking)
void Logistics()
{
    bool dmg = false, any = false, allDocked = true;
    foreach (var d in reg.Values)
    {
        if (d.LostFlag) continue;
        if (d.Docked) { any = true; if (d.Integ < 0.995) dmg = true; }
        else allDocked = false;
    }
    if (dmg != repairOn) { repairOn = dmg; if (repairBlocks.Count > 0) Log(dmg ? "Repair bay ON" : "Repair bay idle"); }
    rearmOn = any;
    SetAll(repairBlocks, repairOn);
    SetAll(rearmBlocks, rearmOn);

    if (jumpState == Proto.JUMP_PREP)
    {
        SetJumpDrives(false);
        if (allDocked) { jumpState = Proto.JUMP_READY; SetJumpDrives(true); Log("JUMP READY: all drones docked"); }
    }
}

// Tell the Factory PB how many drones to build; read its status
void FactoryLink()
{
    liveStock = 0;
    foreach (var d in reg.Values) if (IsDefense(d.Role) && (!d.LostFlag || T - d.Seen < 120)) liveStock++;
    bool allowed = targetStock > 0 && (mode == Proto.MODE_BASE || factoryInCarrier) && jumpState == Proto.JUMP_NONE && T > 30;
    IGC.SendBroadcastMessage(net.TFac, allowed ? Math.Max(0, targetStock - liveStock) : 0, TransmissionDistance.CurrentConstruct);

    while (facL.HasPendingMessage)
    {
        var st = facL.AcceptMessage().Data as string;
        if (st == null) continue;
        var f = st.Split('|');
        if (f.Length < 5) continue;
        int v;
        facSt = int.TryParse(f[0], out v) ? v : -1; facProg = int.TryParse(f[1], out v) ? v : -1;
        facBuilt = int.TryParse(f[2], out v) ? v : 0; facDemand = int.TryParse(f[3], out v) ? v : 0;
        if (f[4] != facMsg && f[4].Length > 0) Log("Factory: " + f[4]);
        facMsg = f[4]; facT = T;
    }
}

// Limit how many docked drones charge at once (carrier power budget) and stop
// charging entirely while the hub's own batteries are below PowerReserve.
// Held drones get their batteries switched off and run on hub power; drones
// re-enable their batteries themselves before undocking.
void ManagePower()
{
    double cur = 0, max = 0;
    foreach (var b in hubBats) if (!b.Closed && b.IsFunctional) { cur += b.CurrentStoredPower; max += b.MaxStoredPower; }
    hubReserve = max > 0 ? (float)(cur / max) : -1;
    bool low = hubReserve >= 0 && hubReserve < powerReserve;
    int maxC = mode == Proto.MODE_BASE ? maxChargeB : maxChargeC;
    if (maxC <= 0 && !low && !powerLimited) { charging = -1; return; }

    otherBats.Clear();
    GridTerminalSystem.GetBlocksOfType(otherBats, b => !b.IsSameConstructAs(Me));
    chargeOrder.Clear();
    foreach (var d in reg.Values) if (!d.LostFlag && d.Docked && d.DockId != 0) chargeOrder.Add(d);
    chargeOrder.Sort((x, y) => y.Batt.CompareTo(x.Batt));   // fullest first: gets drones ready soonest
    int allowed = low ? 0 : (maxC <= 0 ? int.MaxValue : maxC);
    charging = 0;
    bool limited = false;
    foreach (var d in chargeOrder)
    {
        IMyCubeGrid g = null;
        foreach (var c in allConns)
            if (c.EntityId == d.DockId && c.Status == MyShipConnectorStatus.Connected && c.OtherConnector != null) { g = c.OtherConnector.CubeGrid; break; }
        if (g == null) continue;
        bool full = d.Batt < 0 || d.Batt >= 0.99f;
        bool on = full || charging < allowed;
        if (!full && on) charging++;
        if (!on) limited = true;
        foreach (var b in otherBats)
        {
            if (b.CubeGrid != g || b.Closed) continue;
            if (b.Enabled != on) b.Enabled = on;
            if (on && b.ChargeMode != ChargeMode.Recharge) b.ChargeMode = ChargeMode.Recharge;
        }
    }
    if (limited != powerLimited) Log(limited ? (low ? "Hub power low: drone charging paused" : "Charging limited to " + allowed + " drone(s)") : "Drone charging unrestricted");
    powerLimited = limited;
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
            ForTargets(tgt, Manual(OrderEscort));
            break;
        case "hold":
            ForTargets(tgt, Manual(d => Issue(d, Proto.O_HOLD, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0)));
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
                ForTargets(tgt, Manual(d => Issue(d, Proto.O_GOTO, p, Vector3D.Zero, Vector3D.Zero, 0)));
            }
            break;
        case "dock": case "rtb":
            if (docks.List.Count == 0) { Log("No docks tagged " + dockTag); break; }
            ForTargets(tgt, Manual(OrderDock));
            break;
        case "undock":
            ForTargets(tgt, Manual(d => Issue(d, Proto.O_UNDOCK, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0)));
            break;
        case "stop":
            ForTargets(tgt, Manual(d => Issue(d, Proto.O_STOP, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, 0)));
            break;
        case "recall":
            defense = false; cfg.Set("Defense", "false"); alertOverride = -1;
            Log("RECALL: defense paused, all drones docking");
            if (docks.List.Count > 0) ForTargets("all", OrderDock);
            break;
        case "scramble":
            defense = true; cfg.Set("Defense", "true"); alertOverride = Proto.ALERT_RED;
            foreach (var d in reg.Values) if (IsDefense(d.Role)) d.Managed = true;
            Log("SCRAMBLE: launching all defense drones (alert RED until 'alert auto')");
            nextManage = T; nextTask = T;
            break;
        case "auto":
            ForTargets(tgt, d => d.Managed = true);
            Log("Automatic defense control: " + tgt); nextManage = T;
            break;
        case "manual":
            ForTargets(tgt, d => d.Managed = false);
            Log("Manual control: " + tgt);
            break;
        case "defense":
            {
                string a = tgt.ToLower();
                defense = a == "on" ? true : a == "off" ? false : !defense;
                cfg.Set("Defense", defense ? "true" : "false");
                Log("Defense manager " + (defense ? "ON" : "OFF")); nextManage = T;
            }
            break;
        case "alert":
            {
                string a = tgt.ToUpper();
                int ov = a == "GREEN" ? 0 : a == "YELLOW" ? 1 : a == "RED" ? 2 : a == "AUTO" || a == "ALL" ? -1 : a == "NEXT" ? (alertOverride + 2) % 4 - 1 : -2;
                if (ov == -2) { Log("alert: auto | green | yellow | red | next"); break; }
                alertOverride = ov;
                Log("Alert " + (ov < 0 ? "automatic" : "forced " + Proto.AlertName(ov)));
                nextTask = T;
            }
            break;
        case "jump":
            {
                string a = tgt.ToLower();
                if (a == "toggle" || a == "all") a = jumpState == Proto.JUMP_NONE ? "prep" : "cancel";
                if (a == "prep")
                {
                    jumpState = Proto.JUMP_PREP; SetJumpDrives(false);
                    Log("JUMP PREP: recalling all drones, jump drives locked");
                    if (docks.List.Count > 0) foreach (var d in reg.Values) if (!d.LostFlag && !d.Docked) OrderDock(d);
                    nextTask = T;
                }
                else if (a == "force") { jumpState = Proto.JUMP_READY; SetJumpDrives(true); Log("JUMP FORCED: drives unlocked, undocked drones will be left behind"); }
                else if (a == "cancel" || a == "off") { jumpState = Proto.JUMP_NONE; SetJumpDrives(true); Log("Jump prep cancelled"); nextManage = T; }
                else Log("jump: prep | force | cancel | toggle");
            }
            break;
        case "stock":
            {
                int n;
                if (t.Length > 1 && int.TryParse(t[1], out n) && n >= 0) { targetStock = n; cfg.Set("TargetStock", n.ToString()); Log("Target stock " + n + (n == 0 ? " (factory off)" : "")); }
                else Log("stock: number of defense drones to keep (0 = off)");
            }
            break;
        case "build":
            IGC.SendBroadcastMessage(net.TFac + "cmd", raw, TransmissionDistance.CurrentConstruct);
            Log("Factory: " + Util.Clean(raw));
            break;
        case "safe":
            {
                string a = tgt.ToLower();
                safe = a == "on" ? true : a == "off" ? false : !safe;
                Log(safe ? "SAFE: all combat blocks standing down" : "SAFE off: engagement allowed");
                nextTask = T;
            }
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
      .Append('|').Append(Util.Clean(Me.CubeGrid.CustomName))
      .Append('|').Append(alert).Append('|').Append(alertOverride >= 0 ? 1 : 0).Append('|').Append(safe ? 1 : 0).Append('|').Append(defense ? 1 : 0)
      .Append('|').Append(jumpState)
      .Append('\n');
    foreach (var d in sorted)
    {
        Vector3D lp = HubFrame.ToLocal(M, d.Pos);
        sb.Append("D|").Append(d.Call).Append('|').Append(Util.Clean(d.Role)).Append('|').Append(d.State).Append('|').Append(d.Dir)
          .Append('|').Append(Util.Milli(d.Batt)).Append('|').Append(Util.Milli(d.Integ)).Append('|').Append(Util.Milli(d.Cargo)).Append('|').Append(Util.Milli(d.H2))
          .Append('|').Append((long)lp.Length()).Append('|').Append((int)(d.Vel - vel).Length()).Append('|').Append(d.Flags)
          .Append('|').Append(d.LostFlag ? (int)Math.Min(99999, Math.Max(0, T - d.Seen)) : -1).Append('|').Append(d.Unresp ? 1 : 0)
          .Append('|').Append(d.Dir == Proto.O_DOCK && d.Conn == 0 ? 1 : 0).Append('|').Append(d.Acked ? 1 : 0)
          .Append('|').Append((long)lp.X).Append('|').Append((long)lp.Y).Append('|').Append((long)lp.Z)
          .Append('|').Append(d.Managed ? 1 : 0).Append('|').Append(Util.Milli(d.Ammo)).Append('\n');
    }
    // hangar: docks, factory, logistics
    foreach (var c in docks.List)
    {
        if (c.Closed) continue;
        Drone occ = null, res = null;
        foreach (var d in sorted) { if (d.Docked && d.DockId == c.EntityId) occ = d; if (d.Conn == c.EntityId) res = d; }
        int st = occ != null || c.Status == MyShipConnectorStatus.Connected ? 2 : res != null ? 1 : 0;
        Drone who = occ ?? res;
        sb.Append("K|").Append(Util.Clean(c.CustomName)).Append('|').Append(st).Append('|').Append(who != null ? who.Call : "")
          .Append('|').Append(who != null ? Util.Milli(who.Batt) : -1).Append('|').Append(who != null ? Util.Milli(who.Integ) : -1).Append('\n');
    }
    if (T - facT < 10)
        sb.Append("F|").Append(facSt).Append('|').Append(facProg).Append('|').Append(facBuilt).Append('|').Append(facDemand).Append('|').Append(facMsg).Append('\n');
    sb.Append("P|").Append(repairOn ? 1 : 0).Append('|').Append(rearmOn ? 1 : 0).Append('|').Append(charging)
      .Append('|').Append(mode == Proto.MODE_BASE ? maxChargeB : maxChargeC).Append('|').Append(Util.Milli(hubReserve))
      .Append('|').Append(targetStock).Append('|').Append(liveStock).Append('|').Append(repairBlocks.Count).Append('|').Append(rearmBlocks.Count).Append('\n');
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
    if (jumpState != Proto.JUMP_NONE) e.Append(jumpState == Proto.JUMP_PREP ? "JUMP PREP: waiting for drones to dock\n" : "JUMP READY\n");
    if (targetStock > 0) e.Append("Stock ").Append(liveStock).Append('/').Append(targetStock).Append("   Factory: ").Append(T - facT < 10 ? Proto.FactoryName(facSt) : "no link").Append('\n');
    e.Append("Alert: ").Append(Proto.AlertName(alert)).Append(alertOverride >= 0 ? " (forced)" : " (auto)")
     .Append("   Defense: ").Append(defense ? "on" : "off").Append(safe ? "   SAFE" : "")
     .Append("   Turrets: ").Append(hubTurrets.Count).Append("  Alert sensors: ").Append(alertSensors.Count).Append('\n');
    e.Append(perf.Text()).Append('\n');
    e.Append(warn);
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - 5); i--) e.Append(Util.Age((int)(T - evT[i]))).Append("  ").Append(evText[i]).Append('\n');
    echo = e.ToString();
}
