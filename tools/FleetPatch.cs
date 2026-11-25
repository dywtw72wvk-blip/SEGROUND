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
//   or the block already runs a Fleet System script -> updated to the latest
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
    static readonly string[] Kinds = { "HubCore", "HubUI", "Drone" };
    static readonly string[] Tags = { "[Fleet Core]", "[Fleet UI]", "[Fleet Drone]" };
    static readonly Regex Header = new Regex(@"FLEET SYSTEM (\S+) \S+ (HubCore|HubUI|Drone)\b");

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
            Console.WriteLine("then save, exit the game and run the installer again.");
            return 0;
        }

        Console.WriteLine();
        foreach (var p in plans)
        {
            Console.WriteLine(p.Label);
            foreach (var h in p.Hits) Console.WriteLine("    " + h.Change.PadRight(8) + Short(h.Kind).PadRight(7) + h.Block);
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

    // ------------------------------------------------------------------ core
    static string Short(string kind) { return kind == "HubCore" ? "Core" : kind == "HubUI" ? "UI" : "Drone"; }

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
