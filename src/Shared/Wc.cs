// ============================================================================
// Shared: WeaponCore PB API, only the two calls the Fleet System uses.
// Without WeaponCore (or before it has started) Ok stays false and both calls
// return nothing, so the scripts run the same in vanilla worlds.
//   Focus(grid)   the target the player locked with WeaponCore's targeting HUD
//   Threats(me)   every threat WeaponCore's grid AI is tracking (needs a WC weapon on the grid)
// ============================================================================
class Wc
{
    Action<IMyTerminalBlock, IDictionary<MyDetectedEntityInfo, float>> threats;
    Func<long, int, MyDetectedEntityInfo> focus;
    public bool Ok;
    readonly Dictionary<MyDetectedEntityInfo, float> buf = new Dictionary<MyDetectedEntityInfo, float>();

    public void Init(IMyTerminalBlock me)
    {
        try
        {
            var p = me.GetProperty("WcPbAPI");
            var d = p == null ? null : p.As<IReadOnlyDictionary<string, Delegate>>().GetValue(me);
            if (d == null) return;
            Delegate x;
            if (d.TryGetValue("GetSortedThreats", out x)) threats = x as Action<IMyTerminalBlock, IDictionary<MyDetectedEntityInfo, float>>;
            if (d.TryGetValue("GetAiFocus", out x)) focus = x as Func<long, int, MyDetectedEntityInfo>;
            Ok = threats != null || focus != null;
        }
        catch { Ok = false; }
    }

    public Dictionary<MyDetectedEntityInfo, float> Threats(IMyTerminalBlock me)
    {
        buf.Clear();
        if (threats != null) try { threats(me, buf); } catch { }
        return buf;
    }

    public MyDetectedEntityInfo Focus(long grid)
    {
        if (focus != null) try { return focus(grid, 0); } catch { }
        return new MyDetectedEntityInfo();
    }
}
