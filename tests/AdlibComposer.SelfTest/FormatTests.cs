using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>AdLib 곡 형식 IMF · HSC · CMF (OPL2 VGM으로 바꿔 내기): 사인파 악기로 A4 한 음.</summary>
public static class FormatTests
{
    private const int Rate = 44100;
    // A4 근처: F-number 0x241, 블록 4 → 437.7Hz
    private const int Fnum = 0x241;
    private static readonly double Want = Fnum * 49716.0 / 65536.0;

    public static void Run(Action<string, bool, string> check)
    {
        Imf(check);
        Hsc(check);
        Cmf(check);
        Registry(check);
    }

    private static double[] Render(VgmSong s, double sec)
    {
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
        return a;
    }

    private static double Pitch(double[] a, int from, int to)
    {
        double m = 0; for (int i = from; i < to; i++) m += a[i];
        m /= to - from;
        int n = 0, first = -1, last = -1;
        for (int i = from + 1; i < to; i++) if (a[i - 1] - m < 0 && a[i] - m >= 0) { n++; if (first < 0) first = i; last = i; }
        return n > 1 ? (n - 1) * (double)Rate / (last - first) : 0;
    }

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static void Expect(Action<string, bool, string> check, string name, VgmSong s, double soundSec)
    {
        var a = Render(s, soundSec);
        double hz = Pitch(a, 4410, (int)(soundSec * Rate) - 2000);
        check(name + ": OPL2 VGM, A4 ≈ " + Want.ToString("0.0") + " Hz", s.Ym3812Clock > 0 && Math.Abs(hz - Want) < Want * 0.02, hz.ToString("0.0"));
    }

    // 사인파 악기 (모듈레이터 TL 63, 캐리어 지속)
    private static readonly (int r, int v)[] SineCh0 =
    {
        (0x20, 0x21), (0x23, 0x21), (0x40, 0x3F), (0x43, 0x00), (0x60, 0xF0), (0x63, 0xF0), (0x80, 0x0F), (0x83, 0x0F), (0xE0, 0), (0xE3, 0), (0xC0, 0),
    };

    private static void Imf(Action<string, bool, string> check)
    {
        var rec = new List<byte>();
        void W(int r, int v, int delay) { rec.Add((byte)r); rec.Add((byte)v); rec.Add((byte)delay); rec.Add((byte)(delay >> 8)); }
        foreach (var (r, v) in SineCh0) W(r, v, 0);
        W(0xA0, Fnum & 0xFF, 0);
        W(0xB0, 0x20 | (4 << 2) | (Fnum >> 8), 560);
        W(0xB0, (4 << 2) | (Fnum >> 8), 0);
        var d = new List<byte>(BitConverter.GetBytes((ushort)rec.Count));
        d.AddRange(rec);
        d.Add(0x1A); d.AddRange(Encoding.ASCII.GetBytes("Keen Theme\0Bobby Prince\0test\0"));
        var s = AdlibStreamFormats.ReadImf(d.ToArray(), 560, "x");
        check("IMF type 1: 1 s note + 0.5 s tail, tag", Math.Abs(s.LengthSeconds - 1.5) < 0.01 && s.Title == "Keen Theme" && s.Author == "Bobby Prince", s.LengthSeconds.ToString("0.00") + " " + s.Title);
        Expect(check, "IMF", s, 1.0);
        var t0 = AdlibStreamFormats.ReadImf(rec.ToArray(), 700, "raw");
        check("IMF type 0 (no length word), 700 Hz", Math.Abs(t0.LengthSeconds - (560 / 700.0 + 0.5)) < 0.01 && t0.Title == "raw", t0.LengthSeconds.ToString("0.00"));
    }

    private static void Hsc(Action<string, bool, string> check)
    {
        var d = new byte[128 * 12 + 51 + 64 * 9 * 2];
        byte[] ins = { 0x21, 0x21, 0x00, 0x3F, 0xF0, 0xF0, 0x0F, 0x0F, 0x00, 0x00, 0x00, 0x00 };
        ins.CopyTo(d, 0);
        for (int c = 1; c < 9; c++) { d[c * 12 + 2] = 0x3F; d[c * 12 + 3] = 0x3F; }
        d[128 * 12] = 0; d[128 * 12 + 1] = 0xFF;
        int pat = 128 * 12 + 51;
        d[pat] = 1 + 4 * 12 + 8;   // A4 (음 번호 1부터)
        var s = AdlibStreamFormats.ReadHsc(d, "hsc");
        // 64줄 × 2틱 / 18.2Hz ≈ 7.03초 + 1초
        check("HSC: one pattern at speed 2 ≈ 7 s", Math.Abs(s.LengthSeconds - (64 * 2 / 18.2 + 1)) < 0.2 && s.SourceFormat == "HSC-Tracker", s.LengthSeconds.ToString("0.00"));
        Expect(check, "HSC", s, 2.0);
    }

    private static byte[] MakeCmf(bool rhythm)
    {
        var d = new List<byte>();
        d.AddRange(Encoding.ASCII.GetBytes("CTMF"));
        d.AddRange(BitConverter.GetBytes((ushort)0x0101));
        d.AddRange(BitConverter.GetBytes((ushort)0x28));     // 악기
        d.AddRange(BitConverter.GetBytes((ushort)0x48));     // 음악
        d.AddRange(BitConverter.GetBytes((ushort)48));       // 4분음표당 틱
        d.AddRange(BitConverter.GetBytes((ushort)96));       // 초당 틱
        d.AddRange(new byte[6]);                             // 제목 · 작곡가 · 메모 없음
        d.AddRange(new byte[16]);                            // 채널 사용
        d.AddRange(BitConverter.GetBytes((ushort)2));        // 악기 수
        d.AddRange(BitConverter.GetBytes((ushort)120));
        d.AddRange(new byte[] { 0x21, 0x21, 0x3F, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 0, 0, 0x00, 0, 0, 0, 0, 0 });
        d.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xF8, 0xF8, 0x46, 0x46, 0, 0, 0x00, 0, 0, 0, 0, 0 });
        if (rhythm)
            d.AddRange(new byte[] { 0x00, 0xBB, 0x67, 0x01, 0x00, 0xCB, 0x01, 0x00, 0x9B, 0x24, 0x7F, 0x60, 0x8B, 0x24, 0x00, 0x00, 0xFF, 0x2F, 0x00 });
        else
            d.AddRange(new byte[] { 0x00, 0xC0, 0x00, 0x00, 0x90, 69, 0x7F, 0x60, 0x80, 69, 0x00, 0x00, 0xFF, 0x2F, 0x00 });
        return d.ToArray();
    }

    private static void Cmf(Action<string, bool, string> check)
    {
        var s = AdlibStreamFormats.ReadCmf(MakeCmf(false), "cmf");
        check("CMF: 96 ticks/s, note 1 s + 1 s tail", Math.Abs(s.LengthSeconds - 2.0) < 0.02 && s.SourceFormat == "CMF v1.1", s.LengthSeconds.ToString("0.00"));
        Expect(check, "CMF", s, 1.0);
        var r = AdlibStreamFormats.ReadCmf(MakeCmf(true), "cmf");
        var used = VgmPlayer.ScanUsed(r);
        double rms = Rms(Render(r, 0.5), 0, Rate / 2);
        check("CMF rhythm mode (CC 0x67): channel 12 → BD sounds", rms > 100 && used[6], rms.ToString("0"));
    }

    private static void Registry(Action<string, bool, string> check)
    {
        var ext = ChipSongs.Extensions.ToList();
        check("ChipSongs: VGM · S98 · KSS · IMF · WLF · HSC · CMF", new[] { ".vgm", ".vgz", ".s98", ".kss", ".imf", ".wlf", ".hsc", ".cmf" }.All(ext.Contains), string.Join(" ", ext));
        check("ChipSongs: track path 'a.kss::3' → file part", ChipSongs.FilePart(@"C:\m\a.kss::3") == @"C:\m\a.kss" && ChipSongs.IsChipSong(@"C:\m\a.kss::3"), "");
    }
}
