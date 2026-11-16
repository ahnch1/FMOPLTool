using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 메가드라이브 Z80 드라이버 (HootMegaDrive): 만든 카트리지로 드라이버 올리기 · 요청 주소 · VBlank INT · 뱅크 · YM2612 / PSG 시험, 실제 zip, 진단 조사.</summary>
public static class HootMegaDriveTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// 카트리지 10000h에 Z80 드라이버: 0000h = IM 1 · EI · HALT 고리, 0038h = VBlank: (0100h)가 0이 아니면 YM2612 28h ← F0h (키온), PSG 볼륨 쓰기, 뱅크 2 (10000h)의 8100h 바이트를 0102h에.
    /// hoot: topaddr 10000h · data1 0 · size1 200h · reqaddr 0100h.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        var rom = new byte[0x20000];
        byte[] drv = new byte[0x200];
        byte[] start = { 0xF3, 0x31, 0x00, 0x1F, 0xED, 0x56, 0xFB, 0x76, 0x18, 0xFD };
        Array.Copy(start, drv, start.Length);
        byte[] isr =
        {
            0xF5, 0x3A, 0x00, 0x01, 0xB7, 0x28, 0x15,               // LD A,(0100) · OR A · JR Z,끝
            0x3E, 0x28, 0x32, 0x00, 0x40, 0x3E, 0xF0, 0x32, 0x01, 0x40,   // YM 28h ← F0h
            0x3E, 0x90, 0x32, 0x11, 0x7F,                           // PSG 볼륨 (채널 0 = 0)
            0x3A, 0x80, 0x81, 0x32, 0x02, 0x01,                     // (0102) ← (8180) (뱅크)
            0xF1, 0xFB, 0xC9,
        };
        Array.Copy(isr, 0, drv, 0x38, isr.Length);
        // 뱅크 2 (10000h): 6000h에 bit0을 9번 (0,1,0,0,0,0,0,0,0 → 뱅크 = 2)
        byte[] bankSet = { 0x21, 0x00, 0x60, 0x36, 0x00, 0x36, 0x01, 0x36, 0x00, 0x36, 0x00, 0x36, 0x00, 0x36, 0x00, 0x36, 0x00, 0x36, 0x00, 0x36, 0x00 };
        Array.Copy(start, 0, drv, 0x80, 6);
        Array.Copy(bankSet, 0, drv, 0x86, bankSet.Length);
        drv[0x86 + bankSet.Length] = 0xFB; drv[0x87 + bankSet.Length] = 0x76; drv[0x88 + bankSet.Length] = 0x18; drv[0x89 + bankSet.Length] = 0xFD;
        drv[0] = 0xC3; drv[1] = 0x80; drv[2] = 0x00;   // JP 0080h
        Array.Copy(drv, 0, rom, 0x10000, drv.Length);
        rom[0x10180] = 0x77;
        var files = new Dictionary<string, byte[]> { ["test.bin"] = rom };
        string xml = "<game><name>[Mega Drive] Test</name><driver type=\"generic_z80\">megadrive</driver><options>"
            + "<option name=\"topaddr\" value=\"0x10000\"/><option name=\"data1\" value=\"0x0000\"/><option name=\"size1\" value=\"0x100\"/><option name=\"reqaddr\" value=\"0x0100\"/>"
            + "</options><romlist archive=\"mdtest\"><rom type=\"code\">test.bin</rom></romlist><titlelist><title code=\"0x05\">five</title></titlelist></game>";
        var e = HootArchive.Parse(xml);
        check("hoot megadrive: playable (generic_z80)", HootArchive.Playable(e.Driver, e.Type), e.Driver + "/" + e.Type);
        var m = HootMegaDrive.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        int before = m.Log.Count(x => x.chip == 0 && x.reg == 0x28);
        m.Play(5);
        m.RunSeconds(1);
        int keys = m.Log.Count(x => x.chip == 0 && x.reg == 0x28 && x.val == 0xF0);
        check("hoot megadrive: request byte at reqaddr → driver runs on VBlank (~60 key-ons per second)", before == 0 && m.Ram[0x100] == 5 && keys >= 58 && keys <= 62, keys + " key-ons");
        check("hoot megadrive: PSG at 7F11h", m.Log.Any(x => x.chip == 2 && x.val == 0x90), m.Log.Count(x => x.chip == 2) + " PSG writes");
        check("hoot megadrive: bank register 6000h (9 bits) → 8000h window", m.Ram[0x102] == 0x77, m.Ram[0x102].ToString("X2"));
        var v = m.ToVgm(1, 0);
        check("hoot megadrive: VGM YM2612 + SN76489", v.Ym2612Clock > 0 && v.Sn76489Clock > 0, v.ChipSummary);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "smd");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot megadrive real files (_incoming/Hootarchive/smd not found)"); return; }
        foreach (var arc in new[] { "zoopu_smd", "lordmon_smd" })   // generic_z80 · iyinc (reqaddr 없음 → 1FFFh)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot megadrive " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "megadrive");
            if (hoot == null || hoot.Titles.Count == 0) { check("hoot megadrive " + arc + ": .hoot entry", false, "none"); continue; }
            (bool sound, string cause, string note) r = (false, "", "");
            int k = 0;
            for (; k < Math.Min(4, hoot.Titles.Count) && !r.sound; k++) r = Measure(hoot, dir, hoot.Titles[k].Code, 10);
            check("hoot megadrive " + arc + " (" + hoot.Type + "): song " + k + " makes sound in 10 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootMegaDrive.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int dac = m.Log.Count(x => x.chip == 0 && x.reg == 0x2A);
        int psg = m.Log.Count(x => x.chip == 2 && (x.val & 0x90) == 0x90 && (x.val & 0x0F) != 0x0F);
        bool sound = fm + psg > 0 || dac > 1000;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? "no-chip-writes" : "writes-no-keyon";
        string note = "fm " + fm + " psg " + psg + " dac " + dac + ", writes " + m.Log.Count + ", frames " + m.Frames + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(2)) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootmdsurvey smd폴더 [zip 수] [초].</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
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
                if (!e.Driver.Equals("megadrive", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                string key = e.Type.ToLowerInvariant();
                stats.TryGetValue(key, out var st); st.total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                try
                {
                    for (int k = 0; k < Math.Min(4, e.Titles.Count); k++)
                    {
                        r = Measure(e, dir, e.Titles[k].Code, seconds);
                        if (r.ok) break;
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) st.sound++;
                stats[key] = st;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine("megadrive " + kv.Key + ": " + kv.Value.sound + " / " + kv.Value.total);
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        Console.WriteLine(sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }
}
