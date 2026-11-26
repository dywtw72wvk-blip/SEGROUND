// ============================================================================
// FS MINING — DEPOT (base dispatcher)
// Runs on your base. Registers haulers (role Hauler) on the mining network,
// hears pickup requests from mining carriers (Mine Core's HAUL broadcast),
// sends idle haulers to them, brings them home to a [Dock] connector and
// unloads them into the base's cargo containers.
//
//   request   a carrier broadcasts HAUL (where it is, how much ore waits) every
//             second while it wants a pickup; requests nobody repeats for 20 s
//             are dropped
//   dispatch  per carrier, enough haulers for what waits (ore / HaulerLoad),
//             at most MaxPerCarrier: a hauler docked here, unloaded, charged
//             above LaunchBattery and fuelled above LaunchHydrogen, LaunchInterval
//             apart. The hauler flies to the carrier, asks it for its [Haul]
//             port, docks, is loaded up to what it can lift, and leaves.
//   home      a hauler back from a carrier (loaded: F_FULL, empty: F_DONE) gets a
//             [Dock] connector here; docked, its ore goes into the base's cargo
//             containers and its tanks refill from the base
//   page      text status on LCDs / cockpit screens tagged [FS:Depot]
//
// Commands:   dispatch <on|off>   recall [hauler|all]   provision   forget <callsign>   reload
// Set the same NetId and Key here as on the carriers' Mine Core (default net 2).
// ============================================================================
const string SEC = "Depot";

readonly List<IMyCargoContainer> store = new List<IMyCargoContainer>();
readonly List<IMyTerminalBlock> storeB = new List<IMyTerminalBlock>();
readonly List<IMyTextSurface> pageSurf = new List<IMyTextSurface>();
IMyBroadcastListener haulL;

class Req { public long Addr; public Vector3D P; public double M3, Kg, T; public string Name = ""; }
readonly Dictionary<long, Req> reqs = new Dictionary<long, Req>();
readonly List<long> stale = new List<long>();

double launchBatt, launchH2, launchInterval, haulerLoad, nextLaunch, nextUnload, nextPage;
int maxPer, unloading;
bool dispatchOn = true;

public Program()
{
    net = new Net(IGC);
    uni = IGC.UnicastListener;
    HubRole = "HAULER";
    LoadConfig();
    var ini = new MyIni();
    if (!string.IsNullOrEmpty(Storage) && ini.TryParse(Storage))
    {
        epoch = ini.Get("hub", "epoch").ToInt32(); ctr = ini.Get("hub", "ctr").ToInt64(); callCtr = ini.Get("hub", "calls").ToInt32();
        dispatchOn = ini.Get("hub", "dispatch").ToBoolean(true);
        var secs = new List<string>();
        ini.GetSections(secs);
        foreach (var s in secs) LoadReg(ini, s);
    }
    if (epoch <= 0) epoch = new Random().Next(1, int.MaxValue);
    Scan();
    UpdateFrame(0);
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    Log("Depot online, net " + netId);
}

public void Save()
{
    var ini = new MyIni();
    SaveReg(ini);
    ini.Set("hub", "dispatch", dispatchOn);
    Storage = ini.ToString();
}

void LoadConfig()
{
    HubConfig(SEC, 2);
    launchBatt = cfg.D("LaunchBattery", 0.6);        // a hauler leaves above this charge ...
    launchH2 = cfg.D("LaunchHydrogen", 0.5);         // ... and this much hydrogen (ships without tanks: ignored)
    launchInterval = Math.Max(2, cfg.D("LaunchInterval", 10));
    haulerLoad = cfg.D("HaulerLoad", 250000);        // kg a hauler brings back (ALZ Hezen on a planet: ~250 t)
    maxPer = Math.Max(1, cfg.I("MaxPerCarrier", 3)); // haulers working one carrier at once
    cfg.Save();
    HubListen();
    if (haulL != null) IGC.DisableBroadcastListener(haulL);
    haulL = IGC.RegisterBroadcastListener(net.THaul);
}

void Scan()
{
    tmp.Clear();
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me));
    dockConns.Clear(); store.Clear(); storeB.Clear(); pageSurf.Clear();
    refCtl = null;
    bool antennaOk = false;
    foreach (var b in tmp)
    {
        var c = b as IMyShipConnector; if (c != null) { if (b.CustomName.Contains(dockTag)) dockConns.Add(c); continue; }
        var h = b as IMyCargoContainer; if (h != null) { store.Add(h); storeB.Add(h); continue; }
        var s = b as IMyShipController; if (s != null && (refCtl == null || s.IsMainCockpit)) refCtl = s;
        var a = b as IMyRadioAntenna; if (a != null && a.IsWorking && a.EnableBroadcasting) antennaOk = true;
        var p = b as IMyTextSurfaceProvider;
        if (p != null && HasTag(b.CustomName, "FS:Depot"))
        {
            int idx = TagIndex(b.CustomName, "FS:Depot");
            if (idx < p.SurfaceCount) { var sf = p.GetSurface(idx); sf.ContentType = ContentType.TEXT_AND_IMAGE; sf.Font = "Monospace"; pageSurf.Add(sf); }
        }
    }
    refBlk = refCtl != null ? (IMyTerminalBlock)refCtl : Me;
    docks.Bind(dockConns);
    var w = new StringBuilder();
    if (cfg.Error.Length > 0) w.Append(cfg.Error).Append('\n');
    if (dockConns.Count == 0) w.Append("WARN: no connectors named with " + dockTag + ": haulers can't dock here.\n");
    if (store.Count == 0) w.Append("WARN: no cargo containers: nowhere to unload.\n");
    if (!antennaOk) w.Append("WARN: no working broadcasting antenna: haulers and carriers can't hear the depot.\n");
    if (key == "change-me") w.Append("NOTE: change Key in Custom Data (and on the carriers) before multiplayer.\n");
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
    while (haulL.HasPendingMessage)
    {
        var m = haulL.AcceptMessage();
        Vector3D pos; double m3, kg; string name;
        if (!net.ReadHaul(m, out pos, out m3, out kg, out name)) continue;
        Req r;
        if (!reqs.TryGetValue(m.Source, out r)) { reqs[m.Source] = r = new Req { Addr = m.Source }; Log("Pickup request: " + Util.Clean(name) + ", " + (int)(kg / 1000) + " t"); }
        r.P = pos; r.M3 = m3; r.Kg = kg; r.T = T; r.Name = name;
    }
    if (T >= nextNav) { SendNav(); nextNav = T + (moving ? 0.1 : 1); }
    if (T >= nextTask) { Tasking(); nextTask = T + 0.5; }
    if (T >= nextUnload) { Unload(); nextUnload = T + 0.5; }
    if (autoProv && T >= nextProv) { Provision(false); nextProv = T + 10; }
    if (T >= nextPage) { Page(); nextPage = T + 1; }
    if (T >= nextEcho) { BuildEcho(); nextEcho = T + 1; }
    UpdateFrequency f = moving || perfMode == "HIGH" ? UpdateFrequency.Update1 : UpdateFrequency.Update10;
    if (Runtime.UpdateFrequency != f) Runtime.UpdateFrequency = f;
}

void OnUnicast(MyIGCMessage m) { }
void OnDock(Drone d) { }

bool Ready(Drone d) { return d.Docked && d.Dir != Proto.O_HAUL && d.Cargo < 0.02 && d.Batt >= launchBatt && (d.H2 < 0 || d.H2 >= launchH2); }

int On(long carrier) { int n = 0; foreach (var d in reg.Values) if (!d.LostFlag && d.Dir == Proto.O_HAUL && d.Id == carrier) n++; return n; }

void Tasking()
{
    TaskRegistry();
    stale.Clear();
    foreach (var kv in reqs) if (T - kv.Value.T > 20) stale.Add(kv.Key);
    foreach (var k in stale) { Log("Pickup request ended: " + Util.Clean(reqs[k].Name)); reqs.Remove(k); }
    // haulers back from a carrier: home
    foreach (var d in sorted)
    {
        if (d.LostFlag || d.Dir != Proto.O_HAUL) continue;
        if ((d.Flags & Proto.F_FULL) != 0) { Log(d.Call + " loaded: coming home"); OrderDock(d); }
        else if ((d.Flags & Proto.F_DONE) != 0) { Log(d.Call + " back empty"); OrderDock(d); }
    }
    if (!dispatchOn || !bootLocked || T < nextLaunch) return;
    // the request that most needs a hauler, and a ready hauler for it
    Req best = null; double need = 0;
    foreach (var r in reqs.Values)
    {
        double n = Math.Min(maxPer, Math.Ceiling(r.Kg / Math.Max(1000, haulerLoad))) - On(r.Addr);
        if (n > need) { need = n; best = r; }
    }
    if (best == null) return;
    foreach (var d in sorted)
    {
        if (d.LostFlag || !Ready(d)) continue;
        Issue(d, Proto.O_HAUL, best.P, Vector3D.Zero, Vector3D.Zero, best.Addr);
        Log(d.Call + " -> " + Util.Clean(best.Name) + ", " + Util.Dist((best.P - M.Translation).Length()));
        nextLaunch = T + launchInterval;
        return;
    }
}

// ore from a hauler on a [Dock] connector into the base's cargo containers
void Unload()
{
    unloading = 0;
    foreach (var c in docks.List)
    {
        if (c.Closed || c.Status != MyShipConnectorStatus.Connected || c.OtherConnector == null) continue;
        bool left;
        MoveOre(Docked(c, false), storeB, 8, 1e12, out left);
        if (left) unloading++;
    }
}

void Command(string raw)
{
    string[] t = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (t.Length == 0) return;
    string v = t[0].ToLower(), a1 = t.Length > 1 ? t[1] : "";
    switch (v)
    {
        case "dispatch":
            dispatchOn = a1.ToLower() != "off";
            Log("Dispatch " + (dispatchOn ? "on" : "off: no new trips"));
            break;
        case "recall": case "dock": ForTargets(a1.Length > 0 ? a1 : "all", OrderDock); break;
        case "provision": Provision(true); break;
        case "forget":
            {
                Drone hit = null;
                foreach (var d in reg.Values) if (d.Call.Equals(a1, StringComparison.OrdinalIgnoreCase)) hit = d;
                if (hit == null) { Log("forget: no hauler " + a1); break; }
                docks.Release(hit); reg.Remove(hit.Addr); Log("Forgot " + hit.Call);
            }
            break;
        case "refresh": case "reload": LoadConfig(); Scan(); Log("Config reloaded"); break;
        default: Log("Unknown command: " + Util.Clean(raw)); break;
    }
}

void Status(StringBuilder e, int evN)
{
    double max = 0, free = 0;
    foreach (var h in store) { if (h.Closed) continue; var inv = h.GetInventory(0); max += (float)inv.MaxVolume; free += (float)inv.MaxVolume - (float)inv.CurrentVolume; }
    e.Append("DEPOT  net ").Append(netId).Append(dispatchOn ? "" : "  DISPATCH OFF").Append('\n');
    e.Append("Storage ").Append(Util.Pct((float)(max > 0 ? 1 - free / max : 0))).Append("  docks ").Append(docks.Free()).Append('/').Append(docks.List.Count).Append(" free").Append(unloading > 0 ? "  UNLOADING" : "").Append('\n');
    e.Append("Requests ").Append(reqs.Count).Append('\n');
    foreach (var r in reqs.Values) e.Append(" ").Append(Pad(Util.Clean(r.Name), 14)).Append(Pad((int)(r.Kg / 1000) + "t", 7)).Append(On(r.Addr)).Append(" hauler(s)\n");
    e.Append("Haulers ").Append(out_).Append(" out, ").Append(docked_).Append(" docked\n");
    foreach (var d in sorted)
        e.Append(" ").Append(Pad(d.Call, 10)).Append(Pad(d.LostFlag ? "LOST" : Proto.StateName(d.State), 10)).Append(Pad(Util.Pct(d.Cargo), 5)).Append(Pad(Util.Pct(d.Batt), 5)).Append(Util.Pct(d.H2)).Append('\n');
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - evN); i--) e.Append(Util.Age((int)(T - evT[i]))).Append(' ').Append(evText[i]).Append('\n');
}

void Page()
{
    if (pageSurf.Count == 0) return;
    sb.Clear(); Status(sb, 4);
    string txt = sb.ToString();
    foreach (var s in pageSurf) { s.FontSize = Math.Max(0.3f, s.SurfaceSize.X / (32 * 19.5f)); s.WriteText(txt); }
}

void BuildEcho()
{
    var e = new StringBuilder();
    Status(e, 0);
    e.Append(perf.Text()).Append('\n').Append(warn);
    for (int i = evText.Count - 1; i >= Math.Max(0, evText.Count - 6); i--) e.Append(Util.Age((int)(T - evT[i]))).Append("  ").Append(evText[i]).Append('\n');
    echo = e.ToString();
}
