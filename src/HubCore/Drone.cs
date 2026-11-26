// ============================================================================
// Hub Core: registry record for one drone
// ============================================================================
class Drone
{
    public long Addr;
    public string Call = "", Role = "", Grid = "";
    public long Caps;
    public double MaxAccel, MaxSpeed;

    // last reported status
    public int State, Task, Flags;
    public long Ack, DockId;
    public Vector3D Pos, Vel;
    public float Batt = -1, H2 = -1, Cargo = -1, Integ = 1, Ammo = -1;
    public double DockedSince;
    public double Seen = -1e9;
    public bool LostFlag = true, Restored;

    // hub directive (what we want this drone doing)
    public int Dir;
    public Vector3D A, B, C;
    public long Id;
    public bool SlotOk;          // escort slot computed & sent
    public int Slot;             // formation slot index (nearest-slot assignment)

    // reliable delivery
    public long Seq;             // seq of outstanding directive (0 = nothing to deliver)
    public double Sent;
    public int Tries;
    public bool Unresp;

    public long Conn;            // reserved dock connector
    public bool Managed;         // under automatic defense control
    public bool Held;            // kept docked: auto-RTB would recall it at once (logged once)
    public double Miss = 1e11;   // dive: closest approach to the target so far (1e11 = not diving)
    public Vector3D MissOff, MissTo, From;
    public bool Passed;
    public int Dep = -1, Shaft = -1;   // mining: deposit and shaft being worked

    public bool Docked { get { return (Flags & Proto.F_DOCKED) != 0; } }
    public bool Acked { get { return Seq == 0 || Ack == Seq; } }
}
