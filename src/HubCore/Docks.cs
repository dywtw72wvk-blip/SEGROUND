// ============================================================================
// Hub Core: dock (connector) reservations
// ============================================================================
class Docks
{
    public readonly List<IMyShipConnector> List = new List<IMyShipConnector>();
    public readonly Dictionary<long, long> Res = new Dictionary<long, long>();   // connector id -> drone addr

    public void Bind(List<IMyShipConnector> l) { List.Clear(); List.AddRange(l); }

    public IMyShipConnector Get(long id)
    {
        foreach (var c in List) if (c.EntityId == id && !c.Closed) return c;
        return null;
    }

    public IMyShipConnector Reserve(Drone d)
    {
        // already sitting on one of our docks: that's its dock, don't send it elsewhere
        if (d.Docked && d.DockId != 0)
        {
            var at = Get(d.DockId);
            if (at != null) { Claim(d, d.DockId); Res[d.DockId] = d.Addr; return at; }
        }
        if (d.Conn != 0)
        {
            var e = Get(d.Conn);
            if (e != null && e.IsFunctional) { Res[e.EntityId] = d.Addr; return e; }
            Release(d);
        }
        foreach (var c in List)
        {
            if (c.Closed || !c.IsFunctional || !c.Enabled) continue;
            long who;
            if (Res.TryGetValue(c.EntityId, out who) && who != d.Addr) continue;
            if (c.Status == MyShipConnectorStatus.Connected) continue;
            Res[c.EntityId] = d.Addr;
            d.Conn = c.EntityId;
            return c;
        }
        return null;
    }

    public void Claim(Drone d, long connId)
    {
        if (d.Conn == connId || Get(connId) == null) return;
        Release(d);
        Res[connId] = d.Addr;
        d.Conn = connId;
    }

    public void Release(Drone d)
    {
        if (d.Conn == 0) return;
        long who;
        if (Res.TryGetValue(d.Conn, out who) && who == d.Addr) Res.Remove(d.Conn);
        d.Conn = 0;
    }

    public int Free()
    {
        int n = 0;
        foreach (var c in List)
            if (!c.Closed && c.IsFunctional && c.Status != MyShipConnectorStatus.Connected && !Res.ContainsKey(c.EntityId)) n++;
        return n;
    }

    public string NameOf(long id)
    {
        var c = Get(id);
        return c != null ? c.CustomName : "-";
    }
}
