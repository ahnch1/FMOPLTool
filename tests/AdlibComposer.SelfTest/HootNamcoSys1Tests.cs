using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 남코 System 1 (HootNamcoSys1): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNamcoSys1Tests
{
    public static readonly string[] Archives = { "shadowld", "dspirit", "blazer", "quester", "pacmania", "pacmaniaj", "galaga88", "ws", "berabohm", "mmaze", "bakutotu", "wldcourt", "splatter", "faceoff", "rompers", "blastoff", "ws89", "dangseed", "pistoldm", "boxyboy", "puzlclub", "tankfrce" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot namco system1: playable, own board", HootArchive.Playable("namco", "system1") && HootNamcoSys1.Handles("Namco", "System1") && !HootNamcoSys1.Handles("namco", "system2"), "");

        // CUS30: 파형 0 = 톱니 (0~F), 목소리 0 주파수 8000h → 32768 × 24000 / 2^20 = 750Hz, 왼쪽 음량 15 · 오른쪽 0
        var c = new AdlibComposer.Audio.Chips.NamcoCus30();
        for (int i = 0; i < 16; i++) c.Write(0, i, ((2 * i) << 4) | (2 * i + 1));
        c.Write(0, 0x100, 15); c.Write(0, 0x101, 0x00); c.Write(0, 0x102, 0x80); c.Write(0, 0x103, 0x00); c.Write(0, 0x104, 0);
        int lo = int.MaxValue, hi = int.MinValue, rmax = 0;
        for (int i = 0; i < 4410; i++) { c.Generate(out int l, out int r); lo = Math.Min(lo, l); hi = Math.Max(hi, l); rmax = Math.Max(rmax, Math.Abs(r)); }
        check("CUS30: wave RAM voice, 750 Hz, left only", Math.Abs(c.ChannelFrequency(0) - 750) < 0.01 && hi - lo > 4000 && rmax == 0, c.ChannelFrequency(0) + " Hz, swing " + (hi - lo) + ", right " + rmax);

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot namco system1 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco system1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoSys1.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco system1 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "blazer")
            {
                // 블레이저 효과음 100h = CUS30 슬롯 → VGM (명령 D8h)을 재생기로 돌려 소리가 나는지
                var v = HootNamcoSys1.Render(hoot, dir, 0x100, 3, 0);
                var p = new VgmPlayer(); p.Start(v, 0, null);
                double peak = 0;
                for (int i = 0; i < 44100 * 3; i++) { p.Step(); p.Generate(out double l, out double rr); peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(rr))); }
                check("hoot namco system1 blazer: CUS30 effect plays through VGM (D8h)", v.Cus30Clock > 0 && v.ChipSummary.Contains("CUS30") && peak > 100, "peak " + peak.ToString("0") + ", " + v.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Where(t => t.Code != 0xFF).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco system1 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoSys1.Start(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; }, code);
        m.RunSeconds(seconds - m.PlayedSeconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int wsg = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg >= 0x100 && ((x.reg & 7) == 0 || (x.reg & 7) == 4) && (x.val & 15) != 0);
        string note = (m.QueueDriver ? "queue " : "byte ") + "opm " + keyOn + " cus30 vol " + wsg + "/" + m.Cus30Writes + ", writes " + m.Log.Count + ", irq " + m.Irqs + " firq " + m.Firqs + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn + wsg > 0, note);
    }
}
