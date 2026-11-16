using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 네오지오 (HootNeoGeo): 만든 M1 프로그램으로 NMI 명령 · 뱅크 · YM2610 타이머 IRQ 시험, 실제 zip, 진단 조사.</summary>
public static class HootNeoGeoTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// M1: 0000h = IM 1 · 타이머 A 켜기 (27h ← 15h) · EI · HALT 고리, 0038h = IRQ (27h ← 15h로 플래그 지움 · F800h 증가), 0066h = NMI (IN A,(00) → F801h, OUT (0Ch),A, RETN).
    /// 뱅크: IN A,(0Bh) (B = 03h) 뒤 8000h 읽기 = ROM 0C000h.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x10000];
        byte[] start =
        {
            0xF3, 0x31, 0xFF, 0xFF, 0xED, 0x56,                         // DI · LD SP,FFFF · IM 1
            0x3E, 0x24, 0xD3, 0x04, 0x3E, 0x00, 0xD3, 0x05,             // TA 위 = 0
            0x3E, 0x27, 0xD3, 0x04, 0x3E, 0x15, 0xD3, 0x05,             // 27h ← 15h (A 켬 · 알림 · 지움)
            0x06, 0x03, 0x0E, 0x0B, 0xED, 0x78, 0x3A, 0x00, 0x80, 0x32, 0x02, 0xF8,   // IN A,(C) (뱅크 3 → 8000h) · (F802) ← (8000)
            0xFB, 0x76, 0x18, 0xFD,                                     // EI · HALT · JR -3
        };
        Array.Copy(start, rom, start.Length);
        byte[] irq = { 0xF5, 0x3E, 0x27, 0xD3, 0x04, 0x3E, 0x15, 0xD3, 0x05, 0x3A, 0x00, 0xF8, 0x3C, 0x32, 0x00, 0xF8, 0xF1, 0xFB, 0xC9 };
        Array.Copy(irq, 0, rom, 0x38, irq.Length);
        byte[] nmi = { 0xF5, 0xDB, 0x00, 0x32, 0x01, 0xF8, 0xD3, 0x0C, 0xF1, 0xED, 0x45 };
        Array.Copy(nmi, 0, rom, 0x66, nmi.Length);
        rom[0xC000] = 0x5A;
        var m = new HootNeoGeo.Machine(rom, new byte[16], null);
        m.RunSeconds(1);
        int ticks = m.Ram[0];   // F800h
        // 타이머 A: TA = 0 → 144 × 1024 / 8MHz = 18.4ms → 1초에 약 54번
        check("hoot neogeo: YM2610 timer A IRQ (IM 1) ≈ 54 per second", ticks >= 52 && ticks <= 56, ticks + " IRQs");
        check("hoot neogeo: bank window (IN 0Bh with B = 3 → 8000h = ROM C000h)", m.Ram[2] == 0x5A, m.Ram[2].ToString("X2"));
        m.Play(0x21);
        m.RunSeconds(0.1);
        check("hoot neogeo: command via NMI (port 00h, reply 0Ch)", m.Ram[1] == 0x21 && m.Nmis == 1 && m.Replies == 1, "F801 = " + m.Ram[1].ToString("X2") + ", NMIs " + m.Nmis);
        var v = m.ToVgm(1, 0);
        check("hoot neogeo: VGM YM2610 with ADPCM ROM blocks", v.Ym2610Clock > 0 && v.IsPlayable, v.ChipSummary);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot neogeo real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "maglord", "aof" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot neogeo " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "neogeo");
            if (hoot == null) { check("hoot neogeo " + arc + ": .hoot entry", false, "none"); continue; }
            var song = hoot.Titles.FirstOrDefault(t => t.Code >= 0x10);
            var r = Measure(hoot, dir, song.Code, 10);
            check("hoot neogeo " + arc + ": song 0x" + song.Code.ToString("X2") + " makes sound in 10 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNeoGeo.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.port == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.port == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int pa = m.Log.Count(x => x.port == 1 && x.reg == 0x00 && (x.val & 0x80) == 0 && (x.val & 0x3F) != 0);
        int pb = m.Log.Count(x => x.port == 0 && x.reg == 0x10 && (x.val & 0x80) != 0);
        bool sound = fm + ssg + pa + pb > 0;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? (m.Irqs == 0 ? "no-irq" : "no-chip-writes") : "writes-no-keyon";
        string note = (m.Makoto ? "MAKOTO " : "") + "fm " + fm + " ssg " + ssg + " adpcmA " + pa + " adpcmB " + pb + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " reply " + m.Replies + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootneosurvey roms폴더 [zip 수] [초] — hoot 목록에 neogeo로 있는 zip만.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains(">neogeo</driver>")))
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
                if (!e.Driver.Equals("neogeo", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                try
                {
                    // 로고 · 효과음 말고 음악일 만한 곡 (코드 10h 이상) 몇 개
                    foreach (var t in e.Titles.Where(t => t.Code >= 0x10).Take(3).DefaultIfEmpty(e.Titles[0]))
                    {
                        r = Measure(e, dir, t.Code, seconds);
                        if (r.ok) break;
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("neogeo: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
