using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>VGM 듀얼 AY · hoot 아이렘 M62 / M52 (HootIremM62): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootIremM62Tests
{
    public static readonly string[] Archives = { "mpatrol", "10yard", "travrusa", "kungfum", "battroad", "ldrun", "ldrun2", "ldrun3", "ldrun4", "lotlot", "kidniki", "spelunkr", "spelunk2", "youjyudn", "horizon" };

    public static void Run(Action<string, bool, string> check)
    {
        // VGM 듀얼 AY: 클럭 bit30 → 둘째 칩, A0h aa bit7 = 둘째에 (첫 칩은 조용, 둘째만 톤 A → 소리)
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockAy8910, 1789772, 0x40000000u);
        w.Write(0xA0, 0x80 | 0, 0xFE); w.Write(0xA0, 0x80 | 1, 0x00); w.Write(0xA0, 0x80 | 7, 0x3E); w.Write(0xA0, 0x80 | 8, 0x0F);
        w.Wait(44100);
        var v = VgmFormat.Read(w.ToArray());
        var p = new VgmPlayer(); p.Start(v, 0, null);
        double peak = 0;
        for (int i = 0; i < 44100; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("VGM dual AY: clock bit30 + A0h bit7 plays the second chip", v.AyClock2 > 0 && v.ChipSummary.Contains("×2") && peak > 100, "peak " + peak.ToString("0") + ", " + v.ChipSummary);
        check("hoot irem m62: playable, own board", HootArchive.Playable("irem", "m62") && HootIremM62.Handles("Irem", "M62") && !HootIremM62.Handles("irem", "m72"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot irem m62 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot irem m62 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootIremM62.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot irem m62 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "mpatrol")
            {
                var v2 = HootIremM62.Render(hoot, dir, 0x21, 2, 0);   // 효과음 = MSM5205 → OKIM6258
                var p2 = new VgmPlayer(); p2.Start(v2, 0, null);
                double pk = 0;
                for (int i = 0; i < 44100 * 2; i++) { p2.Step(); p2.Generate(out double l, out _); pk = Math.Max(pk, Math.Abs(l)); }
                check("hoot irem m62 mpatrol: MSM5205 effect plays through VGM OKIM6258", v2.Okim6258Clock > 0 && pk > 100, "peak " + pk.ToString("0") + ", " + v2.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot irem m62 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootIremM62.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int vol = m.Log.Count(x => x.t > 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int msm = m.Adpcm.Count(x => x.nibble != 0 && x.nibble != 8);
        string note = "vol " + vol + " msm " + msm + "/" + m.Adpcm.Count + " @" + m.MsmRate + " (ay2 " + m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0) + "), writes " + m.Log.Count + ", irq " + m.Irqs + " nmi " + m.Nmis + " latch " + m.LatchReads + " adpcm " + m.AdpcmWrites + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps + (m.Cpu.FirstUnknownPc >= 0 ? "@" + m.Cpu.FirstUnknownPc.ToString("X4") : "")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (vol + msm > 0, note);
    }
}
