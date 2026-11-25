// ============================================================================
// Shared: protocol constants (must be identical in every Fleet System script)
// ============================================================================
static class Proto
{
    public const int Version = 2;   // v2: NAV carries patrol phase/rate; ESCORT carries engage/leash/patrol

    // Message types (header Item4)
    public const int M_NAV = 1, M_HELLO = 2, M_STS = 3, M_ORD = 4;

    // Orders / hub directives
    public const int O_NONE = 0, O_REGISTER = 1, O_ESCORT = 2, O_HOLD = 3, O_GOTO = 4,
                     O_DOCK = 5, O_UNDOCK = 6, O_STOP = 7;

    // Hub modes (MODE_AUTO is a setting only; the active mode is CARRIER or BASE)
    public const int MODE_AUTO = 0, MODE_CARRIER = 1, MODE_BASE = 2;

    // Drone states
    public const int S_BOOT = 0, S_UNPROV = 1, S_REGISTERING = 2, S_IDLE = 3, S_HOLD = 4,
                     S_ESCORT = 5, S_GOTO = 6, S_APPROACH = 7, S_FINAL = 8, S_DOCKED = 9,
                     S_UNDOCKING = 10, S_LINKLOST = 11, S_ERROR = 12, S_PATROL = 13, S_ENGAGED = 14;

    // Drone status flags
    public const int F_DOCKED = 1, F_WANT_RTB = 2, F_LINKLOST = 4, F_NO_THRUST = 8, F_ARRIVED = 16,
                     F_ENGAGED = 32,   // combat blocks active
                     F_CONTACT = 64;   // drone's own turrets report a target (feeds hub alert)

    // Hub alert levels
    public const int ALERT_GREEN = 0, ALERT_YELLOW = 1, ALERT_RED = 2;

    static readonly string[] states = { "BOOT", "UNPROV", "REGISTER", "IDLE", "HOLD", "ESCORT", "GOTO",
                                        "APPROACH", "FINAL", "DOCKED", "UNDOCK", "NO LINK", "ERROR", "PATROL", "ENGAGED" };
    static readonly string[] orders = { "-", "REG", "ESCORT", "HOLD", "GOTO", "DOCK", "UNDOCK", "STOP" };
    static readonly string[] modes = { "AUTO", "CARRIER", "BASE" };
    static readonly string[] alerts = { "GREEN", "YELLOW", "RED" };

    public static string StateName(int s) { return s >= 0 && s < states.Length ? states[s] : "?"; }
    public static string OrderName(int o) { return o >= 0 && o < orders.Length ? orders[o] : "?"; }
    public static string ModeName(int m) { return m >= 0 && m < modes.Length ? modes[m] : "?"; }
    public static string AlertName(int a) { return a >= 0 && a < alerts.Length ? alerts[a] : "?"; }
}
