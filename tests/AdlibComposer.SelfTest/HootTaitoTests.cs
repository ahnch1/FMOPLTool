using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 타이토 YM2610 보드 (HootTaito): 만든 Z80 프로그램으로 TC0140SYT 니블 명령 · NMI · 뱅크 · 타이머 IRQ 시험, 실제 zip, 진단 조사.</summary>
public static class HootTaitoTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// 0000h: IM 1 · 타이머 A 켜기 · TC0140SYT NMI 켬 (모드 6) · 뱅크 3 → (4000h)를 C002h에 · EI · HALT 고리.
    /// 0038h = IRQ (27h ← 15h로 플래그 지움, C000h 증가), 0066h = NMI (모드 0 → 니블 둘 → C010h · C011h, RETN).
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x10000];
        byte[] start =
        {
            0xF3, 0x31, 0xFF, 0xDF, 0xED, 0x56,                         // DI · LD SP,DFFF · IM 1
            0x3E, 0x24, 0x32, 0x00, 0xE0, 0x3E, 0x00, 0x32, 0x01, 0xE0, // TA 위 = 0
            0x3E, 0x27, 0x32, 0x00, 0xE0, 0x3E, 0x15, 0x32, 0x01, 0xE0, // 27h ← 15h
            0x3E, 0x06, 0x32, 0x00, 0xE2, 0x32, 0x01, 0xE2,             // TC0140SYT 모드 6 = NMI 켬
            0x3E, 0x03, 0x32, 0x00, 0xF2, 0x3A, 0x00, 0x40, 0x32, 0x02, 0xC0,   // 뱅크 3 · (C002) ← (4000)
            0xFB, 0x76, 0x18, 0xFD,                                     // EI · HALT · JR -3
        };
        Array.Copy(start, rom, start.Length);
        byte[] irq = { 0xF5, 0x3E, 0x27, 0x32, 0x00, 0xE0, 0x3E, 0x15, 0x32, 0x01, 0xE0, 0x3A, 0x00, 0xC0, 0x3C, 0x32, 0x00, 0xC0, 0xF1, 0xFB, 0xC9 };
        Array.Copy(irq, 0, rom, 0x38, irq.Length);
        byte[] nmi = { 0xF5, 0xAF, 0x32, 0x00, 0xE2, 0x3A, 0x01, 0xE2, 0x32, 0x10, 0xC0, 0x3A, 0x01, 0xE2, 0x32, 0x11, 0xC0, 0xF1, 0xED, 0x45 };
        Array.Copy(nmi, 0, rom, 0x66, nmi.Length);
        rom[0xC000] = 0x5A;
        var m = new HootTaito.Machine(rom, new byte[16], null, false);
        m.RunSeconds(1);
        int ticks = m.Ram[0];
        check("hoot taito: YM2610 timer A IRQ (IM 1) ≈ 54 per second", ticks >= 52 && ticks <= 56, ticks + " IRQs");
        check("hoot taito: bank window (F200h = 3 → 4000h = ROM C000h)", m.Ram[2] == 0x5A, m.Ram[2].ToString("X2"));
        m.CommandPort = 0;
        m.Play(0x2B);
        m.RunSeconds(0.1);
        check("hoot taito: TC0140SYT command as two nibbles via NMI", m.Ram[0x10] == 0x0B && m.Ram[0x11] == 0x02 && m.Nmis == 1, m.Ram[0x10].ToString("X") + " " + m.Ram[0x11].ToString("X") + ", NMIs " + m.Nmis);
        var v = new HootTaito.Machine(rom, new byte[16], null, true).ToVgm(1, 0);
        check("hoot taito: VGM YM2610B with ADPCM ROM block", v.Ym2610Clock > 0 && v.Ym2610B, v.ChipSummary);
        check("hoot taito: playable types", HootArchive.Playable("taito", "f2system") && HootArchive.Playable("taito", "dual68") && HootArchive.Playable("taito", "f3system") && !HootTaito.Handles("taito", "f3system"), "");
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot taito real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "topland", "tetrist", "ninjaw" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot taito " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "taito");
            if (hoot == null) { check("hoot taito " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, cause: "", note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot taito " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootTaito.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
        string w = Environment.GetEnvironmentVariable("HOOT_WATCH");
        if (w != null) m.WatchAddr = Convert.ToInt32(w, 16);
        m.Play(code);
        m.RunSeconds(seconds);
        if (Environment.GetEnvironmentVariable("HOOT_REGS") != null) Console.WriteLine("  regs: " + string.Join(" ", m.Log.GroupBy(x => x.port * 256 + x.reg).OrderByDescending(g => g.Count()).Take(30).Select(g => g.Key.ToString("X3") + ":" + g.Count())));
        if (w != null) Console.WriteLine("  read " + w + " at: " + string.Join(" ", m.WatchPcs.Select(x => x.ToString("X4"))));
        string ramEnv = Environment.GetEnvironmentVariable("HOOT_RAM");
        if (ramEnv != null) { var pr = ramEnv.Split(','); int st = Convert.ToInt32(pr[0], 16) - 0xC000, ln = Convert.ToInt32(pr[1], 16); Console.WriteLine("  RAM " + pr[0] + ": " + BitConverter.ToString(m.Ram, st, ln)); }
        string q = Environment.GetEnvironmentVariable("HOOT_PCQ");
        if (m.PcCount != null && q != null) foreach (var x in q.Split(',')) { int k = Convert.ToInt32(x, 16); m.PcCount.TryGetValue(k, out int cnt); Console.WriteLine("  PC " + x + " = " + cnt); }
        if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
        int fm = m.Log.Count(x => x.port == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.port == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int pa = m.Log.Count(x => x.port == 1 && x.reg == 0x00 && (x.val & 0x80) == 0 && (x.val & 0x3F) != 0);
        int pb = m.Log.Count(x => x.port == 0 && x.reg == 0x10 && (x.val & 0x80) != 0);
        bool sound = fm + ssg + pa + pb > 0;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? (m.Irqs == 0 ? "no-irq" : "no-chip-writes") : m.Nmis == 0 ? "no-nmi" : "writes-no-keyon";
        string note = (m.SoundOn ? "EF " : "") + "fm " + fm + " ssg " + ssg + " adpcmA " + pa + " adpcmB " + pb + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " read " + m.Polls + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hoottaitosurvey roms폴더 [zip 수] [초] — hoot 목록에 taito (YM2610 보드)로 있는 zip만. 첫 곡이 조용하면 다음 곡 3개까지.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains(">taito</driver>")))
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
                if (!HootTaito.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                int code = 0;
                try
                {
                    foreach (var t in e.Titles.Take(4))
                    {
                        code = t.Code;
                        r = Measure(e, dir, t.Code, seconds);
                        if (r.ok) break;
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + e.Name + " | 0x" + code.ToString("X2") + " " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("taito: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
