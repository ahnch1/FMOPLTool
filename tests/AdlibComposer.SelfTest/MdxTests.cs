using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>MDX (MXDRV): 만든 곡으로 음높이 · 템포 · q · 되풀이 · 이음 · 되돌아감 · 동기 · ADPCM(PDX), Hootarchive/X68k의 실제 MDX.</summary>
public static class MdxTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Basics(check);
        Sequencing(check);
        Adpcm(check);
        Real(check);
    }

    // ------------------------------------------------------------ 만들기

    /// <summary>사인 음색 (알고리즘 7, C2만 TL 0, 나머지 127), 슬롯 마스크 15, RR 15.</summary>
    private static byte[] SineVoice(int n)
    {
        var v = new byte[27];
        v[0] = (byte)n; v[1] = 0x07; v[2] = 0x0F;
        for (int o = 0; o < 4; o++)
        {
            v[3 + o] = 0x01;                       // MUL 1
            v[7 + o] = (byte)(o == 3 ? 0 : 127);   // TL
            v[11 + o] = 0x1F;                      // AR 31
            v[15 + o] = 0x00;
            v[19 + o] = 0x00;
            v[23 + o] = 0x0F;                      // D1L 0, RR 15
        }
        return v;
    }

    /// <summary>트랙 바이트들로 MDX (트랙 수 = tracks.Length, 빈 트랙은 F1 00).</summary>
    private static byte[] MakeMdx(string title, string pdx, params byte[][] tracks)
    {
        var head = new List<byte>();
        head.AddRange(System.Text.Encoding.ASCII.GetBytes(title));
        head.AddRange(new byte[] { 0x0D, 0x0A, 0x1A });
        head.AddRange(System.Text.Encoding.ASCII.GetBytes(pdx));
        head.Add(0);
        int n = tracks.Length;
        var body = new List<byte>();
        var offs = new int[n];
        int at = 2 + 2 * n;
        for (int i = 0; i < n; i++) { offs[i] = at; at += tracks[i].Length; }
        int voiceOfs = at;
        body.Add((byte)(voiceOfs >> 8)); body.Add((byte)voiceOfs);
        foreach (int o in offs) { body.Add((byte)(o >> 8)); body.Add((byte)o); }
        foreach (var t in tracks) body.AddRange(t);
        body.AddRange(SineVoice(0));
        head.AddRange(body);
        return head.ToArray();
    }

    private static byte[] T(params int[] b) { return b.Select(x => (byte)x).ToArray(); }

    private static byte[][] Tracks9(params byte[][] first)
    {
        var l = new List<byte[]>(first);
        while (l.Count < 9) l.Add(T(0xF1, 0x00));
        return l.ToArray();
    }

    private static double[] Render(VgmSong s, double sec)
    {
        var p = new VgmPlayer(); p.Start(s, 0, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
        return a;
    }

    private static double Freq(double[] a, int from, int to)
    {
        double mean = 0; for (int i = from; i < to; i++) mean += a[i]; mean /= Math.Max(1, to - from);
        int cross = 0;
        for (int i = from + 1; i < to; i++) if ((a[i - 1] - mean < 0) != (a[i] - mean < 0)) cross++;
        return cross / 2.0 / ((to - from) / (double)Rate);
    }

    /// <summary>오르는 0 지남을 보간해 잰 주파수 (짧은 구간에서도 정확).</summary>
    private static double PreciseFreq(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double mean = 0; for (int i = from; i < to; i++) mean += a[i]; mean /= Math.Max(1, to - from);
        double first = -1, last = -1; int n = 0;
        for (int i = from + 1; i < to; i++)
            if (a[i - 1] - mean < 0 && a[i] - mean >= 0)
            {
                double x = i - 1 + (mean - a[i - 1]) / (a[i] - a[i - 1]);
                if (first < 0) first = x;
                last = x; n++;
            }
        return n > 1 ? (n - 1) / ((last - first) / Rate) : 0;
    }

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length); from = Math.Max(0, from);
        double m = 0; for (int i = from; i < to; i++) m += a[i]; m /= Math.Max(1, to - from);
        double s = 0; for (int i = from; i < to; i++) s += (a[i] - m) * (a[i] - m);
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    /// <summary>OPM 키온(슬롯이 켜진 0x08 쓰기)들의 시간 (초).</summary>
    private static List<double> KeyOns(VgmSong v, int ch = 0)
    {
        var l = new List<double>();
        int p = v.DataOffset; long t = 0;
        var d = v.Data;
        while (p < v.DataEnd && d[p] != 0x66)
        {
            int c = d[p];
            if (c == 0x61) t += d[p + 1] | (d[p + 2] << 8);
            else if (c == 0x62) t += 735; else if (c == 0x63) t += 882;
            else if (c >= 0x70 && c <= 0x7F) t += (c & 15) + 1;
            else if (c == 0x54 && d[p + 1] == 0x08 && (d[p + 2] & 7) == ch && (d[p + 2] & 0x78) != 0) l.Add(t / (double)Rate);
            int len = VgmFormat.CommandLength(d, p);
            if (len <= 0) break;
            p += len;
        }
        return l;
    }

    // 템포 200 → 한 클럭 = 1024 × 56 / 4MHz = 14.336ms
    private const double Tick = 1024.0 * 56 / 4000000;

    // ------------------------------------------------------------ 기본

    private static void Basics(Action<string, bool, string> check)
    {
        // o4a = 0x80 + 54 (0x80 = o0d+) → KC 0x48 (4MHz) ≈ 438Hz, 길이 70클럭 ≈ 1초
        var m = MakeMdx("Test Song", "", Tracks9(T(0xFF, 200, 0xFD, 0x00, 0xFB, 0x0F, 0xFC, 0x03, 0x80 + 54, 69, 0xF1, 0x00)));
        var h = Mdx.ReadHeader(m);
        check("MDX header: title, PDX name, 9 tracks, voice table", h.Title == "Test Song" && h.PdxName == "" && h.Tracks.Length == 9 && h.Voices.ContainsKey(0), h.Title + " / " + h.Tracks.Length);
        var v = Mdx.Render(m, null);
        var a = Render(v, 1.2);
        double f = PreciseFreq(a, 4410, Rate);
        check("MDX: o4a (0x80 + 54) = KC 0x48 on 4 MHz OPM = 438.0 Hz", Math.Abs(f - 438.03) < 0.5 && v.ChipSummary == "YM2151", f.ToString("0.0") + " Hz, [" + v.ChipSummary + "]");
        check("MDX: tempo 200 → 70 clocks = 1.0035 s, song ends with track", Math.Abs(v.LengthSeconds - 70 * Tick) < 0.001 && v.FadeSeconds == 0, v.LengthSeconds.ToString("0.000") + " s");

        // q4: 70클럭 중 앞 35클럭만 소리
        var mq = MakeMdx("", "", Tracks9(T(0xFF, 200, 0xFD, 0x00, 0xFB, 0x0F, 0xF8, 0x04, 0x80 + 54, 69, 0x45, 0xF1, 0x00)));
        var aq = Render(Mdx.Render(mq, null), 1.0);
        double on = Rms(aq, (int)(0.1 * Rate), (int)(0.45 * Rate)), off = Rms(aq, (int)(0.6 * Rate), (int)(0.95 * Rate));
        check("MDX q4: key off at half the note length", on > 1000 && off < on / 50, on.ToString("0") + " → " + off.ToString("0"));

        // 디튠 +32 (반 반음) · 음량 v15 대 v0 · @v
        var md = MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0xF3, 0x00, 0x20, 0x80 + 54, 69, 0xF1, 0x00)));
        double fd = PreciseFreq(Render(Mdx.Render(md, null), 1.0), 4410, Rate);
        check("MDX detune F3 +32/64 semitone", Math.Abs(fd / f - Math.Pow(2, 0.5 / 12)) < 0.002, fd.ToString("0.0") + " Hz");
        double r15 = Rms(Render(Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0x80 + 54, 69, 0xF1, 0x00))), null), 0.5), 4410, Rate / 2);
        double r0 = Rms(Render(Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x00, 0x80 + 54, 69, 0xF1, 0x00))), null), 0.5), 4410, Rate / 2);
        double rv = Rms(Render(Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x80 + 0x2A, 0x80 + 54, 69, 0xF1, 0x00))), null), 0.5), 4410, Rate / 2);
        check("MDX volume: v0 = 30 dB below v15 (TL +40 × 0.75 dB), @v42 = v0", r0 < r15 / 20 && r0 > r15 / 80 && Math.Abs(rv - r0) < r0 * 0.1, (20 * Math.Log10(r15 / r0)).ToString("0.0") + " dB");
    }

    // ------------------------------------------------------------ 순서

    private static void Sequencing(Action<string, bool, string> check)
    {
        // [ c ; ! e ]3 c  → c e c e c c (F4는 마지막에 빠져나감)
        var rep = T(0xFD, 0x00, 0xFB, 0x0F,
            0xF6, 0x03, 0x00,              // 되풀이 3번
            0x80 + 50, 9,                  // c (10클럭)
            0xF4, 0x00, 0x05,              // 마지막이면 F5 다음으로 (F5의 거리 바이트로)
            0x80 + 54, 9,                  // e
            0xF5, 0xFF, 0xF6,              // F6 다음으로 (−10)
            0x80 + 50, 9,
            0xF1, 0x00);
        // F4 거리: F4 끝(+3) → F5 거리 바이트 = 음 2바이트 뒤 + 1 → 3
        rep[11] = 0x03;
        var kr = KeyOns(Mdx.Render(MakeMdx("", "", Tracks9(rep)), null));
        check("MDX repeat F6/F5 ×3 with F4 escape on the last pass → 6 notes", kr.Count == 6 && Math.Abs(kr[5] - 5 * 10 * Tick) < 0.002, kr.Count + " key-ons, last at " + (kr.Count > 0 ? kr[^1].ToString("0.000") : "-"));

        // 이음: F7 + 같은 음 둘 → 키온 한 번
        var tie = KeyOns(Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0xF7, 0x80 + 54, 9, 0x80 + 56, 9, 0x80 + 54, 9, 0xF1, 0x00))), null));
        check("MDX tie F7: legato note does not key on again", tie.Count == 2 && Math.Abs(tie[1] - 20 * Tick) < 0.002, string.Join(",", tie.Select(x => x.ToString("0.000"))));

        // 키온 늦춤 F0 5
        var kd = KeyOns(Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0xF0, 0x05, 0x80 + 54, 19, 0xF1, 0x00))), null));
        check("MDX key-on delay F0 5 clocks", kd.Count == 1 && Math.Abs(kd[0] - 5 * Tick) < 0.002, kd.Count > 0 ? kd[0].ToString("0.000") : "-");

        // 되돌아감: 음 10클럭 + F1 (−5) → 두 번째 F1에서 끝 (본체 두 번), 줄임 있음
        var loop = Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0x80 + 54, 9, 0xF1, 0xFF, 0xFB))), null);
        check("MDX loop F1: plays loop body twice, then fades", Math.Abs(loop.LengthSeconds - 20 * Tick) < 0.003 && loop.FadeSeconds > 0 && KeyOns(loop).Count == 2, loop.LengthSeconds.ToString("0.000") + " s, fade " + loop.FadeSeconds.ToString("0.00"));

        // 동기: 트랙 2가 EE로 기다리다 트랙 1의 EF 1 (20클럭 뒤)에 시작
        var s1 = T(0xFD, 0x00, 0xFB, 0x0F, 0x13, 0xEF, 0x01, 0x80 + 54, 9, 0xF1, 0x00);
        var s2 = T(0xFD, 0x00, 0xFB, 0x0F, 0xEE, 0x80 + 58, 9, 0xF1, 0x00);
        var sv = Mdx.Render(MakeMdx("", "", Tracks9(s1, s2)), null);
        var k2 = KeyOns(sv, 1);
        check("MDX sync EE waits for EF from another track", k2.Count == 1 && Math.Abs(k2[0] - 20 * Tick) < 0.002, k2.Count > 0 ? k2[0].ToString("0.000") : "-");

        // 포르타멘토 F2 +0x4000/클럭 (1반음) 12클럭 → 한 옥타브 위로
        var pm = Mdx.Render(MakeMdx("", "", Tracks9(T(0xFD, 0x00, 0xFB, 0x0F, 0xF2, 0x40, 0x00, 0x80 + 42, 59, 0xF1, 0x00))), null);
        var pa = Render(pm, 60 * Tick);
        double fp0 = Freq(pa, 0, (int)(4 * Tick * Rate)), fp1 = Freq(pa, (int)(20 * Tick * Rate), (int)(58 * Tick * Rate));
        check("MDX portamento F2 +0x4000: rises 1 semitone per clock", fp1 > fp0 * 2.5, fp0.ToString("0") + " → " + fp1.ToString("0") + " Hz");
    }

    // ------------------------------------------------------------ ADPCM (PDX)

    private static void Adpcm(Action<string, bool, string> check)
    {
        // PDX: 샘플 0 = 400Hz, 15.6kHz로 0.5초
        var smp = SmallChipTests.OkiSine(400, 15625, 15625 / 2);
        var pdx = new byte[0x300 + smp.Length];
        pdx[0] = 0; pdx[1] = 0; pdx[2] = 0x03; pdx[3] = 0x00;
        pdx[4] = (byte)(smp.Length >> 24); pdx[5] = (byte)(smp.Length >> 16); pdx[6] = (byte)(smp.Length >> 8); pdx[7] = (byte)smp.Length;
        Array.Copy(smp, 0, pdx, 0x300, smp.Length);
        var tracks = Tracks9();
        tracks[8] = T(0xED, 0x04, 0xFC, 0x03, 0x80 + 0, 69, 0xF1, 0x00);
        var m = MakeMdx("PCM", "TEST.PDX", tracks);
        var v = Mdx.Render(m, pdx);
        var a = Render(v, 0.6);
        double f = Freq(a, 2205, (int)(0.45 * Rate));
        check("MDX ADPCM track: PDX sample 0 at 15.6 kHz via MSM6258 stream (400 Hz)", v.ChipSummary == "YM2151 + OKIM6258" && Math.Abs(f - 400) < 6 && Rms(a, 2205, (int)(0.45 * Rate)) > 1000,
            "[" + v.ChipSummary + "] " + f.ToString("0.0") + " Hz");
        // 같은 곡을 ED 02 (7.8kHz)로: 반 빠르기 → 200Hz
        tracks[8] = T(0xED, 0x02, 0x80 + 0, 69, 0xF1, 0x00);
        double f2 = Freq(Render(Mdx.Render(MakeMdx("PCM", "TEST.PDX", tracks), pdx), 0.6), 2205, (int)(0.45 * Rate));
        check("MDX ADPCM rate ED 02 → 7.8 kHz (200 Hz)", Math.Abs(f2 - 200) < 4, f2.ToString("0.0") + " Hz");

        // 파일로: PDX를 같은 폴더에서 (대소문자 무시)
        string dir = Path.Combine(Path.GetTempPath(), "fmopl_mdx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "song.mdx"), m);
            File.WriteAllBytes(Path.Combine(dir, "test.pdx"), pdx);
            var lv = ChipSongs.Load(Path.Combine(dir, "song.mdx"));
            check("MDX Load finds PDX beside it (case-insensitive)", lv.ChipSummary.Contains("OKIM6258") && lv.SourceFormat.Contains("test.pdx") && lv.Title == "PCM", lv.SourceFormat);
            File.Delete(Path.Combine(dir, "test.pdx"));
            var nv = ChipSongs.Load(Path.Combine(dir, "song.mdx"));
            check("MDX without its PDX: plays FM, reports missing PDX", nv.ChipSummary == "YM2151" && nv.UnsupportedChips.Any(u => u.Contains("TEST.PDX")), string.Join(",", nv.UnsupportedChips));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ------------------------------------------------------------ 실제 파일 (Hootarchive/X68k의 zip)

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string x68 = dir == null ? null : Path.Combine(dir, "Hootarchive", "X68k");
        if (x68 == null || !Directory.Exists(x68)) { Console.WriteLine("SKIP  real MDX (_incoming/Hootarchive/X68k not found)"); return; }
        int files = 0, ok = 0, adpcm = 0, notMdx = 0;
        var bad = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var z in Directory.GetFiles(x68, "*.zip").OrderBy(x => x))
        {
            using (var zip = ZipFile.OpenRead(z))
            {
                var mdx = zip.Entries.Where(e => e.FullName.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)).ToList();
                if (mdx.Count == 0) continue;
                foreach (var e in mdx.Take(4))
                {
                    byte[] data = Read(e);
                    Mdx.Header h;
                    try { h = Mdx.ReadHeader(data); } catch (InvalidDataException) { notMdx++; continue; }
                    files++;
                    var pe = h.PdxName.Length == 0 ? null : zip.Entries.FirstOrDefault(x => string.Equals(x.Name, h.PdxName, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, h.PdxName + ".pdx", StringComparison.OrdinalIgnoreCase));
                    var v = Mdx.Render(data, pe != null ? Read(pe) : null, 60);
                    if (v.ChipSummary.Contains("OKIM6258")) adpcm++;
                    double r = Rms(Render(v, Math.Min(8, v.LengthSeconds)), Rate, (int)(Math.Min(8, v.LengthSeconds) * Rate));
                    if (r > 200 && v.LengthSeconds > 1) ok++; else bad.Add(e.Name + " rms " + r.ToString("0") + " len " + v.LengthSeconds.ToString("0.0"));
                }
            }
        }
        check("Real MDX (" + files + " files, " + adpcm + " with PDX ADPCM): all sound", files > 0 && ok == files,
            ok + "/" + files + (bad.Count > 0 ? " bad: " + string.Join("; ", bad) : "") + ", not MXDRV " + notMdx + ", " + sw.ElapsedMilliseconds + " ms");
    }

    private static byte[] Read(ZipArchiveEntry e)
    {
        using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
    }
}
