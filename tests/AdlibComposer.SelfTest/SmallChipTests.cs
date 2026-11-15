using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>작은 칩 묶음: MSM6258 (VGM 0xB7 · DAC 스트림) · SAA1099 (VGM 0xBD) · MSM5205 · BEEP · DAC · 남코 WSG.</summary>
public static class SmallChipTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Saa(check);
        Msm6258Direct(check);
        Msm6258Stream(check);
        Msm5205Chip(check);
        BeepChip(check);
        DacAndWsg(check);
    }

    // ------------------------------------------------------------ 도우미

    private static double[] Render(VgmSong s, double sec, bool right = false, bool left = true)
    {
        var p = new VgmPlayer(); p.Start(s, 0, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++)
        {
            p.Step(); p.Generate(out double l, out double r);
            a[i] = left && right ? (l + r) / 2 : left ? l : r;
        }
        return a;
    }

    private static double[] Render(ISoundChip c, double sec)
    {
        var a = new double[(int)(sec * c.NativeRate)];
        for (int i = 0; i < a.Length; i++) { c.Generate(out int l, out int r); a[i] = (l + r) / 2.0; }
        return a;
    }

    private static double Freq(double[] a, int from, int to, int rate = Rate)
    {
        double mean = 0; for (int i = from; i < to; i++) mean += a[i]; mean /= Math.Max(1, to - from);
        int cross = 0;
        for (int i = from + 1; i < to; i++) if ((a[i - 1] - mean < 0) != (a[i] - mean < 0)) cross++;
        return cross / 2.0 / ((to - from) / (double)rate);
    }

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double m = 0; for (int i = from; i < to; i++) m += a[i]; m /= Math.Max(1, to - from);
        double s = 0; for (int i = from; i < to; i++) s += (a[i] - m) * (a[i] - m);
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static readonly int[] Steps = Enumerable.Range(0, 49).Select(i => (int)Math.Floor(16.0 * Math.Pow(1.1, i))).ToArray();
    private static readonly int[] Shift = { -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>시험용 OKI ADPCM 인코더 (디코더를 그대로 따라가며 오차가 가장 작은 니블, 시작 신호 −2).</summary>
    private static int[] EncodeOki(double[] x)
    {
        var o = new int[x.Length];
        int sig = -2, step = 0;
        for (int i = 0; i < x.Length; i++)
        {
            int best = 0, bestErr = int.MaxValue, bs = 0, bt = 0;
            for (int n = 0; n < 16; n++)
            {
                int ss = Steps[step], d = ss >> 3;
                if ((n & 1) != 0) d += ss >> 2;
                if ((n & 2) != 0) d += ss >> 1;
                if ((n & 4) != 0) d += ss;
                int ns = Math.Max(-2048, Math.Min(2047, sig + ((n & 8) != 0 ? -d : d)));
                int err = Math.Abs(ns - (int)x[i]);
                if (err < bestErr) { bestErr = err; best = n; bs = ns; bt = Math.Max(0, Math.Min(48, step + Shift[n & 7])); }
            }
            o[i] = best; sig = bs; step = bt;
        }
        return o;
    }

    /// <summary>사인 nSamples개 → 바이트 (아래 니블 먼저).</summary>
    private static byte[] OkiSine(double hz, double rate, int nSamples, double amp = 1500)
    {
        var x = new double[nSamples];
        for (int i = 0; i < nSamples; i++) x[i] = amp * Math.Sin(2 * Math.PI * hz * i / rate);
        var n = EncodeOki(x);
        var b = new byte[nSamples / 2];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(n[2 * i] | (n[2 * i + 1] << 4));
        return b;
    }

    // ------------------------------------------------------------ SAA1099

    private static VgmWriter SaaWriter()
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockSaa1099, 8000000);
        return w;
    }

    private static void Saa(Action<string, bool, string> check)
    {
        // 채널 1: 옥타브 3, 주파수 227 → 8MHz/512 × 8 / 284 = 440.1Hz, 왼쪽만
        var w = SaaWriter();
        w.Write(0xBD, 0x00, 0x0F);
        w.Write(0xBD, 0x08, 227);
        w.Write(0xBD, 0x10, 0x03);
        w.Write(0xBD, 0x14, 0x01);
        w.Write(0xBD, 0x1C, 0x01);
        w.Wait(Rate);
        var s = VgmFormat.Read(w.ToArray());
        var labels = string.Join(",", VgmPlayer.ChannelsOf(s).Select(c => c.Label));
        check("SAA1099: VGM clock 0xC8 → chip, Q1~Q6", s.Saa1099Clock == 8000000 && s.UnsupportedChips.Count == 0 && s.ChipSummary == "SAA1099" && labels == "Q1,Q2,Q3,Q4,Q5,Q6", labels);
        var l = Render(s, 1, false, true);
        var r = Render(s, 1, true, false);
        double f = Freq(l, 4410, Rate);
        check("SAA1099: tone 440 Hz (octave 3, freq 227), left only", Math.Abs(f - 440.1) < 3 && Rms(l, 4410, Rate) > 500 && Rms(r, 4410, Rate) < 1 && VgmPlayer.ScanUsed(s)[0], f.ToString("0.0") + " Hz, R=" + Rms(r, 4410, Rate).ToString("0"));

        // 전체 끄기 (0x1C bit0 = 0) → 조용
        var w2 = SaaWriter();
        w2.Write(0xBD, 0x00, 0xFF); w2.Write(0xBD, 0x08, 227); w2.Write(0xBD, 0x10, 0x03); w2.Write(0xBD, 0x14, 0x01);
        w2.Wait(Rate / 4);
        check("SAA1099: all-off (0x1C = 0) is silent", Rms(Render(VgmFormat.Read(w2.ToArray()), 0.25), 0, Rate / 4) < 1, "");

        // 노이즈만 (채널 0, 31kHz): 소리는 나지만 음높이 없음
        var c = new Saa1099(8000000, Rate);
        c.Write(0, 0x00, 0xFF); c.Write(0, 0x15, 0x01); c.Write(0, 0x16, 0x00); c.Write(0, 0x1C, 0x01);
        var n = Render(c, 0.5);
        check("SAA1099: noise generator sounds (channel 1, noise only)", Rms(n, 0, n.Length) > 300 && c.ChannelFrequency(0) == 0 && c.ChannelActive(0), Rms(n, 0, n.Length).ToString("0"));

        // 엔벨로프 (묶음 0 → 채널 3): 한 번 줄어듦, 안 클럭 = 채널 2(Q2)의 주파수 → 앞이 크고 뒤는 조용
        var e = new Saa1099(8000000, Rate);
        e.Write(0, 0x02, 0xFF); e.Write(0, 0x0A, 200); e.Write(0, 0x11, 0x03);   // 채널 2(3번째) 소리
        e.Write(0, 0x09, 0); e.Write(0, 0x10, 0x00);                                // 채널 1(2번째) 옥타브 0, 주파수 0 → 엔벨로프 클럭 ≈ 61 칸/초
        e.Write(0, 0x14, 0x04); e.Write(0, 0x18, 0x80 | (2 << 1)); e.Write(0, 0x1C, 0x01);
        var ea = Render(e, 0.5);
        double early = Rms(ea, 0, Rate / 20), late = Rms(ea, Rate * 2 / 5, Rate / 2);
        check("SAA1099: envelope single decay on channel 3 (loud → silent)", early > 500 && late < 1, early.ToString("0") + " → " + late.ToString("0"));
        // 엔벨로프 모드 1 (최대) + 왼쪽 · 오른쪽 뒤집기: 오른쪽 = 15 − 15 = 0
        var e2 = new Saa1099(8000000, Rate);
        e2.Write(0, 0x02, 0xFF); e2.Write(0, 0x0A, 200); e2.Write(0, 0x11, 0x03); e2.Write(0, 0x14, 0x04);
        e2.Write(0, 0x18, 0x80 | (1 << 1) | 1); e2.Write(0, 0x1C, 0x01);
        double sl = 0, sr = 0;
        for (int i = 0; i < 4410; i++) { e2.Generate(out int a, out int b); sl += Math.Abs(a); sr += Math.Abs(b); }
        check("SAA1099: envelope 'max' with right inverted → left only", sl > 1000 && sr == 0, sl.ToString("0") + " / " + sr.ToString("0"));
    }

    // ------------------------------------------------------------ MSM6258

    private static VgmWriter MsmWriter(int flags)
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockOkim6258, 4096000);
        w.SetHeaderByte(0x94, (byte)flags);
        return w;
    }

    /// <summary>0xB7 0x01로 직접: 분주 512 → 8000Hz, 바이트 = 4000/초 (44100 / 4000 = 11.025샘플마다).</summary>
    private static void Msm6258Direct(Action<string, bool, string> check)
    {
        var data = OkiSine(400, 8000, 8000);   // 1초
        var w = MsmWriter(2);
        w.Write(0xB7, 0x00, 0x02);
        long t = 0;
        for (int i = 0; i < data.Length; i++)
        {
            w.Write((byte)0xB7, 0x01, (int)data[i]);
            long next = (long)Math.Round((i + 1) * Rate / 4000.0);
            w.Wait(next - t); t = next;
        }
        var s = VgmFormat.Read(w.ToArray());
        var a = Render(s, 1);
        double f = Freq(a, 4410, Rate - 2205);
        var labels = string.Join(",", VgmPlayer.ChannelsOf(s).Select(c => c.Label));
        check("MSM6258: VGM clock 0x90 + flags 0x94 → chip, ADPCM channel", s.Okim6258Clock == 4096000 && s.Okim6258Flags == 2 && s.UnsupportedChips.Count == 0 && s.ChipSummary == "OKIM6258" && labels == "ADPCM", labels);
        check("MSM6258: 0xB7 data writes at 8 kHz play 400 Hz", Math.Abs(f - 400) < 4 && Rms(a, 4410, Rate) > 2000 && VgmPlayer.ScanUsed(s)[0], f.ToString("0.0") + " Hz, rms " + Rms(a, 4410, Rate).ToString("0"));

        // 분주 1024로 같은 데이터를 같은 속도로 넣으면 (칩이 반 빠르기) 소리는 200Hz
        var w2 = MsmWriter(0);
        w2.Write(0xB7, 0x00, 0x02);
        t = 0;
        for (int i = 0; i < data.Length / 2; i++)
        {
            w2.Write((byte)0xB7, 0x01, (int)data[i]);
            long next = (long)Math.Round((i + 1) * Rate / 2000.0);
            w2.Wait(next - t); t = next;
        }
        double f2 = Freq(Render(VgmFormat.Read(w2.ToArray()), 1), 4410, Rate - 2205);
        check("MSM6258: divider 1024 (flags 0) → 4 kHz chip rate, 200 Hz", Math.Abs(f2 - 200) < 3, f2.ToString("0.0") + " Hz");
    }

    /// <summary>DAC 스트림: 데이터 블록 0x04 + 0x90~0x93 · 0x95, 팬 (0xB7 0x02).</summary>
    private static void Msm6258Stream(Action<string, bool, string> check)
    {
        var a400 = OkiSine(400, 8000, 8000);
        var a800 = OkiSine(800, 8000, 8000);
        VgmWriter Setup(int pan)
        {
            var w = MsmWriter(2);
            foreach (var blk in new[] { a400, a800 })
            {
                var cmd = new List<byte> { 0x67, 0x66, 0x04 };
                cmd.AddRange(BitConverter.GetBytes(blk.Length));
                cmd.AddRange(blk);
                w.Write(cmd.ToArray());
            }
            w.Write(0x90, 0x00, 0x17, 0x00, 0x01);           // 스트림 0 → OKIM6258 reg 0x01
            w.Write(0x91, 0x00, 0x04, 0x01, 0x00);           // 데이터 뱅크 0x04, 한 칸씩
            w.Write(0x92, 0x00, 0xA0, 0x0F, 0x00, 0x00);     // 4000 바이트 / 초
            w.Write(0xB7, 0x02, pan);
            w.Write(0xB7, 0x00, 0x02);
            return w;
        }

        // 0x93: 처음 블록 위치부터 4000 명령 (모드 1)
        var w1 = Setup(0);
        w1.Write(0x93, 0x00, 0, 0, 0, 0, 0x01, 0xA0, 0x0F, 0, 0);
        w1.Wait(Rate);
        var s1 = VgmFormat.Read(w1.ToArray());
        var r1 = Render(s1, 1);
        double f1 = Freq(r1, 4410, Rate - 2205);
        check("VGM DAC stream 0x90~0x93: OKIM6258 400 Hz from data block 0x04", Math.Abs(f1 - 400) < 4 && Rms(r1, 4410, Rate) > 2000, f1.ToString("0.0") + " Hz");

        // 0x95: 블록 1 (800Hz) 바로 시작, 팬 bit0 = 오른쪽 끔
        var w2 = Setup(0x01);
        w2.Write(0x95, 0x00, 0x01, 0x00, 0x00);
        w2.Wait(Rate);
        var s2 = VgmFormat.Read(w2.ToArray());
        var l2 = Render(s2, 1, false, true);
        var rr = Render(s2, 1, true, false);
        double f2 = Freq(l2, 4410, Rate - 2205);
        check("VGM DAC stream 0x95: block 1 → 800 Hz, pan bit0 = right off", Math.Abs(f2 - 800) < 6 && Rms(l2, 4410, Rate) > 2000 && Rms(rr, 4410, Rate) < 1, f2.ToString("0.0") + " Hz, R " + Rms(rr, 4410, Rate).ToString("0"));

        // 0x94 멈춤 → 0.5초 뒤 조용 (칩은 데이터가 없으면 줄여 감)
        var w3 = Setup(0);
        w3.Write(0x95, 0x00, 0x00, 0x00, 0x00);
        w3.Wait(Rate / 4);
        w3.Write(0x94, 0x00);
        w3.Wait(Rate * 3 / 4);
        var r3 = Render(VgmFormat.Read(w3.ToArray()), 1);
        check("VGM DAC stream 0x94 stops the stream (sound decays)", Rms(r3, 2205, Rate / 4) > 2000 && Rms(r3, Rate * 3 / 4, Rate) < 50, Rms(r3, Rate * 3 / 4, Rate).ToString("0.0"));

        // 시작 위치가 처음이 아니어도 (빨리 감기 중 스트림이 따라감)
        var p = new VgmPlayer(); p.Start(VgmFormat.Read(w1.ToArray()), 500, null);
        var b = new double[Rate / 4];
        for (int i = 0; i < b.Length; i++) { p.Step(); p.Generate(out double l, out double r); b[i] = l; }
        double fb = Freq(b, 2205, b.Length);
        check("VGM DAC stream: start at 500 ms keeps stream position", Math.Abs(fb - 400) < 8, fb.ToString("0.0") + " Hz");
    }

    // ------------------------------------------------------------ MSM5205 · BEEP · DAC · WSG

    private static void Msm5205Chip(Action<string, bool, string> check)
    {
        var m = new Msm5205(384000, Rate);
        m.Write(0, 1, 1);   // /48 → 8kHz
        var data = OkiSine(500, 8000, 4000);
        var a = new List<double>();
        double feed = 0;
        int k = 0;
        for (int i = 0; i < Rate / 2; i++)
        {
            feed += 8000.0 / Rate;
            while (feed >= 1 && k < data.Length * 2) { feed -= 1; m.Write(0, 0, (k & 1) == 0 ? data[k >> 1] & 15 : data[k >> 1] >> 4); k++; }
            m.Generate(out int l, out int r); a.Add(l);
        }
        double f = Freq(a.ToArray(), 2205, a.Count);
        check("MSM5205: 384 kHz /48 = 8 kHz, nibbles per VCK → 500 Hz", Math.Abs(f - 500) < 5 && Rms(a.ToArray(), 2205, a.Count) > 2000, f.ToString("0.0") + " Hz");
        m.Write(0, 2, 1);
        var z = Render(m, 0.1);
        check("MSM5205: reset holds output at 0", z.All(v => v == 0), "");
    }

    private static void BeepChip(Action<string, bool, string> check)
    {
        var b = new Beeper(Beeper.Pc98Clock, Rate);
        int cnt = (int)Math.Round(Beeper.Pc98Clock / 1000);
        b.Write(0, 0, cnt & 0xFF); b.Write(0, 1, cnt >> 8); b.Write(0, 2, 1);
        var a = Render(b, 0.5);
        double f = Freq(a, 0, a.Length);
        check("BEEP: PC-98 2.4576 MHz / " + cnt + " = 1000 Hz square", Math.Abs(f - 1000) < 3 && Rms(a, 0, a.Length) > 5000 && Math.Abs(b.ChannelFrequency(0) - 1000) < 1, f.ToString("0.0") + " Hz");
        b.Write(0, 2, 0);
        check("BEEP: off is silent", Render(b, 0.1).All(v => v == 0), "");
        // 아주 높은 음 (카운터 2 = 1.2MHz)은 평균이 0 근처 (겹침 없이)
        b.Write(0, 0, 2); b.Write(0, 1, 0); b.Write(0, 2, 1);
        var hi = Render(b, 0.1);
        check("BEEP: ultrasonic counter averages out (no aliasing tone)", Rms(hi, 0, hi.Length) < 200, Rms(hi, 0, hi.Length).ToString("0"));
    }

    private static void DacAndWsg(Action<string, bool, string> check)
    {
        var d = new DacChip(Rate);
        d.Write(0, 0, 0xFF); d.Generate(out int hi, out _);
        d.Write(0, 0, 0x80); d.Generate(out int mid, out _);
        d.Write(0, 0, 0x00); d.Generate(out int lo, out _);
        d.Write(0, 1, 1); d.Write(0, 0, 0x80); d.Generate(out int sneg, out _);
        check("DAC: unsigned 0xFF / 0x80 / 0x00 → + / 0 / −, signed 0x80 → −", hi > 15000 && mid == 0 && lo < -15000 && sneg < -15000, hi + " " + mid + " " + lo + " " + sneg);

        // 남코 WSG: 목소리 0 주파수 4806 → 96000 × 4806 / 2^20 = 440.0Hz, 목소리 1 = 0x16~0x19 (위 4니블)
        var w = new NamcoWsg(Rate);
        void SetFreq(int v, int f)
        {
            int b = v == 0 ? 0x10 : v * 5 + 0x10;
            if (v == 0) w.Write(0, 0x10, f & 15);
            for (int i = 1; i <= 4; i++) w.Write(0, b + i, (f >> (4 * i)) & 15);
        }
        SetFreq(0, 4806); w.Write(0, 0x05, 0); w.Write(0, 0x15, 15);
        var a = Render(w, 0.5);
        double f0 = Freq(a, 0, a.Length);
        check("Namco WSG: voice 1 freq 4806 → 440 Hz (sine wave)", Math.Abs(f0 - 440) < 3 && Math.Abs(w.ChannelFrequency(0) - 440) < 0.5 && Rms(a, 0, a.Length) > 2000, f0.ToString("0.0") + " Hz");
        w.Write(0, 0x15, 0);
        SetFreq(1, 0x2580); w.Write(0, 0x0A, 0); w.Write(0, 0x1A, 15);   // 목소리 1 (맨 아래 니블 없음): 0x2580 → 879Hz
        var b2 = Render(w, 0.5);
        double f1 = Freq(b2, 0, b2.Length);
        double want = 0x2580 * 96000.0 / (1 << 20);
        check("Namco WSG: voice 2 uses registers 0x16~0x1A", Math.Abs(f1 - want) < 5 && w.ChannelLevel(0) == 0 && w.ChannelLevel(1) == 1, f1.ToString("0.0") + " / " + want.ToString("0.0") + " Hz");
    }
}
