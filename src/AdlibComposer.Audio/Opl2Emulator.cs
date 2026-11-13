using System;

namespace AdlibComposer.Audio;

/// <summary>
/// YM3812(OPL2) 에뮬레이터 (간이형). 9채널 2-오퍼레이터 FM + 리듬 모드(BD/SD/TOM/CYM/HH),
/// 웨이브폼 4종, 트레몰로/비브라토 LFO, KSL/KSR, 엔벨로프(ADSR)를 구현한다.
/// 엔벨로프 타이밍은 데이터시트의 시간 곡선을 수식으로 근사한 값이라 실제 칩과 완전히 같지는 않다.
/// 정밀도가 필요하면 IOplChip를 구현하는 Nuked-OPL3 포팅으로 교체한다.
/// </summary>
public sealed class Opl2Emulator : IOplChip
{
    public const int Rate = 49716;
    public int NativeRate { get { return Rate; } }
    /// <summary>출력 이득 (채널 합산 후 곱함).</summary>
    public double MasterGain { get; set; } = 1.0;

    private const int StateOff = 0, StateAttack = 1, StateDecay = 2, StateSustain = 3, StateRelease = 4;

    private static readonly int[] LogSin = new int[256];
    private static readonly int[] ExpTab = new int[256];
    private static readonly int[] KslRom = { 0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64 };
    private static readonly int[] KslShift = { 8, 1, 2, 0 };
    private static readonly int[] MultX2 = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly double[] DecayStep = new double[64];
    private static readonly double[] AttackMul = new double[64];
    private static readonly int[] SlotOfOffset = new int[0x20];

    static Opl2Emulator()
    {
        for (int i = 0; i < 256; i++)
        {
            LogSin[i] = (int)Math.Round(-Math.Log(Math.Sin((i + 0.5) * Math.PI / 512.0), 2.0) * 256.0);
            ExpTab[i] = (int)Math.Round(4095.0 * Math.Pow(2.0, -i / 256.0));
        }
        for (int r = 0; r < 64; r++)
        {
            if (r < 4) { DecayStep[r] = 0; AttackMul[r] = 1; continue; }
            double scale = Math.Pow(2.0, -(r - 4) / 4.0);
            double decaySamples = 39280.64 * scale * Rate / 1000.0;
            DecayStep[r] = 512.0 / Math.Max(1.0, decaySamples);
            double attackSamples = 2826.24 * scale * Rate / 1000.0;
            AttackMul[r] = r >= 60 ? 0.0 : Math.Exp(Math.Log(0.5 / 511.0) / Math.Max(1.0, attackSamples));
        }
        for (int i = 0; i < SlotOfOffset.Length; i++) SlotOfOffset[i] = -1;
        for (int g = 0; g < 3; g++)
            for (int k = 0; k < 6; k++) SlotOfOffset[g * 8 + k] = g * 6 + k;
    }

    private sealed class Slot
    {
        public int Mult, Ksl, Tl, Ar, Dr, Sl, Rr, WfReg;
        public bool Am, Vib, Egt, Ksr;
        public uint Phase;
        public int State;
        public double Att = 511.0;
        public bool KeyNorm, KeyDrum;
        public int Prev0, Prev1;
        public int Channel;
        public bool IsCarrier;
        public int PhaseOut;
    }

    private sealed class Channel
    {
        public int Fnum, Block, Fb, Cnt;
        public bool Kon;
        public Slot Mod, Car;
    }

    private readonly Slot[] slots = new Slot[18];
    private readonly Channel[] chans = new Channel[9];
    private bool wse, nts, deepTrem, deepVib, rhythm;
    private int drumBits;
    private int timer;
    private int noise = 1;

    public Opl2Emulator()
    {
        for (int c = 0; c < 9; c++) chans[c] = new Channel();
        for (int s = 0; s < 18; s++)
        {
            int ch = (s / 6) * 3 + (s % 3);
            bool car = (s % 6) >= 3;
            slots[s] = new Slot { Channel = ch, IsCarrier = car };
            if (car) chans[ch].Car = slots[s]; else chans[ch].Mod = slots[s];
        }
        Reset();
    }

    public void Reset()
    {
        foreach (var s in slots)
        {
            s.Mult = s.Ksl = s.Tl = s.Ar = s.Dr = s.Sl = s.Rr = s.WfReg = 0;
            s.Am = s.Vib = s.Egt = s.Ksr = false;
            s.Phase = 0; s.State = StateOff; s.Att = 511.0;
            s.KeyNorm = s.KeyDrum = false; s.Prev0 = s.Prev1 = 0; s.PhaseOut = 0;
        }
        foreach (var c in chans) { c.Fnum = c.Block = c.Fb = c.Cnt = 0; c.Kon = false; }
        wse = nts = deepTrem = deepVib = rhythm = false;
        drumBits = 0; timer = 0; noise = 1;
    }

    // ---------------------------------------------------------------- 레지스터

    public void Write(int reg, int value)
    {
        reg &= 0xFF; value &= 0xFF;
        if (reg == 0x01) { wse = (value & 0x20) != 0; return; }
        if (reg == 0x08) { nts = (value & 0x40) != 0; return; }
        if (reg == 0xBD) { WriteRhythm(value); return; }

        if (reg >= 0x20 && reg <= 0x95)
        {
            int group = reg & 0xE0;
            int off = reg & 0x1F;
            if (off < SlotOfOffset.Length && SlotOfOffset[off] >= 0 && off <= 0x15)
            {
                var s = slots[SlotOfOffset[off]];
                switch (group)
                {
                    case 0x20:
                        s.Am = (value & 0x80) != 0; s.Vib = (value & 0x40) != 0;
                        s.Egt = (value & 0x20) != 0; s.Ksr = (value & 0x10) != 0; s.Mult = value & 0x0F;
                        break;
                    case 0x40: s.Ksl = value >> 6; s.Tl = value & 0x3F; break;
                    case 0x60: s.Ar = value >> 4; s.Dr = value & 0x0F; break;
                    case 0x80: s.Sl = value >> 4; s.Rr = value & 0x0F; break;
                }
            }
            return;
        }
        if (reg >= 0xE0 && reg <= 0xF5)
        {
            int off = reg - 0xE0;
            if (off < 0x16 && SlotOfOffset[off] >= 0) slots[SlotOfOffset[off]].WfReg = value & 3;
            return;
        }
        if (reg >= 0xA0 && reg <= 0xA8) { chans[reg - 0xA0].Fnum = (chans[reg - 0xA0].Fnum & 0x300) | value; return; }
        if (reg >= 0xB0 && reg <= 0xB8)
        {
            var c = chans[reg - 0xB0];
            c.Fnum = (c.Fnum & 0xFF) | ((value & 3) << 8);
            c.Block = (value >> 2) & 7;
            bool kon = (value & 0x20) != 0;
            if (kon != c.Kon)
            {
                c.Kon = kon;
                if (kon) { KeyOn(c.Mod, false); KeyOn(c.Car, false); }
                else { KeyOff(c.Mod, false); KeyOff(c.Car, false); }
            }
            return;
        }
        if (reg >= 0xC0 && reg <= 0xC8)
        {
            var c = chans[reg - 0xC0];
            c.Fb = (value >> 1) & 7; c.Cnt = value & 1;
        }
    }

    private void WriteRhythm(int value)
    {
        deepTrem = (value & 0x80) != 0;
        deepVib = (value & 0x40) != 0;
        bool newRhythm = (value & 0x20) != 0;
        int bits = newRhythm ? (value & 0x1F) : 0;
        // BD=0x10(슬롯12,15) SD=0x08(16) TOM=0x04(14) TC=0x02(17) HH=0x01(13)
        ApplyDrum(bits, 0x10, 12, 15);
        ApplyDrum(bits, 0x08, 16, -1);
        ApplyDrum(bits, 0x04, 14, -1);
        ApplyDrum(bits, 0x02, 17, -1);
        ApplyDrum(bits, 0x01, 13, -1);
        drumBits = bits;
        rhythm = newRhythm;
    }

    private void ApplyDrum(int newBits, int mask, int slotA, int slotB)
    {
        bool was = (drumBits & mask) != 0, now = (newBits & mask) != 0;
        if (was == now) return;
        if (now) { KeyOn(slots[slotA], true); if (slotB >= 0) KeyOn(slots[slotB], true); }
        else { KeyOff(slots[slotA], true); if (slotB >= 0) KeyOff(slots[slotB], true); }
    }

    private void KeyOn(Slot s, bool drum)
    {
        bool was = s.KeyNorm || s.KeyDrum;
        if (drum) s.KeyDrum = true; else s.KeyNorm = true;
        if (!was) { s.State = StateAttack; s.Phase = 0; }
    }

    private void KeyOff(Slot s, bool drum)
    {
        if (drum) s.KeyDrum = false; else s.KeyNorm = false;
        if (!(s.KeyNorm || s.KeyDrum) && s.State != StateOff) s.State = StateRelease;
    }

    // ---------------------------------------------------------------- 생성

    private int Ksv(Channel c) { return (c.Block << 1) | ((c.Fnum >> (nts ? 8 : 9)) & 1); }

    private int EffRate(Slot s, int r, int ksv)
    {
        if (r == 0) return 0;
        int rks = s.Ksr ? ksv : (ksv >> 2);
        int v = (r << 2) + rks;
        return v > 63 ? 63 : v;
    }

    private void UpdateEnvelope(Slot s)
    {
        var c = chans[s.Channel];
        int ksv = Ksv(c);
        switch (s.State)
        {
            case StateAttack:
            {
                int rate = EffRate(s, s.Ar, ksv);
                if (rate >= 60) s.Att = 0;
                else if (rate >= 4) { s.Att *= AttackMul[rate]; if (s.Att < 0.5) s.Att = 0; }
                if (s.Att <= 0) { s.Att = 0; s.State = StateDecay; }
                break;
            }
            case StateDecay:
            {
                double sl = (s.Sl == 15 ? 31 : s.Sl) * 16.0;
                int rate = EffRate(s, s.Dr, ksv);
                s.Att += DecayStep[rate];
                if (s.Att >= sl) { s.Att = sl; s.State = StateSustain; }
                break;
            }
            case StateSustain:
                if (!s.Egt)
                {
                    s.Att += DecayStep[EffRate(s, s.Rr, ksv)];
                    if (s.Att >= 511.0) { s.Att = 511.0; s.State = StateOff; }
                }
                break;
            case StateRelease:
                s.Att += DecayStep[EffRate(s, s.Rr, ksv)];
                if (s.Att >= 511.0) { s.Att = 511.0; s.State = StateOff; }
                break;
        }
    }

    private int SlotOut(Slot s, int modInput, int tremolo)
    {
        if (s.State == StateOff) return 0;
        var c = chans[s.Channel];
        int env = (int)s.Att;
        int ksl = 0;
        if (s.Ksl != 0)
        {
            ksl = (KslRom[c.Fnum >> 6] << 2) - ((8 - c.Block) << 5);
            if (ksl < 0) ksl = 0;
            ksl >>= KslShift[s.Ksl & 3];
        }
        int atten = env + (s.Tl << 2) + ksl + (s.Am ? tremolo : 0);

        int phase = (s.PhaseOut + modInput) & 0x3FF;
        int wf = wse ? s.WfReg : 0;
        bool neg = (phase & 0x200) != 0;
        int idx = phase & 0xFF;
        if ((phase & 0x100) != 0) idx ^= 0xFF;
        switch (wf)
        {
            case 1: if (neg) return 0; break;
            case 2: neg = false; break;
            case 3: if ((phase & 0x100) != 0) return 0; neg = false; break;
        }
        int level = LogSin[idx] + (atten << 3);
        if (level > 0x1FFF) return 0;
        int amp = ExpTab[level & 0xFF] >> (level >> 8);
        return neg ? -amp : amp;
    }

    public short Generate()
    {
        timer++;
        int tp = (timer >> 6) % 210;
        if (tp >= 105) tp = 210 - tp;
        int tremolo = tp >> 2;
        if (!deepTrem) tremolo >>= 2;
        int vibPos = (timer >> 10) & 7;
        int nbit = ((noise >> 14) ^ noise) & 1;
        noise = (noise >> 1) | (nbit << 22);

        // 위상 누산
        for (int i = 0; i < 18; i++)
        {
            var s = slots[i];
            var c = chans[s.Channel];
            int f = c.Fnum;
            if (s.Vib)
            {
                int range = (f >> 7) & 7;
                if ((vibPos & 3) == 0) range = 0;
                else if ((vibPos & 1) != 0) range >>= 1;
                if (!deepVib) range >>= 1;
                if ((vibPos & 4) != 0) range = -range;
                f += range;
            }
            long inc = (((long)f << c.Block) * MultX2[s.Mult]) >> 1;
            s.Phase = (uint)((s.Phase + (uint)inc) & 0xFFFFF);
            s.PhaseOut = (int)(s.Phase >> 10);
        }

        // 리듬 모드 위상 특수 처리 (HH=13, SD=16, TC=17)
        if (rhythm)
        {
            int p13 = slots[13].PhaseOut, p17 = slots[17].PhaseOut;
            int hh2 = (p13 >> 2) & 1, hh3 = (p13 >> 3) & 1, hh7 = (p13 >> 7) & 1, hh8 = (p13 >> 8) & 1;
            int tc3 = (p17 >> 3) & 1, tc5 = (p17 >> 5) & 1;
            int rmXor = (hh2 ^ hh7) | (hh3 ^ tc5) | (tc3 ^ tc5);
            int n = noise & 1;
            slots[13].PhaseOut = (rmXor << 9) | ((rmXor ^ n) != 0 ? 0xD0 : 0x34);
            slots[16].PhaseOut = (hh8 << 9) | ((hh8 ^ n) << 8);
            slots[17].PhaseOut = (rmXor << 9) | 0x80;
        }

        for (int i = 0; i < 18; i++) UpdateEnvelope(slots[i]);

        int mix = 0;
        for (int ch = 0; ch < 9; ch++)
        {
            var c = chans[ch];
            if (rhythm && ch == 7)
            {
                int hh = SlotOut(slots[13], 0, tremolo);
                int sd = SlotOut(slots[16], 0, tremolo);
                mix += (hh + sd) * 2;
                continue;
            }
            if (rhythm && ch == 8)
            {
                int tom = SlotOut(slots[14], 0, tremolo);
                int tc = SlotOut(slots[17], 0, tremolo);
                mix += (tom + tc) * 2;
                continue;
            }
            var m = c.Mod; var k = c.Car;
            int fb = c.Fb > 0 ? (m.Prev0 + m.Prev1) >> (9 - c.Fb) : 0;
            int mo = SlotOut(m, fb, tremolo);
            m.Prev1 = m.Prev0; m.Prev0 = mo;
            if (rhythm && ch == 6)
            {
                // 리듬 모드 베이스드럼: 캐리어만 출력하고 2배 (CNT=1이면 모듈레이터는 출력에 섞이지 않고 위상 변조도 없음)
                mix += SlotOut(k, c.Cnt == 0 ? mo >> 1 : 0, tremolo) * 2;
                continue;
            }
            if (c.Cnt == 0)
                mix += SlotOut(k, mo >> 1, tremolo);
            else
                mix += mo + SlotOut(k, 0, tremolo);
        }

        double v = mix * MasterGain;
        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
        return (short)v;
    }
}
