using System;

namespace AdlibComposer.Audio;

/// <summary>
/// MSX PSG 에뮬레이터: General Instrument AY-3-8910 / Yamaha YM2149 (MSX 클럭 1.7897725MHz = 3.579545MHz / 2).
/// <para>톤 3개 (12비트 분주, 주파수 = 클럭 / (16 × TP)), 노이즈 1개 (5비트 주기, 17비트 LFSR), 믹서 R7 (톤 · 노이즈 켜기, 0 = 켬),
/// 볼륨 R8~R10 (4비트, bit4 = 엔벨로프 사용), 엔벨로프 1개 (R11 · R12 = 16비트 주기, R13 = 모양 16가지, R13을 쓰면 처음부터).</para>
/// <para>방식은 emu2149 (Mitsutaka Okazaki, MIT)를 따른다: 칩 내부 속도(클럭 / 8)로 한 걸음씩 돌리고 출력 샘플 하나에 든 걸음을 평균 (상자 필터),
/// 볼륨은 32단계 표 (YM2149 = 엔벨로프 32단계, AY-3-8910 = 16단계를 두 칸씩). 출력은 0 이상(한쪽)이므로 낮은 대역 차단(약 20Hz)으로 직류를 뺀다.</para>
/// </summary>
public sealed class PsgEmulator
{
    public const double MsxClock = 1789772.5;
    public const int Channels = 3;

    // emu2149의 볼륨 표 (0~255, 32단계)
    private static readonly int[] YmVolume =
    {
        0x00, 0x01, 0x01, 0x02, 0x02, 0x03, 0x03, 0x04, 0x05, 0x06, 0x07, 0x09, 0x0B, 0x0D, 0x0F, 0x12,
        0x16, 0x1A, 0x1F, 0x25, 0x2D, 0x35, 0x3F, 0x4C, 0x5A, 0x6A, 0x7F, 0x97, 0xB4, 0xD6, 0xEB, 0xFF,
    };
    private static readonly int[] AyVolume =
    {
        0x00, 0x00, 0x03, 0x03, 0x04, 0x04, 0x06, 0x06, 0x09, 0x09, 0x0D, 0x0D, 0x12, 0x12, 0x1D, 0x1D,
        0x22, 0x22, 0x37, 0x37, 0x4D, 0x4D, 0x62, 0x62, 0x82, 0x82, 0xA6, 0xA6, 0xD0, 0xD0, 0xFF, 0xFF,
    };

    private readonly byte[] reg = new byte[16];
    private readonly int[] toneCount = new int[Channels];
    private readonly bool[] toneOut = new bool[Channels];
    private int noiseCount, lfsr = 1;
    private bool noiseOut;
    private int envCount, envStep;
    private bool envHolding, envAttack, envAlternate, envHold, envContinue;
    private double ticksPerSample, tickFrac;
    private double dcIn, dcOut;
    private readonly float[] level = new float[Channels];

    /// <param name="clock">칩 클럭 (Hz, MSX = 1789772.5)</param>
    /// <param name="rate">출력 샘플레이트</param>
    /// <param name="ym2149">true = YM2149 (엔벨로프 32단계), false = AY-3-8910 (16단계)</param>
    public PsgEmulator(double clock = MsxClock, int rate = 44100, bool ym2149 = true)
    {
        Clock = clock;
        Rate = rate;
        IsYm2149 = ym2149;
        ticksPerSample = clock / 8.0 / rate;
        Reset();
    }

    public double Clock { get; }
    public int Rate { get; }
    public bool IsYm2149 { get; }
    /// <summary>채널 끄기 (A · B · C). 노이즈만 내는 채널도 그 채널 비트로 꺼진다.</summary>
    public bool[] Mute { get; } = new bool[Channels];

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        reg[7] = 0x3F;   // 모두 끔 (톤 · 노이즈 비트 = 1)
        for (int i = 0; i < Channels; i++) { toneCount[i] = 0; toneOut[i] = false; level[i] = 0; }
        noiseCount = 0; lfsr = 1; noiseOut = false;
        envCount = 0; envStep = 0; envHolding = false;
        SetShape(0);
        tickFrac = 0; dcIn = dcOut = 0;
    }

    public int Read(int r) { return reg[r & 15]; }

    /// <summary>레지스터 쓰기 (R0~R13, R14 · R15 = I/O 포트는 값만 남긴다).</summary>
    public void Write(int r, int value)
    {
        r &= 15;
        value &= 0xFF;
        // 쓸 수 있는 비트만 (AY: 쓰지 않는 비트는 0으로 읽힌다)
        switch (r)
        {
            case 1: case 3: case 5: value &= 0x0F; break;
            case 6: value &= 0x1F; break;
            case 8: case 9: case 10: value &= 0x1F; break;
            case 13: value &= 0x0F; break;
        }
        reg[r] = (byte)value;
        if (r == 13) { envCount = 0; envHolding = false; SetShape(value); }
    }

    /// <summary>R13 모양: bit3 CONT, bit2 ATT, bit1 ALT, bit0 HOLD. CONT = 0이면 한 번 뒤 0에 멈춤 (ATT면 그 뒤 0).</summary>
    private void SetShape(int shape)
    {
        envAttack = (shape & 4) != 0;
        envContinue = (shape & 8) != 0;
        envAlternate = (shape & 2) != 0;
        envHold = (shape & 1) != 0;
        if (!envContinue) { envHold = true; envAlternate = envAttack; }   // 0~7 = 9 · F (한 번 내려가거나 올라가고 0)
        envStep = 0;
    }

    private int TonePeriod(int ch) { int p = reg[ch * 2] | (reg[ch * 2 + 1] << 8); return p == 0 ? 1 : p; }
    private int NoisePeriod { get { int p = reg[6] & 0x1F; return p == 0 ? 1 : p; } }
    private int EnvPeriod { get { int p = reg[11] | (reg[12] << 8); return p == 0 ? 1 : p; } }

    /// <summary>엔벨로프의 지금 단계 (0~31).</summary>
    private int EnvLevel
    {
        get
        {
            int v = envAttack ? envStep : 31 - envStep;
            return v;
        }
    }

    /// <summary>내부 한 걸음 (클럭 / 8). 반환: 이 걸음의 출력 (0~255 × 3).</summary>
    private int Tick()
    {
        // 톤: 반주기 = 8 × TP 클럭 = TP 걸음
        for (int i = 0; i < Channels; i++)
            if (++toneCount[i] >= TonePeriod(i)) { toneCount[i] = 0; toneOut[i] = !toneOut[i]; }

        // 노이즈: 16 × NP 클럭마다 LFSR 한 칸 (17비트, 탭 0 · 3)
        if (++noiseCount >= NoisePeriod * 2)
        {
            noiseCount = 0;
            int bit = (lfsr ^ (lfsr >> 3)) & 1;
            lfsr = (lfsr >> 1) | (bit << 16);
            noiseOut = (lfsr & 1) != 0;
        }

        // 엔벨로프: 32단계, 한 단계 = 128 × EP 클럭 = 16 × EP 걸음 (AY는 16단계를 두 칸씩 → 같은 길이)
        if (!envHolding && ++envCount >= EnvPeriod * 16)
        {
            envCount = 0;
            if (++envStep > 31)
            {
                if (envHold)
                {
                    if (envAlternate) envAttack = !envAttack;
                    envStep = 31;
                    envHolding = true;
                }
                else
                {
                    if (envAlternate) envAttack = !envAttack;
                    envStep = 0;
                }
            }
        }

        int mix = reg[7];
        var vol = IsYm2149 ? YmVolume : AyVolume;
        int sum = 0;
        for (int i = 0; i < Channels; i++)
        {
            bool tone = toneOut[i] || (mix & (1 << i)) != 0;
            bool noise = noiseOut || (mix & (8 << i)) != 0;
            int a = reg[8 + i];
            int idx = (a & 0x10) != 0 ? EnvLevel : ((a & 0x0F) == 0 ? 0 : (a & 0x0F) * 2 + 1);
            if (!IsYm2149 && (a & 0x10) != 0) idx |= 1;   // AY 엔벨로프: 16단계
            int v = tone && noise && !Mute[i] ? vol[idx] : 0;
            sum += v;
        }
        return sum;
    }

    /// <summary>출력 샘플 1개 (모노, 직류 뺌). 크기는 세 채널 모두 최대일 때 약 ±24000.</summary>
    public short Generate()
    {
        tickFrac += ticksPerSample;
        int n = (int)tickFrac;
        tickFrac -= n;
        double acc = 0;
        for (int k = 0; k < n; k++) acc += Tick();
        double x = n > 0 ? acc / n : 0;      // 0 ~ 765
        // 채널 표시용 대략의 음량
        for (int i = 0; i < Channels; i++)
        {
            int a = reg[8 + i];
            bool on = (reg[7] & (1 << i)) == 0 || (reg[7] & (8 << i)) == 0;
            float target = !on || Mute[i] ? 0 : (a & 0x10) != 0 ? EnvLevel / 31f : (a & 0x0F) / 15f;
            level[i] = target;
        }
        // 직류 차단 (한 극 하이패스 약 20Hz)
        double y = x - dcIn + 0.997 * dcOut;
        dcIn = x; dcOut = y;
        double s = y * 31.0;
        return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s));
    }

    /// <summary>채널 톤 주파수 (Hz). 톤이 꺼졌거나 소리가 없으면 0.</summary>
    public double ChannelFrequency(int ch)
    {
        if ((reg[7] & (1 << ch)) != 0 || ChannelLevel(ch) <= 0) return 0;
        return Clock / (16.0 * TonePeriod(ch));
    }

    /// <summary>채널 음량 0~1 (볼륨 레지스터 또는 엔벨로프 단계, 톤 · 노이즈 모두 꺼졌으면 0).</summary>
    public float ChannelLevel(int ch) { return level[ch]; }

    /// <summary>노이즈가 켜진 채널이 있으면 그 주기 (5비트), 없으면 -1.</summary>
    public int NoiseOn(int ch) { return (reg[7] & (8 << ch)) == 0 ? reg[6] & 0x1F : -1; }
}
