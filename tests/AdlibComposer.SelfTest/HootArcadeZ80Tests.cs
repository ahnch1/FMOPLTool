using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 세가 System 1 (HootSegaSystem1) · 아이렘 M72 (HootM72): 만든 Z80 프로그램 시험 (NMI · IRQ · RST 벡터 · 칩 쓰기), 듀얼 SN76489 VGM, 실제 zip, 진단 조사.</summary>
public static class HootArcadeZ80Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        SegaSynthetic(check);
        M72Synthetic(check);
        Real(check);
    }

    /// <summary>System 1: 0000h = IM 1 · EI · HALT 고리, 0038h = IRQ (8000h 증가 · A000h ← 9Fh · C000h ← BFh), 0066h = NMI (E000h → 8001h, RETN).</summary>
    private static void SegaSynthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x8000];
        byte[] start = { 0xF3, 0x31, 0xFF, 0x87, 0xED, 0x56, 0xFB, 0x76, 0x18, 0xFD };
        Array.Copy(start, rom, start.Length);
        byte[] irq = { 0xF5, 0x3A, 0x00, 0x80, 0x3C, 0x32, 0x00, 0x80, 0x3E, 0x9F, 0x32, 0x00, 0xA0, 0x3E, 0xBF, 0x32, 0x00, 0xC0, 0xF1, 0xFB, 0xC9 };
        Array.Copy(irq, 0, rom, 0x38, irq.Length);
        byte[] nmi = { 0xF5, 0x3A, 0x00, 0xE0, 0x32, 0x01, 0x80, 0xF1, 0xED, 0x45 };
        Array.Copy(nmi, 0, rom, 0x66, nmi.Length);
        var m = new HootSegaSystem1.Machine(rom);
        m.RunSeconds(1);
        check("hoot sega system1: IRQ 4 per frame ≈ 240 per second", m.Irqs >= 238 && m.Irqs <= 242, m.Irqs + " IRQs");
        m.Play(0x85);
        m.RunSeconds(0.01);
        check("hoot sega system1: command via NMI (E000h)", m.Ram[1] == 0x85 && m.Nmis == 1, m.Ram[1].ToString("X2"));
        check("hoot sega system1: writes to both SN76489", m.Log.Any(x => x.chip == 0 && x.val == 0x9F) && m.Log.Any(x => x.chip == 1 && x.val == 0xBF), "");
        var v = m.ToVgm(1, 0);
        check("hoot sega system1: VGM SN76489 ×2 (2 MHz + 4 MHz)", v.Sn76489Clock == 2000000 && v.Sn76489Clock2 == 4000000, v.ChipSummary);
        // 표준 VGM 듀얼 칩 (클럭 bit30) 읽기
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockSn76489, 3579545, 0x40000000u);
        w.Write(0x30, 0x90); w.Wait(100);
        var dual = VgmFormat.Read(w.ToArray());
        check("VGM dual SN76489 (clock bit30) read", dual.Sn76489Clock == 3579545 && dual.Sn76489Clock2 == 3579545 && dual.ChipSummary.Contains("×2"), dual.ChipSummary);
    }

    /// <summary>M72: 0000h = YM2151 타이머 A 켜기 · EI · HALT 고리, 0018h = 명령 (IN 02h → 8001h, OUT 06h), 0028h = 타이머 (지움, 8000h 증가).</summary>
    private static void M72Synthetic(Action<string, bool, string> check)
    {
        var mem = new byte[0x10000];
        byte[] start =
        {
            0xF3, 0x31, 0xFF, 0xFF,
            0x3E, 0x10, 0xD3, 0x00, 0x3E, 0x00, 0xD3, 0x01,   // TA 위 = 0
            0x3E, 0x14, 0xD3, 0x00, 0x3E, 0x15, 0xD3, 0x01,   // 14h ← 15h
            0xC3, 0x40, 0x00,
        };
        Array.Copy(start, mem, start.Length);
        byte[] loop = { 0xFB, 0x76, 0x18, 0xFD };
        Array.Copy(loop, 0, mem, 0x40, loop.Length);
        byte[] cmd = { 0xF5, 0xDB, 0x02, 0x32, 0x01, 0x80, 0xD3, 0x06, 0xF1, 0xFB, 0xC9 };
        Array.Copy(cmd, 0, mem, 0x60, cmd.Length);
        mem[0x18] = 0xC3; mem[0x19] = 0x60; mem[0x1A] = 0x00;   // RST 18h → 0060h
        byte[] tim = { 0xF5, 0x3E, 0x14, 0xD3, 0x00, 0x3E, 0x15, 0xD3, 0x01, 0x3A, 0x00, 0x80, 0x3C, 0x32, 0x00, 0x80, 0xF1, 0xFB, 0xC9 };
        Array.Copy(tim, 0, mem, 0x80, tim.Length);
        mem[0x28] = 0xC3; mem[0x29] = 0x80; mem[0x2A] = 0x00;   // RST 28h → 0080h
        var m = new HootM72.Machine(mem, 0x10000);
        m.RunSeconds(1);
        check("hoot m72: YM2151 timer → RST 28h ≈ 54 per second", m.Mem[0x8000] >= 52 && m.Mem[0x8000] <= 56, m.Mem[0x8000] + " IRQs");
        m.Play(0x33);
        m.RunSeconds(0.01);
        check("hoot m72: command → RST 18h (port 02h, ack 06h)", m.Mem[0x8001] == 0x33 && m.LatchIrqs == 1, m.Mem[0x8001].ToString("X2"));
        check("hoot m72 / system1: playable types", HootArchive.Playable("irem", "m72") && HootArchive.Playable("sega", "system1") && !HootArchive.Playable("sega", "system18"), "");
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot system1 / m72 real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "nob", "wbml", "rtype", "bchopper" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot arcade " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSegaSystem1.Handles(x.Driver, x.Type) || HootM72.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot arcade " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, cause: "", note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot " + hoot.Type + " " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        Func<string, byte[]> file = name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; };
        if (HootSegaSystem1.Handles(e.Driver, e.Type))
        {
            var m = HootSegaSystem1.Boot(e, file);
            m.Play(code);
            m.RunSeconds(seconds);
            // 소리 = 볼륨 레지스터(1rr1vvvv)에 15 아닌 값, 그리고 톤 쓰기
            int vol = m.Log.Count(x => (x.val & 0x90) == 0x90 && (x.val & 0x0F) != 0x0F);
            int tone = m.Log.Count(x => (x.val & 0x90) == 0x80);
            bool sound = vol >= 8 && tone >= 8;
            string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? "no-chip-writes" : "writes-quiet";
            return (sound, cause, "vol " + vol + " tone " + tone + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
                + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : ""));
        }
        else
        {
            var m = HootM72.Boot(e, file);
            if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
            m.Play(code);
            m.RunSeconds(seconds);
            string q = Environment.GetEnvironmentVariable("HOOT_PCQ");
            if (m.PcCount != null && q != null) foreach (var x in q.Split(',')) { int k = Convert.ToInt32(x, 16); m.PcCount.TryGetValue(k, out int cnt); Console.WriteLine("  PC " + x + " = " + cnt); }
            if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
            int fm = m.Log.Count(x => x.reg == 0x08 && (x.val & 0x78) != 0);
            bool sound = fm >= 16;
            string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? (m.TimerIrqs == 0 ? "no-irq" : "no-chip-writes") : "writes-no-keyon";
            return (sound, cause, m.TimerInfo + ", fm " + fm + ", writes " + m.Log.Count + ", cmd-irq " + m.LatchIrqs + " timer-irq " + m.TimerIrqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
                + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : ""));
        }
    }

    /// <summary>진단: --hootarcadesurvey roms폴더 [zip 수] [초] — hoot 목록에 sega system1 · irem m72로 있는 zip. 첫 곡이 조용하면 다음 곡 5개까지.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains("type=\"system1\"") || x.Contains("type=\"m72\"")))
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
                if (!(HootSegaSystem1.Handles(e.Driver, e.Type) || HootM72.Handles(e.Driver, e.Type)) || e.Titles.Count == 0) continue;
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
        Console.WriteLine("system1 / m72: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
