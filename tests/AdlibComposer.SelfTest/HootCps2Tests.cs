using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>QSound (HLE) 칩 · VGM, hoot 캡콤 CPS-2 (HootCps2): 실제 zip, 진단 조사.</summary>
public static class HootCps2Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        // QSound: 채널 0 = 뱅크 0 (채널 15의 뱅크 레지스터) · 주소 0 · 속도 1000h · 끝 100h · 루프 100h · 볼륨 2000h → 소리 · 되풀이
        var q = new QSound(QSound.StandardClock, 44100);
        var rom = new byte[0x200];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(sbyte)(i % 32 < 16 ? 100 : -100);
        q.LoadRom(rom, 0);
        q.Write(0, 15 * 8 + 0, 0);   // 채널 0의 뱅크
        q.Write(0, 0x80, 0x20);      // 가운데 팬
        q.Write(0, 1, 0); q.Write(0, 2, 0x1000); q.Write(0, 4, 0x100); q.Write(0, 5, 0x100); q.Write(0, 6, 0x2000);
        int peak = 0;
        for (int i = 0; i < 44100; i++) { q.Generate(out int l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("QSound HLE: looping channel sounds after 1 s", peak > 5000 && q.ChannelActive(0), "peak " + peak);
        q.Write(0, 6, 0);
        check("QSound HLE: volume 0 keys off", !q.ChannelActive(0), "");
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockQsound, 4000000);
        w.Write(0xC4, 0x20, 0x00, 0x06); w.Wait(10);
        var v = VgmFormat.Read(w.ToArray());
        check("VGM QSound (B4h) read as playable", v.QsoundClock == 4000000 && v.IsPlayable && v.ChipSummary.Contains("QSound") && !v.UnsupportedChips.Contains("QSound"), v.ChipSummary);
        check("hoot cps2: playable type", HootArchive.Playable("capcom", "cps2") && HootArchive.Playable("capcom", "cps1"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot cps2 real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "ssf2t", "mshvsf", "gigawing" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot cps2 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootCps2.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot cps2 " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8, 0); if (r.sound) break; }
            check("hoot cps2 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds, int layout)
    {
        var m = HootCps2.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; }, layout);
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.reg < 0x80 && (x.reg & 7) == 6 && x.val != 0);
        bool sound = keyOn >= 8;
        return (sound, "key-on " + keyOn + ", writes " + m.Log.Count + ", irq " + m.Irqs + " taken " + m.Taken + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : ""));
    }

    /// <summary>진단: --hootcps2survey roms폴더 [zip 수] [초] [모양 0|1].</summary>
    public static int Survey(string folder, int count, double seconds, int layout)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => x.Contains("type=\"cps2\"")))
            .Where(z => Environment.GetEnvironmentVariable("HOOT_ONLY") == null || Environment.GetEnvironmentVariable("HOOT_ONLY").Split(',').Contains(Path.GetFileNameWithoutExtension(z), StringComparer.OrdinalIgnoreCase))
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
                if (!HootCps2.Handles(e.Driver, e.Type) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string note) r = (false, "");
                int code = 0;
                try { foreach (var t in e.Titles.Take(6)) { code = t.Code; r = Measure(e, dir, t.Code, seconds, layout); if (r.ok) break; } }
                catch (Exception ex) { r = (false, "exception " + ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | 0x" + code.ToString("X2") + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("cps2: " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }
}
