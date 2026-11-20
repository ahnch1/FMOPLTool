using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 남코 System 1 (HootNamcoSys1): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNamcoSys1Tests
{
    public static readonly string[] Archives = { "shadowld", "dspirit", "blazer", "quester", "pacmania", "pacmaniaj", "galaga88", "ws", "berabohm", "mmaze", "bakutotu", "wldcourt", "splatter", "faceoff", "rompers", "blastoff", "ws89", "dangseed", "pistoldm", "boxyboy", "puzlclub", "tankfrce" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot namco system1: playable, own board", HootArchive.Playable("namco", "system1") && HootNamcoSys1.Handles("Namco", "System1") && !HootNamcoSys1.Handles("namco", "system2"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot namco system1 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco system1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoSys1.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco system1 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Where(t => t.Code != 0xFF).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco system1 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoSys1.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_NSCMD") != null) m.PlayCommand = Convert.ToInt32(Environment.GetEnvironmentVariable("HOOT_NSCMD"), 16);
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        string note = (m.QueueDriver ? "queue " : "byte ") + "opm " + keyOn + " cus30 " + m.Cus30Writes + ", writes " + m.Log.Count + ", irq " + m.Irqs + " firq " + m.Firqs + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn > 0, note);
    }
}
