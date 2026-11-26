// ============================================================================
// FLEET SYSTEM — FACTORY (phase 3, base fabrication line)
// Builds replacement drones with a projector + welders + merge block, then
// hands each new drone to the fleet through a bay connector.
//
// Blocks (on the hub, name contains the tag, default [Factory]):
//   1 projector  - load the drone blueprint (e.g. "FS Buster Atmo"), align it so the
//                  drone's merge block meets the [Factory] merge block, and tick
//                  "Keep projection"
//   welders      - covering the whole projection
//   1 merge block- holds the drone while it is welded
//   1 connector  - lines up with the drone's connector; locks it after release
//
// Cycle: BUILDING (weld) -> SECURING (welders off) -> RELEASING (merge off,
// connector locks) -> drone is docked in the bay; Hub Core provisions it, it
// charges, and the defense manager launches it -> bay free -> next build.
// Hub Core requests builds (TargetStock); "build N" queues builds by hand.
//
// Arguments: build <n> | on | off | reset | rescan
// ============================================================================
const string SEC = "Factory";

Net net;
Cfg cfg;
readonly Perf perf = new Perf();
IMyBroadcastListener needL, cmdL;
IMyProjector proj;
IMyShipMergeBlock merge;
IMyShipConnector bay;
readonly List<IMyShipWelder> welders = new List<IMyShipWelder>();
readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();

string tag = "[Factory]";
double buildTimeout, stallTime;

int state = Proto.FAC_IDLE, need, queue, built, lastRemaining = -1, total;
double T, stateT, progressT, needT = -1e9, nextScan, nextStatus, cooldown;
bool enabled = true, sawProjection;
string msg = "", echo = "", warn = "";

public Program()
{
    net = new Net(IGC);
    LoadConfig();
    Scan();
    var ini = new MyIni();
    if (!string.IsNullOrEmpty(Storage) && ini.TryParse(Storage))
    {
        built = ini.Get("f", "built").ToInt32(); queue = ini.Get("f", "queue").ToInt32(); enabled = ini.Get("f", "on").ToBoolean(true);
    }
    StopAll();
    if (!enabled) state = Proto.FAC_OFF;
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
}

public void Save()
{
    var ini = new MyIni();
    ini.Set("f", "built", built); ini.Set("f", "queue", queue); ini.Set("f", "on", enabled);
    Storage = ini.ToString();
}

void LoadConfig()
{
    cfg = new Cfg(Me, SEC);
    int netId = cfg.I("NetId", 1);
    tag = cfg.S("Tag", "[Factory]");
    buildTimeout = cfg.D("BuildTimeout", 1800);
    stallTime = cfg.D("StallTime", 60);
    cfg.Save();
    net.Configure(netId, "");
    if (needL != null) IGC.DisableBroadcastListener(needL);
    if (cmdL != null) IGC.DisableBroadcastListener(cmdL);
    needL = IGC.RegisterBroadcastListener(net.TFac);
    cmdL = IGC.RegisterBroadcastListener(net.TFac + "cmd");    // "build N" etc. forwarded by Hub Core / UI
}

void Scan()
{
    proj = null; merge = null; bay = null; welders.Clear();
    tmp.Clear();
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me) && b.CustomName.Contains(tag));
    foreach (var b in tmp)
    {
        if (b is IMyProjector && proj == null) proj = (IMyProjector)b;
        else if (b is IMyShipMergeBlock && merge == null) merge = (IMyShipMergeBlock)b;
        else if (b is IMyShipConnector && bay == null) bay = (IMyShipConnector)b;
        else if (b is IMyShipWelder) welders.Add((IMyShipWelder)b);
    }
    var w = new StringBuilder();
    if (cfg.Error.Length > 0) w.Append(cfg.Error).Append('\n');
    if (proj == null) w.Append("MISSING: projector named with " + tag + "\n");
    if (welders.Count == 0) w.Append("MISSING: welders named with " + tag + "\n");
    if (merge == null) w.Append("MISSING: merge block named with " + tag + "\n");
    if (bay == null) w.Append("MISSING: bay connector named with " + tag + "\n");
    warn = w.ToString();
}

bool Ready() { return proj != null && merge != null && bay != null && welders.Count > 0; }

int Demand() { return queue + (T - needT < 10 ? need : 0); }

public void Main(string arg, UpdateType src)
{
    T += Runtime.TimeSinceLastRun.TotalSeconds;
    perf.Tick(Runtime.LastRunTimeMs, T);
    while (needL.HasPendingMessage)
    {
        var d = needL.AcceptMessage().Data;
        if (d is int) { need = (int)d; needT = T; }
    }
    while (cmdL.HasPendingMessage)
    {
        var c = cmdL.AcceptMessage().Data as string;
        if (c != null) Command(c.Trim().ToLower());
    }
    if (!string.IsNullOrEmpty(arg)) Command(arg.Trim().ToLower());
    if ((src & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0) Tick();
    Echo(echo);
}

void Command(string a)
{
    var t = a.Split(' ');
    switch (t[0])
    {
        case "build":
            {
                int n = 1;
                if (t.Length > 1) int.TryParse(t[1], out n);
                queue += Math.Max(1, n);
                Set(state == Proto.FAC_OFF ? Proto.FAC_OFF : state, "queued " + queue + " build(s)");
            }
            break;
        case "off": case "stop":
            enabled = false; StopAll(); Set(Proto.FAC_OFF, "stopped");
            break;
        case "on": case "start":
            enabled = true; Set(Proto.FAC_IDLE, ""); cooldown = 0;
            break;
        case "reset":
            StopAll(); queue = 0; Set(enabled ? Proto.FAC_IDLE : Proto.FAC_OFF, "reset"); cooldown = 0;
            break;
        case "rescan":
            Scan();
            break;
    }
    BuildEcho();
}

void Tick()
{
    if (T >= nextScan) { Scan(); nextScan = T + 10; }
    if (state != Proto.FAC_OFF && !Ready())
    {
        if (state == Proto.FAC_BUILDING || state == Proto.FAC_STALLED || state == Proto.FAC_SECURE || state == Proto.FAC_RELEASE) StopAll();
        Set(Proto.FAC_ERROR, "missing factory blocks (see below)");
    }
    else switch (state)
    {
        case Proto.FAC_IDLE:
        case Proto.FAC_BAYBUSY:
        case Proto.FAC_ERROR:
            if (!enabled) { Set(Proto.FAC_OFF, ""); break; }
            if (T < cooldown) break;
            if (Demand() <= 0) { if (state != Proto.FAC_IDLE) Set(Proto.FAC_IDLE, ""); break; }
            if (bay.Status != MyShipConnectorStatus.Unconnected)
            {
                if (state != Proto.FAC_BAYBUSY) Set(Proto.FAC_BAYBUSY, "bay occupied: waiting for the drone to launch");
                break;
            }
            StartBuild();
            break;

        case Proto.FAC_BUILDING:
        case Proto.FAC_STALLED:
            {
                int rem = proj.RemainingBlocks, tot = proj.TotalBlocks;
                if (proj.IsProjecting && tot > 0) { sawProjection = true; total = tot; }
                if (!sawProjection)
                {
                    if (T - stateT > 8) Fail("projector shows nothing: load the drone blueprint, align it, enable 'Keep projection'");
                    break;
                }
                if (rem != lastRemaining)
                {
                    lastRemaining = rem; progressT = T;
                    if (state == Proto.FAC_STALLED) Set(Proto.FAC_BUILDING, "welding");
                }
                if (rem == 0 && T - stateT > 3) { Secure(); break; }
                if (state == Proto.FAC_BUILDING && T - progressT > stallTime) Set(Proto.FAC_STALLED, "welding stalled: out of components?");
                if (T - stateT > buildTimeout) Fail("build timed out");
            }
            break;

        case Proto.FAC_SECURE:
            // welders/projector are off; give the grid a moment, then release the merge
            if (T - stateT > 1) { merge.Enabled = false; Set(Proto.FAC_RELEASE, "releasing"); }
            break;

        case Proto.FAC_RELEASE:
            if (bay.Status == MyShipConnectorStatus.Connectable) bay.Connect();
            if (bay.Status == MyShipConnectorStatus.Connected)
            {
                built++;
                queue = Math.Max(0, queue - 1);
                IGC.SendBroadcastMessage(net.TCmd, "provision", TransmissionDistance.CurrentConstruct);
                merge.Enabled = true;
                cooldown = T + 5;
                Set(Proto.FAC_IDLE, "built drone #" + built);
            }
            else if (T - stateT > 8) Fail("released but the bay connector did not lock: check it lines up with the drone's connector");
            break;
    }

    if (T >= nextStatus)
    {
        int prog = total > 0 && (state == Proto.FAC_BUILDING || state == Proto.FAC_STALLED) ? (int)(1000.0 * (total - Math.Max(0, lastRemaining)) / total) : -1;
        IGC.SendBroadcastMessage(net.TFacSt, state + "|" + prog + "|" + built + "|" + Demand() + "|" + Util.Clean(msg), TransmissionDistance.CurrentConstruct);
        nextStatus = T + 2;
        BuildEcho();
    }

    // fast ticks only during the release/lock hand-off
    var want = state == Proto.FAC_SECURE || state == Proto.FAC_RELEASE ? UpdateFrequency.Update1 : UpdateFrequency.Update100;
    if (Runtime.UpdateFrequency != want) Runtime.UpdateFrequency = want;
}

void StartBuild()
{
    merge.Enabled = true;
    proj.Enabled = true;
    foreach (var w in welders) if (!w.Enabled) w.Enabled = true;
    sawProjection = false; lastRemaining = -1; progressT = T; total = 0;
    Set(Proto.FAC_BUILDING, "building drone #" + (built + 1));
}

void Secure()
{
    foreach (var w in welders) if (w.Enabled) w.Enabled = false;
    proj.Enabled = false;
    Set(Proto.FAC_SECURE, "securing");
}

void Fail(string why)
{
    StopAll();
    if (merge != null) merge.Enabled = true;
    cooldown = T + 30;
    Set(Proto.FAC_ERROR, why);
}

void StopAll()
{
    foreach (var w in welders) if (!w.Closed && w.Enabled) w.Enabled = false;
    if (proj != null && proj.Enabled) proj.Enabled = false;
}

void Set(int s, string m)
{
    if (s != state) stateT = T;
    state = s;
    msg = m;
}

void BuildEcho()
{
    var e = new StringBuilder();
    e.Append("FLEET FACTORY  ").Append(Proto.FactoryName(state)).Append('\n');
    if (msg.Length > 0) e.Append(msg).Append('\n');
    e.Append("Built: ").Append(built).Append("   Demand: ").Append(Demand()).Append(" (hub ").Append(T - needT < 10 ? need.ToString() : "-").Append(", queued ").Append(queue).Append(")\n");
    if ((state == Proto.FAC_BUILDING || state == Proto.FAC_STALLED) && total > 0)
        e.Append("Progress: ").Append(total - Math.Max(0, lastRemaining)).Append('/').Append(total).Append(" blocks\n");
    e.Append(perf.Text()).Append('\n').Append(warn);
    echo = e.ToString();
}
