using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 토아플랜 1 보드 (HootToaplan): YM 포트 찾기, 실제 zip 9개, 진단 (HOOT_PROBE = 포트 · RAM 읽기, HOOT_SONGS = 곡마다).</summary>
public static class HootToaplanTests
{
    public static readonly string[] Archives = { "fshark", "twincobr", "rallybik", "truxton", "hellfire", "zerowing", "demonwld", "outzone", "gulfwar2" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot toaplan generic: playable type", HootArchive.Playable("toaplan", "generic") && HootToaplan.Handles("Toaplan", "GENERIC") && !HootToaplan.Handles("toaplan", "toaplan2"), "");
        var rom = new byte[0x100];
        byte[] outs = { 0xD3, 0x60, 0xD3, 0x61, 0xD3, 0x60, 0xD3, 0x61, 0xD3, 0x30, 0xD3, 0x30, 0xD3, 0x30 };   // 60h · 61h 둘씩, 30h (동전) 셋 = 짝 없음
        Array.Copy(outs, rom, outs.Length);
        check("hoot toaplan: YM port = most used even port that has port + 1", HootToaplan.FindYmPort(rom) == 0x60, HootToaplan.FindYmPort(rom).ToString("X2"));

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot toaplan real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot toaplan " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootToaplan.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot toaplan " + arc + ": .hoot entry", false, "none"); continue; }
            if (Environment.GetEnvironmentVariable("HOOT_PROBE") != null) { Probe(arc, hoot, dir); continue; }
            if (Environment.GetEnvironmentVariable("HOOT_SONGS") != null)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot toaplan " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "fshark")
            {
                // 렌더한 VGM (YM3812)을 VgmPlayer로 재생하면 들린다 (0Dh = 오프닝)
                var vgm = HootToaplan.Render(hoot, dir, 0x0D, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot toaplan fshark: song 0Dh audible through VGM YM3812", peak > 0.02, "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootToaplan.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0);
        string note = "port " + m.YmPort.ToString("X2") + " key-on " + keyOn + ", writes " + m.Log.Count + ", irq " + m.Irqs + ", cmd " + m.Ram[0].ToString("X2") + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn > 0, note);
    }

    /// <summary>진단: 맞춤 뒤 2초 동안 OUT · IN 포트, Z80이 쓰지 않고 읽기만 한 RAM (68000이 쓰는 자리 후보).</summary>
    private static void Probe(string arc, HootArchive.Entry e, string dir)
    {
        var rom = File.ReadAllBytes(HootArchive.FindFile(dir, e.Roms.First(r => r.Type == "code").Text));
        var m = new HootToaplan.Machine(rom) { YmPort = HootToaplan.FindYmPort(rom) };
        m.Start();
        m.Trace = true;
        m.RunSeconds(2);
        Console.WriteLine(arc + " (" + rom.Length + " bytes) port " + m.YmPort.ToString("X2") + " PC " + m.Cpu.PC.ToString("X4") + " irq " + m.Irqs + " ym writes " + m.Log.Count
            + "\n  out: " + string.Join(" ", m.Outs.OrderBy(x => x.Key).Select(x => x.Key.ToString("X2") + ":" + x.Value))
            + "\n  in:  " + string.Join(" ", m.Ins.OrderBy(x => x.Key).Select(x => x.Key.ToString("X2") + ":" + x.Value))
            + "\n  read, never written (8000h~87FFh): " + string.Join(" ", m.RamReads.Where(x => x.Key < 0x8800 && !m.RamWritten.Contains(x.Key)).OrderByDescending(x => x.Value).Take(16).Select(x => x.Key.ToString("X4") + ":" + x.Value))
            + "\n  most read: " + string.Join(" ", m.RamReads.OrderByDescending(x => x.Value).Take(12).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
    }
}
