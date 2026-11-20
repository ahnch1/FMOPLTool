using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot UPL (HootUpl, type robokid) · VGM 듀얼 YM2203: 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootUplTests
{
    public static readonly string[] Archives = { "psychic5j", "arkarea", "mnight", "robokid", "omegaf" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot upl robokid: playable, own board", HootArchive.Playable("upl", "robokid") && HootUpl.Handles("UPL", "Robokid") && !HootUpl.Handles("upl", "ninjakid2"), "");

        // VGM 듀얼 YM2203: 클럭 bit30 → 둘째 칩, A5h = 둘째 칩에 (첫 칩은 조용, 둘째만 키온 → 소리)
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockYm2203, 1500000, 0x40000000u);
        foreach (var (r, val) in new[] { (0x30, 0x01), (0x40, 0x00), (0x50, 0x1F), (0x60, 0x00), (0x70, 0x00), (0x80, 0x0F), (0xB0, 0x07), (0xA4, 0x22), (0xA0, 0x69) }) w.Write(0xA5, r, val);
        foreach (int op in new[] { 0x34, 0x38, 0x3C }) w.Write(0xA5, op + 0x10, 0x7F);   // 나머지 연산자 TL 최대 (조용)
        w.Write(0xA5, 0x28, 0xF0);
        w.Wait(44100);
        var v = VgmFormat.Read(w.ToArray());
        var p = new VgmPlayer(); p.Start(v, 0, null);
        double peak = 0;
        for (int i = 0; i < 44100; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("VGM dual YM2203: clock bit30 + A5h plays the second chip", v.Ym2203Clock2 == 1500000 && v.ChipSummary.Contains("YM2203 ×2") && peak > 0.02, "peak " + peak.ToString("0.000") + ", " + v.ChipSummary);

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot upl real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot upl " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootUpl.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot upl " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot upl " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootUpl.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int k1 = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int k2 = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        string note = "opn1 " + k1 + " opn2 " + k2 + " ssg " + ssg + ", writes " + m.Log.Count + ", irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherPorts.Count > 0 ? ", other ports " + string.Join(" ", m.OtherPorts.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X3") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (k1 + k2 + ssg > 0, note);
    }
}
