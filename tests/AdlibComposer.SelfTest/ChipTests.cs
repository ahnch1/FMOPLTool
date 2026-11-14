using System;
using AdlibComposer.Audio;

namespace AdlibComposer.SelfTest;

/// <summary>MSX PSG (AY-3-8910 / YM2149) · Konami SCC (K051649 / K052539) 에뮬레이터.</summary>
public static class ChipTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Psg(check);
        Scc(check);
    }

    /// <summary>n개 샘플을 만들어 RMS와 0 교차 수를 잰다.</summary>
    private static void Measure(Func<short> gen, int n, out double rms, out int crossings)
    {
        double sq = 0; crossings = 0; int prev = 0;
        for (int i = 0; i < n; i++)
        {
            int s = gen();
            sq += (double)s * s;
            int sign = s > 0 ? 1 : s < 0 ? -1 : 0;
            if (sign != 0 && prev != 0 && sign != prev) crossings++;
            if (sign != 0) prev = sign;
        }
        rms = Math.Sqrt(sq / n);
    }

    // ------------------------------------------------------------ PSG

    private static PsgEmulator Tone(int period, int vol, bool ym = true)
    {
        var p = new PsgEmulator(PsgEmulator.MsxClock, Rate, ym);
        p.Write(0, period & 0xFF);
        p.Write(1, period >> 8);
        p.Write(7, 0x3E);          // A 톤만 켬
        p.Write(8, vol);
        return p;
    }

    private static void Psg(Action<string, bool, string> check)
    {
        // TP 254 → 1789772.5 / (16 × 254) = 440.4Hz
        var a = Tone(254, 15);
        Measure(a.Generate, 4410, out _, out _);   // 직류 차단이 자리 잡을 때까지
        Measure(a.Generate, Rate, out double rms, out int cross);
        check("PSG tone A pitch ≈ 440.4 Hz", Math.Abs(cross / 2.0 - 440.4) < 2, (cross / 2.0).ToString("0.0") + " Hz");
        check("PSG tone is loud at volume 15", rms > 3000, rms.ToString("0"));
        check("PSG reports channel frequency", Math.Abs(a.ChannelFrequency(0) - 440.4) < 0.5 && a.ChannelFrequency(1) == 0, a.ChannelFrequency(0).ToString("0.0"));

        // 볼륨: 4비트 v = 표의 2v+1번 칸. YM2149: 15 → 13 = 0xFF → 0x97
        var lo = Tone(254, 13);
        Measure(lo.Generate, 4410, out _, out _);
        Measure(lo.Generate, Rate / 2, out double rmsLo, out _);
        double ratio = rmsLo / rms;
        check("PSG log volume 13/15 ≈ 0.59 (YM2149)", Math.Abs(ratio - 0x97 / 255.0) < 0.05, ratio.ToString("0.00"));

        // 믹서: 톤 · 노이즈 모두 끄면 (직류만) 소리 없음
        var off = Tone(254, 15);
        off.Write(7, 0x3F);
        Measure(off.Generate, 4410, out _, out _);
        Measure(off.Generate, Rate / 4, out double rmsOff, out _);
        check("PSG mixer off = silent", rmsOff < 30, rmsOff.ToString("0"));

        // 채널 끄기
        var mute = Tone(254, 15);
        mute.Mute[0] = true;
        Measure(mute.Generate, Rate / 4, out double rmsMute, out _);
        check("PSG channel mute", rmsMute < 30, rmsMute.ToString("0"));

        // 노이즈: 넓은 대역 → 같은 음량의 440Hz 톤보다 0 교차가 훨씬 많다
        var n = new PsgEmulator(PsgEmulator.MsxClock, Rate);
        n.Write(6, 1);
        n.Write(7, 0x37);          // A 노이즈만
        n.Write(8, 15);
        Measure(n.Generate, 4410, out _, out _);
        Measure(n.Generate, Rate, out double rmsN, out int crossN);
        check("PSG noise is broadband", rmsN > 2000 && crossN > 5000, "rms " + rmsN.ToString("0") + ", crossings " + crossN);

        // 엔벨로프 0x00 (\___): 한 번 내려가고 0. EP = 100 → 한 번 = 32 × 128 × 100 클럭 ≈ 0.23초
        var e = Tone(50, 0x10);
        e.Write(11, 100); e.Write(12, 0);
        e.Write(13, 0x00);
        Measure(e.Generate, Rate / 20, out double early, out _);
        Measure(e.Generate, Rate / 2, out _, out _);
        Measure(e.Generate, Rate / 10, out double late, out _);
        check("PSG envelope 0x0 decays to silence", early > 1500 && late < 50, early.ToString("0") + " → " + late.ToString("0"));

        // 엔벨로프 0x0E (/\/\): 되풀이 → 오래 지나도 소리
        var tri = Tone(50, 0x10);
        tri.Write(11, 100); tri.Write(12, 0);
        tri.Write(13, 0x0E);
        Measure(tri.Generate, Rate, out _, out _);
        Measure(tri.Generate, Rate / 2, out double triLate, out _);
        check("PSG envelope 0xE repeats", triLate > 1000, triLate.ToString("0"));

        // 엔벨로프 0x0D (/¯¯¯): 올라가 최대에서 멈춤
        var hold = Tone(50, 0x10);
        hold.Write(11, 100); hold.Write(12, 0);
        hold.Write(13, 0x0D);
        Measure(hold.Generate, Rate, out _, out _);
        Measure(hold.Generate, Rate / 4, out double holdRms, out _);
        check("PSG envelope 0xD holds at max", Math.Abs(holdRms - rms) < rms * 0.15, holdRms.ToString("0") + " vs " + rms.ToString("0"));

        // AY-3-8910 표: 15 → 13 = 0xFF → 0xA6 (YM2149보다 덜 줄어든다)
        var ay15 = Tone(254, 15, false);
        var ay13 = Tone(254, 13, false);
        Measure(ay15.Generate, 4410, out _, out _);
        Measure(ay13.Generate, 4410, out _, out _);
        Measure(ay15.Generate, Rate / 2, out double ayHi, out _);
        Measure(ay13.Generate, Rate / 2, out double ayLo, out _);
        check("AY-3-8910 volume 13/15 ≈ 0.65", Math.Abs(ayLo / ayHi - 0xA6 / 255.0) < 0.05, (ayLo / ayHi).ToString("0.00"));
    }

    // ------------------------------------------------------------ SCC

    private static void SineWave(SccEmulator s, int ch)
    {
        for (int i = 0; i < 32; i++) s.WriteScc(ch * 32 + i, (int)Math.Round(Math.Sin(i * 2 * Math.PI / 32) * 120) & 0xFF);
    }

    private static void Scc(Action<string, bool, string> check)
    {
        // N = 253 → 3579545 / (32 × 254) = 440.4Hz
        var s = new SccEmulator(SccEmulator.MsxClock, Rate);
        SineWave(s, 0);
        s.WriteScc(0x80, 253); s.WriteScc(0x81, 0);
        s.WriteScc(0x8A, 15);
        s.WriteScc(0x8F, 0x01);
        Measure(s.Generate, Rate, out double rms, out int cross);
        check("SCC ch1 pitch ≈ 440.4 Hz", Math.Abs(cross / 2.0 - 440.4) < 2, (cross / 2.0).ToString("0.0") + " Hz");
        check("SCC sine is loud at volume 15", rms > 2500, rms.ToString("0"));
        check("SCC reports channel frequency", Math.Abs(s.ChannelFrequency(0) - 440.4) < 0.5 && s.ChannelFrequency(1) == 0, s.ChannelFrequency(0).ToString("0.0"));

        // 볼륨은 선형: 15 → 5 = 1/3
        s.WriteScc(0x8A, 5);
        Measure(s.Generate, Rate / 2, out double rms5, out _);
        check("SCC linear volume 5/15 ≈ 0.33", Math.Abs(rms5 / rms - 1 / 3.0) < 0.03, (rms5 / rms).ToString("0.00"));

        // 켜짐 마스크 · 분주 8 이하
        s.WriteScc(0x8A, 15);
        s.WriteScc(0x8F, 0x00);
        Measure(s.Generate, Rate / 4, out double rmsOff, out _);
        check("SCC enable mask off = silent", rmsOff < 1, rmsOff.ToString("0"));
        s.WriteScc(0x8F, 0x01);
        s.WriteScc(0x80, 8);
        Measure(s.Generate, Rate / 4, out double rmsLow, out _);
        check("SCC period ≤ 8 is silent", rmsLow < 1, rmsLow.ToString("0"));

        // VGM 포트 쓰기 = 메모리 쓰기
        var a = new SccEmulator(SccEmulator.MsxClock, Rate);
        var b = new SccEmulator(SccEmulator.MsxClock, Rate);
        SineWave(a, 1);
        for (int i = 0; i < 32; i++) b.WritePort(0, 32 + i, a.WaveSample(1, i) & 0xFF);
        a.WriteScc(0x82, 120); a.WriteScc(0x83, 1); a.WriteScc(0x8B, 12); a.WriteScc(0x8F, 0x02);
        b.WritePort(1, 2, 120); b.WritePort(1, 3, 1); b.WritePort(2, 1, 12); b.WritePort(3, 0, 0x02);
        bool same = true;
        for (int i = 0; i < 4000 && same; i++) same = a.Generate() == b.Generate();
        check("SCC VGM port writes match memory writes", same, "");

        // SCC: 4번 파형을 쓰면 5번도 같은 파형, SCC+: 5번은 따로
        var scc = new SccEmulator(SccEmulator.MsxClock, Rate);
        scc.WriteScc(0x60, 0x40);
        var plus = new SccEmulator(SccEmulator.MsxClock, Rate, true);
        plus.WriteSccPlus(0x60, 0x40);
        plus.WriteSccPlus(0x80, 0x10);
        check("SCC shares wave 4/5, SCC+ keeps them apart",
            scc.WaveSample(4, 0) == 0x40 && plus.WaveSample(3, 0) == 0x40 && plus.WaveSample(4, 0) == 0x10, "");

        // 채널 끄기
        var m = new SccEmulator(SccEmulator.MsxClock, Rate);
        SineWave(m, 0);
        m.WriteScc(0x80, 253); m.WriteScc(0x8A, 15); m.WriteScc(0x8F, 1);
        m.Mute[0] = true;
        Measure(m.Generate, Rate / 4, out double rmsMute, out _);
        check("SCC channel mute", rmsMute < 1, rmsMute.ToString("0"));
    }
}
