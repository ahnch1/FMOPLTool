using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 비디오시스템 · 사이쿄 Z80 + YM2610 (HootYm2610Board): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootYm2610BoardTests
{
    public static readonly string[] Archives = { "spinlbrk", "pspikes", "karatblz", "turbofrc", "aerofgt", "gstriker", "gunbird", "btlkroad", "s1945jn", "welltris", "quiz18k", "fromanc2", "fromancr" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot videosystem · psikyo ym2610: playable, own board", HootArchive.Playable("videosystem", "turbofrc") && HootArchive.Playable("psikyo", "gunbird") && HootYm2610Board.LayoutOf("VideoSystem", "AeroFgt") == HootYm2610Board.Aerofgt && !HootYm2610Board.Handles("taito", "f2system"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot ym2610 board real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot ym2610 board " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootYm2610Board.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot ym2610 board " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot ym2610 board " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootYm2610Board.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.t > 0 && x.port == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.port == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int adpcm = m.Log.Count(x => x.t > 0 && ((x.port == 1 && x.reg == 0x00 && (x.val & 0x3F) != 0 && (x.val & 0x80) == 0) || (x.port == 0 && x.reg == 0x10 && (x.val & 0x80) != 0)));
        string note = "fm " + fm + " ssg " + ssg + " adpcm " + adpcm + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherPorts.Count > 0 ? ", ports " + string.Join(" ", m.OtherPorts.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X3") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (fm + ssg + adpcm > 0, note);
    }
}
