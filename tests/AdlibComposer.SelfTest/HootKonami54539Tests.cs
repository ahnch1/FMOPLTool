using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 코나미 Z80 + K054539 (HootKonami54539): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootKonami54539Tests
{
    public static readonly string[] Archives = { "xexex", "bucky", "moomesa", "xmen", "polynetw", "rungun", "metamrph", "mtlchamp", "mystwarr", "viostorm" };

    public static void Run(Action<string, bool, string> check)
    {
        // K054539: 8비트 PCM 사각파 (끝 80h), 채널 0 키온, 팬 가운데, 감쇠 0, 켬 → 소리 (VGM A0h · D3h · 블록 8Ch)
        var rom = new byte[0x800];
        for (int i = 0; i < rom.Length - 1; i++) rom[i] = (byte)((i / 8) % 2 == 0 ? 0x60 : 0xA0);
        rom[rom.Length - 1] = 0x80;
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockK054539, 18432000);
        var blk = new byte[15 + rom.Length]; blk[0] = 0x67; blk[1] = 0x66; blk[2] = 0x8C; int sz = 8 + rom.Length; blk[3] = (byte)sz; blk[4] = (byte)(sz >> 8); blk[7] = (byte)rom.Length; blk[8] = (byte)(rom.Length >> 8);
        Array.Copy(rom, 0, blk, 15, rom.Length); w.Write(blk);
        foreach (var (r, val) in new[] { (0x22F, 1), (0x000, 0x00), (0x001, 0x40), (0x002, 0x00), (0x003, 0x00), (0x005, 0x18), (0x00C, 0), (0x00D, 0), (0x00E, 0), (0x200, 0), (0x201, 0), (0x214, 1) }) w.Write(new byte[] { 0xD3, (byte)(r >> 8), (byte)r, (byte)val });
        w.Wait(22050);
        var kv = VgmFormat.Read(w.ToArray());
        var kp = new VgmPlayer(); kp.Start(kv, 0, null);
        double kpk = 0;
        for (int i = 0; i < 22050; i++) { kp.Step(); kp.Generate(out double l, out _); kpk = Math.Max(kpk, Math.Abs(l)); }
        check("VGM K054539: A0h + block 8Ch + D3h key-on plays", kv.K054539Clock > 0 && kv.ChipSummary.Contains("K054539") && kpk > 100, "peak " + kpk.ToString("0") + ", " + kv.ChipSummary);
        check("hoot konami 054539 · 054539x2: playable, own board", HootArchive.Playable("konami", "054539") && HootArchive.Playable("Konami", "054539X2") && HootKonami54539.Handles("konami", "054539x2") && !HootKonami54539.Handles("konami", "053260"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot konami 054539 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot konami 054539 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootKonami54539.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot konami 054539 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "rungun")
            {
                var v2 = HootKonami54539.Render(hoot, dir, 0xE2, 3, 0);   // K054539 × 2만
                var p2 = new VgmPlayer(); p2.Start(v2, 0, null);
                double pk = 0;
                for (int i = 0; i < 44100 * 3; i++) { p2.Step(); p2.Generate(out double l, out double rr); pk = Math.Max(pk, Math.Max(Math.Abs(l), Math.Abs(rr))); }
                check("hoot konami 054539 rungun: K054539 plays through VGM", v2.K054539Clock > 0 && pk > 100, "peak " + pk.ToString("0") + ", " + v2.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot konami 054539 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootKonami54539.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.Log.Count(x => x.t > 0 && x.chip >= 1 && x.reg == 0x214 && x.val != 0);
        string note = "opm " + keyOn + " pcm " + pcm + ", writes " + m.Log.Count + ", irq " + m.Irqs + " nmi " + m.Nmis + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " | " + m.TimerState;
        return (keyOn + pcm > 0, note);
    }
}
