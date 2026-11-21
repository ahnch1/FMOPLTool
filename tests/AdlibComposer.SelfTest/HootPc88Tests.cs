using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot PC-8801 (HootPc88): 만든 시작 코드로 규약(포트 00h · 01h · 80h, IM 2 RTC 인터럽트, OPN 타이머) 시험, 실제 zip, 진단 조사.</summary>
public static class HootPc88Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// 시작 코드 (starcruiser88 모양): IM 2 · I = 01h, 벡터 표 0104h (RTC 레벨 2) = 드라이버 0x4000, 명령을 기다려 곡 번호를 01h에 써서 데이터를 올린다.
    /// 드라이버 = 곡 데이터 첫 바이트를 SSG 채널 A 볼륨으로, 셋째 프레임마다 OPN 키온 (28h ← F0h). 두 번째 곡은 OPN 타이머 B 인터럽트(레벨 4, 0108h)로 센다.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        byte[] stub =
        {
            0xF3, 0xED, 0x5E, 0x31, 0x00, 0xF1,             // F000 DI · IM 2 · LD SP,F100
            0x3E, 0x01, 0xED, 0x47,                         // F006 LD A,1 · LD I,A
            0x21, 0x00, 0x40, 0x22, 0x04, 0x01,             // F00A LD HL,4000 · LD (0104),HL  (RTC)
            0x21, 0x20, 0x40, 0x22, 0x08, 0x01,             // F010 LD HL,4020 · LD (0108),HL  (OPN)
            0xFB,                                           // F016 EI
            0xDB, 0x00, 0xB7, 0x28, 0xFB,                   // F017 wait: IN A,(00) · OR A · JR Z,wait
            0xFE, 0x01, 0x20, 0x06,                         // F01C CP 1 · JR NZ,stop
            0xDB, 0x01, 0xD3, 0x01,                         // F020 IN A,(01) · OUT (01),A
            0x18, 0xF1,                                     // F024 JR wait
            0xAF, 0xD3, 0x00, 0x18, 0xED,                   // F026 stop
        };
        byte[] driver = new byte[0x40];
        byte[] rtc =
        {
            0xF5, 0x3E, 0x08, 0xD3, 0x44, 0x3A, 0x00, 0x50, 0xD3, 0x45,   // SSG R8 = (5000)
            0x3E, 0x28, 0xD3, 0x44, 0x3E, 0xF0, 0xD3, 0x45,               // 28h ← F0h
            0xF1, 0xFB, 0xC9,
        };
        Array.Copy(rtc, 0, driver, 0, rtc.Length);
        byte[] opn = { 0xF5, 0x3A, 0x10, 0x50, 0x3C, 0x32, 0x10, 0x50, 0x3E, 0x27, 0xD3, 0x44, 0x3E, 0x2A, 0xD3, 0x45, 0xF1, 0xFB, 0xC9 };   // (5010)++, 27h ← 2Ah (B 다시)
        Array.Copy(opn, 0, driver, 0x20, opn.Length);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATCH"] = stub, ["DRIVER"] = driver, ["M0"] = new byte[] { 0x0C }, ["M1"] = new byte[] { 0x09 },
        };
        string xml = "<game><name>[PC-8801] Test</name><driver type=\"opn\">pc88</driver><options>"
            + "<option name=\"init_pc\" value=\"0xf000\"/><option name=\"mdata_addr\" value=\"0x5000\"/><option name=\"use_rtc\" value=\"0x01\"/>"
            + "</options><romlist archive=\"test88\"><rom type=\"code\" offset=\"0x4000\">DRIVER</rom><rom type=\"code\" offset=\"0xf000\">PATCH</rom>"
            + "<rom type=\"bgm\" offset=\"0x00\">M0</rom><rom type=\"bgm\" offset=\"0x01\">M1</rom></romlist>"
            + "<titlelist><title code=\"0x00\">one</title><title code=\"0x01000001\">two</title></titlelist></game>";
        var e = HootArchive.Parse(xml);
        check("hoot pc88: playable (opn)", HootArchive.Playable(e.Driver, e.Type), e.Driver + "/" + e.Type);

        var m = HootPc88.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        check("hoot pc88: boot reaches the command wait loop", m.Waiting && m.Missing.Count == 0, "PC " + m.Cpu.PC.ToString("X4"));
        m.Play(0x01000001);
        m.RunSeconds(0.1);
        check("hoot pc88: OUT 01h loads bgm (low byte of the code)", m.Loads == 1 && m.Ram[0x5000] == 0x09, "loads " + m.Loads + ", 5000 = " + m.Ram[0x5000].ToString("X2"));
        int vol = m.Log.Count(x => x.port == 0 && x.reg == 8 && x.val == 0x09);
        check("hoot pc88: RTC interrupt (IM 2, level 2) 600 Hz → ~60 in 0.1 s", vol >= 55 && vol <= 65, vol + " volume writes, " + m.Interrupts + " interrupts");

        // OPN 타이머 B: 27h ← 2Ah (B 켜기 · 넘침 알림 · 플래그 지우기), TB = 0 → 1152×256 / 3.9936MHz ≈ 73.8ms
        var m2 = HootPc88.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        m2.Rtc = false;
        m2.Out(0x44, 0x26); m2.Out(0x45, 0x00);
        m2.Out(0x44, 0x27); m2.Out(0x45, 0x2A);
        m2.RunSeconds(1);
        int ticks = m2.Ram[0x5010];
        check("hoot pc88: OPN timer B interrupt (level 4) ≈ 13.5 per second", ticks >= 12 && ticks <= 15, ticks + " ticks");
        // hoot mucom88.cpp: 32h bit7 (SINTM) = 사운드 인터럽트 막음, 사운드 보드 II A8h · A9h = 같은 칩, 깃발은 LOAD만으로
        var m3 = HootPc88.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        m3.Rtc = false;
        m3.Out(0x32, 0x80);
        m3.Out(0xA8, 0x26); m3.Out(0xA9, 0x00);
        m3.Out(0xA8, 0x27); m3.Out(0xA9, 0x2A);
        m3.RunSeconds(1);
        int masked = m3.Ram[0x5010];
        m3.Out(0xAA, 0x00);
        m3.RunSeconds(1);
        check("hoot pc88: SINTM (32h / AAh bit7) masks the OPN IRQ; Sound Board II ports A8h / A9h reach the chip", masked == 0 && m3.Ram[0x5010] >= 12, masked + " → " + m3.Ram[0x5010] + " ticks");
        m3.Out(0x44, 0x27); m3.Out(0x45, 0x32);   // B LOAD만 (ENABLE 없음) · 깃발 지우기
        m3.RunSeconds(0.2);
        check("hoot pc88: OPN timer flag set by LOAD alone, without IRQ enable (hoot ssFMTimer)", (m3.In(0x44) & 2) != 0, m3.In(0x44).ToString("X2"));
        var e4 = HootArchive.Parse(xml.Replace("<options>", "<options><option name=\"baseclock\" value=\"0x04\"/>"));
        var m4 = HootPc88.Boot(e4, n => files.TryGetValue(n, out var d) ? d : null);
        check("hoot pc88: Z80 clock = baseclock option (MHz), default 8 MHz (hoot)", m.CpuHz == 7987200 && m4.CpuHz == 3993600, m.CpuHz + " / " + m4.CpuHz);
        var v = m.ToVgm(1, 0);
        check("hoot pc88: VGM YM2203", v.Ym2203Clock > 0 && v.IsPlayable, v.ChipSummary);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "pc88");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot pc88 real files (_incoming/Hootarchive/pc88 not found)"); return; }
        foreach (var arc in new[] { "starcruiser88", "reviver", "tnmbox_88", "xzr" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot pc88 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            foreach (var hoot in Directory.GetFiles(dir, "*" + HootArchive.Extension).Take(2))
            {
                var e = HootArchive.Load(hoot);
                if (!e.Driver.Equals("pc88", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                var r = Measure(e, dir, e.Titles[0].Code, 10);
                check("hoot pc88 " + arc + " (" + e.Type + "): first song makes sound in 10 s", r.sound, r.note);
            }
        }
        // hoot oldfalcom.cpp 규약 (xanadu · xanadu2 · asteka2): 곡마다 prog · 주소 표 · OUT 02h bgm
        foreach (var (arc, need) in new[] { ("xana_88", 0.8), ("xana2_88", 0.8), ("asteka2", 0.1) })   // asteka2: zip의 APRG (2018년 16KB)가 2001년 표와 판이 달라 1 / 8만
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot pc88 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var e = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootPc88.FalcomType(x.Type) != null);
            if (e == null) { check("hoot pc88 " + arc + ": falcom .hoot entry", false, "none"); continue; }
            var silent = new List<string>();
            string firstNote = "";
            foreach (var (code, _) in e.Titles) { var mr = Measure(e, dir, code, 6); if (!mr.sound) { silent.Add(code.ToString("X4")); if (firstNote.Length == 0) firstNote = " (" + mr.cause + ": " + mr.note + ")"; } }
            int ok = e.Titles.Count - silent.Count;
            check("hoot pc88 " + arc + " (falcom " + e.Type + ", hoot oldfalcom.cpp): songs make sound", HootArchive.Playable(e.Driver, e.Type) && ok >= need * e.Titles.Count,
                ok + " / " + e.Titles.Count + (silent.Count > 0 ? ", silent " + string.Join(" ", silent) + firstNote : ""));
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootPc88.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        bool waiting = m.Waiting;
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.port == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.port == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int rhy = m.Log.Count(x => x.port == 0 && x.reg == 0x10 && (x.val & 0x80) == 0 && (x.val & 0x3F) != 0);
        bool sound = fm + ssg + rhy > 0;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : !waiting ? "boot-not-waiting" : m.Log.Count == 0 ? (m.Interrupts == 0 ? "no-interrupts" : "no-chip-writes") : "writes-no-keyon";
        string note = "fm " + fm + " ssg " + ssg + " rhythm " + rhy + ", writes " + m.Log.Count + ", loads " + m.Loads + ", ints " + m.Interrupts + ", PC " + m.Cpu.PC.ToString("X4") + " IM" + m.Cpu.Im + " I=" + m.Cpu.I.ToString("X2")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", rom types " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hoot88survey 폴더 [zip 수] [초] — pc88 opn · opna 항목마다 첫 곡(조용하면 다음 5곡)이 소리를 내는지, 안 나면 까닭별로.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(88);
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
                if (!e.Driver.Equals("pc88", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
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
        foreach (var kv in stats) Console.WriteLine("pc88 " + kv.Key + ": " + kv.Value.sound + " / " + kv.Value.total);
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        Console.WriteLine(sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }
}
