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
    public float Batt = -1, H2 = -1, Cargo = -1, Integ = 1;
    public double Seen = -1e9;
    public bool LostFlag = true, Restored;

    // hub directive (what we want this drone doing)
    public int Dir;
    public Vector3D A, B, C;
    public long Id;
    public bool SlotOk;          // escort slot computed & sent

    // reliable delivery
    public long Seq;             // seq of outstanding directive (0 = nothing to deliver)
    public double Sent;
    public int Tries;
    public bool Unresp;

    public long Conn;            // reserved dock connector

    public bool Docked { get { return (Flags & Proto.F_DOCKED) != 0; } }
    public bool Acked { get { return Seq == 0 || Ack == Seq; } }
}
