using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Dos;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>PC/AT 기계 (hoot pcatdos): BIOS 타이머 (INT 08h → 1Ch), AdLib 찾기 (OPL 타이머 상태), SB16 DSP, 스피커, 실제 pcatdos 항목 재생.</summary>
public static class PcAtTests
{
    private static byte[] Com(int[] code, params (int at, int[] data)[] extra)
    {
        int len = code.Length;
        foreach (var (at, d) in extra) len = Math.Max(len, at - 0x100 + d.Length);
        var b = new byte[len];
        for (int i = 0; i < code.Length; i++) b[i] = (byte)code[i];
        foreach (var (at, d) in extra) for (int i = 0; i < d.Length; i++) b[at - 0x100 + i] = (byte)d[i];
        return b;
    }

    public static void Run(Action<string, bool, string> check)
    {
        // INT 1Ch에 매단 처리기 (BIOS INT 08h가 부름): 1초에 18.2번
        var fs = new DosFileSystem();
        fs.Add("T.COM", Com(new[] { 0xB8, 0x1C, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21, 0xFB, 0xF4, 0xEB, 0xFD },
            (0x140, new[] { 0x2E, 0xFF, 0x06, 0x60, 0x01, 0xCF })));
        var m = new PcAtMachine(fs, PcAtMachine.Card.AdLib, 4);
        m.Shell("T", 1000);
        int psp = m.LastLoadedPsp;
        m.Run((long)(m.CpuHz * 2));
        int ticks = m.PeekWord(psp, 0x160), bios = m.PeekWord(0x40, 0x6C);
        check("PC/AT BIOS timer: PIT 0 at 18.2 Hz → INT 08h (ticks 0040:006C) → INT 1Ch hook", Math.Abs(ticks - 36.4) <= 1.5 && Math.Abs(bios - ticks) <= 1, ticks + " in 2 s, BIOS " + bios + ", CPU " + (m.CpuHz / 1e6).ToString("0.00") + " MHz");

        // AdLib 찾기: 타이머 지우기 → 상태 0, 타이머 1 = FFh 시작 → 80µs 뒤 상태 C0h
        int Status() { return m.In(0x388) & 0xE0; }
        void W(int r, int v) { m.Out(0x388, (byte)r); m.Out(0x389, (byte)v); }
        W(4, 0x60); W(4, 0x80);
        int s1 = Status();
        W(2, 0xFF); W(4, 0x21);
        m.Run((long)(m.CpuHz * 0.0002));
        int s2 = Status();
        W(4, 0x60); W(4, 0x80);
        check("PC/AT AdLib detection: OPL status 00h after reset, C0h after timer 1 overflow (80 µs)", s1 == 0 && s2 == 0xC0, s1.ToString("X2") + " / " + s2.ToString("X2"));

        // SB16 DSP: 리셋 → AAh, E1h → 4.05
        var sb = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.SoundBlaster16, 4);
        sb.Out(0x226, 1); sb.Out(0x226, 0);
        bool ready = (sb.In(0x22E) & 0x80) != 0;
        int aa = sb.In(0x22A);
        sb.Out(0x22C, 0xE1);
        int major = sb.In(0x22A), minor = sb.In(0x22A);
        sb.Out(0x222, 0x05); sb.Out(0x223, 0x01);   // OPL3 켜기 (포트 1)
        check("PC/AT Sound Blaster 16: DSP reset → AAh, version 4.05, OPL3 bank-1 writes logged", ready && aa == 0xAA && major == 4 && minor == 5 && sb.Log.Any(x => x.kind == 1 && x.reg == 5 && x.val == 1),
            aa.ToString("X2") + " v" + major + "." + minor.ToString("00"));

        // 스피커: PIT 2 = 1193 (1kHz), 61h bit0 · bit1 켬
        var sp = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.Beep, 4);
        sp.Out(0x43, 0xB6); sp.Out(0x42, 0xA9); sp.Out(0x42, 0x04); sp.Out(0x61, 0x03);
        check("PC/AT speaker: PIT 2 counter + port 61h → beep log (counter, on)", sp.Log.Any(x => x.kind == 3 && x.val == 0x4A9) && sp.Log.Any(x => x.kind == 2 && x.val == 1), string.Join(" ", sp.Log.Select(x => x.kind + ":" + x.val.ToString("X"))));

        Real(check);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string inc = SopTests.FindDir("_incoming");
        string pc = inc == null ? null : Path.Combine(inc, "Hootarchive", "pc");
        if (pc == null || !Directory.Exists(pc) || HootArchive.FindListDir(Path.Combine(pc, "x.zip")) == null) { Console.WriteLine("SKIP  real hoot pcatdos (Hootarchive/pc not found)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootat_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            foreach (var (zipName, type) in new[] { ("7cogold_at.zip", "adlib"), ("", "soundblaster16") })
            {
                string hp = null;
                var candidates = zipName.Length > 0 ? new[] { Path.Combine(pc, zipName) } : Directory.GetFiles(pc, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var z in candidates)
                {
                    if (!File.Exists(z)) continue;
                    string dir = ZipFolder.Extract(z);
                    hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault(h => { var e = HootArchive.Load(h); return e.Driver == "pcatdos" && e.Type == type; });
                    if (hp != null) break;
                }
                if (hp == null) { Console.WriteLine("SKIP  real hoot pcatdos " + type); continue; }
                var tracks = ChipSongs.Tracks(hp);
                var v = ChipSongs.Load(tracks[0].path);
                var p = new VgmPlayer();
                p.Start(v, 0, null);
                double sum = 0; int n = 5 * 44100;
                for (int i = 0; i < n; i++) { p.Step(); p.Generate(out double l, out double r); sum += l * l; }
                double rms = Math.Sqrt(sum / n);
                check("Real hoot pcatdos " + type + ": " + Path.GetFileName(Path.GetDirectoryName(hp)).Split('_')[0] + " '" + tracks[0].title + "' plays through the jukebox path (" + v.ChipSummary + ")",
                    rms > 200 && v.ChipSummary.Contains("PC/AT"), "rms " + rms.ToString("0") + ", " + tracks.Count + " songs");
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
