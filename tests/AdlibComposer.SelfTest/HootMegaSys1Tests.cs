using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 잘레코 Mega System 1 (HootMegaSys1): 만든 68000 프로그램 (명령 IRQ 4 자동 벡터), 실제 zip, 진단 조사.</summary>
public static class HootMegaSys1Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        // 0000h: SSP 000F0000h, PC 0100h, 자동 벡터 4 (70h) → 0200h. 0100h: MOVE #$2000,SR · BRA *. 0200h: MOVE.B $40001,$E0001 · RTE
        var rom = new byte[0x20000];
        void Put32(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        Put32(0, 0x000F0000); Put32(4, 0x100); Put32(0x70, 0x200);
        byte[] main = { 0x46, 0xFC, 0x20, 0x00, 0x60, 0xFE };
        Array.Copy(main, 0, rom, 0x100, main.Length);
        byte[] irq = { 0x13, 0xF9, 0x00, 0x04, 0x00, 0x01, 0x00, 0x0E, 0x00, 0x01, 0x4E, 0x73 };
        Array.Copy(irq, 0, rom, 0x200, irq.Length);
        var m = new HootMegaSys1.Machine(rom, new byte[0], new byte[0]);
        m.RunSeconds(0.1);
        m.Play(0x5A);
        m.RunSeconds(0.01);
        check("hoot megasys1: command via IRQ 4 autovector (040001h)", m.Ram[1] == 0x5A && m.LatchIrqs == 1, m.Ram[1].ToString("X2") + ", PC " + m.Cpu.PC.ToString("X6"));
        check("hoot megasys1: playable type", HootArchive.Playable("jaleco", "megasys1") && !HootArchive.Playable("jaleco", "argus"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot megasys1 real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "iganinju", "p47", "edf" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot megasys1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootMegaSys1.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot megasys1 " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, cause: "", note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot megasys1 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootMegaSys1.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.Oki1.Starts + m.Oki2.Starts;
        bool sound = fm >= 16 || pcm >= 2;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : m.Log.Count == 0 ? "no-chip-writes" : "writes-no-keyon";
        string note = m.TimerInfo + ", fm " + fm + " oki " + m.Oki1.Starts + "/" + m.Oki2.Starts + ", writes " + m.Log.Count + ", cmd-irq " + m.LatchIrqs + " ym-irq " + m.YmIrqs + " latch " + m.LatchReads
            + ", PC " + m.Cpu.PC.ToString("X6") + (m.Cpu.Exceptions > 0 ? ", exceptions " + m.Cpu.Exceptions + " last " + m.Cpu.LastException : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootms1survey roms폴더 [zip 수] [초] — hoot 목록에 jaleco megasys1로 있는 zip.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains("type=\"megasys1\"")))
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
                if (!HootMegaSys1.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                int code = 0;
                try { foreach (var t in e.Titles.Take(6)) { code = t.Code; r = Measure(e, dir, t.Code, seconds); if (r.ok) break; } }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | 0x" + code.ToString("X2") + " " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("megasys1: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }
}
