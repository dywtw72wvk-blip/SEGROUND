// ============================================================================
// FLEET SYSTEM — HUB UI (phase 3)
// Renders the fleet list on LCDs / cockpit screens tagged [FS:Fleet], the
// tactical map on [FS:Map] and the hangar on [FS:Hangar] (add :1 etc. for a
// cockpit surface index), and
// turns button presses into commands for Hub Core (same construct, local IGC).
//
// Arguments: up | down | left | right | apply | back
//            anything else is forwarded to Hub Core; the word "sel" is replaced
//            by the selected drone (or "all"), e.g. "dock sel", "goto sel GPS:..."
// ============================================================================
const string SEC = "HubUI";

Net net;
Cfg cfg;
readonly Perf perf = new Perf();
IMyBroadcastListener snapL;
readonly List<IMyTextSurface> fleetSurf = new List<IMyTextSurface>();
readonly List<IMyTextSurface> mapSurf = new List<IMyTextSurface>();
readonly List<IMyTextSurface> hangarSurf = new List<IMyTextSurface>();
readonly List<IMyTextSurface> defSurf = new List<IMyTextSurface>();
string defTag = "FS:Defense", defMsg = "";
int defItem, defOpt;
double defMsgT = -1e9;
// Defense menu: up/down = item, left/right = value, apply = send. Commands are "<cmd> <value>".
static readonly string[] DefNames = { "SEARCH", "DESIGNATE", "STRIKE", "ALERT", "DEFENSE", "FORMATION", "SCRAMBLE", "RECALL" };
static readonly string[] DefCmds = { "search", "designate", "", "alert", "defense", "formation", "scramble", "recall" };
static readonly string[] StrikeCmds = { "loiter", "attack", "attackall", "abort" };
static readonly string[][] DefOpts = {
    new[] { "START", "STOP" }, new[] { "GO" }, new[] { "LOITER", "ATTACK 1", "ATTACK ALL", "ABORT" },
    new[] { "AUTO", "GREEN", "YELLOW", "RED" }, new[] { "ON", "OFF" },
    new[] { "WEDGE", "LINE", "COLUMN", "RING", "SPHERE" }, new[] { "GO" }, new[] { "GO" } };
readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();
readonly HashSet<IMyTextSurface> compactSurf = new HashSet<IMyTextSurface>();   // cockpit screens
string fleetTag = "FS:Fleet", mapTag = "FS:Map", hangarTag = "FS:Hangar";
double T, snapT = -1e9, nextScan;
bool flip, cockpitCompact, cpt;
float cockpitZoom = 1;

// snapshot from Hub Core
int hMode, hCfgMode, hSpacing, hFree, hDocks, hOnline, hPerfUs, hAlert, hJump, hSad;
// hangar
readonly List<DockRow> dockRows = new List<DockRow>();
bool facOk, pRepair, pRearm;
int facSt, facProg, facBuilt, facDemand, pCharging, pMaxC, pReserve, pStock, pLive, pRepairN, pRearmN;
string facMsg = "";
bool hAutoRtb, hWp, hAlertForced, hSafe, hDefense;
string hForm = "", hName = "";
readonly List<Row> rows = new List<Row>();
readonly List<string> evts = new List<string>();

// selection
string selCall = "";
int scroll, action;
// Row 1 acts on the selected drone (or ALL); row 2 is fleet-wide.
const int TargetedActions = 7;
static readonly string[] Actions = { "ESCORT", "HOLD", "GOTO", "DOCK", "UNDOCK", "STOP", "AUTO",
                                     "SCRAMBLE", "RECALL", "ALERT", "SAFE", "FORM", "MODE", "JUMP" };
static readonly string[] ActionCmd = { "escort", "hold", "goto", "dock", "undock", "stop", "auto",
                                       "scramble", "recall", "alert next", "safe toggle", "formation next", "mode next", "jump toggle" };

class DockRow { public string Name = "", Call = ""; public int State, Batt, Integ; }

class Row
{
    public string Call = "", Role = "";
    public int State, Dir, Batt, Integ, Cargo, H2, Speed, Flags, LostS, Ammo;
    public long Dist, LX, LY, LZ;
    public bool Unresp, Queued, Acked, Managed;
}

public Program()
{
    net = new Net(IGC);
    LoadConfig();
    Scan();
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

void LoadConfig()
{
    cfg = new Cfg(Me, SEC);
    int netId = cfg.I("NetId", 1);
    fleetTag = cfg.S("FleetTag", "FS:Fleet");
    mapTag = cfg.S("MapTag", "FS:Map");
    hangarTag = cfg.S("HangarTag", "FS:Hangar");
    defTag = cfg.S("DefenseTag", "FS:Defense");
    cockpitCompact = cfg.B("CockpitCompact", true);   // big-text layout on cockpit screens
    cockpitZoom = (float)Math.Max(0.5, Math.Min(3, cfg.D("CockpitZoom", 1.0)));
    cfg.Save();
    net.Configure(netId, "");
    if (snapL != null) IGC.DisableBroadcastListener(snapL);
    snapL = IGC.RegisterBroadcastListener(net.TSnap);
}

void Scan()
{
    compactSurf.Clear();
    Collect(fleetTag, fleetSurf);
    Collect(mapTag, mapSurf);
    Collect(hangarTag, hangarSurf);
    Collect(defTag, defSurf);
}

void Collect(string tag, List<IMyTextSurface> into)
{
    into.Clear();
    tmp.Clear();
    string open = "[" + tag;
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me) && b is IMyTextSurfaceProvider && b.CustomName.Contains(open));
    foreach (var b in tmp)
    {
        var p = (IMyTextSurfaceProvider)b;
        int idx = TagIndex(b.CustomName, tag);
        if (idx >= p.SurfaceCount) continue;
        var s = p.GetSurface(idx);
        into.Add(s);
        if (cockpitCompact && b is IMyShipController) compactSurf.Add(s);
    }
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
    T += Runtime.TimeSinceLastRun.TotalSeconds;
    perf.Tick(Runtime.LastRunTimeMs, T);
    while (snapL.HasPendingMessage)
    {
        var s = snapL.AcceptMessage().Data as string;
        if (s != null) { Parse(s); snapT = T; }
    }
    bool input = !string.IsNullOrEmpty(arg);
    if (input) Input(arg.Trim());
    if (T >= nextScan) { Scan(); nextScan = T + 10; }
    if (input || (src & UpdateType.Update10) != 0) Render();
    Echo("FLEET HUB UI\nScreens: " + fleetSurf.Count + " [" + fleetTag + "], " + mapSurf.Count + " [" + mapTag + "], " + hangarSurf.Count + " [" + hangarTag + "]\nCore link: " +
         (Linked() ? "OK" : "NONE (is Hub Core running with the same NetId?)") + "\n" + perf.Text());
}

bool Linked() { return T - snapT < 3; }

// ---------------------------------------------------------------- input
void Input(string arg)
{
    string a = arg.ToLower();
    if (a.StartsWith("def ")) { DefInput(a.Substring(4).Trim()); return; }
    int n = rows.Count + 1, cur = Cursor();
    switch (a)
    {
        case "up": SetCursor((cur - 1 + n) % n); break;
        case "down": SetCursor((cur + 1) % n); break;
        case "left": action = (action - 1 + Actions.Length) % Actions.Length; break;
        case "right": action = (action + 1) % Actions.Length; break;
        case "apply": case "select":
            {
                string c = ActionCmd[action];
                if (action < TargetedActions) c += " " + Target();
                Send(c);
            }
            break;
        case "back": selCall = ""; break;
        default: Send(ReplaceSel(arg)); break;
    }
}

void DefInput(string a)
{
    int n = DefNames.Length;
    if (a == "up") { defItem = (defItem - 1 + n) % n; defOpt = DefCurrent(defItem); }
    else if (a == "down") { defItem = (defItem + 1) % n; defOpt = DefCurrent(defItem); }
    else if (a == "left" || a == "right")
    {
        int m = DefOpts[defItem].Length;
        defOpt = (defOpt + (a == "left" ? m - 1 : 1)) % m;
    }
    else if (a == "apply" || a == "select")
    {
        string v = DefOpts[defItem][defOpt];
        string c = DefCmds[defItem].Length == 0 ? StrikeCmds[defOpt] : DefCmds[defItem] + (v == "GO" ? "" : " " + v.ToLower());
        Send(c);
        defMsg = "sent: " + c; defMsgT = T;
    }
}

// Option index matching the hub's current state (so the selector starts at "now")
int DefCurrent(int i)
{
    switch (i)
    {
        case 0: return (hSad & 1) != 0 ? 1 : 0;
        case 3: return hAlertForced ? hAlert + 1 : 0;
        case 4: return hDefense ? 0 : 1;
        case 5: return Math.Max(0, Array.IndexOf(DefOpts[5], hForm));
        default: return 0;
    }
}

string Target() { return selCall.Length > 0 ? selCall : "all"; }

string ReplaceSel(string arg)
{
    var parts = arg.Split(' ');
    for (int i = 0; i < parts.Length; i++) if (parts[i].ToLower() == "sel") parts[i] = Target();
    return string.Join(" ", parts);
}

void Send(string cmd) { IGC.SendBroadcastMessage(net.TCmd, cmd, TransmissionDistance.CurrentConstruct); }

int Cursor()
{
    if (selCall.Length == 0) return 0;
    for (int i = 0; i < rows.Count; i++) if (rows[i].Call == selCall) return i + 1;
    selCall = "";
    return 0;
}

void SetCursor(int i) { selCall = i == 0 ? "" : rows[i - 1].Call; }

// ---------------------------------------------------------------- snapshot
static int I(string[] f, int i) { int v; return i < f.Length && int.TryParse(f[i], out v) ? v : 0; }
static long L(string[] f, int i) { long v; return i < f.Length && long.TryParse(f[i], out v) ? v : 0; }

void Parse(string s)
{
    int ri = 0, ki = 0;
    evts.Clear();
    facOk = false;
    foreach (var line in s.Split('\n'))
    {
        if (line.Length < 3) continue;
        var f = line.Split('|');
        switch (f[0])
        {
            case "H":
                hMode = I(f, 1); hCfgMode = I(f, 2); hForm = f.Length > 3 ? f[3] : ""; hSpacing = I(f, 4); hAutoRtb = I(f, 5) == 1;
                hFree = I(f, 6); hDocks = I(f, 7); hWp = I(f, 8) == 1; hPerfUs = I(f, 9); hOnline = I(f, 10); hName = f.Length > 11 ? f[11] : "";
                hAlert = I(f, 12); hAlertForced = I(f, 13) == 1; hSafe = I(f, 14) == 1; hDefense = I(f, 15) == 1; hJump = I(f, 16); hSad = I(f, 17);
                break;
            case "D":
                {
                    if (ri >= rows.Count) rows.Add(new Row());
                    var r = rows[ri++];
                    r.Call = f[1]; r.Role = f.Length > 2 ? f[2] : ""; r.State = I(f, 3); r.Dir = I(f, 4);
                    r.Batt = I(f, 5); r.Integ = I(f, 6); r.Cargo = I(f, 7); r.H2 = I(f, 8);
                    r.Dist = L(f, 9); r.Speed = I(f, 10); r.Flags = I(f, 11); r.LostS = I(f, 12);
                    r.Unresp = I(f, 13) == 1; r.Queued = I(f, 14) == 1; r.Acked = I(f, 15) == 1;
                    r.LX = L(f, 16); r.LY = L(f, 17); r.LZ = L(f, 18); r.Managed = I(f, 19) == 1; r.Ammo = f.Length > 20 ? I(f, 20) : -1;
                }
                break;
            case "K":
                {
                    if (ki >= dockRows.Count) dockRows.Add(new DockRow());
                    var k = dockRows[ki++];
                    k.Name = f[1]; k.State = I(f, 2); k.Call = f.Length > 3 ? f[3] : ""; k.Batt = I(f, 4); k.Integ = I(f, 5);
                }
                break;
            case "F":
                facOk = true; facSt = I(f, 1); facProg = I(f, 2); facBuilt = I(f, 3); facDemand = I(f, 4); facMsg = f.Length > 5 ? f[5] : "";
                break;
            case "P":
                pRepair = I(f, 1) == 1; pRearm = I(f, 2) == 1; pCharging = I(f, 3); pMaxC = I(f, 4); pReserve = I(f, 5);
                pStock = I(f, 6); pLive = I(f, 7); pRepairN = I(f, 8); pRearmN = I(f, 9);
                break;
            case "E":
                if (f.Length > 2) evts.Add(Util.Age(I(f, 1)) + "  " + f[2]);
                break;
        }
    }
    if (rows.Count > ri) rows.RemoveRange(ri, rows.Count - ri);
    if (dockRows.Count > ki) dockRows.RemoveRange(ki, dockRows.Count - ki);
}

void Render()
{
    flip = !flip;
    foreach (var s in fleetSurf) { cpt = compactSurf.Contains(s); DrawFleet(s); }
    foreach (var s in mapSurf) { cpt = compactSurf.Contains(s); DrawMap(s); }
    foreach (var s in hangarSurf) { cpt = compactSurf.Contains(s); DrawHangar(s); }
    foreach (var s in defSurf) { cpt = compactSurf.Contains(s); DrawDefense(s); }
}
