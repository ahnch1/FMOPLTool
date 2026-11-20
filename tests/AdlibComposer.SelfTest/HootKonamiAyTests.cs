using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 코나미 Z80 + AY × 2 (HootKonamiAy, type scramble · timeplt): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootKonamiAyTests
{
    public static readonly string[] Archives = { "theend", "amidar", "scobra", "scramble", "stratgyx", "turtles", "sfx", "jungler", "timeplt", "tutankhm" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot konami scramble · timeplt: playable, own board", HootArchive.Playable("konami", "scramble") && HootArchive.Playable("Konami", "TimePlt") && HootKonamiAy.Handles("konami", "timeplt") && !HootKonamiAy.Handles("konami", "053260"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot konami ay real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot konami ay " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootKonamiAy.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot konami ay " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot konami ay " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootKonamiAy.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int vol = m.Log.Count(x => x.t > 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        string note = "vol " + vol + " (ay2 " + m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0) + "), writes " + m.Log.Count + ", irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (vol > 0, note);
    }
}
