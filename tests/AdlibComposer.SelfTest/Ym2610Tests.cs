using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>YM2610 / YM2610B (OPNB): FM 채널 배치 · ADPCM-A · ADPCM-B (256바이트 단위) · VGM.</summary>
public static class Ym2610Tests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Fm(check);
        AdpcmA(check);
        AdpcmB(check);
    }

    private static VgmWriter Writer(bool b)
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockYm2610, 8000000, b ? 0x80000000u : 0);
        return w;
    }

    /// <summary>FM 채널 하나에 사인 (알고리즘 7, 연산자 4만 소리), F-number 1038 블록 4 ≈ 440Hz.</summary>
    private static void FmTone(VgmWriter w, int port, int chn, int keyCode)
    {
        byte cmd = (byte)(port == 0 ? 0x58 : 0x59);
        for (int op = 0; op < 4; op++)
        {
            int o = op * 4 + chn;
            w.Write(cmd, 0x30 + o, 0x01);
            w.Write(cmd, 0x40 + o, op == 3 ? 0x00 : 0x7F);
            w.Write(cmd, 0x50 + o, 0x1F);
            w.Write(cmd, 0x60 + o, 0x00);
            w.Write(cmd, 0x70 + o, 0x00);
            w.Write(cmd, 0x80 + o, 0x0F);
        }
        w.Write(cmd, 0xB0 + chn, 0x07);
        w.Write(cmd, 0xB4 + chn, 0xC0);
        w.Write(cmd, 0xA4 + chn, 0x24);
        w.Write(cmd, 0xA0 + chn, 0x0E);
        w.Write((byte)0x58, 0x28, 0xF0 | keyCode);
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

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static void Fm(Action<string, bool, string> check)
    {
        var w = Writer(false);
        FmTone(w, 0, 1, 1);          // 채널 코드 1 (있는 채널)
        w.Wait(Rate);
        var s = VgmFormat.Read(w.ToArray());
        var ch = VgmPlayer.ChannelsOf(s);
        var a = Render(s, 1);
        double f = Freq(a, 4410, Rate);
        check("YM2610: VGM clock (0x4C) → OPNB chip, 4 FM + SSG + ADPCM-A 6 + ADPCM-B", s.Ym2610Clock == 8000000 && !s.Ym2610B && s.UnsupportedChips.Count == 0 && s.ChipSummary.Contains("YM2610")
            && string.Join(",", ch.Select(c => c.Label)) == "F1,F2,F3,F4,A,B,C,A1,A2,A3,A4,A5,A6,AB", string.Join(",", ch.Select(c => c.Label)));
        check("YM2610 FM: channel 2 (code 1) plays 440 Hz, shown as F1", Math.Abs(f - 440) < 4 && Rms(a, 4410, Rate) > 500 && VgmPlayer.ScanUsed(s)[0], f.ToString("0.0") + " Hz");

        // 채널 코드 0: YM2610에는 없음, YM2610B에는 있음
        foreach (bool b in new[] { false, true })
        {
            var w0 = Writer(b);
            FmTone(w0, 0, 0, 0);
            w0.Wait(Rate / 2);
            var s0 = VgmFormat.Read(w0.ToArray());
            double r0 = Rms(Render(s0, 0.5), 2205, Rate / 2);
            if (!b) check("YM2610: FM channel 1 does not exist (silent)", r0 < 1, r0.ToString("0"));
            else check("YM2610B: all 6 FM channels (channel 1 sounds)", r0 > 500 && s0.Ym2610B && VgmPlayer.ChannelsOf(s0).Count(c => c.Label.StartsWith("F")) == 6, r0.ToString("0"));
        }
    }

    // ------------------------------------------------------------ ADPCM-A

    private static readonly int[] Steps =
    {
        16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
        279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
    };
    private static readonly int[] StepInc = { -1, -1, -1, -1, 2, 5, 7, 9 };

    /// <summary>시험용 ADPCM-A 인코더 (디코더를 그대로 따라가며 오차가 가장 작은 니블).</summary>
    private static byte[] EncodeA(int[] samples)
    {
        var bytes = new byte[(samples.Length + 1) / 2];
        int acc = 0, idx = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int best = 0, bestErr = int.MaxValue, bestAcc = 0, bestIdx = 0;
            for (int n = 0; n < 16; n++)
            {
                int d = (2 * (n & 7) + 1) * Steps[idx] / 8;
                if ((n & 8) != 0) d = -d;
                int na = (acc + d) & 0xFFF;
                int sv = ((na ^ 0x800) - 0x800);
                int err = Math.Abs(sv - samples[i]);
                if (err < bestErr) { bestErr = err; best = n; bestAcc = na; bestIdx = Math.Max(0, Math.Min(48, idx + StepInc[n & 7])); }
            }
            acc = bestAcc; idx = bestIdx;
            if ((i & 1) == 0) bytes[i >> 1] = (byte)(best << 4); else bytes[i >> 1] |= (byte)best;
        }
        return bytes;
    }

    private static void RomBlock(VgmWriter w, byte type, byte[] data)
    {
        var cmd = new List<byte> { 0x67, 0x66, type };
        cmd.AddRange(BitConverter.GetBytes(data.Length + 8));
        cmd.AddRange(BitConverter.GetBytes(data.Length));   // ROM 크기
        cmd.AddRange(BitConverter.GetBytes(0));             // 시작
        cmd.AddRange(data);
        w.Write(cmd.ToArray());
    }

    private static void AdpcmA(Action<string, bool, string> check)
    {
        // 18518Hz에서 42샘플 주기 네모 (≈ 441Hz), 4096샘플 = 2048바이트 = 256바이트 8칸
        var smp = Enumerable.Range(0, 4096).Select(i => (i % 42) < 21 ? 1200 : -1200).ToArray();
        var rom = EncodeA(smp);
        foreach (int level in new[] { 0x1F, 0x0F })
        {
            var w = Writer(false);
            RomBlock(w, 0x82, rom);
            w.Write((byte)0x59, 0x01, 0x3F);                 // 전체 감쇠 없음
            w.Write((byte)0x59, 0x08, 0xC0 | level);         // 채널 0: 좌우, 크기
            w.Write((byte)0x59, 0x10, 0x00); w.Write((byte)0x59, 0x18, 0x00);   // 시작 0
            w.Write((byte)0x59, 0x20, 0x07); w.Write((byte)0x59, 0x28, 0x00);   // 끝 0x07 (→ 0x7FF바이트)
            w.Write((byte)0x59, 0x00, 0x01);                 // 키 온
            w.Wait(Rate / 2);
            var s = VgmFormat.Read(w.ToArray());
            var a = Render(s, 0.5);
            double f = Freq(a, 441, (int)(0.2 * Rate)), on = Rms(a, 441, (int)(0.2 * Rate)), after = Rms(a, (int)(0.25 * Rate), a.Length);
            if (level == 0x1F)
            {
                check("YM2610 ADPCM-A: 18.5 kHz decode, square ≈ 441 Hz", Math.Abs(f - 440.9) < 8 && on > 1000, f.ToString("0.0") + " Hz, rms " + on.ToString("0"));
                check("YM2610 ADPCM-A: stops at end address (256-byte units), used channel A1", after < 1 && VgmPlayer.ScanUsed(s)[VgmPlayer.ChannelsOf(s).FindIndex(c => c.Label == "A1")], after.ToString("0.0"));
                levelLoud = on;
            }
            else
            {
                // 감쇠 16칸 × 0.75dB = 12dB
                double db = 20 * Math.Log10(on / levelLoud);
                check("YM2610 ADPCM-A: channel level −16 steps = −12 dB (0.75 dB per step)", Math.Abs(db + 12) < 1, db.ToString("0.0") + " dB");
            }
        }
    }

    private static double levelLoud;

    // ------------------------------------------------------------ ADPCM-B

    private static void AdpcmB(Action<string, bool, string> check)
    {
        // 니블 7 · F 번갈아 = 오르내리는 소리. 256바이트 = 512니블, delta-N 0x8000 (≈ 27.8k 니블/초) → 약 18ms
        var rom = Enumerable.Repeat((byte)0x7F, 1024).ToArray();
        var w = Writer(false);
        RomBlock(w, 0x83, rom);
        w.Write((byte)0x58, 0x11, 0xC0);                     // 좌우
        w.Write((byte)0x58, 0x12, 0x00); w.Write((byte)0x58, 0x13, 0x00);     // 시작 0
        w.Write((byte)0x58, 0x14, 0x00); w.Write((byte)0x58, 0x15, 0x00);     // 끝 0 (= 첫 256바이트)
        w.Write((byte)0x58, 0x19, 0x00); w.Write((byte)0x58, 0x1A, 0x80);     // delta-N
        w.Write((byte)0x58, 0x1B, 0xFF);                     // 크기
        w.Write((byte)0x58, 0x10, 0x80);                     // 시작
        w.Wait(Rate / 5);
        var s = VgmFormat.Read(w.ToArray());
        var a = Render(s, 0.2);
        double on = Rms(a, 0, (int)(0.012 * Rate)), after = Rms(a, (int)(0.05 * Rate), a.Length);
        check("YM2610 ADPCM-B: port 0 regs 0x10~0x1B, 256-byte units (stops after 512 nibbles)", on > 100 && after < 1, on.ToString("0") + " → " + after.ToString("0.0"));
    }
}
