using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 테크모 보드: Ninja Gaiden (HootGaiden, type gaiden) 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootTecmoTests
{
    public static readonly string[] Gaiden = { "shadoww", "ryukendn", "wildfang", "stratof" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot tecmo gaiden: playable, own board", HootArchive.Playable("tecmo", "gaiden") && HootGaiden.Handles("Tecmo", "GAIDEN") && !HootGaiden.Handles("tecmo", "tecmo16"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot tecmo real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Gaiden)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot tecmo " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootGaiden.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot tecmo " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot tecmo " + arc + " (gaiden): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "shadoww")
            {
                var vgm = HootGaiden.Render(hoot, dir, code, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot tecmo shadoww: audible through VGM (YM2203 ×2 + OKIM6295)", peak > 0.02 && vgm.ChipSummary.Contains("YM2203 ×2"), "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootGaiden.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int k1 = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int k2 = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.chip < 2 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        string note = "opn1 " + k1 + " opn2 " + k2 + " ssg " + ssg + " oki " + m.Oki.Starts + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (k1 + k2 + ssg > 0 || m.Oki.Starts > 0, note);
    }
}
