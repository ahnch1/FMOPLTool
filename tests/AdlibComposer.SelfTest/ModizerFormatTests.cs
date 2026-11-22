using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// modizer 대조로 더한 형식 (docs/modizer-plan.md): 만든 파일 + 실제 표본 (D:\Retro\fmopldata\Modizer, 없으면 건너뜀 —
/// 환경 변수 FMOPL_MODIZER_SAMPLES로 바꿀 수 있음).
/// </summary>
public static class ModizerFormatTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Gym(check);
    }

    internal static string SampleDir()
    {
        string env = Environment.GetEnvironmentVariable("FMOPL_MODIZER_SAMPLES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        const string d = @"D:\Retro\fmopldata\Modizer";
        return Directory.Exists(d) ? d : null;
    }

    internal static string FirstSample(string pattern)
    {
        string dir = SampleDir();
        if (dir == null) return null;
        var f = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
        Array.Sort(f, StringComparer.OrdinalIgnoreCase);
        return f.Length > 0 ? f[0] : null;
    }

    internal static double[] Render(VgmSong s, double sec, int skip = 0)
    {
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        for (int i = 0; i < skip; i++) { p.Step(); p.Generate(out _, out _); }
        var o = new double[(int)(sec * 44100)];
        for (int i = 0; i < o.Length; i++) { p.Step(); p.Generate(out double l, out double r); o[i] = (l + r) / 2; }
        return o;
    }

    internal static double Rms(double[] a)
    {
        double s = 0;
        foreach (var x in a) s += x * x;
        return a.Length > 0 ? Math.Sqrt(s / a.Length) : 0;
    }

    // ------------------------------------------------------------ M1-1 GYM

    private static void Gym(Action<string, bool, string> check)
    {
        // 만든 GYM: GYMX 머리말 + YM2612 채널 1 음 하나 + SN76489 음 + DAC 몇 개, 120프레임, 루프 = 프레임 61
        var body = new List<byte>();
        void Fm(int port, int r, int v) { body.Add((byte)(port + 1)); body.Add((byte)r); body.Add((byte)v); }
        // 채널 1 (포트 0, 채널 0): 알고리즘 7, 모든 오퍼레이터 TL 0 · AR 31 · RR 15, 주파수, 키 온
        Fm(0, 0xB0, 0x07); Fm(0, 0xB4, 0xC0);
        foreach (int op in new[] { 0, 4, 8, 12 })
        {
            Fm(0, 0x30 + op, 0x01); Fm(0, 0x40 + op, 0x00); Fm(0, 0x50 + op, 0x1F);
            Fm(0, 0x60 + op, 0x00); Fm(0, 0x70 + op, 0x00); Fm(0, 0x80 + op, 0x0F);
        }
        Fm(0, 0xA4, 0x22); Fm(0, 0xA0, 0x69); Fm(0, 0x28, 0xF0);
        body.Add(3); body.Add(0x8E); body.Add(3); body.Add(0x0F); body.Add(3); body.Add(0x90);   // SN 톤 0: 주기 0xFE, 음량 최대
        for (int f = 0; f < 120; f++)
        {
            if (f == 10) { Fm(0, 0x2B, 0x80); for (int k = 0; k < 8; k++) Fm(0, 0x2A, 0x80 + (k & 1) * 0x60); }
            body.Add(0);
        }
        var d = new byte[GymFormat.HeaderSize + body.Count];
        "GYMX"u8.ToArray().CopyTo(d, 0);
        "Test Song"u8.ToArray().CopyTo(d, 4);
        "Test Game"u8.ToArray().CopyTo(d, 0x24);
        BitConverter.GetBytes(61u).CopyTo(d, 0x1A4);
        body.CopyTo(d, GymFormat.HeaderSize);

        var v = GymFormat.Read(d, "x");
        check("GYM: header title / game", v.TitleEn == "Test Song" && v.GameEn == "Test Game", v.TitleEn + " / " + v.GameEn);
        check("GYM: length 120 frames = 2 s", v.TotalSamples == 120 * 735, v.TotalSamples.ToString());
        check("GYM: loop at frame 61 (60 frames)", v.LoopSamples == 60 * 735, v.LoopSamples.ToString());
        check("GYM: YM2612 + SN76489 clocks", v.Ym2612Clock == GymFormat.Ym2612Clock && v.Sn76489Clock == GymFormat.Sn76489Clock, v.Ym2612Clock + " / " + v.Sn76489Clock);
        double r = Rms(Render(v, 1));
        check("GYM: renders sound", r > 100, r.ToString("0"));

        // 머리말 없는 GYM (예전 형식) · zlib 압축 GYMX
        var raw = GymFormat.Read(body.ToArray(), "Raw");
        check("GYM: headerless body", raw.TotalSamples == 120 * 735 && raw.LoopSamples == 0 && raw.TitleEn == "Raw", raw.TotalSamples + " / " + raw.LoopSamples);
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(body.ToArray());
        var packed = new byte[GymFormat.HeaderSize + ms.Length];
        Array.Copy(d, packed, GymFormat.HeaderSize);
        BitConverter.GetBytes((uint)body.Count).CopyTo(packed, 0x1A8);
        ms.ToArray().CopyTo(packed, GymFormat.HeaderSize);
        var pv = GymFormat.Read(packed);
        check("GYM: zlib packed = same length", pv.TotalSamples == v.TotalSamples && pv.SourceFormat.Contains("압축"), pv.SourceFormat);
        check("GYM: ChipSongs knows .gym", ChipSongs.Extensions.Contains(".gym"), "");

        string real = FirstSample("*.gym");
        if (real == null) { Console.WriteLine("SKIP  GYM real file (sample folder not found)"); return; }
        var rv = ChipSongs.Load(real);
        double rr = Rms(Render(rv, 3, 44100));
        check("GYM real " + Path.GetFileName(real) + ": loads, sounds", rv.LengthSeconds > 10 && rr > 100, rv.LengthSeconds.ToString("0") + " s, rms " + rr.ToString("0") + ", " + rv.SourceFormat);
    }
}
