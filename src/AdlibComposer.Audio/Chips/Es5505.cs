using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Ensoniq ES5505 (OTIS): 16비트 PCM 목소리 32개, 4극 필터, 좌우 8비트 음량 (지수 4 + 가수 4), 고정 소수 주소 (정수 20비트 + 소수 9비트), 한쪽 · 양쪽 되풀이.
/// MAME es5506.cpp의 ES5505 부분을 따른다 (엔벨로프 없음, u-law 없음).
/// <para>호스트 쪽: 레지스터 16개 (16비트), 15 = PAGE (00h~1Fh 목소리 레지스터 · 20h~3Fh 필터 상태 · 40h~ 시험), 13 = ACT (돌리는 목소리 수 − 1, 표본율 = 클럭 / (16 × (ACT + 1))).
/// 목소리 레지스터: 0 CR (bit0 · 1 멈춤, bit3 LPE · bit4 BLE 되풀이, bit5 IRQE · bit6 DIR · bit7 IRQ, bit8~9 CA 출력, bit10~11 LP 필터), 1 FC (주파수), 2 · 3 STRT, 4 · 5 END,
/// 6 K2 · 7 K1 (필터), 8 LVOL · 9 RVOL (위 바이트), 10 · 11 ACC (지금 주소), 14 IRQV.</para>
/// <para>표본 ROM = 16비트 낱말 (부호 있음); 목소리마다 바깥 뱅크 (<see cref="VoiceBank"/>, 낱말 단위 더하기 — 타이토 F3는 2MB씩).</para>
/// </summary>
public sealed class Es5505
{
    private const int FracBit = 11, AccShift = 2;   // MAME: ADDRESS_FRAC_BIT 11, ES5505 소수 9비트 → 2만큼 왼쪽
    private const long AccMask = (1L << 31) - 1;     // 정수 20 + 소수 11
    private const int FilterShift = 4, FilterBit = 12;

    private sealed class Voice
    {
        public int Control = 0xF003, Lvol = 0x80, Rvol = 0x80, K1, K2;
        public long Freq, Start, End, Accum;
        public int O1n1, O2n1, O2n2, O3n1, O3n2, O4n1;
    }

    private readonly Voice[] voices = new Voice[32];
    private readonly int[] volumeLookup = new int[256];
    private int page, active = 31, irqv = 0x80;
    public readonly double Clock;
    /// <summary>표본 ROM (16비트 낱말).</summary>
    public short[] Rom = new short[0];
    /// <summary>목소리마다 ROM 낱말 오프셋 (null = 0).</summary>
    public Func<int, int> VoiceBank;
    public readonly bool[] Muted = new bool[32];

    public Es5505(double clock)
    {
        Clock = clock;
        for (int i = 0; i < 32; i++) voices[i] = new Voice();
        // compute_tables(8, 4, 4): (가수 | 16) << 11 >> (16 − 지수)
        for (int i = 0; i < 256; i++)
        {
            int exponent = (i >> 4) & 15, mantissa = (i & 15) | 16;
            volumeLookup[i] = (mantissa << 11) >> (16 - exponent);
        }
    }

    public double SampleRate { get { return Clock / (16.0 * (active + 1)); } }
    public int ActiveVoices { get { return active + 1; } }

    private static long Shifted(long v, int bias = 0) { return v << (AccShift - bias); }
    private static long Res(long v, int bias = 0) { return v >> (AccShift - bias); }

    public int Read(int reg)
    {
        reg &= 15;
        var v = voices[page & 0x1F];
        if (page < 0x20)
        {
            switch (reg)
            {
                case 0: return v.Control | 0xF000;
                case 1: return (int)(Res(v.Freq, 1) & 0xFFFF);
                case 2: return (int)((Res(v.Start) >> 16) & 0xFFFF);
                case 3: return (int)(Res(v.Start) & 0xFFFF);
                case 4: return (int)((Res(v.End) >> 16) & 0xFFFF);
                case 5: return (int)(Res(v.End) & 0xFFFF);
                case 6: return v.K2;
                case 7: return v.K1;
                case 8: return v.Lvol << 8;
                case 9: return v.Rvol << 8;
                case 10: return (int)((Res(v.Accum) >> 16) & 0xFFFF);
                case 11: return (int)(Res(v.Accum) & 0xFFFF);
                case 13: return active;
                case 14: { int r = irqv; irqv = 0x80; return r; }
                case 15: return page;
            }
            return 0;
        }
        if (page < 0x40)
        {
            switch (reg)
            {
                case 0: return v.Control | 0xF000;
                case 1: return v.O4n1 & 0xFFFF;
                case 2: return v.O3n1 & 0xFFFF;
                case 3: return v.O3n2 & 0xFFFF;
                case 4: return v.O2n1 & 0xFFFF;
                case 5: return v.O2n2 & 0xFFFF;
                case 6: return v.O1n1 & 0xFFFF;
                case 13: return active;
                case 14: { int r = irqv; irqv = 0x80; return r; }
                case 15: return page;
            }
            return 0;
        }
        switch (reg)
        {
            case 13: return active;
            case 14: { int r = irqv; irqv = 0x80; return r; }
            case 15: return page;
        }
        return 0;
    }

    /// <summary>호스트 쓰기 (16비트, mask = 쓰는 바이트: 1 아래 · 2 위 · 3 둘).</summary>
    public void Write(int reg, int data, int mask = 3)
    {
        reg &= 15; data &= 0xFFFF;
        bool lo = (mask & 1) != 0, hi = (mask & 2) != 0;
        var v = voices[page & 0x1F];
        if (reg == 13) { if (lo) active = data & 0x1F; return; }
        if (reg == 15) { if (lo) page = data & 0x7F; return; }
        if (page < 0x20)
        {
            switch (reg)
            {
                case 0:
                    v.Control |= 0xF000;
                    if (lo) v.Control = (v.Control & ~0x00FF) | (data & 0x00FF);
                    if (hi) v.Control = (v.Control & ~0x0F00) | (data & 0x0F00);
                    return;
                case 1:
                    if (lo) v.Freq = (v.Freq & ~Shifted(0x00FE, 1)) | Shifted(data & 0x00FE, 1);
                    if (hi) v.Freq = (v.Freq & ~Shifted(0xFF00, 1)) | Shifted(data & 0xFF00, 1);
                    return;
                case 2: v.Start = SetHi(v.Start, data, lo, hi); return;
                case 3: v.Start = SetLo(v.Start, data, lo, hi, 0xE0); return;
                case 4: v.End = SetHi(v.End, data, lo, hi); return;
                case 5: v.End = SetLo(v.End, data, lo, hi, 0xE0); return;
                case 6: if (lo) v.K2 = (v.K2 & ~0x00F0) | (data & 0x00F0); if (hi) v.K2 = (v.K2 & ~0xFF00) | (data & 0xFF00); return;
                case 7: if (lo) v.K1 = (v.K1 & ~0x00F0) | (data & 0x00F0); if (hi) v.K1 = (v.K1 & ~0xFF00) | (data & 0xFF00); return;
                case 8: if (hi) v.Lvol = (data >> 8) & 0xFF; return;
                case 9: if (hi) v.Rvol = (data >> 8) & 0xFF; return;
                case 10: v.Accum = SetHi(v.Accum, data, lo, hi); return;
                case 11: v.Accum = SetLo(v.Accum, data, lo, hi, 0xFF); return;
            }
            return;
        }
        if (page < 0x40)
        {
            switch (reg)
            {
                case 0:
                    v.Control |= 0xF000;
                    if (lo) v.Control = (v.Control & ~0x00FF) | (data & 0x00FF);
                    if (hi) v.Control = (v.Control & ~0x0F00) | (data & 0x0F00);
                    return;
                case 1: v.O4n1 = SetPole(v.O4n1, data, lo, hi); return;
                case 2: v.O3n1 = SetPole(v.O3n1, data, lo, hi); return;
                case 3: v.O3n2 = SetPole(v.O3n2, data, lo, hi); return;
                case 4: v.O2n1 = SetPole(v.O2n1, data, lo, hi); return;
                case 5: v.O2n2 = SetPole(v.O2n2, data, lo, hi); return;
                case 6: v.O1n1 = SetPole(v.O1n1, data, lo, hi); return;
            }
        }
    }

    private static long SetHi(long acc, int data, bool lo, bool hi)
    {
        if (lo) acc = (acc & ~Shifted(0x00FF0000L)) | Shifted((long)(data & 0x00FF) << 16);
        if (hi) acc = (acc & ~Shifted(0x1F000000L)) | Shifted((long)(data & 0x1F00) << 16);
        return acc;
    }

    private static long SetLo(long acc, int data, bool lo, bool hi, int lowMask)
    {
        if (lo) acc = (acc & ~Shifted(lowMask)) | Shifted(data & lowMask);
        if (hi) acc = (acc & ~Shifted(0xFF00)) | Shifted(data & 0xFF00);
        return acc;
    }

    private static int SetPole(int p, int data, bool lo, bool hi)
    {
        if (lo) p = (p & ~0x00FF) | (data & 0x00FF);
        if (hi) p = (short)((p & ~0xFF00) | (data & 0xFF00));
        return p;
    }

    private int ReadSample(int voice, long addr)
    {
        long a = addr + (VoiceBank != null ? VoiceBank(voice) : 0);
        return a >= 0 && a < Rom.Length ? Rom[a] : 0;
    }

    private static int Lowpass(int sample, int cutoff, int prev) { return (int)((long)(cutoff >> FilterShift) * (sample - prev) / (1 << FilterBit)) + prev; }
    private static int Highpass(int sample, int cutoff, int prev, int prev2) { return sample - prev2 + (int)((long)(cutoff >> FilterShift) * prev / (1 << (FilterBit + 1))) + prev / 2; }

    private static void Filters(Voice v, ref int s)
    {
        s = Lowpass(s, v.K1, v.O1n1); v.O1n1 = s;
        s = Lowpass(s, v.K1, v.O2n1); v.O2n2 = v.O2n1; v.O2n1 = s;
        switch ((v.Control >> 10) & 3)
        {
            case 0:
                s = Highpass(s, v.K2, v.O3n1, v.O2n2); v.O3n2 = v.O3n1; v.O3n1 = s;
                s = Highpass(s, v.K2, v.O4n1, v.O3n2); v.O4n1 = s;
                break;
            case 1:   // LP3
                s = Lowpass(s, v.K1, v.O3n1); v.O3n2 = v.O3n1; v.O3n1 = s;
                s = Highpass(s, v.K2, v.O4n1, v.O3n2); v.O4n1 = s;
                break;
            case 2:   // LP4
                s = Lowpass(s, v.K2, v.O3n1); v.O3n2 = v.O3n1; v.O3n1 = s;
                s = Lowpass(s, v.K2, v.O4n1); v.O4n1 = s;
                break;
            default:
                s = Lowpass(s, v.K1, v.O3n1); v.O3n2 = v.O3n1; v.O3n1 = s;
                s = Lowpass(s, v.K2, v.O4n1); v.O4n1 = s;
                break;
        }
    }

    /// <summary>칩 표본 하나 (칩 표본율 <see cref="SampleRate"/>): 좌우 합 (20비트 범위로 자름).</summary>
    public void Step(out int left, out int right)
    {
        long l = 0, r = 0;
        for (int n = 0; n <= active; n++)
        {
            var v = voices[n];
            if ((v.Control & 3) != 0) continue;
            long acc = v.Accum & AccMask;
            int s1 = ReadSample(n, (acc & AccMask) >> FracBit);
            int s2 = ReadSample(n, ((acc + (1L << FracBit)) & AccMask) >> FracBit);
            int frac = (int)(acc & ((1 << FracBit) - 1));
            int s = (int)(((long)s1 * ((1 << FracBit) - frac) + (long)s2 * frac) >> FracBit);
            bool rev = (v.Control & 0x40) != 0;
            acc = rev ? (acc - v.Freq) & AccMask : (acc + v.Freq) & AccMask;
            Filters(v, ref s);
            if (!Muted[n])
            {
                l += ((long)s * volumeLookup[v.Lvol & 0xFF]) >> 11;
                r += ((long)s * volumeLookup[v.Rvol & 0xFF]) >> 11;
            }
            if (!rev) EndForward(v, ref acc); else EndReverse(v, ref acc);
            v.Accum = acc;
            if ((v.Control & 0x80) != 0 && (irqv & 0x80) != 0) { irqv = n; v.Control &= ~0x80; }
        }
        left = (int)Math.Max(-(1 << 19), Math.Min((1 << 19) - 1, l));
        right = (int)Math.Max(-(1 << 19), Math.Min((1 << 19) - 1, r));
    }

    private static void EndForward(Voice v, ref long acc)
    {
        if (acc <= v.End) return;
        if ((v.Control & 0x20) != 0) v.Control |= 0x80;
        switch (v.Control & 0x18)
        {
            case 0: case 0x10: v.Control |= 1; break;
            case 0x08: acc = (v.Start + (acc - v.End)) & AccMask; break;
            default: acc = (v.End - (acc - v.End)) & AccMask; v.Control ^= 0x40; break;
        }
    }

    private static void EndReverse(Voice v, ref long acc)
    {
        if (acc >= v.Start) return;
        if ((v.Control & 0x20) != 0) v.Control |= 0x80;
        switch (v.Control & 0x18)
        {
            case 0: case 0x10: v.Control |= 1; break;
            case 0x08: acc = (v.End - (v.Start - acc)) & AccMask; break;
            default: acc = (v.Start + (v.Start - acc)) & AccMask; v.Control ^= 0x40; break;
        }
    }

    /// <summary>목소리 n이 울리는 중 (멈춤 비트 없음, 음량 0 아님).</summary>
    public bool VoiceActive(int n) { var v = voices[n & 31]; return (v.Control & 3) == 0 && n <= active && (v.Lvol | v.Rvol) != 0; }
    public int VoiceVolume(int n) { var v = voices[n & 31]; return Math.Max(v.Lvol, v.Rvol); }
    public double VoiceFrequency(int n) { var v = voices[n & 31]; return v.Freq / (double)(1 << FracBit) * SampleRate; }
}
