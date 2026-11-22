using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>VGM 쓰기 (머리말 · 기다림 · 루프 · GD3 · vgz) · 곡 → VGM 내보내기 (ROL / SOP / MIDI)를 다시 읽어 재생해 원래 소리와 비교.</summary>
public static class VgmExportTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Writer(check);
        Rol(check);
        Sop(check);
        Midi(check);
    }

    private static void Writer(Action<string, bool, string> check)
    {
        var w = new VgmWriter { Title = "Test", Author = "Me" };
        w.SetClock(VgmWriter.ClockYm3812, 3579545);
        w.Write(0x5A, 0x20, 0x01);
        w.Wait(735); w.Wait(882); w.Wait(5); w.Wait(70000);
        w.MarkLoop();
        w.Write(0x5A, 0xB0, 0x20);
        w.Wait(44100);
        foreach (bool gz in new[] { false, true })
        {
            var s = VgmFormat.Read(w.ToArray(gz));
            string tag = gz ? "vgz" : "vgm";
            check("VGM writer (" + tag + "): header", s.Ym3812Clock == 3579545 && s.HasOpl && !s.IsOpl3 && s.Version == 0x171, s.ChipSummary);
            check("VGM writer (" + tag + "): length = sum of waits", s.TotalSamples == 735 + 882 + 5 + 70000 + 44100, s.TotalSamples.ToString());
            check("VGM writer (" + tag + "): loop", s.LoopOffset > 0 && s.LoopSamples == 44100, s.LoopSamples.ToString());
            check("VGM writer (" + tag + "): GD3", s.TitleEn == "Test" && s.AuthorEn == "Me", s.TitleEn + "/" + s.AuthorEn);
        }
        var w2 = new VgmWriter();
        w2.SetClock(VgmWriter.ClockYm3812, 3579545);
        w2.Wait(100000);
        var s2 = VgmFormat.Read(w2.ToArray());
        int p = s2.DataOffset, cmds = 0; long sum = 0;
        while (p < s2.DataEnd && s2.Data[p] != 0x66)
        {
            int c = s2.Data[p];
            if (c == 0x61) sum += s2.Data[p + 1] | (s2.Data[p + 2] << 8);
            else if (c >= 0x70 && c <= 0x7F) sum += (c & 15) + 1;
            p += VgmFormat.CommandLength(s2.Data, p); cmds++;
        }
        check("VGM writer: long wait split into 0x61 commands", sum == 100000 && cmds == 2, sum + " in " + cmds);
    }

    /// <summary>VGM을 VgmPlayer로 sec초 렌더 (44.1kHz 모노).</summary>
    private static double[] RenderVgm(VgmSong s, double sec)
    {
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        var o = new double[(int)(sec * 44100)];
        for (int i = 0; i < o.Length; i++) { p.Step(); p.Generate(out double l, out double r); o[i] = (l + r) / 2; }
        return o;
    }

    private static double Rms(double[] a, int from, int to)
    {
        double s = 0; to = Math.Min(to, a.Length);
        for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    /// <summary>100ms 창마다 RMS (엔벨로프), 두 곡선의 상관.</summary>
    private static double EnvelopeCorrelation(double[] a, double[] b)
    {
        int win = 4410, n = Math.Min(a.Length, b.Length) / win;
        var x = new double[n]; var y = new double[n];
        for (int k = 0; k < n; k++) { x[k] = Rms(a, k * win, (k + 1) * win); y[k] = Rms(b, k * win, (k + 1) * win); }
        double mx = 0, my = 0; for (int k = 0; k < n; k++) { mx += x[k]; my += y[k]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0, syy = 0;
        for (int k = 0; k < n; k++) { sxy += (x[k] - mx) * (y[k] - my); sxx += (x[k] - mx) * (x[k] - mx); syy += (y[k] - my) * (y[k] - my); }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
    }

    private static void Rol(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        if (dir == null || !File.Exists(Path.Combine(dir, "demo.rol"))) { Console.WriteLine("SKIP  VGM export ROL (samples/demo.rol not found)"); return; }
        Song song; InstrumentBank bank;
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) song = new RolFormat().Read(fs);
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.bnk"))) bank = new BnkFormat().Read(fs);

        var vgm = VgmFormat.Read(VgmExport.FromSong(song, bank).ToArray());
        double songSec = song.LengthTicks / (song.BasicTempo / 60.0 * song.TickBeat);
        check("VGM export ROL: OPL2 file", vgm.Ym3812Clock == 3579545 && !vgm.IsOpl3 && vgm.UnsupportedChips.Count == 0, vgm.ChipSummary);
        check("VGM export ROL: length = song + ~1 s tail", Math.Abs(vgm.LengthSeconds - (songSec + 1)) < 0.2, vgm.LengthSeconds.ToString("0.00") + " / " + songSec.ToString("0.00"));

        // 직접 재생 (칩 고유 속도) → 44.1kHz로 줄여 비교
        var synth = new OplSynth(new OplChip(false));
        var seq = new Sequencer(synth);
        seq.Start(song, bank.Find, 0, -1, -1);
        double sec = Math.Min(vgm.LengthSeconds, 4);
        var direct = new double[(int)(sec * 44100)];
        double step = synth.NativeRate / 44100.0, acc = 0; short cur = 0;
        for (int i = 0; i < direct.Length; i++)
        {
            acc += step;
            while (acc >= 1) { acc -= 1; seq.Step(); cur = synth.Generate(); }
            direct[i] = cur;
        }
        var played = RenderVgm(vgm, sec);
        double ra = Rms(direct, 0, direct.Length), rb = Rms(played, 0, played.Length);
        check("VGM export ROL: replay loudness ≈ direct (±2 dB)", ra > 100 && Math.Abs(20 * Math.Log10(rb / ra)) < 2, rb.ToString("0") + " / " + ra.ToString("0"));
        double corr = EnvelopeCorrelation(direct, played);
        check("VGM export ROL: replay envelope follows direct", corr > 0.9, corr.ToString("0.000"));

        var used = VgmPlayer.ScanUsed(vgm);
        int n = 0; for (int v = 0; v < 9; v++) if (used[v]) n++;
        check("VGM export ROL: FM channels used", n >= 3, n.ToString());

        // SOP로 만든 곡이면 OPL3
        var sopSong = song.Clone();
        sopSong.IsSop = true;
        var v3 = VgmFormat.Read(VgmExport.FromSong(sopSong, bank).ToArray(true));
        check("VGM export ROL as SOP: OPL3 vgz", v3.IsOpl3 && v3.Ymf262Clock == 14318180, v3.ChipSummary);
        double r3 = Rms(RenderVgm(v3, sec), 0, (int)(sec * 44100));
        check("VGM export ROL as SOP: plays", r3 > 100, r3.ToString("0"));
    }

    private static void Sop(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string file = dir != null ? FirstFile(dir, "*.sop") : null;
        if (file == null) { Console.WriteLine("SKIP  VGM export SOP (_incoming/*.sop not found)"); return; }
        var sop = SopFormat.Load(file);
        var vgm = VgmFormat.Read(VgmExport.FromSop(sop).ToArray());
        double r = Rms(RenderVgm(vgm, 6), 44100, 6 * 44100);
        check("VGM export real SOP " + Path.GetFileName(file) + ": OPL3, sounds", vgm.IsOpl3 && r > 100 && vgm.LengthSeconds > 5, r.ToString("0") + ", " + vgm.LengthSeconds.ToString("0") + " s");
    }

    private static void Midi(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string file = dir != null ? FirstFile(dir, "*.mid") : null;
        if (file == null) { Console.WriteLine("SKIP  VGM export MIDI (_incoming/*.mid not found)"); return; }
        string res = SopTests.FindDir(Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        if (res == null || !File.Exists(Path.Combine(res, "GENMIDI.OP2"))) { Console.WriteLine("SKIP  VGM export MIDI (GENMIDI.OP2 not found)"); return; }
        var bank = GmBank.LoadOp2(Path.Combine(res, "GENMIDI.OP2"));
        var midi = MidiFiles.Load(file);
        var vgm = VgmFormat.Read(VgmExport.FromMidi(midi, bank, Path.GetFileNameWithoutExtension(file)).ToArray());
        double r = Rms(RenderVgm(vgm, 8), 2 * 44100, 8 * 44100);
        check("VGM export real MIDI " + Path.GetFileName(file) + ": OPL3, sounds", vgm.IsOpl3 && r > 100 && vgm.LengthSeconds > 10, r.ToString("0") + ", " + vgm.LengthSeconds.ToString("0") + " s");
    }

    private static string FirstFile(string dir, string pattern)
    {
        // _incoming/modizer-master = 참고 소스 (AdPlug 시험 곡 등): 우리 표본이 아니므로 뺀다
        var f = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Where(x => x.IndexOf("modizer-master", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
        Array.Sort(f, StringComparer.OrdinalIgnoreCase);
        return f.Length > 0 ? f[0] : null;
    }
}
