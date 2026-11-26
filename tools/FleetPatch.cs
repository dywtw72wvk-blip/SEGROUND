// ============================================================================
// FleetPatch: writes the Fleet System scripts straight into programmable blocks
// inside your Space Engineers saved worlds and blueprints, so there is nothing
// to paste in game. Run by install.bat (compiled on the fly by Windows
// PowerShell's Add-Type; C# 5, no extra installs).
//
// Which programmable blocks get which script:
//   name contains [Fleet Core]   -> Hub Core
//   name contains [Fleet UI]     -> Hub UI
//   name contains [Fleet Drone]  -> Drone
//   name contains [Fleet Factory]-> Factory
//   or the block already runs a Fleet System script -> updated to the latest
//
// Also installs the ship blueprints in ..\blueprints into Blueprints\local (your
// F10 list) after checking every block against the game's own CubeBlocks
// definitions (block type/ID exists, right grid size, no overlapping blocks).
//
// Safety: shows the plan and asks first; refuses while Space Engineers is
// running; backs up every file it changes (*.fleetbak-<time>); retires the
// game's binary caches (.sbsB5 / .sbcB5 / .sbcPB) so the edited XML is used.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

public static class FleetPatch
{
    const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    static readonly string[] Kinds = { "HubCore", "HubUI", "Drone", "Factory" };
    static readonly string[] Tags = { "[Fleet Core]", "[Fleet UI]", "[Fleet Drone]", "[Fleet Factory]" };
    static readonly Regex Header = new Regex(@"FLEET SYSTEM (\S+) \S+ (HubCore|HubUI|Drone|Factory)\b");

    class Hit { public string Block; public string Kind; public string Change; }
    class Plan { public string Path; public string Label; public List<Hit> Hits = new List<Hit>(); }

    // ------------------------------------------------------------------ entry points
    public static int Main(string[] args)
    {
        string dist = null, se = null; bool yes = false, dry = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dist" && i + 1 < args.Length) dist = args[++i];
            else if (args[i] == "--se" && i + 1 < args.Length) se = args[++i];
            else if (args[i] == "--yes") yes = true;
            else if (args[i] == "--dry-run") dry = true;
        }
        if (dist == null) { Console.WriteLine("usage: FleetPatch --dist <dist folder> [--se <SpaceEngineers data folder>] [--yes] [--dry-run]"); return 1; }
        if (se == null) se = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers");
        return Run(dist, se, yes, dry);
    }

    public static int Run(string dist, string se, bool assumeYes, bool dryRun)
    {
        try { return RunInner(dist, se, assumeYes, dryRun); }
        catch (Exception e) { Console.WriteLine("ERROR: " + e.Message); return 1; }
    }

    static int RunInner(string dist, string se, bool assumeYes, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine("--- Putting scripts into programmable blocks ---");

        // 1. load scripts
        var code = new Dictionary<string, string>();
        string version = "?";
        foreach (var k in Kinds)
        {
            string p = Path.Combine(dist, k + ".cs");
            if (!File.Exists(p)) { Console.WriteLine("ERROR: missing " + p); return 1; }
            code[k] = File.ReadAllText(p, Encoding.UTF8).Replace("\r\n", "\n");
            var m = Header.Match(code[k]);
            if (m.Success) version = m.Groups[1].Value;
        }
        Console.WriteLine("Fleet System version: " + version);

        if (!Directory.Exists(se)) { Console.WriteLine("Space Engineers data folder not found: " + se); Console.WriteLine("Start the game once, save a world, then run the installer again."); return 0; }

        try { InstallShips(Path.GetFullPath(Path.Combine(dist, Path.Combine("..", "blueprints"))), se, dryRun); }
        catch (Exception e) { Console.WriteLine("Ship blueprints: ERROR " + e.Message); }

        // 2. find candidate files
        var files = new List<KeyValuePair<string, string>>();
        foreach (var f in FindFiles(Path.Combine(se, "Saves"), "SANDBOX_0_0_0_.sbs"))
            if (f.IndexOf(Path.DirectorySeparatorChar + "Backup" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                files.Add(new KeyValuePair<string, string>(f, "World     " + Path.GetFileName(Path.GetDirectoryName(f))));
        foreach (var f in FindFiles(Path.Combine(se, Path.Combine("Blueprints", "local")), "bp.sbc"))
            files.Add(new KeyValuePair<string, string>(f, "Blueprint " + Path.GetFileName(Path.GetDirectoryName(f))));
        Console.WriteLine("Scanning " + files.Count + " worlds/blueprints...");

        // 3. dry pass: build the plan
        var plans = new List<Plan>();
        foreach (var kv in files)
        {
            try
            {
                bool gz; XmlDocument doc = Load(kv.Key, true, out gz);
                if (doc == null) continue;
                var plan = new Plan { Path = kv.Key, Label = kv.Value };
                Patch(doc, code, plan.Hits, false);
                if (plan.Hits.Count > 0) plans.Add(plan);
            }
            catch (Exception e) { Console.WriteLine("  skipped " + kv.Value + " (" + e.Message + ")"); }
        }

        int total = 0;
        foreach (var p in plans) total += p.Hits.Count;
        if (total == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No programmable blocks need updating.");
            Console.WriteLine("To have the installer fill them in, rename the programmable blocks in game:");
            Console.WriteLine("   hub brain PB   ->  include [Fleet Core]   e.g.  Programmable Block [Fleet Core]");
            Console.WriteLine("   hub screen PB  ->  include [Fleet UI]");
            Console.WriteLine("   drone PB       ->  include [Fleet Drone]");
            Console.WriteLine("   factory PB     ->  include [Fleet Factory]");
            Console.WriteLine("then save, exit the game and run the installer again.");
            return 0;
        }

        Console.WriteLine();
        foreach (var p in plans)
        {
            Console.WriteLine(p.Label);
            foreach (var h in p.Hits) Console.WriteLine("    " + h.Change.PadRight(8) + Short(h.Kind).PadRight(9) + h.Block);
        }
        Console.WriteLine();
        Console.WriteLine(total + " programmable block(s) in " + plans.Count + " file(s). Every file is backed up first.");
        if (dryRun) { Console.WriteLine("(dry run: nothing written)"); return 0; }

        // 4. game must be closed
        while (GameRunning())
        {
            Console.WriteLine();
            Console.WriteLine("Space Engineers is running. An open world would overwrite these changes when it saves.");
            if (assumeYes) { Console.WriteLine("Close the game and run the installer again."); return 2; }
            Console.Write("Close the game, then press Enter to continue (or type S + Enter to skip): ");
            string a = Console.ReadLine();
            if (a == null || a.Trim().ToUpperInvariant() == "S") { Console.WriteLine("Skipped. No worlds or blueprints were changed."); return 2; }
        }

        if (!assumeYes)
        {
            Console.Write("Apply these changes? [Y/N]: ");
            string a = Console.ReadLine();
            if (a == null || !a.Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelled. Nothing was changed."); return 2; }
        }

        // 5. apply
        string stamp = ".fleetbak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int done = 0;
        foreach (var p in plans)
        {
            try
            {
                bool gz; XmlDocument doc = Load(p.Path, false, out gz);
                var hits = new List<Hit>();
                Patch(doc, code, hits, true);
                if (hits.Count == 0) continue;
                File.Copy(p.Path, p.Path + stamp, true);
                Save(doc, p.Path, gz);
                foreach (var ext in new[] { "B5", "PB" })
                {
                    string cache = p.Path + ext;
                    if (File.Exists(cache)) File.Move(cache, cache + stamp);
                }
                done += hits.Count;
                Console.WriteLine("  updated " + p.Label.Trim() + "  (" + hits.Count + " block(s))");
            }
            catch (Exception e) { Console.WriteLine("  FAILED " + p.Label.Trim() + ": " + e.Message + " (file left unchanged)"); }
        }
        Console.WriteLine();
        Console.WriteLine("Done: " + done + " programmable block(s) now run Fleet System " + version + ".");
        Console.WriteLine("Load the world in game and the scripts start automatically.");
        return 0;
    }

    // ------------------------------------------------------------------ ship blueprints
    class Def { public string CubeSize; public int X, Y, Z; }

    static void InstallShips(string src, string se, bool dryRun)
    {
        if (!Directory.Exists(src)) return;
        Console.WriteLine();
        Console.WriteLine("--- Ship blueprints (F10 menu) ---");
        string where;
        var defs = LoadDefinitions(out where);
        if (defs == null) Console.WriteLine("WARNING: Space Engineers game files not found, so blueprints are installed WITHOUT verification.\n  (Set FLEET_SE_GAME to your SpaceEngineers install folder to enable the check.)");
        else Console.WriteLine("Verifying against " + defs.Count + " block definitions in " + where);
        foreach (var dir in Directory.GetDirectories(src))
        {
            string bp = Path.Combine(dir, "bp.sbc");
            if (!File.Exists(bp)) continue;
            string name = Path.GetFileName(dir);
            var problems = defs != null ? Validate(bp, defs) : new List<string>();
            if (problems.Count > 0)
            {
                Console.WriteLine("  NOT installed: " + name + " (" + problems.Count + " problem(s), please report these):");
                for (int i = 0; i < Math.Min(12, problems.Count); i++) Console.WriteLine("     " + problems[i]);
                continue;
            }
            if (dryRun) { Console.WriteLine("  would install: " + name + (defs != null ? "  (verified)" : "")); continue; }
            string dest = Path.Combine(se, Path.Combine("Blueprints", Path.Combine("local", name)));
            Directory.CreateDirectory(dest);
            File.Copy(bp, Path.Combine(dest, "bp.sbc"), true);
            foreach (var ext in new[] { "B5", "PB" }) { string c = Path.Combine(dest, "bp.sbc" + ext); if (File.Exists(c)) File.Delete(c); }
            Console.WriteLine("  installed: " + name + (defs != null ? "  (verified)" : ""));
        }
    }

    static string FindGame()
    {
        string env = Environment.GetEnvironmentVariable("FLEET_SE_GAME");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, Path.Combine("Content", "Data")))) return env;
        var roots = new List<string>();
        foreach (var k in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam|SteamPath", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam|InstallPath" })
        {
            try
            {
                var parts = k.Split('|');
                var v = Microsoft.Win32.Registry.GetValue(parts[0], parts[1], null) as string;
                if (!string.IsNullOrEmpty(v)) roots.Add(v.Replace('/', '\\'));
            }
            catch { }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        var libs = new List<string>();
        foreach (var r in roots)
        {
            libs.Add(r);
            string vdf = Path.Combine(r, Path.Combine("steamapps", "libraryfolders.vdf"));
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        foreach (var l in libs)
        {
            string g = Path.Combine(l, Path.Combine("steamapps", Path.Combine("common", "SpaceEngineers")));
            if (Directory.Exists(Path.Combine(g, Path.Combine("Content", "Data")))) return g;
        }
        return null;
    }

    // TypeId/SubtypeId -> grid size + footprint, from the game's CubeBlocks*.sbc files
    static Dictionary<string, Def> LoadDefinitions(out string where)
    {
        where = FindGame();
        if (where == null) return null;
        var defs = new Dictionary<string, Def>();
        foreach (var f in Directory.GetFiles(Path.Combine(where, Path.Combine("Content", "Data")), "*.sbc", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.IndexOf("<CubeBlocks", StringComparison.Ordinal) < 0) continue;
            var doc = new XmlDocument();
            try { doc.LoadXml(text); } catch { continue; }
            foreach (XmlNode d in doc.SelectNodes("//CubeBlocks/Definition"))
            {
                var id = d.SelectSingleNode("Id");
                if (id == null) continue;
                string type = Text(id.SelectSingleNode("TypeId")) ?? Attr(id, "Type");
                string sub = Text(id.SelectSingleNode("SubtypeId")) ?? Attr(id, "Subtype") ?? "";
                var size = d.SelectSingleNode("Size");
                if (type == null || size == null) continue;
                type = type.Replace("MyObjectBuilder_", "");
                defs[type + "/" + sub] = new Def { CubeSize = Text(d.SelectSingleNode("CubeSize")) ?? "", X = Int(Attr(size, "x")), Y = Int(Attr(size, "y")), Z = Int(Attr(size, "z")) };
            }
        }
        return defs.Count > 0 ? defs : null;
    }

    static string Text(XmlNode n) { return n == null ? null : n.InnerText.Trim(); }
    static string Attr(XmlNode n, string a) { var x = n.Attributes != null ? n.Attributes[a] : null; return x == null ? null : x.Value; }
    static int Int(string s) { int v; return int.TryParse(s, out v) ? v : 1; }

    static int[] Dir(string d)
    {
        switch (d)
        {
            case "Forward": return new[] { 0, 0, -1 };
            case "Backward": return new[] { 0, 0, 1 };
            case "Left": return new[] { -1, 0, 0 };
            case "Right": return new[] { 1, 0, 0 };
            case "Up": return new[] { 0, 1, 0 };
            default: return new[] { 0, -1, 0 };
        }
    }

    static List<string> Validate(string bp, Dictionary<string, Def> defs)
    {
        var problems = new List<string>();
        var doc = new XmlDocument();
        doc.Load(bp);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("xsi", XsiNs);
        foreach (XmlNode grid in doc.SelectNodes("//CubeGrid"))
        {
            string gsize = Text(grid.SelectSingleNode("GridSizeEnum")) ?? "";
            var cells = new Dictionary<string, string>();
            foreach (XmlNode b in grid.SelectNodes("CubeBlocks/*"))
            {
                var ta = b.Attributes["type", XsiNs];
                string type = ta != null ? ta.Value.Replace("MyObjectBuilder_", "") : "CubeBlock";
                string sub = Text(b.SelectSingleNode("SubtypeName")) ?? "";
                string label = (Text(b.SelectSingleNode("CustomName")) ?? sub) + " [" + type + "/" + sub + "]";
                Def def;
                if (!defs.TryGetValue(type + "/" + sub, out def)) { problems.Add("unknown block: " + type + "/" + (sub.Length > 0 ? sub : "(empty)")); continue; }
                if (!string.Equals(def.CubeSize, gsize, StringComparison.OrdinalIgnoreCase)) { problems.Add("wrong grid size: " + label + " is " + def.CubeSize + ", ship is " + gsize); continue; }
                var mn = b.SelectSingleNode("Min"); var or = b.SelectSingleNode("BlockOrientation");
                int[] f = Dir(Attr(or, "Forward") ?? "Forward"), u = Dir(Attr(or, "Up") ?? "Up");
                int[] r = { f[1] * u[2] - f[2] * u[1], f[2] * u[0] - f[0] * u[2], f[0] * u[1] - f[1] * u[0] };
                int[] e = new int[3];
                for (int i = 0; i < 3; i++) e[i] = Math.Abs(r[i]) * def.X + Math.Abs(u[i]) * def.Y + Math.Abs(f[i]) * def.Z;
                int x0 = Int(Attr(mn, "x")), y0 = Int(Attr(mn, "y")), z0 = Int(Attr(mn, "z"));
                for (int x = 0; x < e[0]; x++) for (int y = 0; y < e[1]; y++) for (int z = 0; z < e[2]; z++)
                {
                    string c = (x0 + x) + "," + (y0 + y) + "," + (z0 + z);
                    string other;
                    if (cells.TryGetValue(c, out other)) { problems.Add("overlap at " + c + ": " + label + " and " + other); goto nextBlock; }
                    cells[c] = label;
                }
                nextBlock:;
            }
        }
        return problems;
    }

    // ------------------------------------------------------------------ core
    static string Short(string kind) { return kind == "HubCore" ? "Core" : kind == "HubUI" ? "UI" : kind == "Factory" ? "Factory" : "Drone"; }

    static string KindOf(string name, string program)
    {
        for (int i = 0; i < Tags.Length; i++)
            if (name != null && name.IndexOf(Tags[i], StringComparison.OrdinalIgnoreCase) >= 0) return Kinds[i];
        if (program != null)
        {
            var m = Header.Match(program);
            if (m.Success) return m.Groups[2].Value;
        }
        return null;
    }

    // Finds Fleet PBs; when apply is true, writes the code into them.
    static void Patch(XmlDocument doc, Dictionary<string, string> code, List<Hit> hits, bool apply)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("xsi", XsiNs);
        var pbs = doc.SelectNodes("//*[@xsi:type='MyObjectBuilder_MyProgrammableBlock']", ns);
        foreach (XmlNode pb in pbs)
        {
            var nameNode = pb.SelectSingleNode("CustomName");
            var progNode = pb.SelectSingleNode("Program");
            string name = nameNode != null ? nameNode.InnerText : "(unnamed programmable block)";
            string prog = progNode != null ? progNode.InnerText.Replace("\r\n", "\n") : null;
            string kind = KindOf(name, prog);
            if (kind == null || prog == code[kind]) continue;

            string change = string.IsNullOrEmpty(prog) ? "new" : (Header.IsMatch(prog) ? "update" : "replace");
            hits.Add(new Hit { Block = name, Kind = kind, Change = change });
            if (!apply) continue;

            if (progNode == null)
            {
                progNode = doc.CreateElement("Program");
                XmlNode before = pb.SelectSingleNode("Storage") ?? pb.SelectSingleNode("DefaultRunArgument");
                if (before != null) pb.InsertBefore(progNode, before); else pb.AppendChild(progNode);
            }
            progNode.InnerText = code[kind];
        }
    }

    static IEnumerable<string> FindFiles(string root, string pattern)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        try { result.AddRange(Directory.GetFiles(root, pattern, SearchOption.AllDirectories)); }
        catch (Exception e) { Console.WriteLine("  could not search " + root + ": " + e.Message); }
        return result;
    }

    // quickFilter: skip files that obviously contain no Fleet PB (fast path for big worlds)
    static XmlDocument Load(string path, bool quickFilter, out bool gz)
    {
        byte[] bytes = File.ReadAllBytes(path);
        gz = bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;
        if (gz)
        {
            using (var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                bytes = output.ToArray();
            }
        }
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        if (quickFilter)
        {
            if (text.IndexOf("MyObjectBuilder_MyProgrammableBlock", StringComparison.Ordinal) < 0) return null;
            bool any = text.IndexOf("FLEET SYSTEM", StringComparison.Ordinal) >= 0;
            foreach (var t in Tags) any |= text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!any) return null;
        }
        var doc = new XmlDocument();
        doc.PreserveWhitespace = true;
        doc.LoadXml(text);
        return doc;
    }

    static void Save(XmlDocument doc, string path, bool gz)
    {
        var settings = new XmlWriterSettings();
        settings.Encoding = new UTF8Encoding(true);
        settings.NewLineHandling = NewLineHandling.None;   // keep script line endings exactly
        string tmp = path + ".fleettmp";
        using (var fs = File.Create(tmp))
        {
            Stream s = gz ? (Stream)new GZipStream(fs, CompressionMode.Compress) : fs;
            using (var w = XmlWriter.Create(s, settings)) doc.Save(w);
            if (gz) s.Dispose();
        }
        // verify the written file parses before replacing the original
        bool gz2; Load(tmp, false, out gz2);
        File.Copy(tmp, path, true);
        File.Delete(tmp);
    }

    static bool GameRunning()
    {
        try { return Process.GetProcessesByName("SpaceEngineers").Length > 0; }
        catch { return false; }
    }
}
