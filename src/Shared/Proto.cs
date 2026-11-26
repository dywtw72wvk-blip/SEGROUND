// ============================================================================
// Shared: protocol constants (must be identical in every Fleet System script)
// ============================================================================
static class Proto
{
    public const int Version = 4;   // v4: NAV hull box, SEARCH/ATTACK orders, CONTACT reports (v3: status ammo)

    // Message types (header Item4)
    public const int M_NAV = 1, M_HELLO = 2, M_STS = 3, M_ORD = 4, M_CON = 5, M_DES = 6,
                     M_HAUL = 7, M_VIS = 8, M_VREQ = 9;   // pickup request, carrier -> visiting hauler, hauler -> carrier

    // Orders / hub directives
    public const int O_NONE = 0, O_REGISTER = 1, O_ESCORT = 2, O_HOLD = 3, O_GOTO = 4,
                     O_DOCK = 5, O_UNDOCK = 6, O_STOP = 7,
                     O_SEARCH = 8,    // a = track start, b = forward * depth (world)
                     O_ATTACK = 9,    // dive now: a = target pos, b = target vel, c = aim offset from target centre (world), id = target
                     O_LOITER = 10,   // death ring: a = target pos, b = target vel (world), c = (slot, slots, ring distance), id = target
                     O_INTERCEPT = 11, // chase a launched drone: a = its pos, b = its vel (world), id = its entity
                     O_MINE = 12,      // bore one shaft: a = axis point at the deposit's depth, b = up * metres past it, c = (shaft, shafts, max depth), id = deposit (world)
                     O_HAUL = 13;      // pick up ore: a = the carrier's position when it asked (world), id = the carrier's IGC address

    // carrier -> visiting hauler (M_VIS): dock at this port (a, b, c = its position, out axis, up in the carrier frame;
    // id = connector), wait (port busy), loaded (leave), nothing to haul.  hauler -> carrier (M_VREQ): dock please, left
    public const int V_DOCK = 1, V_WAIT = 2, V_LOADED = 3, V_NONE = 4, R_DOCK = 1, R_LEFT = 2;

    // Hub modes (MODE_AUTO is a setting only; the active mode is CARRIER or BASE)
    public const int MODE_AUTO = 0, MODE_CARRIER = 1, MODE_BASE = 2;

    // Drone states
    public const int S_BOOT = 0, S_UNPROV = 1, S_REGISTERING = 2, S_IDLE = 3, S_HOLD = 4,
                     S_ESCORT = 5, S_GOTO = 6, S_APPROACH = 7, S_FINAL = 8, S_DOCKED = 9,
                     S_UNDOCKING = 10, S_LINKLOST = 11, S_ERROR = 12, S_PATROL = 13, S_ENGAGED = 14,
                     S_RECON = 15, S_ATTACK = 16, S_LOITER = 17, S_INTERCEPT = 18,
                     S_TRANSIT = 19, S_BORE = 20, S_ASCEND = 21;   // miner: to the shaft, drilling, climbing out

    // Drone status flags
    public const int F_DOCKED = 1, F_WANT_RTB = 2, F_LINKLOST = 4, F_NO_THRUST = 8, F_ARRIVED = 16,
                     F_ENGAGED = 32,   // combat blocks active
                     F_CONTACT = 64,   // drone's own turrets report a target (feeds hub alert)
                     F_FULL = 128,     // miner: cargo full, send me to a pad
                     F_DONE = 256;     // miner: shaft finished with room left, send the next one

    // Hub alert levels
    public const int ALERT_GREEN = 0, ALERT_YELLOW = 1, ALERT_RED = 2;

    // Carrier jump interlock
    public const int JUMP_NONE = 0, JUMP_PREP = 1, JUMP_READY = 2;

    // Factory states
    public const int FAC_IDLE = 0, FAC_BUILDING = 1, FAC_SECURE = 2, FAC_RELEASE = 3, FAC_BAYBUSY = 4,
                     FAC_STALLED = 5, FAC_ERROR = 6, FAC_OFF = 7;
    static readonly string[] facs = { "IDLE", "BUILDING", "SECURING", "RELEASING", "BAY BUSY", "WAITING PARTS", "ERROR", "OFF" };
    public static string FactoryName(int s) { return s >= 0 && s < facs.Length ? facs[s] : "?"; }

    static readonly string[] states = { "BOOT", "UNPROV", "REGISTER", "IDLE", "HOLD", "ESCORT", "GOTO",
                                        "APPROACH", "FINAL", "DOCKED", "UNDOCK", "NO LINK", "ERROR", "PATROL", "ENGAGED", "RECON", "ATTACK", "LOITER", "INTERCEPT", "TRANSIT", "BORE", "ASCEND" };
    static readonly string[] orders = { "-", "REG", "ESCORT", "HOLD", "GOTO", "DOCK", "UNDOCK", "STOP", "SEARCH", "ATTACK", "LOITER", "INTERCEPT", "MINE", "HAUL" };
    static readonly string[] modes = { "AUTO", "CARRIER", "BASE" };
    static readonly string[] alerts = { "GREEN", "YELLOW", "RED" };

    public static string StateName(int s) { return s >= 0 && s < states.Length ? states[s] : "?"; }
    public static string OrderName(int o) { return o >= 0 && o < orders.Length ? orders[o] : "?"; }
    public static string ModeName(int m) { return m >= 0 && m < modes.Length ? modes[m] : "?"; }
    public static string AlertName(int a) { return a >= 0 && a < alerts.Length ? alerts[a] : "?"; }
}
