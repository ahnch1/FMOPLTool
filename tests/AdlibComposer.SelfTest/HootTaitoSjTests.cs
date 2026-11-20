using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 타이토 SJ (HootTaitoSj, Z80 + AY × 3 + DAC): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootTaitoSjTests
{
    public static readonly string[] Archives = { "spaceskr", "alpine", "frontlin", "timetunl", "wwestern", "bioatack", "elevator", "hwrace", "tinstar", "waterski", "kikstart", "sfposeid" };

    public static void Run(Action<string, bool, string> check)
    {
        // 셋째 AY (D9h): 첫 두 칩은 조용, 셋째만 톤 → 소리
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockAy8910, 1500000, 0x40000000u);
        foreach (var (r, val) in new[] { (0, 0xFE), (1, 0), (7, 0x3E), (8, 0x0F) }) w.Write(new byte[] { 0xD9, (byte)r, (byte)val, 0 });
        w.Wait(44100);
        var v = VgmFormat.Read(w.ToArray());
        v.AyClock3 = 1500000;
        var p = new VgmPlayer(); p.Start(v, 0, null);
        double peak = 0;
        for (int i = 0; i < 44100; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("VGM third AY (in-app D9h) plays", v.ChipSummary.Contains("×3") && peak > 100, "peak " + peak.ToString("0") + ", " + v.ChipSummary);
        check("hoot taito taitosj: playable, own board", HootArchive.Playable("taito", "taitosj") && HootTaitoSj.Handles("Taito", "TaitoSJ") && !HootTaitoSj.Handles("taito", "fullt"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot taitosj real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot taitosj " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootTaitoSj.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot taitosj " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot taitosj " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootTaitoSj.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int vol = m.Log.Count(x => x.t > 0 && x.chip <= 2 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int dac = m.Log.Count(x => x.t > 0 && x.chip == 3 && x.val != 0x80);
        string note = "vol " + vol + " dac " + dac + " (ay3 " + m.Log.Count(x => x.t > 0 && x.chip == 2 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0) + " ay2 " + m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0) + "), writes " + m.Log.Count + ", irq " + m.Irqs + " nmi " + m.Nmis + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (vol + dac > 0, note);
    }
}
