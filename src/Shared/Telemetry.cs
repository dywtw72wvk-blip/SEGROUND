// ============================================================================
// Shared (drones): power / gas / cargo / integrity readers
// ============================================================================
class Telemetry
{
    public readonly List<IMyBatteryBlock> Bats = new List<IMyBatteryBlock>();
    readonly List<IMyGasTank> h2 = new List<IMyGasTank>();
    readonly List<IMyTerminalBlock> inv = new List<IMyTerminalBlock>();
    readonly List<IMyTerminalBlock> guns = new List<IMyTerminalBlock>();
    readonly List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
    public int Baseline;   // highest block count seen = "100% hull"
    public float Batt = -1, H2 = -1, Cargo = -1, Integ = 1, Ammo = -1;

    public void Bind(List<IMyTerminalBlock> blocks)
    {
        Bats.Clear(); h2.Clear(); inv.Clear(); all.Clear(); guns.Clear();
        foreach (var b in blocks)
        {
            all.Add(b);
            var bat = b as IMyBatteryBlock;
            if (bat != null) { Bats.Add(bat); continue; }
            var tank = b as IMyGasTank;
            if (tank != null)
            {
                if (b.BlockDefinition.SubtypeName.Contains("Hydrogen") || b.DefinitionDisplayNameText.Contains("Hydrogen")) h2.Add(tank);
                continue;
            }
            if (b is IMyUserControllableGun && b.HasInventory) { guns.Add(b); continue; }
            if (b is IMyCargoContainer && b.HasInventory) inv.Add(b);
        }
        if (all.Count > Baseline) Baseline = all.Count;
    }

    public void Read()
    {
        double cur = 0, max = 0;
        foreach (var b in Bats) if (!b.Closed && b.IsFunctional) { cur += b.CurrentStoredPower; max += b.MaxStoredPower; }
        Batt = max > 0 ? (float)(cur / max) : -1;

        cur = 0; max = 0;
        foreach (var t in h2) if (!t.Closed && t.IsFunctional) { cur += t.FilledRatio * t.Capacity; max += t.Capacity; }
        H2 = max > 0 ? (float)(cur / max) : -1;

        cur = 0; max = 0;
        foreach (var b in inv)
        {
            if (b.Closed) continue;
            var i = b.GetInventory(0);
            cur += (float)i.CurrentVolume; max += (float)i.MaxVolume;
        }
        Cargo = max > 0 ? (float)(cur / max) : -1;

        // ammo = fill of weapon magazines/inventories (turrets and fixed guns)
        cur = 0; max = 0;
        foreach (var b in guns)
        {
            if (b.Closed) continue;
            var i = b.GetInventory(0);
            cur += (float)i.CurrentVolume; max += (float)i.MaxVolume;
        }
        Ammo = max > 0 ? (float)(cur / max) : -1;

        int ok = 0;
        foreach (var b in all) if (!b.Closed && b.IsFunctional) ok++;
        Integ = Baseline > 0 ? (float)ok / Baseline : 1;
    }
}
