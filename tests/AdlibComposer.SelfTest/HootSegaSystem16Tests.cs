using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>uPD7759 (슬레이브 모드 상태 기계 · VGM 칩) · hoot 세가 System 16 (HootSegaSystem16): 실제 zip, 진단 조사.</summary>
public static class HootSegaSystem16Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        // 슬레이브 모드: 마지막 곡 번호 10h · 빈 바이트 · 주소 두 바이트 · 블록 머리 5Fh (256니블, 속도 32) · 77h × 128 · 00h (끝)
        var chip = new Upd7759(Upd7759Core.StandardClock, 44100);
        chip.Write(0, 0, 1);
        chip.Write(0, 1, 1);
        var bytes = new List<int> { 0x10, 0x00, 0x00, 0x00, 0x5F };
        for (int i = 0; i < 128; i++) bytes.Add(0x77);
        bytes.Add(0x00);
        foreach (var b in bytes) chip.Write(0, 2, b);
        int peak = 0; bool wasBusy = false;
        for (int i = 0; i < 44100; i++)
        {
            chip.Generate(out int l, out _);
            peak = Math.Max(peak, l);
            if (chip.ChannelActive(0)) wasBusy = true;
        }
        check("uPD7759: slave-mode stream (queued bytes) plays ADPCM and returns to idle", wasBusy && peak > 2000 && !chip.ChannelActive(0), "peak " + peak);
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockUpd7759, 640000);
        w.Write(0xB6, 0, 1); w.Wait(10);
        var v = VgmFormat.Read(w.ToArray());
        check("VGM uPD7759 (8Ch, slave) read", v.Upd7759Clock == 640000 && v.IsPlayable && v.ChipSummary.Contains("uPD7759") && !v.UnsupportedChips.Contains("uPD7759"), v.ChipSummary);
        check("hoot sega system16: playable types", HootArchive.Playable("sega", "system16b") && HootArchive.Playable("sega", "system16a") && !HootArchive.Playable("sega", "system24"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot system16 real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "dunkshot", "hwchamp", "aliensyn" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot system16 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootSegaSystem16.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot system16 " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, pcm: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(10)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot system16 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
        string ryu = Path.Combine(folder, "ryukyu.zip");
        if (File.Exists(ryu))
        {
            string dir = ZipFolder.Extract(ryu);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).First(x => HootSegaSystem16.Handles(x.Driver, x.Type));
            var m = HootSegaSystem16.Boot(hoot, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
            m.Play(0x01);
            m.RunSeconds(6);
            int updPeak = UpdOnlyPeak(m);
            check("hoot system16 ryukyu: uPD7759 bytes fed by DRQ NMI, replayed alone through the VGM chip make sound", m.UpdBytes > 1000 && m.Nmis > 1000 && updPeak > 3000, "bytes " + m.UpdBytes + ", nmi " + m.Nmis + ", peak " + updPeak);
        }
    }

    /// <summary>기계 기록의 uPD7759 쓰기만 재생기용 칩(쌓아 두는 슬레이브 모드)에 넣어 나는 소리의 봉우리.</summary>
    private static int UpdOnlyPeak(HootSegaSystem16.Machine m)
    {
        var chip = new Upd7759(HootSegaSystem16.UpdClock, 44100);
        chip.Write(0, 0, 1);
        int peak = 0, i = 0;
        var writes = m.Log.Where(x => x.chip == 1).OrderBy(x => x.t).ToList();
        double end = writes.Count > 0 ? writes[writes.Count - 1].t + 44100 : 0;
        for (double t = 0; t < end; t++)
        {
            while (i < writes.Count && writes[i].t <= t) { chip.Write(0, writes[i].reg, writes[i].val); i++; }
            chip.Generate(out int l, out _);
            peak = Math.Max(peak, Math.Abs(l));
        }
        return peak;
    }

    public static (bool sound, bool pcm, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootSegaSystem16.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        if (Environment.GetEnvironmentVariable("HOOT_PC") != null) m.PcCount = new Dictionary<int, int>();
        m.Play(code);
        m.RunSeconds(seconds);
        if (m.PcCount != null) Console.WriteLine("  PC: " + string.Join(" ", m.PcCount.OrderByDescending(x => x.Value).Take(24).Select(x => x.Key.ToString("X4") + ":" + x.Value)));
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        bool pcm = m.UpdBytes >= 100;
        bool sound = fm >= 16 || pcm;
        string note = "fm " + fm + " upd starts " + m.UpdStarts + " bytes " + m.UpdBytes + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", board " + m.Board.ToString("X") + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "");
        return (sound, pcm, note);
    }

    /// <summary>진단: --hoots16survey roms폴더 [zip 수] [초] — 세가 system16a · 16b. 처음 10곡 중 FM이 나는지 · uPD7759가 쓰이는지.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains("type=\"system16a\"") || x.Contains("type=\"system16b\"")))
            .Where(z => Environment.GetEnvironmentVariable("HOOT_ONLY") == null || Environment.GetEnvironmentVariable("HOOT_ONLY").Split(',').Contains(Path.GetFileNameWithoutExtension(z), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        int total = 0, sound = 0, withPcm = 0, pcmOk = 0;
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootSegaSystem16.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                bool hasPcm = e.Roms.Any(r => r.Type == "adpcm");
                if (hasPcm) withPcm++;
                bool ok = false, anyPcm = false; string note = ""; int code = 0;
                try
                {
                    foreach (var t in e.Titles.Take(10))
                    {
                        var r = Measure(e, dir, t.Code, seconds);
                        if (r.pcm) anyPcm = true;
                        if (r.sound && !ok) { ok = true; code = t.Code; note = r.note; }
                        if (ok && (anyPcm || !hasPcm)) break;
                        if (!ok) { code = t.Code; note = r.note; }
                    }
                }
                catch (Exception ex) { note = "exception " + ex.GetType().Name + ": " + ex.Message; }
                if (ok) sound++;
                if (hasPcm && anyPcm) pcmOk++;
                Console.WriteLine((ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Type + " | " + e.Name + " | 0x" + code.ToString("X2") + (hasPcm ? (anyPcm ? " pcm-ok" : " pcm-none") : "") + " | " + note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("system16: " + sound + " / " + total + ", uPD7759 used " + pcmOk + " / " + withPcm + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }
}
