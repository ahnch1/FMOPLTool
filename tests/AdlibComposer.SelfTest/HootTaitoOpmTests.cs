using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 타이토 YM2151 보드 (HootTaitoOpm, type fullt): 만든 Z80 프로그램으로 명령 · MSM5205 시작 / 멈춤 시험, 실제 zip 9개.</summary>
public static class HootTaitoOpmTests
{
    public static readonly string[] Archives = { "rastan", "topspeed", "asuka", "mofflott", "cadash", "exzisus", "galmedes", "earthjkr", "eto", "masterw", "tetrista", "champwr", "viofight", "hitice", "cameltrya", "rainbow", "twinhawk", "opwolf", "volfied", "darius" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot taito fullt: playable type, own board", HootArchive.Playable("taito", "fullt") && HootTaitoOpm.Handles("taito", "FULLT")
            && !HootTaito.Handles("taito", "fullt") && !HootTaitoOpm.Handles("taito", "f2system"), "");

        // 0000h: IM 1 · NMI 켬 (A000h ← 6, A001h ← 0) · EI · HALT 고리. 0066h = NMI: 니블 둘 읽어 → B000h (MSM 주소 위) · C000h (시작) · RETN
        var rom = new byte[0x8000];
        byte[] start = { 0xF3, 0x31, 0xFF, 0x8F, 0xED, 0x56, 0x3E, 0x06, 0x32, 0x00, 0xA0, 0xAF, 0x32, 0x01, 0xA0, 0xFB, 0x76, 0x18, 0xFD };
        Array.Copy(start, rom, start.Length);
        byte[] nmi =
        {
            0xF5, 0xAF, 0x32, 0x00, 0xA0,       // 모드 0
            0x3A, 0x01, 0xA0, 0x47,             // 낮은 니블 → B
            0x3A, 0x01, 0xA0, 0x07, 0x07, 0x07, 0x07, 0xB0,   // 높은 니블 << 4 | B
            0x32, 0x00, 0xB0, 0x32, 0x00, 0xC0, // MSM 주소 위 · 시작
            0xF1, 0xED, 0x45,
        };
        Array.Copy(nmi, 0, rom, 0x66, nmi.Length);
        var pcm = new byte[0x10000];
        for (int i = 0; i < 0x100; i++) pcm[0x1200 + i] = 0x7F;
        var m = new HootTaitoOpm.Machine(rom, pcm, false);
        m.RunSeconds(0.1);
        m.Play(0x12);
        m.RunSeconds(0.1);
        var data = m.Log.Where(x => x.chip == 1 && x.reg == 1).Select(x => x.val).ToList();
        check("hoot taito fullt: command 12h → MSM from 1200h, nibbles swapped for VGM", m.Nmis == 1 && m.MsmStarts == 1 && data.Count > 300 && data[0] == 0xF7 && data[300] == 0x00,
            "nmi " + m.Nmis + " starts " + m.MsmStarts + " bytes " + data.Count + (data.Count > 0 ? " first " + data[0].ToString("X2") : ""));
        var v = m.ToVgm(0.2, 0);
        check("hoot taito fullt: VGM has YM2151 + OKIM6258 8 kHz", v.ChipSummary.Contains("YM2151") && v.Okim6258Clock == HootTaitoOpm.VgmMsmClock, v.ChipSummary);

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot taito fullt real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot taito fullt " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootTaitoOpm.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot taito fullt " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot taito fullt " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "darius")
            {
                // YM2203 둘 (9000h · A000h): 둘째 칩도 VGM (A5h)에
                var vd = HootTaitoOpm.Render(hoot, dir, 0x07, 6, 0);
                int second = 0;
                for (int p = vd.DataOffset; p + 2 < vd.Data.Length && vd.Data[p] != 0x66; p += Math.Max(1, VgmFormat.CommandLength(vd.Data, p))) if (vd.Data[p] == 0xA5) second++;
                check("hoot taito darius: two YM2203 (second chip at A000h → VGM A5h)", vd.Ym2203Clock > 0 && second > 100, second + " writes, " + vd.ChipSummary);
            }
            if (arc == "topspeed")
            {
                // Accel #1 (09h) = MSM5205 드럼만: 렌더한 VGM을 VgmPlayer로 재생하면 OKIM6258이 소리를 낸다
                var vgm = HootTaitoOpm.Render(hoot, dir, 0x09, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot taito fullt topspeed: MSM5205 drums (09h) audible through VGM OKIM6258", vgm.Okim6258Clock > 0 && peak > 0.02, "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootTaitoOpm.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
        if (Environment.GetEnvironmentVariable("HOOT_MSMLOG") != null) m.PortLog = new List<(long, int, int)>();
        m.Play(code);
        m.RunSeconds(seconds);
        if (m.PortLog != null) foreach (var x in m.PortLog.Where(x => x.a != 0xB800).Take(60)) Console.WriteLine("    " + (x.cyc / HootTaitoOpm.CpuClock).ToString("0.0000") + " " + x.a.ToString("X4") + " <- " + x.v.ToString("X2"));
        int keyOn = m.Log.Count(x => x.chip == 0 && (m.Opn ? (x.reg == 0x28 && (x.val & 0xF0) != 0) || (x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0) : x.reg == 0x08 && (x.val & 0x78) != 0)) + m.Log.Count(x => x.chip == 2 && (x.val & 0x80) != 0);
        int msm = m.Log.Count(x => x.chip == 1 && x.reg == 1);
        string note = (m.SoundOn ? "EF " : "") + "key-on " + keyOn + " msm bytes " + msm + " starts " + m.MsmStarts + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " read " + m.Polls + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_REGS") != null) note += "\n  regs: " + string.Join(" ", m.Log.GroupBy(x => x.chip * 256 + x.reg).OrderByDescending(g => g.Count()).Take(30).Select(g => g.Key.ToString("X3") + ":" + g.Count()))
            + "\n  08h: " + string.Join(" ", m.Log.Where(x => x.chip == 0 && x.reg == 8).Take(30).Select(x => x.val.ToString("X2")));
        string q = Environment.GetEnvironmentVariable("HOOT_PCQ");
        if (m.PcCount != null && q != null) foreach (var x in q.Split(',')) { m.PcCount.TryGetValue(Convert.ToInt32(x, 16), out int cnt); note += "\n  PC " + x + " = " + cnt; }
        string ramEnv = Environment.GetEnvironmentVariable("HOOT_RAM");
        if (ramEnv != null) { var pr = ramEnv.Split(','); note += "\n  RAM " + pr[0] + ": " + BitConverter.ToString(m.Ram, Convert.ToInt32(pr[0], 16) - 0x8000, Convert.ToInt32(pr[1], 16)); }
        if (m.PcCount != null) note += "\n  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key.ToString("X4") + ":" + x.Value));
        return (keyOn > 0 || msm > 0, note);
    }
}
