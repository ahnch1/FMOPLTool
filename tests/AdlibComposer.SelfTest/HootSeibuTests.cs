using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 세이부 사운드 시스템 (HootSeibu, type raiden): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootSeibuTests
{
    public static readonly string[] Archives = { "raiden", "totmejan", "heatbrl", "cupsoc", "raidndx", "raiden2" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot seibu raiden: playable, own board", HootArchive.Playable("seibu", "raiden") && HootSeibu.Handles("Seibu", "Raiden") && HootArchive.Playable("seibu", "raiden2") && !HootSeibu.Handles("seibu", "sengoku"), "");


        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot seibu raiden real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot seibu raiden " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSeibu.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot seibu raiden " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot seibu raiden " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootSeibu.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && m.Opm && x.reg == 0x08 && (x.val & 0x78) != 0) + m.Log.Count(x => x.t > 0 && x.chip == 0 && !m.Opm && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));
        int oki = m.Log.Count(x => x.t > 0 && (x.chip == 1 || x.chip == 2) && (x.val & 0x80) != 0);
        string note = "opl " + keyOn + " oki " + oki + ", writes " + m.Log.Count + ", rst10 " + m.Irqs10 + " rst18 " + m.Irqs18 + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn + oki > 0, note);
    }
}
