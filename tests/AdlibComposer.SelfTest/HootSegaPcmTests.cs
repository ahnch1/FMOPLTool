using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>세가 PCM 칩 · VGM, hoot 세가 PCM 보드 (HootSegaPcmBoard: Out Run · X · Y · Hang-On 계열): 실제 zip, 진단 조사.</summary>
public static class HootSegaPcmTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // 채널 0: 주소 0, 끝 7Fh (주소 위 바이트가 80h = 8000h 바이트에서 멈춤 · 되풀이 안 함), 속도 80h (반 샘플씩), 볼륨 7Fh
        var c = new SegaPcm(SegaPcm.StandardClock, SegaPcm.Bank512, 44100);
        var rom = new byte[0x10000];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i % 64 < 32 ? 0xC0 : 0x40);
        c.LoadRom(rom, 0);
        c.Write(0, 2, 0x7F); c.Write(0, 3, 0x7F); c.Write(0, 6, 0x7F); c.Write(0, 7, 0x80);
        c.Write(0, 0x84, 0); c.Write(0, 0x85, 0); c.Write(0, 0x86, 0x02);
        int peak = 0;
        for (int i = 0; i < 4410; i++) { c.Generate(out int l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("SegaPCM: channel plays", peak > 2000 && c.ChannelActive(0), "peak " + peak);
        // 8000h 바이트 ÷ (31250 × 0.5) ≈ 2.1초 뒤 끝 → 멈춤 bit0
        for (int i = 0; i < 44100 * 5; i++) c.Generate(out _, out _);
        check("SegaPCM: no-loop channel stops at end (86h bit0 set by chip)", !c.ChannelActive(0) && (c.Ram[0x86] & 1) == 1, c.Ram[0x86].ToString("X2"));
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockSegaPcm, 4000000);
        w.SetClock(VgmWriter.SegaPcmInterface, SegaPcm.Bank12M);
        w.Write(0xC0, 0x86, 0x00, 0x01); w.Wait(10);
        var v = VgmFormat.Read(w.ToArray());
        check("VGM SegaPCM (38h, interface 3Ch) read", v.SegaPcmClock == 4000000 && v.SegaPcmInterface == SegaPcm.Bank12M && v.IsPlayable && !v.UnsupportedChips.Contains("SegaPCM"), v.ChipSummary);
        check("hoot sega pcm boards: playable types", HootArchive.Playable("sega", "outrun") && HootArchive.Playable("sega", "gforce") && HootArchive.Playable("sega", "hangon") && HootArchive.Playable("bootleg", "outrun"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot sega pcm real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "outrun", "hangon", "gforce2", "aburner2" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot sega pcm " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSegaPcmBoard.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot sega pcm " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot sega pcm " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootSegaPcmBoard.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
        m.Play(code);
        m.RunSeconds(seconds);
        if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
        var live = m.Log.Where(x => x.t > 0).ToList();
        int fm = m.HangOn ? live.Count(x => x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0) + live.Count(x => x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x0F) != 0)
                          : live.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = live.Count(x => x.chip == 1 && x.reg >= 0x80 && (x.reg & 7) == 6 && (x.val & 1) == 0);
        bool sound = fm >= 16 || pcm >= 2;
        return (sound, "fm " + fm + " pcm starts " + pcm + ", writes " + live.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : ""));
    }

    /// <summary>진단: --hootspcmsurvey roms폴더 [zip 수] [초].</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var types = new[] { "outrun", "shangon", "toutrun", "aburner", "spmonaco", "gforce", "pdrift", "hangon", "sharrier", "eropnx2" };
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => types.Any(t => x.Contains("type=\"" + t + "\""))))
            .Where(z => Environment.GetEnvironmentVariable("HOOT_ONLY") == null || Environment.GetEnvironmentVariable("HOOT_ONLY").Split(',').Contains(Path.GetFileNameWithoutExtension(z), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        int total = 0, sound = 0;
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootSegaPcmBoard.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string note) r = (false, "");
                int code = 0;
                try { foreach (var t in e.Titles.Take(6)) { code = t.Code; r = Measure(e, dir, t.Code, seconds); if (r.ok) break; } }
                catch (Exception ex) { r = (false, "exception " + ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + e.Name + " | 0x" + code.ToString("X2") + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("sega pcm boards: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }
}
