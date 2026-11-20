using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot SNK 보드: SNK68 (HootSnk68, type 3812) 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootSnkTests
{
    public static readonly string[] Snk68 = { "pow", "searchar", "streetsm", "ikari3" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot snk 3812: playable, SNK68 board", HootArchive.Playable("snk", "3812") && HootSnk68.Handles("SNK", "3812") && !HootSnk68.Handles("snk", "3526_8950"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot snk real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Snk68)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot snk " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSnk68.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot snk " + arc + ": .hoot entry", false, "none"); continue; }
            string scan = Environment.GetEnvironmentVariable("HOOT_SNKSCAN");   // 진단: "곡 코드" — 부팅 명령 00h~FFh를 하나씩 보내 보고 그 곡이 소리 나는 것
            if (scan != null)
            {
                int bgm = Convert.ToInt32(scan, 16);
                var hits = new List<string>();
                for (int c = 0; c < 256; c++)
                {
                    Environment.SetEnvironmentVariable("HOOT_SNKINIT", c.ToString("X2"));
                    var r1 = Measure(hoot, dir, bgm, 2);
                    if (r1.sound) hits.Add(c.ToString("X2"));
                }
                Environment.SetEnvironmentVariable("HOOT_SNKINIT", null);
                Console.WriteLine("  " + arc + " init commands that make 0x" + bgm.ToString("X2") + " sound: " + string.Join(" ", hits));
                continue;
            }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            // 3Ah = 소리 초기화 (RAM · ROM 시험), BFh = 짧은 효과 — 곡이 아니라서 건너뛴다
            foreach (var t in hoot.Titles.Where(t => t.Code != 0x3A && t.Code != 0xBF).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot snk " + arc + " (3812): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "pow")
            {
                // uPD7759: 목소리 · 효과음이 나오는 곡을 찾아 VgmPlayer로 들리는지 (보드가 ROM 바이트를 넣은 슬레이브 모드 스트림)
                foreach (var t in hoot.Titles)
                {
                    var m = HootSnk68.Boot(hoot, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
                    m.Play(t.Code);
                    m.RunSeconds(6);
                    if (m.UpdStarts == 0) continue;
                    var vgm = m.ToVgm(6, 0);
                    var p = new VgmPlayer(); p.Start(vgm, 0, null);
                    double peak = 0;
                    for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                    check("hoot snk pow: uPD7759 (song 0x" + t.Code.ToString("X2") + ") fed from ROM, VGM audible", peak > 0.02 && vgm.ChipSummary.Contains("uPD7759"),
                        "starts " + m.UpdStarts + " bytes " + m.UpdBytes + ", peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
                    break;
                }
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootSnk68.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        string pcq = Environment.GetEnvironmentVariable("HOOT_PCQ");
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null || pcq != null) m.PcCount = m.PcCount ?? new Dictionary<int, int>();
        m.Play(code);
        m.RunSeconds(seconds);
        if (pcq != null) { foreach (var q in pcq.Split(',')) { m.PcCount.TryGetValue(Convert.ToInt32(q, 16), out int c); Console.Write("  PC " + q + "=" + c); } Console.WriteLine(); }
        else if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key.ToString("X4") + ":" + x.Value))
            + "\n  cmd path: " + string.Join(" ", m.PcCount.Where(x => x.Key >= 0x0060 && x.Key < 0x00A7 || x.Key >= 0x0763 && x.Key < 0x0900).OrderBy(x => x.Key).Select(x => x.Key.ToString("X4") + ":" + x.Value))
            + "\n  timer: " + string.Join(" ", m.TimerWrites.Select(x => (x.cyc / HootSnk68.CpuClock).ToString("0.00") + "s r" + x.reg + "=" + x.val.ToString("X2"))));
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));
        string note = "opl " + keyOn + " upd starts " + m.UpdStarts + " bytes " + m.UpdBytes + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherOuts.Count > 0 ? ", other out " + string.Join(" ", m.OtherOuts.Select(x => x.Key.ToString("X2") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn > 0 || m.UpdStarts > 0, note);
    }
}
