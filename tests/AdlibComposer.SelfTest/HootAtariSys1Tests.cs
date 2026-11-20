using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 아타리 System 1 (HootAtariSys1): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootAtariSys1Tests
{
    public static readonly string[] Archives = { "marble", "peterpak", "indytemp", "roadrunn" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot atari system1: playable, own board", HootArchive.Playable("atari", "system1") && HootAtariSys1.Handles("Atari", "System1") && !HootAtariSys1.Handles("atari", "gauntlet"), "");

        // POKEY: 순음 (AUDC A8h = poly5 안 거침 + 순음, 음량 8), 기본 64kHz 클럭, AUDF 99 → 63920 / 100 / 2 ≈ 319.6Hz
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockPokey, 1789772);
        w.Write(0xBB, 0x0F, 0x03); w.Write(0xBB, 0x08, 0x00); w.Write(0xBB, 0x00, 99); w.Write(0xBB, 0x01, 0xA8);
        w.Wait(44100);
        var v = VgmFormat.Read(w.ToArray());
        var p = new VgmPlayer(); p.Start(v, 0, null);
        int crossings = 0; double prev = 0, lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < 44100; i++)
        {
            p.Step(); p.Generate(out double l, out _);
            if (i > 4410) { lo = Math.Min(lo, l); hi = Math.Max(hi, l); }
            if (i > 4410 && (l - (lo + hi) / 2) * (prev - (lo + hi) / 2) < 0) crossings++;
            prev = l;
        }
        double hz = crossings / 2.0 / 0.9;
        check("VGM POKEY: clock B0h + BBh pure tone ~320 Hz", v.PokeyClock > 0 && v.ChipSummary.Contains("POKEY") && hi - lo > 0.02 && Math.Abs(hz - 319.6) < 15, hz.ToString("0.0") + " Hz, swing " + (hi - lo).ToString("0.000") + ", " + v.ChipSummary);
        var chip = new AdlibComposer.Audio.Chips.Pokey();
        chip.Write(0, 0x0F, 3); chip.Write(0, 8, 0x50); chip.Write(0, 0, 0x34); chip.Write(0, 2, 0x12); chip.Write(0, 3, 0xAF);   // 1+2 16비트, 1.79MHz: 1789772 / (1234h + 7) / 2
        check("POKEY: 16-bit joined channel pitch", Math.Abs(chip.ChannelFrequency(1) - 1789772.0 / (0x1234 + 7) / 2) < 0.5 && chip.ChannelFrequency(0) == 0, chip.ChannelFrequency(1).ToString("0.00"));

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot atari system1 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot atari system1 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootAtariSys1.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot atari system1 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Skip(1).Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot atari system1 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootAtariSys1.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        var pcs = new Dictionary<int, int>();
        if (Environment.GetEnvironmentVariable("HOOT_PCS") != null)
            for (int i = 0; i < 20000; i++) { m.Run(80); pcs.TryGetValue(m.Cpu.PC & 0xFFF0, out int c); pcs[m.Cpu.PC & 0xFFF0] = c + 1; }
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pokey = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg <= 7 && (x.reg & 1) == 1 && (x.val & 15) != 0);
        string note = "opm " + keyOn + " pokey vol " + pokey + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + " pokey " + m.PokeyWrites + " via " + m.ViaWrites + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " regs " + string.Join(" ", m.Log.Where(x => x.t > 0).GroupBy(x => x.reg).OrderByDescending(g => g.Count()).Take(12).Select(g => g.Key.ToString("X2") + ":" + g.Count()));
        if (pcs.Count > 0) note += " pcs " + string.Join(" ", pcs.OrderByDescending(x => x.Value).Take(10).Select(x => x.Key.ToString("X4") + ":" + x.Value)) + " ram " + BitConverter.ToString(m.Mem, 0x200, 0x20);
        return (keyOn + pokey > 0, note);
    }
}
