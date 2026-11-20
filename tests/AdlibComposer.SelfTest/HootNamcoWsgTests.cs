using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 남코 6809 + 15XX (HootNamcoWsg6809, type wsg6809): 15XX 합성, 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNamcoWsgTests
{
    public static readonly string[] Archives = { "grobda", "superpac", "pacnpal", "phozon", "mappy", "liblrabl", "todruaga", "gaplus", "digdug2", "motos", "toypop" };

    public static void Run(Action<string, bool, string> check)
    {
        // 15XX: PROM 파형 0 = 톱니 (i & 15), 목소리 1 주파수 8000h → 750Hz, 음량 15 (모노)
        var c = new AdlibComposer.Audio.Chips.NamcoCus30(HootNamcoWsg6809.WsgClock, 44100, true);
        for (int i = 0; i < 32; i++) c.Write(0, 0x200 + i, i & 15);
        c.Write(0, 8 + 3, 15); c.Write(0, 8 + 4, 0x00); c.Write(0, 8 + 5, 0x80); c.Write(0, 8 + 6, 0x00);
        int lo = int.MaxValue, hi = int.MinValue; bool same = true;
        for (int i = 0; i < 4410; i++) { c.Generate(out int l, out int r); lo = Math.Min(lo, l); hi = Math.Max(hi, l); same &= l == r; }
        check("15XX: PROM waveform voice, 750 Hz, mono", Math.Abs(c.ChannelFrequency(1) - 750) < 0.01 && hi - lo > 4000 && same, c.ChannelFrequency(1) + " Hz, swing " + (hi - lo));
        check("hoot namco wsg6809: playable, own board", HootArchive.Playable("namco", "wsg6809") && HootNamcoWsg6809.Handles("Namco", "WSG6809") && !HootNamcoWsg6809.Handles("namco", "system1"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot namco wsg6809 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco wsg6809 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoWsg6809.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco wsg6809 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "mappy")
            {
                var v = HootNamcoWsg6809.Render(hoot, dir, 0x15, 3, 0);
                var p = new VgmPlayer(); p.Start(v, 0, null);
                double peak = 0;
                for (int i = 0; i < 44100 * 3; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot namco wsg6809 mappy: 15XX plays through VGM (D8h)", v.Cus30Is15xx && v.ChipSummary.Contains("15XX") && peak > 100, "peak " + peak.ToString("0") + ", " + v.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco wsg6809 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }

        // Z80 + WSG (갤러그 계열, type wsgz80)
        check("hoot namco wsgz80: playable, own board", HootArchive.Playable("namco", "wsgz80") && HootNamcoWsgZ80.Handles("Namco", "WSGZ80") && !HootNamcoWsgZ80.Handles("namco", "wsg6809"), "");
        foreach (var arc in Z80Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco wsgz80 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoWsgZ80.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco wsgz80 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + MeasureZ80(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (arc == "galaga")
            {
                var v = HootNamcoWsgZ80.Render(hoot, dir, 0x0B, 3, 0);
                var p = new VgmPlayer(); p.Start(v, 0, null);
                double peak = 0;
                for (int i = 0; i < 44100 * 3; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot namco wsgz80 galaga: WSG plays through VGM (D8h)", v.Cus30Is15xx && peak > 100, "peak " + peak.ToString("0") + ", " + v.ChipSummary);
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = MeasureZ80(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco wsgz80 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static readonly string[] Z80Archives = { "bosco", "galaga", "digdug", "xevious" };

    public static (bool sound, string note) MeasureZ80(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoWsgZ80.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int vol = m.Log.Count(x => x.t > 0 && (x.reg & 7) == 3 && (x.val & 15) != 0);
        string note = "vol " + vol + ", writes " + m.Log.Count + ", nmi " + m.Nmis + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (vol > 0, note);
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoWsg6809.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int vol = m.Log.Count(x => x.t > 0 && (x.reg & 7) == 3 && (x.val & 15) != 0);
        string note = "vol " + vol + ", writes " + m.Log.Count + ", irq " + m.Irqs + " hs " + m.Handshakes + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (vol > 0, note);
    }
}
