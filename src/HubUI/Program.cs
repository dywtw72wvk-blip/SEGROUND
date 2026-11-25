// ============================================================================
// FLEET SYSTEM — HUB UI (phase 1)
// Renders the fleet list on LCDs / cockpit screens tagged [FS:Fleet] (or
// [FS:Fleet:1] for surface index 1) and turns button presses into commands
// for Hub Core (same construct, local IGC).
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
readonly List<IMyTerminalBlock> tmp = new List<IMyTerminalBlock>();
string fleetTag = "FS:Fleet";
double T, snapT = -1e9, nextScan;
bool flip;

// snapshot from Hub Core
int hMode, hCfgMode, hSpacing, hFree, hDocks, hOnline, hPerfUs;
bool hAutoRtb, hWp;
string hForm = "", hName = "";
readonly List<Row> rows = new List<Row>();
readonly List<string> evts = new List<string>();

// selection
string selCall = "";
int scroll, action;
static readonly string[] Actions = { "ESCORT", "HOLD", "GOTO", "DOCK", "UNDOCK", "STOP", "FORM", "MODE" };
static readonly string[] ActionCmd = { "escort", "hold", "goto", "dock", "undock", "stop", "formation next", "mode next" };

class Row
{
    public string Call = "", Role = "";
    public int State, Dir, Batt, Integ, Cargo, H2, Speed, Flags, LostS;
    public long Dist, LX, LY, LZ;
    public bool Unresp, Queued, Acked;
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
    cfg.Save();
    net.Configure(netId, "");
    if (snapL != null) IGC.DisableBroadcastListener(snapL);
    snapL = IGC.RegisterBroadcastListener(net.TSnap);
}

void Scan()
{
    fleetSurf.Clear();
    tmp.Clear();
    string open = "[" + fleetTag;
    GridTerminalSystem.GetBlocksOfType(tmp, b => b.IsSameConstructAs(Me) && b is IMyTextSurfaceProvider && b.CustomName.Contains(open));
    foreach (var b in tmp)
    {
        var p = (IMyTextSurfaceProvider)b;
        int idx = TagIndex(b.CustomName, fleetTag);
        if (idx < p.SurfaceCount) fleetSurf.Add(p.GetSurface(idx));
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
    Echo("FLEET HUB UI\nScreens: " + fleetSurf.Count + " tagged [" + fleetTag + "]\nCore link: " +
         (Linked() ? "OK" : "NONE (is Hub Core running with the same NetId?)") + "\n" + perf.Text());
}

bool Linked() { return T - snapT < 3; }

// ---------------------------------------------------------------- input
void Input(string arg)
{
    int n = rows.Count + 1, cur = Cursor();
    switch (arg.ToLower())
    {
        case "up": SetCursor((cur - 1 + n) % n); break;
        case "down": SetCursor((cur + 1) % n); break;
        case "left": action = (action - 1 + Actions.Length) % Actions.Length; break;
        case "right": action = (action + 1) % Actions.Length; break;
        case "apply": case "select":
            {
                string c = ActionCmd[action];
                if (action < 6) c += " " + Target();
                Send(c);
            }
            break;
        case "back": selCall = ""; break;
        default: Send(ReplaceSel(arg)); break;
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
    int ri = 0;
    evts.Clear();
    foreach (var line in s.Split('\n'))
    {
        if (line.Length < 3) continue;
        var f = line.Split('|');
        switch (f[0])
        {
            case "H":
                hMode = I(f, 1); hCfgMode = I(f, 2); hForm = f.Length > 3 ? f[3] : ""; hSpacing = I(f, 4); hAutoRtb = I(f, 5) == 1;
                hFree = I(f, 6); hDocks = I(f, 7); hWp = I(f, 8) == 1; hPerfUs = I(f, 9); hOnline = I(f, 10); hName = f.Length > 11 ? f[11] : "";
                break;
            case "D":
                {
                    if (ri >= rows.Count) rows.Add(new Row());
                    var r = rows[ri++];
                    r.Call = f[1]; r.Role = f.Length > 2 ? f[2] : ""; r.State = I(f, 3); r.Dir = I(f, 4);
                    r.Batt = I(f, 5); r.Integ = I(f, 6); r.Cargo = I(f, 7); r.H2 = I(f, 8);
                    r.Dist = L(f, 9); r.Speed = I(f, 10); r.Flags = I(f, 11); r.LostS = I(f, 12);
                    r.Unresp = I(f, 13) == 1; r.Queued = I(f, 14) == 1; r.Acked = I(f, 15) == 1;
                    r.LX = L(f, 16); r.LY = L(f, 17); r.LZ = L(f, 18);
                }
                break;
            case "E":
                if (f.Length > 2) evts.Add(Util.Age(I(f, 1)) + "  " + f[2]);
                break;
        }
    }
    if (rows.Count > ri) rows.RemoveRange(ri, rows.Count - ri);
}

void Render()
{
    flip = !flip;
    foreach (var s in fleetSurf) DrawFleet(s);
}
