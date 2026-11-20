using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 알파 Alpha 68k II / V (HootAlpha68k, type 68k2): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootAlpha68kTests
{
    public static readonly string[] Archives = { "skyadvnt", "gangwars", "sbasebal", "timesold", "skysoldr", "goldmedl" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot alpha 68k2: playable, own board", HootArchive.Playable("alpha", "68k2") && HootAlpha68k.Handles("Alpha", "68K2") && !HootAlpha68k.Handles("alpha", "equites"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot alpha real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot alpha " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootAlpha68k.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot alpha " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot alpha " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "timesold")
            {
                var vgm = HootAlpha68k.Render(hoot, dir, code, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot alpha timesold: audible through VGM", peak > 0.02, "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootAlpha68k.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int opn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int opll = m.Log.Count(x => x.t > 0 && x.chip == 1 && ((x.reg >= 0x20 && x.reg <= 0x28 && (x.val & 0x10) != 0) || (x.reg == 0x0E && (x.val & 0x1F) != 0)));
        int dac = m.Log.Count(x => x.t > 0 && x.chip == 2);
        string note = "opn " + opn + " ssg " + ssg + " opll " + opll + " dac " + dac + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherPorts.Count > 0 ? ", other ports " + string.Join(" ", m.OtherPorts.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X3") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (opn + ssg + opll > 0 || dac > 50, note);
    }
}
