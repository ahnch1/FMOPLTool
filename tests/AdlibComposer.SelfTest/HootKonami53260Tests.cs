using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 코나미 Z80 + YM2151 + K053260 (HootKonami53260): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootKonami53260Tests
{
    public static readonly string[] Archives = { "lgtnfght", "parodius", "esckids", "thndrx2", "vendetta", "punkshot", "ssriders", "blswhstl", "tmnt2", "qgakumon", "simpsons" };

    public static void Run(Action<string, bool, string> check)
    {
        // K053260: 선형 PCM 사각파, 목소리 0 키온, 팬 가운데, 소리 켬 → 소리 (VGM ACh · BAh · 블록 8Eh)
        var rom = new byte[0x800];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)((i / 8) % 2 == 0 ? 0x60 : 0xA0);
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockK053260, 3579545);
        var blk = new byte[15 + rom.Length]; blk[0] = 0x67; blk[1] = 0x66; blk[2] = 0x8E; int sz = 8 + rom.Length; blk[3] = (byte)sz; blk[4] = (byte)(sz >> 8); blk[7] = (byte)rom.Length; blk[8] = (byte)(rom.Length >> 8);
        Array.Copy(rom, 0, blk, 15, rom.Length); w.Write(blk);
        foreach (var (r, val) in new[] { (0x2F, 2), (0x08, 0x00), (0x09, 0x0E), (0x0A, 0xFF), (0x0B, 0x07), (0x0C, 0), (0x0D, 0), (0x0E, 0), (0x0F, 0x7F), (0x2A, 0x01), (0x2C, 0x04), (0x28, 0x01) }) w.Write(0xBA, r, val);
        w.Wait(22050);
        var kv = VgmFormat.Read(w.ToArray());
        var kp = new VgmPlayer(); kp.Start(kv, 0, null);
        double kpk = 0;
        for (int i = 0; i < 22050; i++) { kp.Step(); kp.Generate(out double l, out _); kpk = Math.Max(kpk, Math.Abs(l)); }
        check("VGM K053260: ACh + block 8Eh + BAh key-on plays", kv.K053260Clock > 0 && kv.ChipSummary.Contains("K053260") && kpk > 100, "peak " + kpk.ToString("0") + ", " + kv.ChipSummary);
        check("hoot konami 053260: playable, own board", HootArchive.Playable("konami", "053260") && HootKonami53260.Handles("Konami", "053260") && !HootKonami53260.Handles("konami", "054539"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot konami 053260 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot konami 053260 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootKonami53260.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot konami 053260 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot konami 053260 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootKonami53260.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg == 0x28 && (x.val & 15) != 0);
        string note = "opm " + keyOn + " pcm " + pcm + ", writes " + m.Log.Count + ", irq " + m.Irqs + " nmi " + m.Nmis + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn + pcm > 0, note);
    }
}
