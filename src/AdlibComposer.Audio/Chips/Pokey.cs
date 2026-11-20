using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Atari POKEY (C012294): 4채널 분주기 + 다항식 (poly 4 · 5 · 9 · 17) 일그러짐. 아타리 8비트 컴퓨터 · 아케이드 (System 1 · Gauntlet …).
/// <para>레지스터: 0 · 2 · 4 · 6 = AUDF1~4 (분주), 1 · 3 · 5 · 7 = AUDC1~4 (bit0~3 음량, bit4 음량만, bit5 = 순음, bit6 = poly4 (아니면 poly17/9), bit7 = poly5 안 거침),
/// 8 = AUDCTL (bit0 = 기본 클럭 15kHz (아니면 64kHz), bit1 · 2 = 채널 2 · 1 고역 필터 (4 · 3이 클럭), bit3 = 3+4 16비트, bit4 = 1+2 16비트, bit5 · 6 = 채널 3 · 1을 1.79MHz로, bit7 = poly9),
/// 9 = STIMER (분주기 다시 채움), F = SKCTL (bit0~1 = 0이면 멈춤 · 다항식 리셋).</para>
/// <para>분주기가 0을 지나면 출력이 바뀐다: poly5를 거치면 (bit7 = 0) poly5 비트가 1일 때만, 순음은 뒤집기, 아니면 poly4 / poly17(9) 비트. 다시 채우는 값 = AUDF + 1 (기본 클럭), AUDF + 4 (1.79MHz), 16비트 + 7.
/// 사이클마다 돌리고 출력은 한 샘플 동안의 평균. VGM 0xBB aa dd (aa bit7 = 둘째 칩). MAME pokey를 따른다.</para>
/// </summary>
public sealed class Pokey : ISoundChip
{
    public const double DefaultClock = 1789772;

    private static readonly byte[] Poly4 = Lfsr(4, 3), Poly5 = Lfsr(5, 3), Poly9 = Lfsr(9, 5), Poly17 = Lfsr(17, 12);

    /// <summary>최대 길이 LFSR (x^n + x^tap + 1)의 출력 비트를 한 바퀴 (2^n − 1).</summary>
    private static byte[] Lfsr(int n, int tap)
    {
        int len = (1 << n) - 1;
        var t = new byte[len];
        int x = 1;
        for (int i = 0; i < len; i++)
        {
            t[i] = (byte)(x & 1);
            int bit = ((x >> (n - 1)) ^ (x >> (tap - 1))) & 1;
            x = ((x << 1) | bit) & len;
        }
        return t;
    }

    private readonly int[] audf = new int[4], audc = new int[4], counter = new int[4];
    private readonly int[] output = new int[4];
    private readonly bool[] mute = new bool[4];
    private readonly double[] pitch = { 1, 1, 1, 1 };
    private int audctl, skctl;
    private int hp1, hp2;              // 고역 필터 플립플롭 (채널 1 ← 3, 2 ← 4)
    private int p4, p5, p9, p17;       // 다항식 자리 (사이클마다 하나씩)
    private int baseDiv = 28;
    private int level;                 // 지금 출력 (음량 합)
    private readonly double clock;
    private readonly double cyclesPerSample;
    private double cycleAcc;

    public Pokey(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
        cyclesPerSample = this.clock / NativeRate;
        Reset();
    }

    public string Name { get { return "Atari POKEY"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 4; } }
    public string ChannelLabel(int c) { return "P" + (c + 1); }

    public void Reset()
    {
        for (int i = 0; i < 4; i++) { audf[i] = audc[i] = 0; counter[i] = 1; output[i] = 0; }
        audctl = 0; skctl = 3; hp1 = hp2 = 0;
        p4 = p5 = p9 = p17 = 0;
        baseDiv = 28; level = 0; cycleAcc = 0;
    }

    private bool Fast(int c) { return (c == 0 && (audctl & 0x40) != 0) || (c == 2 && (audctl & 0x20) != 0); }

    /// <summary>분주기를 다시 채울 값 (그 채널의 클럭 단위). 16비트로 묶이면 위 채널 (2 · 4)이 둘을 합친 값을 쓴다.</summary>
    private int Reload(int c)
    {
        bool join = (c == 1 && (audctl & 0x10) != 0) || (c == 3 && (audctl & 0x08) != 0);
        if (join)
        {
            int f = audf[c - 1] | (audf[c] << 8);
            return Fast(c - 1) ? f + 7 : f + 1;
        }
        return Fast(c) ? audf[c] + 4 : audf[c] + 1;
    }

    public void Write(int port, int reg, int v)
    {
        reg &= 0x0F; v &= 0xFF;
        switch (reg)
        {
            case 0: case 2: case 4: case 6: audf[reg >> 1] = v; break;
            case 1: case 3: case 5: case 7: audc[reg >> 1] = v; break;
            case 8: audctl = v; break;
            case 9: for (int c = 0; c < 4; c++) counter[c] = Reload(c); break;   // STIMER
            case 0x0F:
                skctl = v;
                if ((v & 3) == 0) { p4 = p5 = p9 = p17 = 0; }
                break;
        }
        Level();
    }

    private void Level()
    {
        int s = 0;
        for (int c = 0; c < 4; c++)
        {
            if (mute[c]) continue;
            int vol = audc[c] & 15;
            if (vol == 0) continue;
            if ((audc[c] & 0x10) != 0) { s += vol; continue; }   // 음량만 (DAC처럼)
            int o = output[c];
            if (c == 0 && (audctl & 0x04) != 0) o ^= hp1;
            if (c == 1 && (audctl & 0x02) != 0) o ^= hp2;
            if (o != 0) s += vol;
        }
        level = s;
    }

    private void Fire(int c)
    {
        int ac = audc[c];
        if ((ac & 0x80) != 0 || Poly5[p5] != 0)
        {
            if ((ac & 0x20) != 0) output[c] ^= 1;
            else if ((ac & 0x40) != 0) output[c] = Poly4[p4];
            else output[c] = (audctl & 0x80) != 0 ? Poly9[p9] : Poly17[p17];
        }
        if (c == 2 && (audctl & 0x04) != 0) hp1 = output[0];
        if (c == 3 && (audctl & 0x02) != 0) hp2 = output[1];
    }

    /// <summary>채널 하나를 한 클럭 센다. 0이 되면 다시 채우고 출력을 바꾼다.</summary>
    private bool Count(int c)
    {
        if (--counter[c] > 0) return false;
        counter[c] = Reload(c);
        return true;
    }

    private void Cycle()
    {
        if ((skctl & 3) == 0) return;   // 리셋 중: 멈춤
        if (++p4 == Poly4.Length) p4 = 0;
        if (++p5 == Poly5.Length) p5 = 0;
        if (++p9 == Poly9.Length) p9 = 0;
        if (++p17 == Poly17.Length) p17 = 0;
        bool tick = --baseDiv == 0;
        if (tick) baseDiv = (audctl & 1) != 0 ? 114 : 28;
        bool changed = false;
        // 채널 1 · 2 (16비트로 묶이면 1은 2의 아래 바이트: 2만 센다)
        if ((audctl & 0x10) != 0)
        {
            if ((Fast(0) || tick) && Count(1)) { Fire(1); changed = true; }
        }
        else
        {
            if ((Fast(0) || tick) && Count(0)) { Fire(0); changed = true; }
            if (tick && Count(1)) { Fire(1); changed = true; }
        }
        if ((audctl & 0x08) != 0)
        {
            if ((Fast(2) || tick) && Count(3)) { Fire(3); changed = true; }
        }
        else
        {
            if ((Fast(2) || tick) && Count(2)) { Fire(2); changed = true; }
            if (tick && Count(3)) { Fire(3); changed = true; }
        }
        if (changed) Level();
    }

    public void Generate(out int left, out int right)
    {
        cycleAcc += cyclesPerSample;
        int n = (int)cycleAcc;
        cycleAcc -= n;
        long sum = 0;
        for (int i = 0; i < n; i++) { Cycle(); sum += level; }
        double avg = n > 0 ? (double)sum / n : level;
        // 네 채널 최대 60 → 16비트의 반쯤 (다른 칩과 섞을 자리)
        left = right = SoundChips.Clip(avg * 280.0);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 4) { mute[c] = m; Level(); } }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 4) pitch[c] = ratio > 0 ? ratio : 1; }   // 분주기 칩: 음높이 바꾸기는 하지 않음 (표시만)

    public double ChannelFrequency(int c)
    {
        if (c < 0 || c >= 4 || (audc[c] & 15) == 0 || (audc[c] & 0x10) != 0 || (audc[c] & 0xA0) != 0xA0) return 0;   // 순음 · poly5 안 거침만 음높이
        if ((c == 0 && (audctl & 0x10) != 0) || (c == 2 && (audctl & 0x08) != 0)) return 0;
        double unit = Fast(c) || ((c == 1 || c == 3) && Fast(c - 1) && (audctl & (c == 1 ? 0x10 : 0x08)) != 0) ? clock : clock / ((audctl & 1) != 0 ? 114 : 28);
        return unit / Reload(c) / 2;
    }

    public float ChannelLevel(int c)
    {
        if (c < 0 || c >= 4) return 0;
        return (audc[c] & 15) / 15f;
    }

    public bool ChannelActive(int c) { return c >= 0 && c < 4 && (audc[c] & 15) != 0 && (audc[c] & 0x10) == 0; }
}
