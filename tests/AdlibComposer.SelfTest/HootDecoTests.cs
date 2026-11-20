using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 데이터 이스트 HuC6280 보드 (HootDeco): 만든 HuC6280 프로그램 (MPR · YM2151 타이머 IRQ2 · 명령 IRQ1), 듀얼 OKIM6295 VGM, 실제 zip, 진단 조사.</summary>
public static class HootDecoTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// ROM 0000h (논리 E000h, MPR7 = 0): MPR1 = F8h (RAM 2000h), MPR2 = 88h (YM2151 4000h), MPR3 = A0h (명령 6000h), MPR0 = FFh (인터럽트 1402h),
    /// 타이머 A 켜기, 인터럽트 막기 0, CLI, BRA 고리. IRQ2 (FFF6h) = 타이머 지움 · (2000h) 증가, IRQ1 (FFF8h) = (6000h) → 2001h.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x10000];
        byte[] start =
        {
            0x78,
            0xA9, 0xF8, 0x53, 0x02, 0xA9, 0x88, 0x53, 0x04, 0xA9, 0xA0, 0x53, 0x08, 0xA9, 0xFF, 0x53, 0x01,
            0xA9, 0x10, 0x8D, 0x00, 0x40, 0xA9, 0x00, 0x8D, 0x01, 0x40,
            0xA9, 0x14, 0x8D, 0x00, 0x40, 0xA9, 0x15, 0x8D, 0x01, 0x40,
            0xA9, 0x00, 0x8D, 0x02, 0x14,
            0x58, 0x80, 0xFE,
        };
        Array.Copy(start, rom, start.Length);
        byte[] irq2 = { 0xA9, 0x14, 0x8D, 0x00, 0x40, 0xA9, 0x15, 0x8D, 0x01, 0x40, 0xEE, 0x00, 0x20, 0x40 };
        Array.Copy(irq2, 0, rom, 0x100, irq2.Length);
        byte[] irq1 = { 0xAD, 0x00, 0x60, 0x8D, 0x01, 0x20, 0x40 };
        Array.Copy(irq1, 0, rom, 0x180, irq1.Length);
        rom[0x1FF6] = 0x00; rom[0x1FF7] = 0xE1;   // IRQ2 → E100h
        rom[0x1FF8] = 0x80; rom[0x1FF9] = 0xE1;   // IRQ1 → E180h
        rom[0x1FFE] = 0x00; rom[0x1FFF] = 0xE0;   // 리셋 → E000h
        var pcm = new byte[0x40000];
        var m = new HootDeco.Machine(rom, pcm, pcm, false);
        m.RunSeconds(1);
        // 타이머 A: TA = 0 → 64 × 1024 / 3.58MHz = 18.3ms → 1초에 약 54번
        check("hoot deco: HuC6280 reset vector + MPR, YM2151 timer IRQ2 ≈ 54 per second", m.Ram[0] >= 52 && m.Ram[0] <= 56, m.Ram[0] + " IRQs, PC " + m.Cpu.PC.ToString("X4"));
        m.Play(0x2C);
        m.RunSeconds(0.01);
        check("hoot deco: command via IRQ1 (140000h)", m.Ram[1] == 0x2C && m.LatchIrqs == 1, m.Ram[1].ToString("X2"));
        var v = m.ToVgm(1, 0);
        check("hoot deco: VGM YM2151 + OKIM6295 ×2 (1 MHz + 2 MHz)", v.Ym2151Clock > 0 && Math.Abs(v.Okim6295Clock - 1006875) < 1 && Math.Abs(v.Okim6295Clock2 - 2013750) < 1, v.ChipSummary);
        // 표준 VGM 듀얼 OKI 읽기
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockOkim6295, 1000000, 0xC0000000u);
        w.Write(0xB8, 0x80, 0x78); w.Wait(100);
        var dual = VgmFormat.Read(w.ToArray());
        check("VGM dual OKIM6295 (clock bit30) read", dual.Okim6295Clock2 == 1000000 && dual.OkiPin7 && dual.ChipSummary.Contains("×2"), dual.ChipSummary);
        check("hoot deco: playable types", HootArchive.Playable("dataeast", "thndzone") && HootArchive.Playable("dataeast", "deco32") && HootDeco.Handles("dataeast", "midres") && !HootArchive.Playable("dataeast", "nslasher"), "");
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot deco real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "darkseal", "supbtime", "mutantf", "midres" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot deco " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootDeco.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot deco " + arc + ": .hoot entry", false, "none"); continue; }
            if (Environment.GetEnvironmentVariable("HOOT_SONGS") != null)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, cause: "", note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            if (arc == "midres")
            {
                // YM3812 + YM2203 + OKI: 렌더한 VGM을 VgmPlayer로 재생하면 들린다
                var vgm = HootDeco.Render(hoot, dir, code, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot deco midres: audible through VGM (YM3812 + YM2203 + OKIM6295)", peak > 0.02 && vgm.ChipSummary.Contains("OPL"), "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
            check("hoot deco " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootDeco.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
        m.Play(code);
        m.RunSeconds(seconds);
        if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int opn = m.Log.Count(x => x.chip == 1 && x.reg == 0x28 && (x.val & 0xF0) != 0)
            + m.Log.Count(x => x.chip == 4 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));   // midres YM3812 키온도
        int pcm = m.Oki1.Starts + m.Oki2.Starts;
        bool sound = fm + opn >= 16 || pcm >= 2;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? (m.YmIrqs + m.TimerIrqs == 0 ? "no-irq" : "no-chip-writes") : "writes-no-keyon";
        string note = "fm " + fm + " opn " + opn + " oki " + m.Oki1.Starts + "/" + m.Oki2.Starts + ", writes " + m.Log.Count + ", cmd-irq " + m.LatchIrqs + " ym-irq " + m.YmIrqs + " timer-irq " + m.TimerIrqs + " latch " + m.LatchReads
            + ", PC " + m.Cpu.PC.ToString("X4") + (m.Cpu.UnknownOps > 0 ? ", unknown ops " + m.Cpu.UnknownOps : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootdecosurvey roms폴더 [zip 수] [초] — hoot 목록에 dataeast (HuC6280 보드 형식)로 있는 zip. 첫 곡이 조용하면 다음 곡 5개까지.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains(">dataeast</driver>")))
            .Where(z => Environment.GetEnvironmentVariable("HOOT_ONLY") == null || Environment.GetEnvironmentVariable("HOOT_ONLY").Split(',').Contains(Path.GetFileNameWithoutExtension(z), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        int total = 0, sound = 0;
        var causes = new SortedDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootDeco.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                int code = 0;
                try
                {
                    foreach (var t in e.Titles.Take(6))
                    {
                        code = t.Code;
                        r = Measure(e, dir, t.Code, seconds);
                        if (r.ok) break;
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                string key = e.Type + " " + r.cause;
                causes.TryGetValue(key, out int c); causes[key] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + e.Name + " | 0x" + code.ToString("X2") + " " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("dataeast: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
