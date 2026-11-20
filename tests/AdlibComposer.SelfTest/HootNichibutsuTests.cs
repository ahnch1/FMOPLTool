using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 니치부츠 보드 (HootNichibutsu, type terracre · terraf): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNichibutsuTests
{
    public static readonly string[] Archives = { "terracre", "galivan", "dangar", "ninjemak", "amazon", "horekid", "terraf", "kozure", "armedf", "skyrobo" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot nichibutsu terracre · terraf: playable, own board", HootArchive.Playable("nichibutsu", "terracre") && HootArchive.Playable("nichibutsu", "terraf")
            && HootNichibutsu.Handles("Nichibutsu", "TERRAF") && !HootNichibutsu.Handles("nichibutsu", "generic"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot nichibutsu real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot nichibutsu " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNichibutsu.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot nichibutsu " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot nichibutsu " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "terracre")
            {
                var vgm = HootNichibutsu.Render(hoot, dir, code, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot nichibutsu terracre: audible through VGM", peak > 0.02, "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNichibutsu.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));
        int dac = m.Log.Count(x => x.t > 0 && x.chip == 1);
        string note = (m.Ym3812 ? "YM3812" : "YM3526") + " key-on " + keyOn + " dac " + dac + ", writes " + m.Log.Count + ", irq " + m.Irqs + " im " + m.Cpu.Im + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherPorts.Count > 0 ? ", other ports " + string.Join(" ", m.OtherPorts.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X3") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn > 0 || dac > 50, note);
    }
}
