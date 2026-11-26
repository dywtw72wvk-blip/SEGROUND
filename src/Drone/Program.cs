// ============================================================================
// FLEET SYSTEM — DRONE (phase 2: Buster defense role)
// Runs on every drone. Registers with a hub, follows orders (escort/screen,
// patrol, hold, goto, dock, undock, stop), docks itself, reports telemetry.
// Search & destroy: sweeps a strip with camera lidar, reports contacts, and on ATTACK
// goes terminal into the target with armed warheads.
// Buster role: when the hub says ENGAGE, turns on the drone's [Combat] blocks
// (turrets, weapons, AI combat blocks) and fires [Engage] timers. In
// EngageMode=AI it hands flight to the vanilla AI blocks inside a leash around
// its station, then takes control back. The fighting itself is vanilla.
// Arguments: provision | reset | stop | rescan
// ============================================================================
const string SEC = "Drone";

Net net;
Cfg cfg;
readonly Flight fl = new Flight();
readonly Telemetry tel = new Telemetry();
readonly Perf perf = new Perf();

IMyShipController ctl;
IMyShipConnector conn;
IMyRadioAntenna ant;
IMyBroadcastListener navL;
IMyUnicastListener uni;
readonly List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
readonly List<IMyThrust> thr = new List<IMyThrust>();
readonly List<IMyGyro> gyr = new List<IMyGyro>();
readonly List<IMyShipConnector> conns = new List<IMyShipConnector>();
readonly List<IMyFunctionalBlock> combat = new List<IMyFunctionalBlock>();
readonly List<IMyTimerBlock> tEng = new List<IMyTimerBlock>(), tDis = new List<IMyTimerBlock>();
readonly List<IMyLargeTurretBase> turrets = new List<IMyLargeTurretBase>();
readonly List<IMyCameraBlock> cams = new List<IMyCameraBlock>();      // lidar: search and homing
readonly List<IMyWarhead> heads = new List<IMyWarhead>();             // terminal attack payload
readonly List<IMyOffensiveCombatBlock> aiOff = new List<IMyOffensiveCombatBlock>();   // intercept: AI lock (Intercept pattern)
readonly List<IMyFlightMovementBlock> aiFly = new List<IMyFlightMovementBlock>();     // intercept: AI flies (off otherwise)
readonly Wc wc = new Wc();                                                        // WeaponCore threats (spotter's turret)

// --- config
int netId;
string key = "", role = "Buster", perfMode = "NORMAL", connTag = "", ctlTag = "";
string engageMode = "STATION", combatTag = "[Combat]", engTag = "[Engage]", disTag = "[Disengage]";
double maxSpeed, dockSpeed, approach, rtbBatt, linkTimeout, clearance, reconSpeed, minAlt, standoff,
       lidarRange, armDist, fuse, cone, weave, lidarStep, lidarSwath, lidarRate, rays, ringRate, handoff, ringT0;
bool provisioned, combatIdleOff = true, inFactory, targetNeutral;

// --- runtime state
double T, lastFlightT, nextSts, nextHello, nextTel, nextScan, nextEcho, stateT, navT = -1e9, lostSince, atkT, nextCon;
int wp, scanI;                                    // recon waypoint, lidar sweep step
MyDetectedEntityInfo det; double detT = -1e9, detSize;           // best lidar/turret contact
MyDetectedEntityInfo held; double heldSize; int heldN;          // contact seen out of radio range, still to report
bool reconBack;                                                  // search: on the way back along the track
long atkId; bool handed;                          // attack target; intercept: AI has the controls
Vector3D atkAz;                                   // attack: chosen approach azimuth (horizontal, from target)
int state = Proto.S_BOOT, prevState = Proto.S_IDLE;
bool registered, controlling, arrived, combatOn, leashBreak;
long hubAddr;
string call = "";
readonly HubFrame hub = new HubFrame();

int task; Vector3D tA, tB, tC; long tId;          // current directive
int pTask; Vector3D pA, pB, pC; long pId;         // directive to run after undocking
long epoch = -1, lastN = -1, ack;
Vector3D holdPos, holdFwd = Vector3D.Forward, holdUp = Vector3D.Up;
Vector3D uP, uF; bool uLocal;                     // undock reference point/axis
string warn = "", echo = "";

public Program()
{
    net = new Net(IGC);
    uni = IGC.UnicastListener;
    LoadConfig();
    Scan();
    Load();
    Resume();
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

public void Save()
{
    var ini = new MyIni();
    ini.Set("s", "reg", registered); ini.Set("s", "hub", hubAddr); ini.Set("s", "call", call);
    ini.Set("s", "task", task); ini.Set("s", "tId", tId); ini.Set("s", "pTask", pTask); ini.Set("s", "pId", pId);
    ini.Set("s", "epoch", epoch); ini.Set("s", "lastN", lastN); ini.Set("s", "ack", ack);
    ini.Set("s", "base", tel.Baseline); ini.Set("s", "uLocal", uLocal);
    SetV(ini, "tA", tA); SetV(ini, "tB", tB); SetV(ini, "tC", tC);
    SetV(ini, "pA", pA); SetV(ini, "pB", pB); SetV(ini, "pC", pC);
    SetV(ini, "hold", holdPos); SetV(ini, "hf", holdFwd); SetV(ini, "hu", holdUp);
    SetV(ini, "uP", uP); SetV(ini, "uF", uF);
    Storage = ini.ToString();
}

void Load()
{
    var ini = new MyIni();
    if (string.IsNullOrEmpty(Storage) || !ini.TryParse(Storage)) return;
    registered = ini.Get("s", "reg").ToBoolean(); hubAddr = ini.Get("s", "hub").ToInt64(); call = ini.Get("s", "call").ToString("");
    task = ini.Get("s", "task").ToInt32(); tId = ini.Get("s", "tId").ToInt64();
    pTask = ini.Get("s", "pTask").ToInt32(); pId = ini.Get("s", "pId").ToInt64();
    epoch = ini.Get("s", "epoch").ToInt64(-1); lastN = ini.Get("s", "lastN").ToInt64(-1); ack = ini.Get("s", "ack").ToInt64();
    tel.Baseline = Math.Max(tel.Baseline, ini.Get("s", "base").ToInt32()); uLocal = ini.Get("s", "uLocal").ToBoolean();
    tA = GetV(ini, "tA"); tB = GetV(ini, "tB"); tC = GetV(ini, "tC");
    pA = GetV(ini, "pA"); pB = GetV(ini, "pB"); pC = GetV(ini, "pC");
    holdPos = GetV(ini, "hold"); holdFwd = GetV(ini, "hf"); holdUp = GetV(ini, "hu");
    uP = GetV(ini, "uP"); uF = GetV(ini, "uF");
}

static void SetV(MyIni ini, string k, Vector3D v) { ini.Set("v", k + "x", v.X); ini.Set("v", k + "y", v.Y); ini.Set("v", k + "z", v.Z); }
static Vector3D GetV(MyIni ini, string k) { return new Vector3D(ini.Get("v", k + "x").ToDouble(), ini.Get("v", k + "y").ToDouble(), ini.Get("v", k + "z").ToDouble()); }

void LoadConfig()
{
    cfg = new Cfg(Me, SEC);
    netId = cfg.I("NetId", 0);
    key = cfg.S("Key", "");
    // blueprints drop Custom Data on paste: a PB or grid named "...Spotter..." defaults to Role=Spotter
    bool spot = Me.CustomName.Contains("Spotter") || Me.CubeGrid.CustomName.Contains("Spotter");
    role = cfg.S("Role", spot ? "Spotter" : "Buster");
    perfMode = cfg.S("Perf", "Normal").ToUpper();
    maxSpeed = cfg.D("MaxSpeed", 100);
    dockSpeed = cfg.D("DockSpeed", 1.5);
    approach = cfg.D("ApproachDistance", 25);
    clearance = cfg.D("Clearance", 15);
    reconSpeed = cfg.D("ReconSpeed", spot ? 35 : 60); // search: cruise speed while sweeping
    minAlt = cfg.D("MinAltitude", 150);              // keep at least this far above the surface on S&D
    standoff = cfg.D("RingDistance", 2800);          // death ring distance from the target (hub sends its own)
    cone = cfg.D("RingCone", 20);                    // ring angle off vertical over the target (degrees)
    ringRate = cfg.D("RingRate", 2);                 // ring rotation (degrees per second)
    handoff = cfg.D("AiHandoff", 600);               // intercept: hand the controls to the AI blocks inside this range
    weave = cfg.D("Weave", 30);                      // lateral weave in the dive (m), fades out near the target
    lidarRange = cfg.D("LidarRange", 3000);          // camera raycast reach while searching (space / high up)
    lidarStep = Math.Max(5, cfg.D("LidarStep", spot ? 40 : 50)); // ground-scan grid spacing
    lidarRate = Math.Max(1, cfg.D("LidarRate", spot ? 12 : 10)); // raycasts per second, all cameras: each one costs sim speed
    lidarSwath = cfg.D("LidarSwath", 1100);          // ground-scan width (line spacing + overlap)
    armDist = cfg.D("ArmDistance", 300);             // warheads arm inside this distance of the target
    fuse = cfg.D("Fuse", 8);
    targetNeutral = cfg.B("TargetNeutral", false);    // also hunt neutral / unowned grids                         // proximity detonation (plus half the target size)
    rtbBatt = cfg.D("RtbBattery", 0.2);
    linkTimeout = cfg.D("LinkTimeout", 5);
    connTag = cfg.S("ConnectorTag", "");
    ctlTag = cfg.S("ControllerTag", "");
    engageMode = cfg.S("EngageMode", "Station").ToUpper();     // Station | AI
    combatTag = cfg.S("CombatTag", "[Combat]");
    engTag = cfg.S("EngageTimerTag", "[Engage]");
    disTag = cfg.S("DisengageTimerTag", "[Disengage]");
    combatIdleOff = cfg.B("CombatOffWhenIdle", true);
    fl.UseObserver = cfg.B("DisturbanceComp", true);
    fl.Kp = cfg.D("PositionGain", 0.8);
    fl.Kv = cfg.D("VelocityGain", 2.5);
    fl.GyroGain = cfg.D("GyroGain", 3);
    fl.GyroMax = cfg.D("GyroMaxRate", 3);
    fl.GyroSign = cfg.B("GyroInvert", false) ? 1 : -1;
    fl.SpeedCap = maxSpeed;
    cfg.Save();

    provisioned = netId != 0 && key.Length > 0 && cfg.Error.Length == 0;
    if (navL != null) IGC.DisableBroadcastListener(navL);
    net.Configure(netId, key);
    navL = IGC.RegisterBroadcastListener(net.TNav);
}

void Scan()
{
    blocks.Clear();
    GridTerminalSystem.GetBlocksOfType(blocks, b => b.IsSameConstructAs(Me));
    thr.Clear(); gyr.Clear(); conns.Clear(); combat.Clear(); tEng.Clear(); tDis.Clear(); turrets.Clear(); cams.Clear(); heads.Clear(); aiOff.Clear(); aiFly.Clear();
    if (!wc.Ok) wc.Init(Me);
    ctl = null; conn = null; ant = null;
    IMyShipController anyCtl = null;
    foreach (var b in blocks)
    {
        var fb = b as IMyFunctionalBlock;
        if (fb != null && combatTag.Length > 0 && b.CustomName.Contains(combatTag)) combat.Add(fb);
        var tm = b as IMyTimerBlock;
        if (tm != null) { if (b.CustomName.Contains(engTag)) tEng.Add(tm); else if (b.CustomName.Contains(disTag)) tDis.Add(tm); }
        var tu = b as IMyLargeTurretBase; if (tu != null) turrets.Add(tu);
        var cm = b as IMyCameraBlock; if (cm != null) { cams.Add(cm); if (!cm.EnableRaycast) cm.EnableRaycast = true; }
        var wh = b as IMyWarhead; if (wh != null) heads.Add(wh);
        var ao = b as IMyOffensiveCombatBlock; if (ao != null) aiOff.Add(ao);
        var af = b as IMyFlightMovementBlock; if (af != null) { aiFly.Add(af); if (af.Enabled && !handed) af.Enabled = false; }
        var t = b as IMyThrust; if (t != null) { thr.Add(t); continue; }
        var g = b as IMyGyro; if (g != null) { gyr.Add(g); continue; }
        var c = b as IMyShipController;
        if (c != null)
        {
            if (ctlTag.Length > 0 ? b.CustomName.Contains(ctlTag) : c is IMyRemoteControl) ctl = c;
            if (anyCtl == null) anyCtl = c;
            continue;
        }
        var k = b as IMyShipConnector; if (k != null) { conns.Add(k); continue; }
        var a = b as IMyRadioAntenna; if (a != null && (ant == null || a.IsWorking)) ant = a;
    }
    if (ctl == null) ctl = anyCtl;
    // Freshly welded in a factory bay, the drone is merged into the hub's grid and would
    // "see" the hub's blocks as its own. Stay passive until the merge block releases it.
    inFactory = false;
    foreach (var b in blocks) if (b != Me && b is IMyProgrammableBlock && (b.CustomData.Contains("[Hub]") || b.CustomData.Contains("[Factory]"))) { inFactory = true; break; }
    if (ctl != null) thr.RemoveAll(t => t.CubeGrid != ctl.CubeGrid);
    foreach (var k in conns) if (connTag.Length == 0 || k.CustomName.Contains(connTag)) { conn = k; break; }

    tel.Bind(blocks);
    if (ctl != null) fl.Bind(ctl, thr, gyr);
    if (combatIdleOff || combatOn) foreach (var b in combat) if (b.Enabled != combatOn) b.Enabled = combatOn;

    var w = new StringBuilder();
    if (cfg.Error.Length > 0) w.Append(cfg.Error).Append('\n');
    if (ctl == null) w.Append("ERROR: no remote control / cockpit.\n");
    if (thr.Count == 0) w.Append("ERROR: no thrusters.\n");
    if (gyr.Count == 0) w.Append("ERROR: no gyroscopes.\n");
    if (conn == null) w.Append("WARN: no connector" + (connTag.Length > 0 ? " tagged " + connTag : "") + ": cannot dock.\n");
    if (ant == null) w.Append("WARN: no antenna: no hub link.\n");
    if (cams.Count == 0 && turrets.Count == 0) w.Append("WARN: no camera: cannot search for targets.\n");
    else if (!ant.IsWorking || !ant.EnableBroadcasting) w.Append("WARN: antenna off or not broadcasting.\n");
    if (engageMode == "AI" && combat.Count == 0 && tEng.Count == 0) w.Append("WARN: EngageMode=AI but no " + combatTag + " blocks or " + engTag + " timers.\n");
    warn = w.ToString();
}

void Resume()
{
    if (ctl == null) return;
    if (Connected()) { Enter(Proto.S_DOCKED); return; }
    switch (task)
    {
        case Proto.O_ESCORT: Enter(Proto.S_ESCORT); break;
        case Proto.O_GOTO: Enter(Proto.S_GOTO); break;
        case Proto.O_DOCK: Enter(Proto.S_APPROACH); break;
        case Proto.O_HOLD: Enter(Proto.S_HOLD); break;      // stored holdPos
        case Proto.O_SEARCH: wp = 0; reconBack = false; Enter(Proto.S_RECON); break;
        case Proto.O_ATTACK: atkT = T; atkId = tId; Enter(Proto.S_ATTACK); SetCombat(true); break;
        case Proto.O_LOITER: atkT = T; atkId = tId; ringT0 = T; Enter(Proto.S_LOITER); break;
        case Proto.O_INTERCEPT: atkT = T; atkId = tId; handed = false; Enter(Proto.S_INTERCEPT); break;
        case Proto.O_UNDOCK:
            if (pTask != Proto.O_NONE) Enter(Proto.S_UNDOCKING);
            else { CaptureHold(); Enter(Proto.S_HOLD); }
            break;
        default: Enter(Proto.S_IDLE); break;
    }
}

public void Main(string arg, UpdateType src)
{
    T += Runtime.TimeSinceLastRun.TotalSeconds;
    perf.Tick(Runtime.LastRunTimeMs, T);
    if (!string.IsNullOrEmpty(arg)) Command(arg.Trim().ToLower());
    if ((src & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0) Tick();
    Echo(echo);
}

void Command(string a)
{
    if (a == "provision" || a == "reload")
    {
        LoadConfig(); Scan();
        registered = false; nextHello = T;
    }
    else if (a == "reset")
    {
        registered = false; hubAddr = 0; epoch = -1; lastN = -1; task = Proto.O_NONE; pTask = Proto.O_NONE;
        if (ctl != null) { fl.Release(true); Enter(Connected() ? Proto.S_DOCKED : Proto.S_IDLE); }
    }
    else if (a == "stop")
    {
        task = Proto.O_STOP; pTask = Proto.O_NONE;
        if (ctl != null) { fl.Release(true); Enter(Connected() ? Proto.S_DOCKED : Proto.S_IDLE); }
    }
    else if (a == "rescan") Scan();
    BuildEcho();
}

void Tick()
{
    if (T >= nextScan) { Scan(); nextScan = T + 10; }
    if (inFactory)
    {
        if (controlling && ctl != null) fl.Release(true);
        controlling = false; state = Proto.S_BOOT;
        if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
        nextScan = Math.Min(nextScan, T + 2);       // rescan often so release is noticed quickly
        return;
    }
    if (ctl == null || thr.Count == 0 || gyr.Count == 0 || !provisioned)
    {
        if (controlling && ctl != null) fl.Release(true);
        controlling = false;
        state = provisioned ? Proto.S_ERROR : Proto.S_UNPROV;
        SetFreq();
        if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
        return;
    }
    if (state == Proto.S_UNPROV || state == Proto.S_ERROR || state == Proto.S_BOOT) Resume();

    Receive();
    double dt = T - lastFlightT;
    lastFlightT = T;
    if (dt <= 0 || dt > 1) dt = 1.0 / 60;
    fl.Sense(dt);
    if (T >= nextTel) { tel.Read(); nextTel = T + 0.5; }

    if (state == Proto.S_RECON || state == Proto.S_ATTACK || state == Proto.S_LOITER || state == Proto.S_INTERCEPT) Lidar();
    Step();

    if (registered) { if (T >= nextSts) { SendStatus(); nextSts = T + 0.5; } if (T >= nextCon) { ReportContact(); nextCon = T + 1; } }
    else if (T >= nextHello) { net.SendHello(role, 0, fl.MaxAccel, maxSpeed, Me.CubeGrid.CustomName); nextHello = T + 3; }

    SetFreq();
    if (T >= nextEcho) { BuildEcho(); nextEcho = T + 0.5; }
}

// ---------------------------------------------------------------- messages
void Receive()
{
    while (navL.HasPendingMessage)
    {
        var m = navL.AcceptMessage();
        if (!registered || m.Source != hubAddr) continue;
        if (net.ReadNav(m, hub)) { hub.T = T; navT = T; }
    }
    while (uni.HasPendingMessage)
    {
        var m = uni.AcceptMessage();
        long seq; int code; Vector3D a, b, c; long id; string text;
        if (!net.ReadOrder(m, out seq, out code, out a, out b, out c, out id, out text)) continue;
        if (code == Proto.O_REGISTER)
        {
            // Only switch hubs if we have no live link to the current one
            if (registered && m.Source != hubAddr && NavFresh()) continue;
            hubAddr = m.Source; registered = true; call = text;
        }
        else if (!registered || m.Source != hubAddr) continue;

        long ep = seq >> 32, n = seq & 0xFFFFFFFFL;
        if (ep != epoch) { epoch = ep; lastN = -1; }
        if (n <= lastN) continue;           // duplicate / stale resend
        lastN = n; ack = seq;
        if (code != Proto.O_REGISTER) Apply(code, a, b, c, id);
        nextSts = T;                        // acknowledge promptly
    }
}

void SendStatus()
{
    bool docked = Connected();
    int f = 0;
    if (docked) f |= Proto.F_DOCKED;
    if (!docked && tel.Batt >= 0 && tel.Batt < rtbBatt) f |= Proto.F_WANT_RTB;
    if (state == Proto.S_LINKLOST) f |= Proto.F_LINKLOST;
    if (!docked && !fl.HasThrust) f |= Proto.F_NO_THRUST;
    if (arrived) f |= Proto.F_ARRIVED;
    if (combatOn) f |= Proto.F_ENGAGED;
    if (!docked && T - detT < 3) f |= Proto.F_CONTACT;
    long dockId = docked && conn.OtherConnector != null ? conn.OtherConnector.EntityId : 0;
    net.SendStatus(hubAddr, role, state, task, ack, dockId, fl.Pos, fl.Vel, tel.Batt, tel.H2, tel.Cargo, tel.Integ, tel.Ammo, f);
}

// ---------------------------------------------------------------- orders
void Apply(int code, Vector3D a, Vector3D b, Vector3D c, long id)
{
    bool connected = Connected();
    switch (code)
    {
        case Proto.O_ESCORT:
        case Proto.O_HOLD:
        case Proto.O_GOTO:
        case Proto.O_SEARCH:
        case Proto.O_ATTACK:
        case Proto.O_LOITER:
        case Proto.O_INTERCEPT:
            if (connected) { task = code; SetPending(code, a, b, c, id); BeginUndock(); }
            else if (state == Proto.S_UNDOCKING) { task = code; SetPending(code, a, b, c, id); }
            else Execute(code, a, b, c, id);
            break;

        case Proto.O_DOCK:
            pTask = Proto.O_NONE;
            if (connected)
            {
                if (conn.OtherConnector != null && conn.OtherConnector.EntityId == id) { Store(code, a, b, c, id); Enter(Proto.S_DOCKED); }
                else { task = code; SetPending(code, a, b, c, id); BeginUndock(); }
            }
            else Execute(code, a, b, c, id);
            break;

        case Proto.O_UNDOCK:
            task = code; pTask = Proto.O_NONE;
            if (connected) BeginUndock();
            else if (state == Proto.S_APPROACH || state == Proto.S_FINAL) StationHere();
            break;

        case Proto.O_STOP:
            task = code; pTask = Proto.O_NONE;
            if (connected) Enter(Proto.S_DOCKED);
            else { fl.Release(true); Enter(Proto.S_IDLE); }
            break;
    }
}

void Store(int code, Vector3D a, Vector3D b, Vector3D c, long id) { task = code; tA = a; tB = b; tC = c; tId = id; }
void SetPending(int code, Vector3D a, Vector3D b, Vector3D c, long id) { pTask = code; pA = a; pB = b; pC = c; pId = id; }

void Execute(int code, Vector3D a, Vector3D b, Vector3D c, long id)
{
    Store(code, a, b, c, id);
    arrived = false;
    switch (code)
    {
        case Proto.O_ESCORT:
            // already on station: just take the new slot/flags, FlyStation adapts
            if (state != Proto.S_ESCORT && state != Proto.S_PATROL && state != Proto.S_ENGAGED) Enter(Proto.S_ESCORT);
            break;
        case Proto.O_HOLD: CaptureHold(); Enter(Proto.S_HOLD); break;
        case Proto.O_GOTO: CaptureHold(); Enter(Proto.S_GOTO); break;
        case Proto.O_DOCK: Enter(Proto.S_APPROACH); break;
        case Proto.O_SEARCH: wp = 0; reconBack = false; Enter(Proto.S_RECON); break;
        case Proto.O_ATTACK:                           // target fix as of now; dive immediately
            atkT = T; atkId = id;
            if (state != Proto.S_ATTACK) { atkAz = Vector3D.Zero; Enter(Proto.S_ATTACK); }
            if (!combatOn) SetCombat(true);
            break;
        case Proto.O_LOITER:
            atkT = T;
            if (state != Proto.S_LOITER || id != atkId) { atkId = id; ringT0 = T; atkAz = Vector3D.Zero; }
            if (state != Proto.S_LOITER && state != Proto.S_INTERCEPT) Enter(Proto.S_LOITER);
            break;
        case Proto.O_INTERCEPT:
            atkT = T; atkId = id;
            if (state != Proto.S_INTERCEPT) { handed = false; Enter(Proto.S_INTERCEPT); }
            break;
    }
}

void CaptureHold()
{
    holdPos = fl.Pos;
    holdFwd = ctl.WorldMatrix.Forward;
    holdUp = ctl.WorldMatrix.Up;
}

// Keep station relative to the hub at the current spot (or hold in place if no link)
void StationHere()
{
    if (NavFresh())
    {
        MatrixD m = hub.Matrix(T);
        Store(Proto.O_ESCORT, HubFrame.ToLocal(m, fl.Pos), HubFrame.DirToLocal(m, ctl.WorldMatrix.Forward), Vector3D.Zero, 0);
        Enter(Proto.S_ESCORT);
    }
    else { CaptureHold(); Enter(Proto.S_HOLD); }
}

void BeginUndock()
{
    var oc = conn != null ? conn.OtherConnector : null;
    if (oc != null) { uF = oc.WorldMatrix.Forward; uP = oc.GetPosition(); }
    else if (conn != null) { uF = -conn.WorldMatrix.Forward; uP = conn.GetPosition(); }
    else { uF = -ctl.WorldMatrix.Forward; uP = fl.Pos; }
    uLocal = NavFresh();
    if (uLocal) { MatrixD m = hub.Matrix(T); uP = HubFrame.ToLocal(m, uP); uF = HubFrame.DirToLocal(m, uF); }
    DockedPower(false);
    if (conn != null) conn.Disconnect();
    Enter(Proto.S_UNDOCKING);
}

// ---------------------------------------------------------------- state machine
void Enter(int s)
{
    bool flying = s == Proto.S_HOLD || s == Proto.S_ESCORT || s == Proto.S_PATROL || s == Proto.S_GOTO || s == Proto.S_APPROACH ||
                  s == Proto.S_FINAL || s == Proto.S_UNDOCKING || s == Proto.S_LINKLOST || s == Proto.S_RECON || s == Proto.S_ATTACK ||
                  s == Proto.S_LOITER || s == Proto.S_INTERCEPT;
    bool station = s == Proto.S_ESCORT || s == Proto.S_PATROL || s == Proto.S_ENGAGED;
    if (!station) { leashBreak = false; if (combatOn) SetCombat(false); }
    if (s == Proto.S_DOCKED) { fl.Release(true); DockedPower(true); controlling = false; }
    else if (s == Proto.S_ENGAGED) { if (controlling) fl.Release(true); controlling = false; }   // vanilla AI flies
    else if (!flying) { if (controlling) fl.Release(true); controlling = false; }
    else if (!controlling) { fl.Engage(); controlling = true; }
    fl.Freeze = s == Proto.S_FINAL;
    if (s != Proto.S_ATTACK && s != Proto.S_INTERCEPT) foreach (var w in heads) if (!w.Closed && w.IsArmed) w.IsArmed = false;
    if (s != Proto.S_INTERCEPT && handed) { handed = false; foreach (var b in aiFly) if (!b.Closed) b.Enabled = false; }
    state = s; stateT = T;
}

void Step()
{
    bool fresh = NavFresh();
    switch (state)
    {
        case Proto.S_IDLE:
            if (Connected()) Enter(Proto.S_DOCKED);
            break;
        case Proto.S_HOLD:
            // never fly against a locked connector: if something locked us, we're docked
            if (Connected()) { Enter(Proto.S_DOCKED); break; }
            FlyHoldAt(holdPos);
            break;
        case Proto.S_ESCORT:
        case Proto.S_PATROL:
        case Proto.S_ENGAGED:
            if (!fresh) { LinkLost(); break; }
            FlyStation();
            break;
        case Proto.S_GOTO:
            FlyGoto();
            break;
        case Proto.S_APPROACH:
            if (!fresh) { LinkLost(); break; }
            FlyApproach();
            break;
        case Proto.S_FINAL:
            if (!fresh) { LinkLost(); break; }
            FlyFinal();
            break;
        case Proto.S_DOCKED:
            // a jolt can drop the lock without us leaving the port: re-lock instead of flying off
            if (conn != null && conn.Status == MyShipConnectorStatus.Connectable) conn.Connect();
            else if (!Connected()) { DockedPower(false); CaptureHold(); Enter(Proto.S_HOLD); }
            break;
        case Proto.S_UNDOCKING:
            FlyUndock();
            break;
        case Proto.S_RECON:                 // autonomous: keeps going out of hub range
            FlyRecon();
            break;
        case Proto.S_ATTACK:
            FlyAttack();
            break;
        case Proto.S_LOITER:                // autonomous: keeps flying the ring out of hub range
            FlyLoiter();
            break;
        case Proto.S_INTERCEPT:
            FlyIntercept();
            break;
        case Proto.S_LINKLOST:
            FlyHoldAt(holdPos);
            if (fresh) Enter(prevState);
            else if (registered && T - lostSince > 60) { registered = false; nextHello = T; }
            break;
    }
}

void LinkLost()
{
    // resume as ESCORT: combat blocks were switched off, FlyStation re-engages if still ordered
    prevState = state == Proto.S_ENGAGED ? Proto.S_ESCORT : state; lostSince = T;
    CaptureHold();
    Enter(Proto.S_LINKLOST);
}

void FlyHoldAt(Vector3D p)
{
    fl.FlyTo(p, Vector3D.Zero, maxSpeed);
    fl.Aim(ctl.WorldMatrix, holdFwd, UpRef(holdUp));
}

Vector3D UpRef(Vector3D fallback)
{
    return fl.Grav.LengthSquared() > 0.01 ? -Util.Norm(fl.Grav) : fallback;
}

// Station keeping for ESCORT / PATROL / ENGAGED.
// Order params: tA = slot offset (hub-local), tB = facing (hub-local),
//               tC = (engage 0/1, leash metres, patrol 0/1)
void FlyStation()
{
    MatrixD m = hub.Matrix(T);
    Vector3D off = tA, face = tB.LengthSquared() > 0.1 ? tB : new Vector3D(0, 0, -1);
    bool patrol = tC.Z > 0.5, engage = tC.X > 0.5;
    double leash = tC.Y > 1 ? tC.Y : 600;
    Vector3D center = hub.KeepOut(T);
    if (patrol)
    {
        // every drone rotates its slot by the same hub-supplied phase -> spacing is kept
        Vector3D lc = HubFrame.ToLocal(m, center);
        double ph = hub.PhaseAt(T);
        off = lc + HubFrame.RotY(tA - lc, ph);
        face = HubFrame.RotY(face, ph);
    }
    Vector3D tgt = HubFrame.ToWorld(m, off);
    Vector3D tv = hub.VelAt(tgt, m);
    if (patrol) tv += Vector3D.Cross(m.Up * hub.Rate, tgt - center);
    double away = (fl.Pos - tgt).Length();

    // AI engage: vanilla AI blocks fly and fight while inside the leash
    if (engage && engageMode == "AI" && !leashBreak)
    {
        if (away > leash) leashBreak = true;
        else
        {
            if (state != Proto.S_ENGAGED) { SetCombat(true); Enter(Proto.S_ENGAGED); }
            return;
        }
    }
    if (leashBreak && (!engage || away < leash * 0.3)) leashBreak = false;

    int want = patrol ? Proto.S_PATROL : Proto.S_ESCORT;
    if (state != want) Enter(want);                     // from ENGAGED: script takes the controls back
    bool combatWanted = engage && engageMode != "AI";   // Station mode: weapons on, keep flying the slot
    if (combatOn != combatWanted) SetCombat(combatWanted);

    fl.FlyTo(Avoid(tgt, m), tv, maxSpeed);
    fl.Aim(ctl.WorldMatrix, HubFrame.DirToWorld(m, face), m.Up);
}

// Turn [Combat] blocks on/off and fire the matching timer blocks
// ---------------------------------------------------------------- search & destroy
// SEARCH: tA = start of our track, tB = forward * depth (world). The hub spaces the tracks into a
// line abreast; each Buster joins its track and sweeps forward along it, nose and cameras ahead.
// F_ARRIVED at the end of the track.
// The track runs out SearchDepth and back: a small-grid antenna reaches ~5 km and the hub only hears
// the spotter inside that, so it reports the end of the sweep (F_ARRIVED) back near the start, and a
// contact seen out of range turns it straight back at full speed to report it.
void FlyRecon()
{
    double depth = tB.Length();
    Vector3D f = Util.Norm(tB), u = UpRef(ctl.WorldMatrix.Up);
    double along = Util.Clamp(Vector3D.Dot(fl.Pos - tA, f), 0, depth), look = Math.Max(300, reconSpeed * 6);
    bool report = heldN > 0 && !InRange();
    if (along > depth - 150 || report) reconBack = true;
    Vector3D dir = reconBack ? -f : f;
    Vector3D dest = tA + f * Util.Clamp(along + (reconBack ? -look : look), 0, depth);
    fl.FlyTo(Lift(dest), Vector3D.Zero, report ? maxSpeed : reconSpeed);
    Vector3D h = dir - u * Vector3D.Dot(dir, u);
    if (h.LengthSquared() > 0.01) holdFwd = Util.Norm(h);
    fl.Aim(ctl.WorldMatrix, holdFwd, u);
    arrived = reconBack && along < 150;
}

// can the hub hear us? (its NAV reaches us; our antenna has to reach it)
bool InRange() { return NavFresh() && (ant == null || (hub.Pos - fl.Pos).Length() < ant.Radius - 300); }

// Target fix: our own lidar ping on tId if fresh, else the hub's last fix extrapolated.
bool Fix() { return det.EntityId == tId && T - detT < 2; }
Vector3D AtkPos() { return Fix() ? det.Position + (Vector3D)det.Velocity * (T - detT) : tA + tB * Math.Min(5, T - atkT); }
Vector3D AtkVel() { return Fix() ? (Vector3D)det.Velocity : tB; }

// LOITER: tC = (slot, slots, ring distance). Death ring RingDistance up a RingCone cone over the
// target, slots evenly spaced and rotating at RingRate; nose on the target so the cameras keep a fix.
void FlyLoiter()
{
    Vector3D tp = AtkPos(), tv = AtkVel(), u = UpRef(ctl.WorldMatrix.Up), e1 = Util.Norm(Util.Perp(u)), e2 = Vector3D.Cross(u, e1);
    double R = tC.Z > 1 ? tC.Z : standoff, th = cone * Math.PI / 180, w = ringRate * Math.PI / 180;
    double a = 2 * Math.PI * tC.X / Math.Max(1, tC.Y) + w * (T - ringT0);
    Vector3D rad = e1 * Math.Cos(a) + e2 * Math.Sin(a), tan = e2 * Math.Cos(a) - e1 * Math.Sin(a);
    Vector3D slot = tp + (u * Math.Cos(th) + rad * Math.Sin(th)) * R;
    fl.FlyTo(Lift(slot), tv + tan * (w * R * Math.Sin(th)), maxSpeed);
    atkAz = rad;
    AimLevel(tp - fl.Pos, u);
    arrived = (slot - fl.Pos).Length() < 100;
}

// ATTACK: dive from where we are (the ring) at tA + tC (tC = aim offset: our side of the target),
// full speed through a lead point with a lateral weave that fades out before ArmDistance.
void FlyAttack()
{
    Vector3D tp = AtkPos() + tC, tv = AtkVel(), u = UpRef(ctl.WorldMatrix.Up), to = tp - fl.Pos;
    double dist = to.Length();
    if (atkAz.LengthSquared() < 0.5) { Vector3D h = -to - u * Vector3D.Dot(-to, u); atkAz = h.LengthSquared() > 1 ? Util.Norm(h) : Util.Norm(Util.Perp(u)); }
    double closing = Math.Max(20, Vector3D.Dot(fl.Vel - tv, Util.Norm(to)));
    Vector3D aim = tp + tv * Math.Min(6, dist / closing), dir = Util.Norm(aim - fl.Pos);
    Vector3D side = Util.Norm(Vector3D.Cross(dir, atkAz));
    aim += side * (weave * Util.Clamp((dist - armDist) / Math.Max(1, standoff - armDist), 0, 1) * Math.Sin(T * 1.6 + tC.X * 0.01));
    Terminal(aim, dir, tv, dist, u, Fix() ? detSize * 0.4 : 0);
}

// full speed at a point past the aim point (no braking), warheads armed inside ArmDistance
void Terminal(Vector3D aim, Vector3D dir, Vector3D tv, double dist, Vector3D u, double size)
{
    fl.FlyTo(aim + dir * 300, tv, maxSpeed);
    AimLevel(dir, u);
    bool arm = dist < armDist;
    foreach (var w in heads) if (!w.Closed && w.IsArmed != arm) w.IsArmed = arm;
    if (arm && dist < fuse + size) foreach (var w in heads) if (!w.Closed) w.Detonate();
}

// INTERCEPT a launched drone: chase the hub/lidar fix; inside AiHandoff give the controls to the
// AI Flight + AI Offensive (Intercept, Closest) blocks with warheads armed. Without AI blocks the
// script rams it itself.
void FlyIntercept()
{
    if (handed) return;
    Vector3D tp = AtkPos(), tv = AtkVel(), u = UpRef(ctl.WorldMatrix.Up), to = tp - fl.Pos;
    double dist = to.Length();
    if (atkAz.LengthSquared() < 0.5) atkAz = Util.Norm(Util.Perp(u));
    if (dist < handoff && aiFly.Count > 0 && aiOff.Count > 0)
    {
        fl.Release(true); controlling = false; handed = true;
        foreach (var b in aiFly) if (!b.Closed) b.Enabled = true;
        foreach (var b in aiOff) if (!b.Closed && !b.Enabled) b.Enabled = true;
        foreach (var w in heads) if (!w.Closed) w.IsArmed = true;
        return;
    }
    double closing = Math.Max(20, Vector3D.Dot(fl.Vel - tv, Util.Norm(to)));
    Vector3D aim = tp + tv * Math.Min(6, dist / closing);
    Terminal(aim, Util.Norm(aim - fl.Pos), tv, dist, u, Fix() ? detSize * 0.4 : 0);
}

// Stay level and only yaw toward the target: the Buster's lift thruster is its only strong axis, and
// pointing the nose down a dive leaves it unable to steer. Gravity brings it down, the lift thruster
// and the horizontal thrusters correct the path.
void AimLevel(Vector3D dir, Vector3D u)
{
    Vector3D h = dir - u * Vector3D.Dot(dir, u);
    if (h.LengthSquared() < 0.01) h = -atkAz;
    if (h.LengthSquared() > 0.01) holdFwd = Util.Norm(h);
    fl.Aim(ctl.WorldMatrix, holdFwd, u);
}

// Camera lidar, capped at LidarRate rays/s (a raycast into terrain is a costly physics query; many
// Buster x many rays kills sim speed). Over a planet while searching: a grid of ground points ahead,
// LidarSwath wide, LidarStep apart, one band just inside the camera's 45 deg cone. Elsewhere: a fan.
// Attacking: ping the target to home on it. Opportunistic: designators are the reliable source.
static readonly float[] Pitches = { -6, -15, -28 };
void Lidar()
{
    rays = Math.Min(lidarRate, rays + lidarRate * Math.Max(0, Math.Min(0.2, Runtime.TimeSinceLastRun.TotalSeconds)));
    double e = 0;
    bool ground = state == Proto.S_RECON && fl.Grav.LengthSquared() > 0.01
                  && ctl.TryGetPlanetElevation(MyPlanetElevation.Surface, out e) && e < 1500;
    Vector3D u = UpRef(ctl.WorldMatrix.Up), f = holdFwd, r = Vector3D.Cross(f, u);
    double half = lidarSwath / 2, near = Math.Sqrt(half * half + e * e) + 60;
    int cols = (int)(lidarSwath / lidarStep) + 1;
    foreach (var c in cams)
    {
        if (c.Closed || !c.IsWorking || rays < 1) continue;
        Vector3D cp = c.GetPosition();
        if (state != Proto.S_RECON)
        {
            if (handed) continue;
            Vector3D tp = AtkPos();
            double d = (tp - cp).Length() + 50;
            if (d < lidarRange * 2 && c.CanScan(d)) { rays--; Seen(c.Raycast(tp)); }
            continue;
        }
        if (ground && Vector3D.Dot(c.WorldMatrix.Forward, u) < -0.7)
        {
            // down-looking camera (its Up = the way it scans ahead): rows across the track, LidarStep apart
            int n = Math.Max(3, (int)(1.4 * e / lidarStep) + 1);
            while (rays >= 1)
            {
                int col = scanI % n, row = (scanI / n) % 2;
                float yaw = (float)(-35 + 70.0 * col / (n - 1)), pitch = row == 0 ? 25 : 10;
                double rng = e * 1.2 / (Math.Cos(yaw * Math.PI / 180) * Math.Cos(pitch * Math.PI / 180)) + 30;
                if (!c.CanScan(rng)) break;
                scanI++; rays--;
                Seen(c.Raycast(rng, pitch, yaw));
            }
            continue;
        }
        if (ground)
        {
            while (rays >= 1)
            {
                int col = scanI % cols;
                Vector3D gp = fl.Pos - u * e + f * near + r * (-half + col * lidarStep);
                Vector3D ray = (gp - cp) * 1.15;      // a little past the ground point
                if (!c.CanScan(ray.Length())) break;
                scanI++; rays--;
                Seen(c.Raycast(cp + ray));
            }
            continue;
        }
        if (!c.CanScan(lidarRange)) continue;
        int yi = scanI % 9, pi = (scanI / 9) % Pitches.Length;
        scanI++; rays--;
        Seen(c.Raycast(lidarRange, Pitches[pi], -40 + yi * 10));
    }
}

// keep the biggest enemy grid seen lately (and always refresh the attack target)
void Seen(MyDetectedEntityInfo e)
{
    if (e.IsEmpty()) return;
    var rel = e.Relationship;
    if (rel != MyRelationsBetweenPlayerAndBlock.Enemies && !(targetNeutral && (rel == MyRelationsBetweenPlayerAndBlock.Neutral || rel == MyRelationsBetweenPlayerAndBlock.NoOwnership))) return;
    if (e.Type != MyDetectedEntityType.SmallGrid && e.Type != MyDetectedEntityType.LargeGrid) return;
    double s = e.BoundingBox.Size.Length();
    if (T - detT > 3 || s >= detSize || e.EntityId == det.EntityId || e.EntityId == tId) { det = e; detSize = s; detT = T; }
}

// Send the hub what we see (lidar, plus turret locks on drones that have turrets)
void ReportContact()
{
    foreach (var t in turrets) if (!t.Closed && t.IsWorking && t.HasTarget) Seen(t.GetTargetedEntity());
    if (wc.Ok) foreach (var kv in wc.Threats(Me)) Seen(kv.Key);
    bool fresh = T - detT < 2, near = InRange();
    if (fresh && !near) { held = det; heldSize = detSize; heldN = 5; }
    if (fresh && near) { net.SendContact(hubAddr, det.EntityId, (int)det.Type, detSize, det.Position, det.Velocity); heldN = 0; }
    else if (heldN > 0 && near) { net.SendContact(hubAddr, held.EntityId, (int)held.Type, heldSize, held.Position, held.Velocity); heldN--; }
}

// Raise a destination so the drone stays MinAltitude above the ground under it (planets only)
Vector3D Lift(Vector3D p)
{
    double e;
    if (fl.Grav.LengthSquared() < 0.01 || !ctl.TryGetPlanetElevation(MyPlanetElevation.Surface, out e) || e >= minAlt) return p;
    Vector3D u = -Util.Norm(fl.Grav);
    return p + u * (Vector3D.Dot(fl.Pos - p, u) + (minAlt - e));
}

void SetCombat(bool on)
{
    if (on || combatIdleOff) foreach (var b in combat) if (!b.Closed && b.Enabled != on) b.Enabled = on;
    foreach (var t in on ? tEng : tDis) if (!t.Closed && t.IsFunctional) t.Trigger();
    combatOn = on;
}

void FlyGoto()
{
    Vector3D wp = NavFresh() ? Avoid(tA, hub.Matrix(T)) : tA;
    double dist = (tA - fl.Pos).Length();
    fl.FlyTo(wp, Vector3D.Zero, maxSpeed);
    if (dist > 50) { holdFwd = Util.Norm(wp - fl.Pos); holdUp = ctl.WorldMatrix.Up; }
    fl.Aim(ctl.WorldMatrix, holdFwd, UpRef(holdUp));
    arrived = dist < 3 && fl.Vel.Length() < 0.5;
}

// Hub connector frame from the order: P (face position), F (out axis), U (up)
void DockFrame(MatrixD m, out Vector3D P, out Vector3D F, out Vector3D U)
{
    P = HubFrame.ToWorld(m, tA);
    F = Util.Norm(HubFrame.DirToWorld(m, tB));
    U = Util.Norm(HubFrame.DirToWorld(m, tC));
}

void FlyApproach()
{
    if (conn == null) { CaptureHold(); Enter(Proto.S_HOLD); return; }
    MatrixD m = hub.Matrix(T);
    Vector3D P, F, U; DockFrame(m, out P, out F, out U);
    Vector3D A = P + F * approach;
    Vector3D cp = conn.GetPosition();
    Vector3D off = cp - fl.Pos;
    Vector3D tv = hub.VelAt(A, m);
    fl.FlyTo(Avoid(A - off, m), tv, maxSpeed);
    fl.Aim(conn.WorldMatrix, -F, U);

    double posErr = (cp - A).Length();
    double relV = (fl.Vel - tv).Length();
    if (posErr < 1.5 && relV < 1.0 && Flight.AimError(conn.WorldMatrix, -F) < 0.05) Enter(Proto.S_FINAL);
}

void FlyFinal()
{
    MatrixD m = hub.Matrix(T);
    Vector3D P, F, U; DockFrame(m, out P, out F, out U);
    Vector3D cp = conn.GetPosition();
    Vector3D rel = cp - P;
    double axial = Vector3D.Dot(rel, F);
    double lateral = (rel - F * axial).Length();
    if (lateral > 2.5 || axial < -1 || T - stateT > 60) { Enter(Proto.S_APPROACH); return; }

    if (conn.Status == MyShipConnectorStatus.Connectable) conn.Connect();
    if (conn.Status == MyShipConnectorStatus.Connected) { Enter(Proto.S_DOCKED); return; }

    fl.FlyTo(P - (cp - fl.Pos), hub.VelAt(P, m), dockSpeed);
    fl.Aim(conn.WorldMatrix, -F, U);
}

void FlyUndock()
{
    Vector3D P, F, tv;
    if (uLocal)
    {
        MatrixD m = hub.Matrix(T);
        P = HubFrame.ToWorld(m, uP); F = Util.Norm(HubFrame.DirToWorld(m, uF)); tv = hub.VelAt(P, m);
    }
    else { P = uP; F = Util.Norm(uF); tv = Vector3D.Zero; }

    Vector3D cp = conn != null ? conn.GetPosition() : fl.Pos;
    fl.FlyTo(P + F * approach - (cp - fl.Pos), tv, Math.Max(dockSpeed * 3, 5));
    if (conn != null) fl.Aim(conn.WorldMatrix, -F, conn.WorldMatrix.Up);

    double outDist = Vector3D.Dot(cp - P, F);
    if (outDist > approach * 0.8 || T - stateT > 30)
    {
        if (pTask != Proto.O_NONE)
        {
            int c = pTask; pTask = Proto.O_NONE;
            Execute(c, pA, pB, pC, pId);
        }
        else StationHere();
    }
}

// Route around the hub's hull (its box from NAV, grown by Clearance) only when the straight path
// actually crosses it. The detour is on the drone's own side of the hull, so a formation keeps its
// shape: abreast drones simply drift past a stopping carrier and creep back to their slots.
Vector3D Avoid(Vector3D tgt, MatrixD m)
{
    if (!hub.Valid || hub.BoxH.LengthSquared() < 1) return tgt;
    Vector3D h = hub.BoxH + new Vector3D(clearance), bc = hub.BoxC;
    Vector3D p = HubFrame.ToLocal(m, fl.Pos) - bc, t = HubFrame.ToLocal(m, tgt) - bc;
    if (InBox(p, h))
    {
        // inside the margin (e.g. just off a dock): leave across the nearest face first,
        // unless the target is in here too (docking) or close
        if (InBox(t, h) || (t - p).Length() < clearance) return tgt;
        return HubFrame.ToWorld(m, bc + OutOf(p, h, 1.1));
    }
    if (!SegHits(p, t, h)) return tgt;
    Vector3D d = t - p;
    Vector3D q = p + d * Util.Clamp(-Vector3D.Dot(p, d) / Math.Max(1e-6, d.LengthSquared()), 0, 1);
    return HubFrame.ToWorld(m, bc + OutOf(q, h, 1.2));
}

static double Dim(Vector3D v, int i) { return i == 0 ? v.X : i == 1 ? v.Y : v.Z; }
static bool InBox(Vector3D v, Vector3D h) { return Math.Abs(v.X) < h.X && Math.Abs(v.Y) < h.Y && Math.Abs(v.Z) < h.Z; }

// v pushed out across the box face it is nearest to (by relative depth), other axes kept
static Vector3D OutOf(Vector3D v, Vector3D h, double k)
{
    int ax = 0; double best = -1;
    for (int i = 0; i < 3; i++) { double r = Math.Abs(Dim(v, i)) / Math.Max(1, Dim(h, i)); if (r > best) { best = r; ax = i; } }
    double s = Dim(v, ax) < 0 ? -1 : 1, e = s * Dim(h, ax) * k;
    return new Vector3D(ax == 0 ? e : v.X, ax == 1 ? e : v.Y, ax == 2 ? e : v.Z);
}

// segment p->t against the box [-h, h] (slab test)
static bool SegHits(Vector3D p, Vector3D t, Vector3D h)
{
    double t0 = 0, t1 = 1;
    for (int i = 0; i < 3; i++)
    {
        double pi = Dim(p, i), di = Dim(t, i) - pi, hi = Dim(h, i);
        if (Math.Abs(di) < 1e-9) { if (pi < -hi || pi > hi) return false; continue; }
        double a = (-hi - pi) / di, b = (hi - pi) / di;
        if (a > b) { double x = a; a = b; b = x; }
        t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
        if (t0 > t1) return false;
    }
    return true;
}

// ---------------------------------------------------------------- helpers
bool Connected() { return conn != null && conn.Status == MyShipConnectorStatus.Connected; }
bool NavFresh() { return hub.Valid && T - navT < linkTimeout; }

void DockedPower(bool docked)
{
    fl.SetPower(!docked);
    foreach (var b in tel.Bats)
    {
        if (!docked && !b.Enabled) b.Enabled = true;   // hub may have parked them during power-limited charging
        var mode = docked ? ChargeMode.Recharge : ChargeMode.Auto;
        if (b.ChargeMode != mode) b.ChargeMode = mode;
    }
}

void SetFreq()
{
    bool active = controlling || state == Proto.S_UNDOCKING;
    UpdateFrequency want = perfMode == "HIGH" ? UpdateFrequency.Update1
                         : perfMode == "LOW" ? UpdateFrequency.Update10
                         : (active ? UpdateFrequency.Update1 : UpdateFrequency.Update10);
    if (Runtime.UpdateFrequency != want) Runtime.UpdateFrequency = want;
}

void BuildEcho()
{
    var sb = new StringBuilder();
    sb.Append("FLEET DRONE  ").Append(call.Length > 0 ? call : Me.CubeGrid.CustomName).Append("  [").Append(role).Append("]\n");
    if (inFactory) sb.Append("IN FACTORY BAY: waiting for release\n");
    sb.Append("State: ").Append(Proto.StateName(state)).Append("   Order: ").Append(Proto.OrderName(task)).Append('\n');
    if (!provisioned) sb.Append("NOT PROVISIONED\nDock to a hub (auto-provision) or set\nNetId + Key in Custom Data, then run 'provision'.\n");
    else sb.Append("Net ").Append(netId).Append("   Hub: ").Append(registered ? (NavFresh() ? "linked" : "NO SIGNAL") : "searching...").Append('\n');
    sb.Append("Battery ").Append(Util.Pct(tel.Batt)).Append("   Hull ").Append(Util.Pct(tel.Integ)).Append("   Speed ").Append((int)fl.Vel.Length()).Append(" m/s\n");
    sb.Append("Engage mode ").Append(engageMode).Append("   Combat ").Append(combatOn ? "ON" : "off")
      .Append("   (").Append(heads.Count).Append(" warheads, ").Append(cams.Count).Append(" cameras, ").Append(aiOff.Count + aiFly.Count).Append(" AI)").Append(handed ? "  AI FLYING" : "").Append("\n");
    sb.Append(perf.Text()).Append('\n');
    sb.Append(warn);
    echo = sb.ToString();
}
