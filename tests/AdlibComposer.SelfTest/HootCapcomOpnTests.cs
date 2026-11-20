using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 캡콤 YM2203 × 2 (HootCapcomOpn, type opn2 · rushcrsh · tigerroad): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootCapcomOpnTests
{
    public static readonly string[] Archives = { "gng", "gunsmoke", "sectionz", "lwings", "trojan", "1943", "1943kai", "avengers", "srumbler", "rushcrsh", "tigeroad", "f1dream", "sidearms", "blktiger" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot capcom opn2 · rushcrsh · tigerroad: playable, own board",
            HootArchive.Playable("capcom", "opn2") && HootArchive.Playable("Capcom", "TigerRoad") && HootCapcomOpn.Handles("capcom", "rushcrsh")
            && HootCapcomOpn.LayoutOf("capcom", "opn2", "[Ghosts and Goblins] Section Z") == HootCapcomOpn.Lwings
            && HootCapcomOpn.LayoutOf("capcom", "opn2", "[Ghosts and Goblins] 1943") == HootCapcomOpn.Gng
            && !HootCapcomOpn.Handles("capcom", "cps1"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot capcom opn real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot capcom opn " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootCapcomOpn.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot capcom opn " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Skip(1).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot capcom opn " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootCapcomOpn.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int k1 = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int k2 = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        string note = "opn1 " + k1 + " opn2 " + k2 + " ssg " + ssg + ", writes " + m.Log.Count + ", irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " regs " + string.Join(" ", m.Log.Where(x => x.t > 0).GroupBy(x => x.chip * 0x100 + x.reg).OrderByDescending(g => g.Count()).Take(12).Select(g => g.Key.ToString("X3") + ":" + g.Count() + "=" + g.Last().val.ToString("X2")));
        return (k1 + k2 + ssg > 0, note);
    }
}
