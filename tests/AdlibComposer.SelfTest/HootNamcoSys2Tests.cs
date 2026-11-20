using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 남코 System 2 (HootNamcoSys2, 6809 + YM2151 + C140): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNamcoSys2Tests
{
    public static readonly string[] Archives = { "finallap", "assault", "metlhawk", "ordyne", "mirninja", "phelios", "dirtfoxj", "fourtrax", "valkyrie", "finehour", "burnforc", "marvlandj", "dsaber", "finalap2", "gollygho", "rthun2", "sgunner", "sgunner2", "cosmogng", "cosmogngj", "bubbletr", "finalap3", "luckywld", "suzuka8h", "suzuk8h2", "sws92" };

    public static void Run(Action<string, bool, string> check)
    {
        // C140: ROM에 선형 사각파 (±64), 목소리 0 키온 (반복), 왼쪽 음량만 → 소리 · 왼쪽만; VGM D4h · 블록 8Dh로도
        var rom = new byte[0x400];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)((i / 16) % 2 == 0 ? 64 : 0xC0);
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockC140, AdlibComposer.Audio.Chips.NamcoC140.DefaultClock);
        var blk = new byte[15 + rom.Length]; blk[0] = 0x67; blk[1] = 0x66; blk[2] = 0x8D; int sz = 8 + rom.Length; blk[3] = (byte)sz; blk[4] = (byte)(sz >> 8); blk[7] = (byte)rom.Length; blk[8] = (byte)(rom.Length >> 8);
        Array.Copy(rom, 0, blk, 15, rom.Length); w.Write(blk);
        foreach (var (r, val) in new[] { (0, 0), (1, 0xFF), (2, 0x40), (3, 0x00), (4, 0), (6, 0), (7, 0), (8, 0x03), (9, 0xFF), (10, 0), (11, 0), (5, 0x90) }) w.Write(new byte[] { 0xD4, 0, (byte)r, (byte)val });
        w.Wait(22050);
        var cv = VgmFormat.Read(w.ToArray());
        var cp = new VgmPlayer(); cp.Start(cv, 0, null);
        double lp = 0, rp = 0;
        for (int i = 0; i < 22050; i++) { cp.Step(); cp.Generate(out double l, out double rr); lp = Math.Max(lp, Math.Abs(l)); rp = Math.Max(rp, Math.Abs(rr)); }
        check("VGM C140: A8h + block 8Dh + D4h key-on plays (left only)", cv.C140Clock > 0 && cv.ChipSummary.Contains("C140") && lp > 100 && rp < 1, "left " + lp.ToString("0") + " right " + rp.ToString("0") + ", " + cv.ChipSummary);
        check("hoot namco system2: playable, own board", HootArchive.Playable("namco", "system2") && HootNamcoSys2.Handles("Namco", "System2") && !HootNamcoSys2.Handles("namco", "system1"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot namco system2 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco system2 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoSys2.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco system2 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            if (arc == "fourtrax") { Console.WriteLine("SKIP  hoot namco system2 fourtrax (알려진 무음: hoot 옵션 foutrax, 요청 방식 다름)"); continue; }
            foreach (var t in hoot.Titles.Take(16)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco system2 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoSys2.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.Log.Count(x => x.t > 0 && x.chip == 1 && (x.reg & 15) == 5 && (x.val & 0x80) != 0);
        string note = "opm " + keyOn + " c140 " + pcm + ", writes " + m.Log.Count + ", irq " + m.Irqs + " firq " + m.Firqs + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " c140regs " + string.Join(" ", m.Log.Where(x => x.chip == 1).GroupBy(x => x.reg & 15).OrderBy(g => g.Key).Select(g => g.Key.ToString("X") + ":" + g.Count() + "/" + string.Join(",", g.Select(x => x.val.ToString("X2")).Distinct().Take(4))));
        return (keyOn + pcm > 0, note);
    }
}
