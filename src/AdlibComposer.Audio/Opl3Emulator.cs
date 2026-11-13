using System;

namespace AdlibComposer.Audio;

/// <summary>
/// YMF262(OPL3) 에뮬레이터 (간이형, <see cref="Opl2Emulator"/>와 같은 방식). 레지스터 뱅크 2개(0x000~, 0x100~) = 18채널,
/// 4-오퍼레이터 짝(0x104), 웨이브폼 8종, 채널별 출력 A~D(C0의 0x10 · 0x20 · 0x40 · 0x80; A · C = 왼쪽, B · D = 오른쪽), 첫 뱅크 리듬 모드.
/// 0x105의 NEW 비트가 0이면 OPL2처럼 동작한다 (웨이브폼 4종, 4-op 없음, 양쪽 출력).
/// </summary>
public sealed class Opl3Emulator : IOplChip
{
    public const int Rate = 49716;
    public int NativeRate { get { return Rate; } }
    public double MasterGain { get; set; } = 1.0;
    /// <summary>되먹임(feedback) 세기 배율. 1 = 칩 그대로, Jukebox '표준' 음색은 0.875 (feedback 6~7의 거친 잡음을 덜어 낸다).</summary>
    public double FeedbackScale { get; set; } = 1.0;

    private const int StateOff = 0, StateAttack = 1, StateDecay = 2, StateSustain = 3, StateRelease = 4;

    private static readonly int[] LogSin = new int[256];
    private static readonly int[] ExpTab = new int[256];
    private static readonly int[] KslRom = { 0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64 };
    private static readonly int[] KslShift = { 8, 1, 2, 0 };
    private static readonly int[] MultX2 = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly double[] DecayStep = new double[64];
    private static readonly double[] AttackMul = new double[64];
    private static readonly int[] SlotOfOffset = new int[0x20];

    static Opl3Emulator()
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
        public int PhaseOut;
    }

    private sealed class Channel
    {
        public int Fnum, Block, Fb, Cnt;
        public bool Kon, Left = true, Right = true;
        public Slot Mod, Car;
        /// <summary>4-op 짝: 첫 채널이면 짝 채널, 둘째 채널이면 첫 채널 (아니면 null).</summary>
        public Channel Pair;
        public bool IsPrimary;
        /// <summary>주파수 · 키온을 가져올 채널 (4-op 둘째 채널은 첫 채널).</summary>
        public Channel Freq;
    }

    private readonly Slot[] slots = new Slot[36];
    private readonly Channel[] chans = new Channel[18];
    private bool opl3, wse, nts, deepTrem, deepVib, rhythm;
    private int fourOpBits;
    private int drumBits;
    private int timer;
    private int noise = 1;

    public Opl3Emulator()
    {
        for (int c = 0; c < 18; c++) chans[c] = new Channel();
        for (int s = 0; s < 36; s++)
        {
            int bank = s / 18, local = s % 18;
            int ch = bank * 9 + (local / 6) * 3 + (local % 3);
            bool car = (local % 6) >= 3;
            slots[s] = new Slot { Channel = ch };
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
        foreach (var c in chans) { c.Fnum = c.Block = c.Fb = c.Cnt = 0; c.Kon = false; c.Left = c.Right = true; }
        opl3 = wse = nts = deepTrem = deepVib = rhythm = false;
        fourOpBits = 0;
        UpdatePairs();
        drumBits = 0; timer = 0; noise = 1;
    }

    // ---------------------------------------------------------------- 레지스터

    /// <summary>register: 0x000~0x0FF = 첫 뱅크, 0x100~0x1FF = 둘째 뱅크.</summary>
    public void Write(int register, int value)
    {
        int bank = (register >> 8) & 1;
        int reg = register & 0xFF;
        value &= 0xFF;
        if (bank == 1)
        {
            if (reg == 0x04) { fourOpBits = value & 0x3F; UpdatePairs(); return; }
            if (reg == 0x05) { opl3 = (value & 1) != 0; UpdatePairs(); return; }
        }
        else
        {
            if (reg == 0x01) { wse = (value & 0x20) != 0; return; }
            if (reg == 0x08) { nts = (value & 0x40) != 0; return; }
            if (reg == 0xBD) { WriteRhythm(value); return; }
        }

        if (reg >= 0x20 && reg <= 0x95)
        {
            int group = reg & 0xE0, off = reg & 0x1F;
            if (off <= 0x15 && SlotOfOffset[off] >= 0)
            {
                var s = slots[bank * 18 + SlotOfOffset[off]];
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
            if (SlotOfOffset[off] >= 0) slots[bank * 18 + SlotOfOffset[off]].WfReg = value & 7;
            return;
        }
        if (reg >= 0xA0 && reg <= 0xA8)
        {
            var c = chans[bank * 9 + reg - 0xA0];
            c.Fnum = (c.Fnum & 0x300) | value;
            return;
        }
        if (reg >= 0xB0 && reg <= 0xB8)
        {
            var c = chans[bank * 9 + reg - 0xB0];
            c.Fnum = (c.Fnum & 0xFF) | ((value & 3) << 8);
            c.Block = (value >> 2) & 7;
            bool kon = (value & 0x20) != 0;
            if (kon != c.Kon)
            {
                c.Kon = kon;
                if (c.Pair != null && !c.IsPrimary) return;   // 4-op 둘째 채널의 키온은 무시
                SetKey(c, kon);
                if (c.Pair != null) SetKey(c.Pair, kon);
            }
            return;
        }
        if (reg >= 0xC0 && reg <= 0xC8)
        {
            var c = chans[bank * 9 + reg - 0xC0];
            c.Fb = (value >> 1) & 7; c.Cnt = value & 1;
            c.Left = (value & 0x50) != 0;    // A · C
            c.Right = (value & 0xA0) != 0;   // B · D
        }
    }

    private void SetKey(Channel c, bool on)
    {
        if (on) { KeyOn(c.Mod, false); KeyOn(c.Car, false); }
        else { KeyOff(c.Mod, false); KeyOff(c.Car, false); }
    }

    /// <summary>0x104 비트: 0 = 채널 0+3, 1 = 1+4, 2 = 2+5 (첫 뱅크), 3~5 = 둘째 뱅크의 같은 짝.</summary>
    private void UpdatePairs()
    {
        foreach (var c in chans) { c.Pair = null; c.IsPrimary = false; c.Freq = c; }
        if (!opl3) return;
        for (int bit = 0; bit < 6; bit++)
        {
            if ((fourOpBits & (1 << bit)) == 0) continue;
            var a = chans[(bit / 3) * 9 + bit % 3];
            var b = chans[(bit / 3) * 9 + bit % 3 + 3];
            a.Pair = b; b.Pair = a;
            a.IsPrimary = true;
            b.Freq = a;
        }
    }

    private void WriteRhythm(int value)
    {
        deepTrem = (value & 0x80) != 0;
        deepVib = (value & 0x40) != 0;
        bool newRhythm = (value & 0x20) != 0;
        int bits = newRhythm ? (value & 0x1F) : 0;
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

    private static void KeyOn(Slot s, bool drum)
    {
        bool was = s.KeyNorm || s.KeyDrum;
        if (drum) s.KeyDrum = true; else s.KeyNorm = true;
        if (!was) { s.State = StateAttack; s.Phase = 0; }
    }

    private static void KeyOff(Slot s, bool drum)
    {
        if (drum) s.KeyDrum = false; else s.KeyNorm = false;
        if (!(s.KeyNorm || s.KeyDrum) && s.State != StateOff) s.State = StateRelease;
    }

    // ---------------------------------------------------------------- 생성

    private int Ksv(Channel c) { return (c.Block << 1) | ((c.Fnum >> (nts ? 8 : 9)) & 1); }

    private static int EffRate(Slot s, int r, int ksv)
    {
        if (r == 0) return 0;
        int rks = s.Ksr ? ksv : (ksv >> 2);
        int v = (r << 2) + rks;
        return v > 63 ? 63 : v;
    }

    private void UpdateEnvelope(Slot s)
    {
        if (s.State == StateOff) return;
        int ksv = Ksv(chans[s.Channel].Freq);
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
                s.Att += DecayStep[EffRate(s, s.Dr, ksv)];
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
        var c = chans[s.Channel].Freq;
        int ksl = 0;
        if (s.Ksl != 0)
        {
            ksl = (KslRom[c.Fnum >> 6] << 2) - ((8 - c.Block) << 5);
            if (ksl < 0) ksl = 0;
            ksl >>= KslShift[s.Ksl & 3];
        }
        int atten = (int)s.Att + (s.Tl << 2) + ksl + (s.Am ? tremolo : 0);
        int env = atten << 3;

        int phase = (s.PhaseOut + modInput) & 0x3FF;
        int wf = opl3 ? s.WfReg : wse ? s.WfReg & 3 : 0;
        bool neg = false;
        int level;
        switch (wf)
        {
            default:   // 0: 사인
                neg = (phase & 0x200) != 0;
                level = LogSin[Quarter(phase)];
                break;
            case 1:    // 반파
                if ((phase & 0x200) != 0) return 0;
                level = LogSin[Quarter(phase)];
                break;
            case 2:    // 절댓값 사인
                level = LogSin[Quarter(phase)];
                break;
            case 3:    // 사분 펄스
                if ((phase & 0x100) != 0) return 0;
                level = LogSin[phase & 0xFF];
                break;
            case 4:    // 짝수 사인 (앞 반주기에 2배 속도 사인, 뒤 반은 0)
                if ((phase & 0x200) != 0) return 0;
                neg = (phase & 0x100) != 0;
                level = LogSin[(phase & 0x80) != 0 ? ((phase ^ 0xFF) << 1) & 0xFF : (phase << 1) & 0xFF];
                break;
            case 5:    // 절댓값 짝수 사인
                if ((phase & 0x200) != 0) return 0;
                level = LogSin[(phase & 0x80) != 0 ? ((phase ^ 0xFF) << 1) & 0xFF : (phase << 1) & 0xFF];
                break;
            case 6:    // 사각파
                neg = (phase & 0x200) != 0;
                level = 0;
                break;
            case 7:    // 지수 톱니(유도 사각파)
            {
                int p = phase;
                if ((p & 0x200) != 0) { neg = true; p = (p & 0x1FF) ^ 0x1FF; }
                level = p << 3;
                break;
            }
        }
        level += env;
        if (level > 0x1FFF) return 0;
        int amp = ExpTab[level & 0xFF] >> (level >> 8);
        return neg ? -amp : amp;
    }

    private static int Quarter(int phase)
    {
        int idx = phase & 0xFF;
        return (phase & 0x100) != 0 ? idx ^ 0xFF : idx;
    }

    private int lastLeft, lastRight;

    /// <summary>스테레오 샘플 1개 (왼쪽/오른쪽, 16bit 범위로 자름).</summary>
    public void GenerateStereo(out int left, out int right)
    {
        Clock();
        left = lastLeft; right = lastRight;
    }

    /// <summary>모노 샘플 1개 (좌우 평균).</summary>
    public short Generate()
    {
        Clock();
        return (short)((lastLeft + lastRight) / 2);
    }

    private void Clock()
    {
        timer++;
        int tp = (timer >> 6) % 210;
        if (tp >= 105) tp = 210 - tp;
        int tremolo = tp >> 2;
        if (!deepTrem) tremolo >>= 2;
        int vibPos = (timer >> 10) & 7;
        int nbit = ((noise >> 14) ^ noise) & 1;
        noise = (noise >> 1) | (nbit << 22);

        for (int i = 0; i < 36; i++)
        {
            var s = slots[i];
            var c = chans[s.Channel].Freq;
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

        if (rhythm)
        {
            int p13 = (int)(slots[13].Phase >> 10), p17 = (int)(slots[17].Phase >> 10);
            int hh2 = (p13 >> 2) & 1, hh3 = (p13 >> 3) & 1, hh7 = (p13 >> 7) & 1, hh8 = (p13 >> 8) & 1;
            int tc3 = (p17 >> 3) & 1, tc5 = (p17 >> 5) & 1;
            int rmXor = (hh2 ^ hh7) | (hh3 ^ tc5) | (tc3 ^ tc5);
            int n = noise & 1;
            slots[13].PhaseOut = (rmXor << 9) | ((rmXor ^ n) != 0 ? 0xD0 : 0x34);
            slots[16].PhaseOut = (hh8 << 9) | ((hh8 ^ n) << 8);
            slots[17].PhaseOut = (rmXor << 9) | 0x80;
        }

        for (int i = 0; i < 36; i++) UpdateEnvelope(slots[i]);

        int l = 0, r = 0;
        for (int ch = 0; ch < 18; ch++)
        {
            var c = chans[ch];
            int o;
            if (rhythm && ch == 7) o = (SlotOut(slots[13], 0, tremolo) + SlotOut(slots[16], 0, tremolo)) * 2;
            else if (rhythm && ch == 8) o = (SlotOut(slots[14], 0, tremolo) + SlotOut(slots[17], 0, tremolo)) * 2;
            else if (c.Pair != null)
            {
                if (!c.IsPrimary) continue;   // 짝 채널은 첫 채널에서 함께 계산
                o = FourOp(c, c.Pair, tremolo);
            }
            else
            {
                var m = c.Mod; var k = c.Car;
                int mo = Feedback(c, tremolo);
                if (rhythm && ch == 6) o = SlotOut(k, c.Cnt == 0 ? mo >> 1 : 0, tremolo) * 2;
                else if (c.Cnt == 0) o = SlotOut(k, mo >> 1, tremolo);
                else o = mo + SlotOut(k, 0, tremolo);
            }
            if (!opl3 || c.Left) l += o;
            if (!opl3 || c.Right) r += o;
        }
        lastLeft = Clip(l * MasterGain);
        lastRight = Clip(r * MasterGain);
    }

    /// <summary>모듈레이터(피드백 포함) 출력.</summary>
    private int Feedback(Channel c, int tremolo)
    {
        var m = c.Mod;
        int fb = c.Fb > 0 ? (int)(((m.Prev0 + m.Prev1) >> (9 - c.Fb)) * FeedbackScale) : 0;
        int mo = SlotOut(m, fb, tremolo);
        m.Prev1 = m.Prev0; m.Prev0 = mo;
        return mo;
    }

    /// <summary>4-op 연결 (첫 채널 CNT, 짝 채널 CNT): 00 = 1→2→3→4, 01 = (1→2)+(3→4), 10 = 1+(2→3→4), 11 = 1+(2→3)+4.</summary>
    private int FourOp(Channel a, Channel b, int tremolo)
    {
        int o1 = Feedback(a, tremolo);
        int o2, o3, o4;
        switch ((a.Cnt << 1) | b.Cnt)
        {
            case 0:
                o2 = SlotOut(a.Car, o1 >> 1, tremolo);
                o3 = SlotOut(b.Mod, o2 >> 1, tremolo);
                return SlotOut(b.Car, o3 >> 1, tremolo);
            case 1:
                o2 = SlotOut(a.Car, o1 >> 1, tremolo);
                o3 = SlotOut(b.Mod, 0, tremolo);
                o4 = SlotOut(b.Car, o3 >> 1, tremolo);
                return o2 + o4;
            case 2:
                o2 = SlotOut(a.Car, 0, tremolo);
                o3 = SlotOut(b.Mod, o2 >> 1, tremolo);
                o4 = SlotOut(b.Car, o3 >> 1, tremolo);
                return o1 + o4;
            default:
                o2 = SlotOut(a.Car, 0, tremolo);
                o3 = SlotOut(b.Mod, o2 >> 1, tremolo);
                o4 = SlotOut(b.Car, 0, tremolo);
                return o1 + o3 + o4;
        }
    }

    private static int Clip(double v)
    {
        if (v > 32767) return 32767;
        if (v < -32768) return -32768;
        return (int)v;
    }
}
