using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot X1 (HootX1): 만든 시작 코드로 규약(16비트 포트 0000h · 0001h · 000Fh, CTC 채널 0 → 3 연결 인터럽트, use_ctc3 시간 상수) 시험, 실제 zip, 진단 조사.</summary>
public static class HootX1Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// 시작 코드 (x1wibarm 모양): CTC 1FA0h 벡터 00h, 채널 0 = 타이머 ÷256 · 시간 상수 2 (128µs), 채널 3 = 카운터 · 인터럽트 · 시간 상수는 옵션 use_ctc3 (0x80) → 약 61Hz.
    /// 인터럽트 처리기 (F106h 벡터 → 4000h) = 곡 데이터 첫 바이트를 PSG 채널 A 볼륨으로.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        byte[] stub =
        {
            0xF3, 0x31, 0x00, 0xF1, 0xED, 0x5E,             // DI · LD SP,F100 · IM 2
            0x01, 0xA0, 0x1F, 0x21, 0x00, 0xF1, 0x7C, 0xED, 0x47, 0xED, 0x69,   // LD BC,1FA0 · LD HL,F100 · LD A,H · LD I,A · OUT (C),L (벡터 00h)
            0x3E, 0x35, 0xED, 0x79, 0x3E, 0x02, 0xED, 0x79,  // ch0 = 35h (타이머 ÷256, TC 따라옴), TC = 2
            0x03, 0x03, 0x03, 0x3E, 0xD5, 0xED, 0x79,       // ch3 = D5h (인터럽트 · 카운터 · TC 따라옴 — 쓰지 않음)
            0x21, 0x00, 0x40, 0x22, 0x06, 0xF1,             // LD HL,4000 · LD (F106),HL
            0xFB,                                           // EI
            0x01, 0x00, 0x00, 0xED, 0x78, 0xB7, 0x28, 0xF8, // wait: LD BC,0000 · IN A,(C) · OR A · JR Z,wait
            0xFE, 0x01, 0x20, 0x09,                         // CP 1 · JR NZ,stop
            0x01, 0x01, 0x00, 0xED, 0x78, 0xED, 0x79,       // LD BC,0001 · IN A,(C) · OUT (C),A
            0x18, 0xEB,                                     // JR wait
            0xAF, 0xD3, 0x00, 0x18, 0xE6,                   // stop
        };
        byte[] isr =
        {
            0xF5, 0xC5,
            0x01, 0x00, 0x1C, 0x3E, 0x08, 0xED, 0x79,       // PSG 레지스터 8
            0x01, 0x00, 0x1B, 0x3A, 0x00, 0x50, 0xED, 0x79, // 데이터 = (5000)
            0xC1, 0xF1, 0xFB, 0xED, 0x4D,                   // EI · RETI
        };
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATCH"] = stub, ["ISR"] = isr, ["M0"] = new byte[] { 0x0D }, ["M1"] = new byte[] { 0x07 },
        };
        string xml = "<game><name>[X1] Test</name><driver type=\"psg\">x1</driver><options>"
            + "<option name=\"init_pc\" value=\"0xf000\"/><option name=\"mdata_addr\" value=\"0x5000\"/><option name=\"use_ctc3\" value=\"0x80\"/>"
            + "</options><romlist archive=\"x1test\"><rom type=\"code\" offset=\"0xf000\">PATCH</rom><rom type=\"code\" offset=\"0x4000\">ISR</rom>"
            + "<rom type=\"bgm\" offset=\"0x00\">M0</rom><rom type=\"bgm\" offset=\"0x01\">M1</rom></romlist>"
            + "<titlelist><title code=\"0x00\">one</title><title code=\"0x01\">two</title></titlelist></game>";
        var e = HootArchive.Parse(xml);
        check("hoot x1: playable (psg)", HootArchive.Playable(e.Driver, e.Type), e.Driver + "/" + e.Type);
        var m = HootX1.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        check("hoot x1: boot reaches the command wait loop (16-bit port 0000h)", m.Waiting && m.Missing.Count == 0, "PC " + m.Cpu.PC.ToString("X4"));
        m.Play(1);
        m.RunSeconds(1);
        check("hoot x1: OUT 0001h loads bgm", m.Loads == 1 && m.Ram[0x5000] == 0x07, "loads " + m.Loads + ", 5000 = " + m.Ram[0x5000].ToString("X2"));
        int vol = m.Log.Count(x => x.chip == 0 && x.reg == 8 && x.val == 0x07);
        check("hoot x1: CTC ch0 (÷256 × 2) → ch3 counter (use_ctc3 = 80h) ≈ 61 interrupts per second", vol >= 58 && vol <= 64, vol + " volume writes, " + m.Interrupts + " interrupts");
        var v = m.ToVgm(1, 0);
        check("hoot x1: VGM with PSG", v.HasPsg && v.IsPlayable, v.ChipSummary);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "x1");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot x1 real files (_incoming/Hootarchive/x1 not found)"); return; }
        foreach (var arc in new[] { "x1rv", "x1wibarm" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot x1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            foreach (var hoot in Directory.GetFiles(dir, "*" + HootArchive.Extension).Take(2))
            {
                var e = HootArchive.Load(hoot);
                if (!e.Driver.Equals("x1", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                var r = Measure(e, dir, e.Titles[0].Code, 10);
                check("hoot x1 " + arc + " (" + e.Type + "): first song makes sound in 10 s", r.sound, r.note);
            }
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootX1.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        bool waiting = m.Waiting;
        m.Play(code);
        m.RunSeconds(seconds);
        int psg = m.Log.Count(x => x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int opm = m.Log.Count(x => x.chip == 1 && x.reg == 0x08 && (x.val & 0x78) != 0);
        bool sound = psg + opm > 0;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : !waiting ? "boot-not-waiting" : m.Log.Count == 0 ? (m.Interrupts == 0 ? "no-interrupts" : "no-chip-writes") : "writes-no-keyon";
        string note = "psg " + psg + " opm " + opm + ", writes " + m.Log.Count + ", loads " + m.Loads + ", ints " + m.Interrupts + " (skip " + m.Ignored + "), PC " + m.Cpu.PC.ToString("X4") + " I=" + m.Cpu.I.ToString("X2")
            + ", ctc " + Ctc(m.Main) + " fm " + Ctc(m.Fm)
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", rom types " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>CTC 채널마다 제어어:시간 상수 (w = 시간 상수 기다림, - = 멈춤) · 벡터.</summary>
    private static string Ctc(HootX1.Ctc c)
    {
        return string.Join("/", Enumerable.Range(0, 4).Select(i => c.Control[i].ToString("X2") + ":" + c.Tc[i].ToString("X2") + (c.WaitTc[i] ? "w" : "") + (c.Next[i] == long.MaxValue && !HootX1.Ctc.CounterMode(c.Control[i]) ? "-" : ""))) + " v" + c.Vector.ToString("X2");
    }

    /// <summary>진단: --hootx1survey 폴더 [zip 수] [초].</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(1);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        var stats = new SortedDictionary<string, (int total, int sound)>();
        var causes = new SortedDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!e.Driver.Equals("x1", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                string key = e.Type.ToLowerInvariant();
                stats.TryGetValue(key, out var st); st.total++;
                (bool ok, string cause, string note) r;
                try
                {
                    r = Measure(e, dir, e.Titles[0].Code, seconds);
                    for (int k = 1; !r.ok && k < Math.Min(6, e.Titles.Count); k++)
                    {
                        var r2 = Measure(e, dir, e.Titles[k].Code, seconds);
                        if (r2.sound) r = (true, "sound-later", "title " + (k + 1) + ": " + r2.note);
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) st.sound++;
                stats[key] = st;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine("x1 " + kv.Key + ": " + kv.Value.sound + " / " + kv.Value.total);
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        Console.WriteLine(sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }
}
