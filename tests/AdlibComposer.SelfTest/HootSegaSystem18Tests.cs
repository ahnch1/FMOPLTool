using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 세가 System 18 (HootSegaSystem18, Z80 + YM3438 × 2 + RF5C68): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootSegaSystem18Tests
{
    public static readonly string[] Archives = { "pontoon", "shdancer", "astorm", "bloxeed", "mwalk", "cltchitr", "arescue", "spidman", "arabfgt", "brival", "ga2", "holo", "darkedge", "f1lap", "f1en" };

    public static void Run(Action<string, bool, string> check)
    {
        // RF5C68: RAM에 부호-크기 사각파 + FFh 반복 표시, 채널 0 켬 → 소리 (VGM 40h · 블록 C0h · B0h)
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockRf5c68, 10000000);
        var ram = new byte[0x200]; for (int i = 0; i < 0x1FF; i++) ram[i] = (byte)((i / 16) % 2 == 0 ? 0xC0 : 0x40); ram[0x1FF] = 0xFF;
        var blk = new byte[9 + ram.Length]; blk[0] = 0x67; blk[1] = 0x66; blk[2] = 0xC0; int sz = 2 + ram.Length; blk[3] = (byte)sz; blk[4] = (byte)(sz >> 8);
        Array.Copy(ram, 0, blk, 9, ram.Length); w.Write(blk);
        foreach (var (r, val) in new[] { (7, 0xC0), (0, 0xFF), (1, 0xFF), (2, 0x00), (3, 0x08), (4, 0), (5, 0), (6, 0), (8, 0xFE) }) w.Write(0xB0, r, val);
        w.Wait(22050);
        var rv = VgmFormat.Read(w.ToArray());
        var rp = new VgmPlayer(); rp.Start(rv, 0, null);
        double rpk = 0;
        for (int i = 0; i < 22050; i++) { rp.Step(); rp.Generate(out double l, out _); rpk = Math.Max(rpk, Math.Abs(l)); }
        check("VGM RF5C68: 40h + RAM block C0h + B0h plays", rv.Rf5c68Clock > 0 && rv.ChipSummary.Contains("RF5C68") && rpk > 100, "peak " + rpk.ToString("0") + ", " + rv.ChipSummary);
        check("hoot sega system18: playable, own board", HootArchive.Playable("sega", "system18") && HootSegaSystem18.Handles("Sega", "System18") && !HootSegaSystem18.Handles("sega", "system16b"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot sega system18 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot sega system18 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSegaSystem18.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot sega system18 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "cltchitr")
            {
                var v2 = HootSegaSystem18.Render(hoot, dir, 0x81, 3, 0);
                var p2 = new VgmPlayer(); p2.Start(v2, 0, null);
                double pk = 0;
                for (int i = 0; i < 44100 * 3; i++) { p2.Step(); p2.Generate(out double l, out double rr); pk = Math.Max(pk, Math.Max(Math.Abs(l), Math.Abs(rr))); }
                check("hoot sega system18 cltchitr: YM2612 × 2 + RF5C68 play through VGM", v2.Rf5c68Clock > 0 && v2.Ym2612Clock > 0 && pk > 100, "peak " + pk.ToString("0") + ", " + v2.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot sega system18 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootSegaSystem18.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip <= 1 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int pcm = m.Log.Count(x => x.t > 0 && x.chip == 2 && x.reg == 8 && x.val != 0xFF);
        string note = "opn " + keyOn + " pcm " + pcm + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherPorts.Count > 0 ? ", ports " + string.Join(" ", m.OtherPorts.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X3") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn + pcm > 0, note);
    }
}
