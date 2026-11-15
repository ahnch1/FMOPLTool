using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// TI SN76489 / 세가 PSG: 사각파 3 + 노이즈 1, 감쇠 4비트(2dB 단계). 마스터 시스템 · 게임 기어 · 메가 드라이브 · BBC · SG-1000 · ColecoVision.
/// <para>칩 내부 속도 = 클럭 / 16. 노이즈 LFSR의 되먹임 모양 · 너비는 VGM 머리말 값 (세가 = 0x0009 / 16비트, TI = 0x0003 / 15비트).
/// 게임 기어 스테레오(0x4F)는 포트 1로 쓴다.</para>
/// </summary>
public sealed class Sn76489 : ISoundChip
{
    public const double DefaultClock = 3579545;
    private readonly double ticksPerSample;
    private readonly int feedback, width;
    private readonly int[] period = new int[4];
    private readonly int[] att = { 15, 15, 15, 15 };
    private readonly double[] count = new double[4];
    private readonly bool[] outBit = new bool[4];
    private readonly bool[] mute = new bool[4];
    private readonly double[] pitch = { 1, 1, 1, 1 };
    private int latchCh, latchType;
    private int lfsr;
    private int stereo = 0xFF;
    private double frac;
    private static readonly double[] Vol = BuildVol();
    private readonly double clock;

    /// <param name="feedback">노이즈 되먹임 탭 (0이면 세가 0x0009).</param>
    /// <param name="width">LFSR 너비 (0이면 16).</param>
    public Sn76489(double clock = DefaultClock, int rate = 44100, int feedback = 0, int width = 0)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate;
        this.feedback = feedback > 0 ? feedback : 0x0009;
        this.width = width > 0 ? width : 16;
        ticksPerSample = this.clock / 16.0 / rate;
        Reset();
    }

    private static double[] BuildVol()
    {
        var v = new double[16];
        for (int i = 0; i < 15; i++) v[i] = Math.Pow(10, -i * 2 / 20.0);
        v[15] = 0;
        return v;
    }

    public string Name { get { return "SN76489 (PSG)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 4; } }
    public string ChannelLabel(int ch) { return ch < 3 ? ((char)('A' + ch)).ToString() : "N"; }

    public void Reset()
    {
        for (int i = 0; i < 4; i++) { period[i] = 0; att[i] = 15; count[i] = 0; outBit[i] = false; }
        lfsr = 1 << (width - 1);
        latchCh = 0; latchType = 0; stereo = 0xFF; frac = 0;
    }

    /// <summary>port 0 = 데이터 바이트, port 1 = 게임 기어 스테레오.</summary>
    public void Write(int port, int r, int v)
    {
        v &= 0xFF;
        if (port == 1) { stereo = v; return; }
        if ((v & 0x80) != 0)
        {
            latchCh = (v >> 5) & 3; latchType = (v >> 4) & 1;
            if (latchType == 1) att[latchCh] = v & 15;
            else if (latchCh == 3) SetNoise(v & 7);
            else period[latchCh] = (period[latchCh] & 0x3F0) | (v & 15);
        }
        else
        {
            if (latchType == 1) att[latchCh] = v & 15;
            else if (latchCh == 3) SetNoise(v & 7);
            else period[latchCh] = (period[latchCh] & 0x00F) | ((v & 0x3F) << 4);
        }
    }

    private void SetNoise(int v) { period[3] = v; lfsr = 1 << (width - 1); }

    private double Period(int ch)
    {
        int p;
        if (ch < 3) p = period[ch];
        else { int n = period[3] & 3; p = n == 3 ? period[2] : 0x10 << n; }
        if (p == 0) p = 1;
        int pc = ch == 3 && (period[3] & 3) == 3 ? 2 : ch;
        return p / pitch[pc];
    }

    public void Generate(out int left, out int right)
    {
        frac += ticksPerSample;
        int n = (int)frac;
        frac -= n;
        double accL = 0, accR = 0;
        for (int k = 0; k < Math.Max(1, n); k++)
        {
            if (n > 0)
                for (int c = 0; c < 4; c++)
                {
                    count[c] -= 1;
                    if (count[c] <= 0)
                    {
                        count[c] += Period(c);
                        if (c < 3) outBit[c] = !outBit[c];
                        else
                        {
                            outBit[3] = !outBit[3];
                            if (outBit[3])   // 반주기 두 번에 한 칸
                            {
                                int fb = (period[3] & 4) != 0 ? Parity(lfsr & feedback) : lfsr & 1;
                                lfsr = (lfsr >> 1) | (fb << (width - 1));
                            }
                        }
                    }
                }
            for (int c = 0; c < 4; c++)
            {
                if (mute[c]) continue;
                bool hi = c < 3 ? (outBit[c] || period[c] <= 1) : (lfsr & 1) != 0;
                double s = (hi ? 1 : -1) * Vol[att[c]];
                if ((stereo & (0x10 << c)) != 0) accL += s;
                if ((stereo & (1 << c)) != 0) accR += s;
            }
        }
        int div = Math.Max(1, n);
        left = SoundChips.Clip(accL / div * 6000);
        right = SoundChips.Clip(accR / div * 6000);
    }

    private static int Parity(int x) { x ^= x >> 8; x ^= x >> 4; x ^= x >> 2; x ^= x >> 1; return x & 1; }

    public void SetMute(int ch, bool m) { if (ch >= 0 && ch < 4) mute[ch] = m; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 4) pitch[ch] = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int ch) { return ch >= 0 && ch < 3 && att[ch] < 15 && period[ch] > 1 ? clock / (32.0 * period[ch]) : 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < 4 ? (float)Vol[att[ch]] : 0; }
    public bool ChannelActive(int ch) { return ch == 3 && att[3] < 15; }
}
