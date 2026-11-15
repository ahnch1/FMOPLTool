using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 게임보이 DMG APU: 사각파 2 (1번은 주파수 쓸기) · 파형 메모리 1 (32칸 × 4비트) · 노이즈 1 (LFSR 15 / 7비트).
/// 길이 · 엔벨로프 · 쓸기는 프레임 순서기(512Hz)로, 채널마다 4비트 DAC → NR51 좌우 · NR50 음량. 출력은 칩처럼 한쪽(0~15)이라 DC를 걸러 낸다.
/// <para>포트: reg = 주소 − $FF10 (0x00 NR10 … 0x16 NR52, 0x20~0x2F 파형 RAM $FF30~). VGM 0xB3 aa dd 그대로, 클럭 4194304Hz (머리말 0x80).</para>
/// <para>출력 샘플 하나 동안의 칩 클럭을 따라가며 채널 값을 시간으로 평균 낸다 (높은 음의 겹침을 줄임). Pan Docs의 동작을 따르되
/// DMG 고유 버그(파형 RAM 깨짐 · 엔벨로프 '좀비' 모드 · 꺼짐 중 길이 쓰기)는 넣지 않았다.</para>
/// </summary>
public sealed class GbApu : ISoundChip
{
    public const double DefaultClock = 4194304;
    private static readonly int[][] Duty =
    {
        new[] { 0, 0, 0, 0, 0, 0, 0, 1 },
        new[] { 1, 0, 0, 0, 0, 0, 0, 1 },
        new[] { 1, 0, 0, 0, 0, 1, 1, 1 },
        new[] { 0, 1, 1, 1, 1, 1, 1, 0 },
    };
    private static readonly int[] NoiseDiv = { 8, 16, 32, 48, 64, 80, 96, 112 };
    // 읽을 때 1로 보이는 비트 (Pan Docs)
    private static readonly byte[] ReadMask =
    {
        0x80, 0x3F, 0x00, 0xFF, 0xBF, 0xFF, 0x3F, 0x00, 0xFF, 0xBF, 0x7F, 0xFF, 0x9F, 0xFF, 0xBF, 0xFF,
        0xFF, 0x00, 0x00, 0xBF, 0x00, 0x00, 0x70, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    private sealed class Ch
    {
        public bool On, Dac, LenOn;
        public int Len, Freq, Vol, EnvDir, EnvPeriod, EnvTimer, Duty, Step;
        public double Timer;
        public int Out;
        public double Pitch = 1;
    }

    private readonly Ch[] ch = { new Ch(), new Ch(), new Ch(), new Ch() };
    private readonly byte[] regs = new byte[0x30];
    private readonly byte[] wave = new byte[32];
    private readonly bool[] mute = new bool[4];
    private bool power;
    private int seqStep;
    private double seqTimer;
    // 쓸기 (1번)
    private int sweepPeriod, sweepShift, sweepTimer, shadow;
    private bool sweepNeg, sweepOn;
    // 노이즈
    private int lfsr = 0x7FFF;
    private readonly double cyclesPerSample;
    private readonly double clock;
    private double dcL, dcR;
    private readonly double[] level = new double[4];
    private readonly double[] sum = new double[4];

    public GbApu(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
        cyclesPerSample = this.clock / NativeRate;
        Reset();
    }

    public string Name { get { return "Game Boy DMG"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 4; } }
    public string ChannelLabel(int c) { return c == 0 ? "P1" : c == 1 ? "P2" : c == 2 ? "W" : "N"; }

    public void Reset()
    {
        Array.Clear(regs, 0, regs.Length);
        foreach (var c in ch) { c.On = c.Dac = c.LenOn = false; c.Len = c.Freq = c.Vol = c.EnvDir = c.EnvPeriod = c.EnvTimer = c.Duty = c.Step = 0; c.Timer = 0; c.Out = 0; }
        power = false; seqStep = 0; seqTimer = 0; lfsr = 0x7FFF;
        sweepOn = false; dcL = dcR = 0;
    }

    /// <summary>레지스터 읽기 (읽히지 않는 비트는 1, NR52 = 채널 켜짐).</summary>
    public int Read(int reg)
    {
        if (reg >= 0x20 && reg < 0x30) return regs[reg];
        if (reg < 0 || reg >= 0x20) return 0xFF;
        if (reg == 0x16) return (power ? 0x80 : 0) | 0x70 | (ch[0].On ? 1 : 0) | (ch[1].On ? 2 : 0) | (ch[2].On ? 4 : 0) | (ch[3].On ? 8 : 0);
        return regs[reg] | ReadMask[reg];
    }

    public void Write(int port, int reg, int v)
    {
        v &= 0xFF;
        if (reg >= 0x20 && reg < 0x30)
        {
            regs[reg] = (byte)v;
            wave[(reg - 0x20) * 2] = (byte)(v >> 4);
            wave[(reg - 0x20) * 2 + 1] = (byte)(v & 15);
            return;
        }
        if (reg < 0 || reg >= 0x20) return;
        if (reg == 0x16)
        {
            bool on = (v & 0x80) != 0;
            if (power && !on)
            {
                for (int r = 0; r < 0x16; r++) WriteReg(r, 0);
                foreach (var c in ch) c.On = false;
            }
            if (!power && on) { seqStep = 0; seqTimer = 0; }
            power = on;
            regs[0x16] = (byte)(v & 0x80);
            return;
        }
        if (!power) return;
        WriteReg(reg, v);
    }

    private void WriteReg(int reg, int v)
    {
        regs[reg] = (byte)v;
        int n = reg < 5 ? 0 : reg < 10 ? 1 : reg < 15 ? 2 : reg < 20 ? 3 : -1;
        if (n < 0) return;
        var c = ch[n];
        switch (reg - n * 5)
        {
            case 0:
                if (n == 0) { sweepPeriod = (v >> 4) & 7; sweepNeg = (v & 8) != 0; sweepShift = v & 7; }
                if (n == 2) { c.Dac = (v & 0x80) != 0; if (!c.Dac) c.On = false; }
                break;
            case 1:
                if (n == 2) c.Len = 256 - v;
                else { c.Len = 64 - (v & 63); if (n < 2) c.Duty = v >> 6; }
                break;
            case 2:
                if (n != 2)
                {
                    c.Dac = (v & 0xF8) != 0;
                    if (!c.Dac) c.On = false;
                }
                break;
            case 3:
                if (n < 3) c.Freq = (c.Freq & 0x700) | v;
                break;
            case 4:
                if (n < 3) c.Freq = (c.Freq & 0xFF) | ((v & 7) << 8);
                c.LenOn = (v & 0x40) != 0;
                if ((v & 0x80) != 0) Trigger(n);
                break;
        }
    }

    private void Trigger(int n)
    {
        var c = ch[n];
        c.On = c.Dac;
        if (c.Len == 0) c.Len = n == 2 ? 256 : 64;
        int env = regs[n * 5 + 2];
        if (n != 2) { c.Vol = env >> 4; c.EnvDir = (env & 8) != 0 ? 1 : -1; c.EnvPeriod = env & 7; c.EnvTimer = c.EnvPeriod == 0 ? 8 : c.EnvPeriod; }
        c.Timer = Period(n);
        if (n == 2) c.Step = 0;
        if (n == 3) lfsr = 0x7FFF;
        if (n == 0)
        {
            shadow = c.Freq;
            sweepTimer = sweepPeriod == 0 ? 8 : sweepPeriod;
            sweepOn = sweepPeriod != 0 || sweepShift != 0;
            if (sweepShift != 0) SweepCalc();
        }
    }

    /// <summary>채널 한 칸의 길이 (칩 클럭): 사각파 = (2048 − f) × 4, 파형 = × 2, 노이즈 = 나눔수 &lt;&lt; 자리.</summary>
    private double Period(int n)
    {
        if (n == 3)
        {
            int nr43 = regs[0x12];
            return (double)NoiseDiv[nr43 & 7] * (1 << ((nr43 >> 4) & 15));
        }
        return (2048 - ch[n].Freq) * (n == 2 ? 2 : 4);
    }

    private int SweepCalc()
    {
        int d = shadow >> sweepShift;
        int f = sweepNeg ? shadow - d : shadow + d;
        if (f > 2047) ch[0].On = false;
        return f;
    }

    private void Sequencer()
    {
        // 0 · 2 · 4 · 6 = 길이, 2 · 6 = 쓸기, 7 = 엔벨로프
        if ((seqStep & 1) == 0)
            foreach (var c in ch) if (c.LenOn && c.Len > 0 && --c.Len == 0) c.On = false;
        if (seqStep == 2 || seqStep == 6)
        {
            if (--sweepTimer <= 0)
            {
                sweepTimer = sweepPeriod == 0 ? 8 : sweepPeriod;
                if (sweepOn && sweepPeriod != 0)
                {
                    int f = SweepCalc();
                    if (f <= 2047 && sweepShift != 0) { shadow = f; ch[0].Freq = f; SweepCalc(); }
                }
            }
        }
        if (seqStep == 7)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == 2) continue;
                var c = ch[i];
                if (c.EnvPeriod == 0) continue;
                if (--c.EnvTimer <= 0)
                {
                    c.EnvTimer = c.EnvPeriod;
                    int nv = c.Vol + c.EnvDir;
                    if (nv >= 0 && nv <= 15) c.Vol = nv;
                }
            }
        }
        seqStep = (seqStep + 1) & 7;
    }

    /// <summary>채널 디지털 값 (0~15).</summary>
    private int Digital(int n)
    {
        var c = ch[n];
        if (!c.On) return 0;
        switch (n)
        {
            case 0: case 1: return Duty[c.Duty][c.Step] != 0 ? c.Vol : 0;
            case 2:
                {
                    int code = (regs[0x0C] >> 5) & 3;
                    return code == 0 ? 0 : wave[c.Step] >> (code - 1);
                }
            default: return (~lfsr & 1) != 0 ? c.Vol : 0;
        }
    }

    private void Advance(int n)
    {
        var c = ch[n];
        switch (n)
        {
            case 0: case 1: c.Step = (c.Step + 1) & 7; break;
            case 2: c.Step = (c.Step + 1) & 31; break;
            default:
                {
                    int x = (lfsr ^ (lfsr >> 1)) & 1;
                    lfsr = (lfsr >> 1) | (x << 14);
                    if ((regs[0x12] & 8) != 0) lfsr = (lfsr & ~0x40) | (x << 6);
                    break;
                }
        }
    }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        Array.Clear(sum, 0, 4);
        double remain = cyclesPerSample;
        while (remain > 0)
        {
            // 다음 사건까지: 프레임 순서기 또는 채널 타이머
            double dt = Math.Min(remain, 8192 - seqTimer);
            for (int n = 0; n < 4; n++)
            {
                var c = ch[n];
                if (!c.On) continue;
                double t = c.Timer / c.Pitch;
                if (t < dt) dt = Math.Max(t, 1e-9);
            }
            for (int n = 0; n < 4; n++) sum[n] += Digital(n) * dt;
            for (int n = 0; n < 4; n++)
            {
                var c = ch[n];
                if (!c.On) continue;
                c.Timer -= dt * c.Pitch;
                while (c.Timer <= 1e-9)
                {
                    double p = Period(n);
                    if (p <= 0) { c.Timer = 1e9; break; }
                    c.Timer += p;
                    Advance(n);
                }
            }
            seqTimer += dt;
            if (seqTimer >= 8192 - 1e-9) { seqTimer -= 8192; Sequencer(); }
            remain -= dt;
        }
        if (!power) { dcL *= 0.995; dcR *= 0.995; }
        int nr51 = regs[0x15], nr50 = regs[0x14];
        double l = 0, r = 0;
        for (int n = 0; n < 4; n++)
        {
            double v = sum[n] / cyclesPerSample;
            level[n] = Math.Max(level[n] * 0.995, ch[n].On ? v / 15.0 : 0);
            if (mute[n] || !ch[n].Dac) continue;
            if ((nr51 & (0x10 << n)) != 0) l += v;
            if ((nr51 & (1 << n)) != 0) r += v;
        }
        l *= ((nr50 >> 4) & 7) + 1;
        r *= (nr50 & 7) + 1;
        // 칩 출력은 한쪽 → 축전기처럼 DC를 걸러 낸다 (약 15Hz)
        dcL += (l - dcL) * 0.002;
        dcR += (r - dcR) * 0.002;
        left = SoundChips.Clip((l - dcL) * 36);
        right = SoundChips.Clip((r - dcR) * 36);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 4) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 4) ch[c].Pitch = ratio > 0 ? ratio : 1; }

    public double ChannelFrequency(int c)
    {
        if (c < 0 || c > 2 || !ch[c].On || !ch[c].Dac) return 0;
        if (c < 2 && ch[c].Vol == 0) return 0;
        if (c == 2 && ((regs[0x0C] >> 5) & 3) == 0) return 0;
        return clock / ((2048 - ch[c].Freq) * (c == 2 ? 64.0 : 32.0));
    }

    public float ChannelLevel(int c)
    {
        if (c < 0 || c >= 4 || !ch[c].On) return 0;
        if (c == 2) { int code = (regs[0x0C] >> 5) & 3; return code == 0 ? 0 : 1f / (1 << (code - 1)); }
        return ch[c].Vol / 15f;
    }

    public bool ChannelActive(int c) { return ChannelLevel(c) > 0; }
}
