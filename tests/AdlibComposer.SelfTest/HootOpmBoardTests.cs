using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 아케이드 Z80 + YM2151 + OKIM6295 보드 (HootOpmBoard: CPS-1 · Double Dragon 2 · 3): 만든 Z80 프로그램 시험, 실제 zip, 진단 조사.</summary>
public static class HootOpmBoardTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// CPS-1 배치: 0000h = IM 1 · YM2151 타이머 A 켜기 (14h ← 15h) · 뱅크 1 → (8000h)를 D002h에 · OKI 구절 1을 음성 1로 · EI · HALT 고리.
    /// 0038h = IRQ (14h ← 15h로 플래그 지움, D000h 증가, 래치 F008h → D001h, OKI 상태 → D003h).
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x10000];
        byte[] start =
        {
            0xF3, 0x31, 0xFF, 0xD7, 0xED, 0x56,                         // DI · LD SP,D7FF · IM 1
            0x3E, 0x10, 0x32, 0x00, 0xF0, 0x3E, 0x00, 0x32, 0x01, 0xF0, // TA 위 = 0
            0x3E, 0x14, 0x32, 0x00, 0xF0, 0x3E, 0x15, 0x32, 0x01, 0xF0, // 14h ← 15h (A 켬 · IRQ A · 지움)
            0x3E, 0x01, 0x32, 0x04, 0xF0, 0x3A, 0x00, 0x80, 0x32, 0x02, 0xD0,   // 뱅크 1 · (D002) ← (8000)
            0x3E, 0x81, 0x32, 0x02, 0xF0, 0x3E, 0x10, 0x32, 0x02, 0xF0,         // OKI: 구절 1 · 음성 1
            0xFB, 0x76, 0x18, 0xFD,                                     // EI · HALT · JR -3
        };
        Array.Copy(start, rom, start.Length);
        byte[] irq = { 0xF5, 0x3E, 0x14, 0x32, 0x00, 0xF0, 0x3E, 0x15, 0x32, 0x01, 0xF0, 0x3A, 0x00, 0xD0, 0x3C, 0x32, 0x00, 0xD0,
            0x3A, 0x08, 0xF0, 0x32, 0x01, 0xD0, 0x3A, 0x02, 0xF0, 0x32, 0x03, 0xD0, 0xF1, 0xFB, 0xC9 };
        Array.Copy(irq, 0, rom, 0x38, irq.Length);
        rom[0xC000] = 0x5A;   // 뱅크 1 = ROM 8000h + 4000h
        var pcm = new byte[0x40000];
        // 구절 1: 400h ~ 400h + 8000h → 65538 니블 / (1MHz / 132) ≈ 8.65초
        pcm[8] = 0x00; pcm[9] = 0x04; pcm[10] = 0x00; pcm[11] = 0x00; pcm[12] = 0x84; pcm[13] = 0x00;
        var m = new HootOpmBoard.Machine(HootOpmBoard.Cps1, rom, pcm);
        m.RunSeconds(1);
        int ticks = m.Ram[0];
        // 타이머 A: TA = 0 → 64 × 1024 / 3.58MHz = 18.3ms → 1초에 약 54번
        check("hoot opm board: YM2151 timer A IRQ (IM 1) ≈ 54 per second", ticks >= 52 && ticks <= 56, ticks + " IRQs");
        check("hoot opm board: CPS-1 bank (F004h = 1 → 8000h = ROM C000h)", m.Ram[2] == 0x5A, m.Ram[2].ToString("X2"));
        check("hoot opm board: CPS-1 latch idle = FFh, OKI voice 1 busy", m.Ram[1] == 0xFF && (m.Ram[3] & 0x0F) == 1 && m.OkiStarts == 1, m.Ram[1].ToString("X2") + " / " + m.Ram[3].ToString("X2"));
        m.Play(0x21);
        m.RunSeconds(0.1);
        check("hoot opm board: CPS-1 command in latch F008h", m.Ram[1] == 0x21, m.Ram[1].ToString("X2"));
        var v = m.ToVgm(1, 0);
        check("hoot opm board: VGM YM2151 + OKIM6295 with ROM block", v.Ym2151Clock > 0 && v.Okim6295Clock > 0 && v.OkiPin7, v.ChipSummary);
        check("hoot opm board: playable types", HootArchive.Playable("capcom", "cps1") && HootArchive.Playable("technos", "ddragon2") && HootArchive.Playable("technos", "ddragon3") && !HootArchive.Playable("capcom", "cps2simm") && !HootOpmBoard.Handles("capcom", "opn2"), "");
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot opm board real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "forgottn", "ddragon2", "ddragon3", "fstarfrc", "ginkun", "riot", "bionicc" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot opm board " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootOpmBoard.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot opm board " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, cause: "", note: "");
            int code = 0;
            if (Environment.GetEnvironmentVariable("HOOT_SONGS") != null)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot opm board " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootOpmBoard.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        string pcq = Environment.GetEnvironmentVariable("HOOT_PCQ");
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null || pcq != null || Environment.GetEnvironmentVariable("HOOT_PCR") != null) m.PcCount = m.PcCount ?? new Dictionary<int, int>();
        m.Play(code);
        m.RunSeconds(seconds);
        string pcr = Environment.GetEnvironmentVariable("HOOT_PCR");   // 진단: "시작,끝" 범위의 PC 전부 (주소 순)
        if (pcr != null && m.PcCount != null) { var lr = pcr.Split(','); int lo = Convert.ToInt32(lr[0], 16), hi = Convert.ToInt32(lr[1], 16); Console.WriteLine("  PCR: " + string.Join(" ", m.PcCount.Where(x => x.Key >= lo && x.Key < hi).OrderBy(x => x.Key).Select(x => x.Key.ToString("X4") + ":" + x.Value))); }
        if (pcq != null) Console.WriteLine("  PCQ:" + string.Concat(pcq.Split(',').Select(q => { m.PcCount.TryGetValue(Convert.ToInt32(q, 16), out int c); return " " + q + "=" + c; })));
        else if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
        if (Environment.GetEnvironmentVariable("HOOT_REGS") != null) Console.WriteLine("  regs: " + string.Join(" ", m.Log.GroupBy(x => x.chip * 256 + x.reg).OrderByDescending(g => g.Count()).Take(30).Select(g => g.Key.ToString("X3") + ":" + g.Count())));
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.OkiStarts;
        bool sound = fm >= 16 || pcm >= 2;   // 멈춤 · 짧은 효과음 말고 곡다운 것
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? (m.Irqs == 0 ? "no-irq" : "no-chip-writes") : "writes-no-keyon";
        string note = "fm " + fm + " oki " + pcm + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootopmsurvey roms폴더 [zip 수] [초] — hoot 목록에 cps1 · ddragon2 · ddragon3 · ctribe로 있는 zip만. 첫 곡이 조용하면 다음 곡 3개까지.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains("type=\"cps1\"") || x.Contains("type=\"ddragon2\"") || x.Contains("type=\"ddragon3\"") || x.Contains("type=\"ctribe\"")))
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
                if (!HootOpmBoard.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
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
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + e.Name + " | 0x" + code.ToString("X2") + " " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("opm boards: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
