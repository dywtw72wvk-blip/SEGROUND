// ============================================================================
// Shared: Custom Data config via MyIni. Missing keys are written back with
// their defaults so every option is visible to the player.
// ============================================================================
class Cfg
{
    readonly MyIni ini = new MyIni();
    readonly IMyTerminalBlock blk;
    readonly string sec;
    bool dirty;
    public string Error = "";

    public Cfg(IMyTerminalBlock b, string section)
    {
        blk = b; sec = section;
        MyIniParseResult r;
        if (!ini.TryParse(b.CustomData, out r))
            Error = "Custom Data error (line " + r.LineNo + "): " + r.Error + ". Fix it, then recompile.";
    }

    public string S(string k, string d)
    {
        if (!ini.ContainsKey(sec, k)) { ini.Set(sec, k, d); dirty = true; }
        return ini.Get(sec, k).ToString(d);
    }

    public double D(string k, double d)
    {
        if (!ini.ContainsKey(sec, k)) { ini.Set(sec, k, d); dirty = true; }
        return ini.Get(sec, k).ToDouble(d);
    }

    public int I(string k, int d)
    {
        if (!ini.ContainsKey(sec, k)) { ini.Set(sec, k, d); dirty = true; }
        return ini.Get(sec, k).ToInt32(d);
    }

    public bool B(string k, bool d)
    {
        if (!ini.ContainsKey(sec, k)) { ini.Set(sec, k, d); dirty = true; }
        return ini.Get(sec, k).ToBoolean(d);
    }

    // Writes a single key, re-reading Custom Data first so player edits are not lost.
    public void Set(string k, string v)
    {
        MyIni fresh = new MyIni();
        if (!fresh.TryParse(blk.CustomData)) return;
        fresh.Set(sec, k, v);
        blk.CustomData = fresh.ToString();
        ini.Set(sec, k, v);
    }

    public void Save()
    {
        if (dirty && Error.Length == 0) { blk.CustomData = ini.ToString(); dirty = false; }
    }
}
