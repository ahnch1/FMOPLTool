using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 아타리 System 1 (HootAtariSys1): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootAtariSys1Tests
{
    public static readonly string[] Archives = { "marble", "peterpak", "indytemp", "roadrunn" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot atari system1: playable, own board", HootArchive.Playable("atari", "system1") && HootAtariSys1.Handles("Atari", "System1") && !HootAtariSys1.Handles("atari", "gauntlet"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot atari system1 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot atari system1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootAtariSys1.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot atari system1 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Skip(1).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot atari system1 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootAtariSys1.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        var pcs = new Dictionary<int, int>();
        if (Environment.GetEnvironmentVariable("HOOT_PCS") != null)
            for (int i = 0; i < 20000; i++) { m.Run(80); pcs.TryGetValue(m.Cpu.PC & 0xFFF0, out int c); pcs[m.Cpu.PC & 0xFFF0] = c + 1; }
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        string note = "opm " + keyOn + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + " pokey " + m.PokeyWrites + " via " + m.ViaWrites + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " regs " + string.Join(" ", m.Log.Where(x => x.t > 0).GroupBy(x => x.reg).OrderByDescending(g => g.Count()).Take(12).Select(g => g.Key.ToString("X2") + ":" + g.Count()));
        if (pcs.Count > 0) note += " pcs " + string.Join(" ", pcs.OrderByDescending(x => x.Value).Take(10).Select(x => x.Key.ToString("X4") + ":" + x.Value)) + " ram " + BitConverter.ToString(m.Mem, 0x200, 0x20);
        return (keyOn > 0, note);
    }
}
