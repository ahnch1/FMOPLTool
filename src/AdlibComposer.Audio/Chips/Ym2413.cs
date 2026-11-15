using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// YM2413 (OPLL, MSX-MUSIC · FM-PAC · 마스터 시스템 FM): 9채널 2-op FM, 내장 악기 15개 + 사용자 악기 1개, 리듬 모드(BD · SD · TOM · CYM · HH).
/// 샘플레이트 = 클럭 / 72 (3.579545MHz → 49716Hz).
/// <para>내장 악기 표는 emu2413 (Mitsutaka Okazaki, MIT)이 쓰는 YM2413 ROM 값. 엔벨로프는 OPL 방식(9비트 감쇠, 0.1875dB)을 비율 표의 평균 증가량으로 움직인다.
/// 리듬 모드의 HH · CYM · SD는 OPL과 같은 위상 비트 + 노이즈 조합으로 만든다.</para>
/// </summary>
public sealed class Ym2413 : ISoundChip
{
    public const double DefaultClock = 3579545;

    // [악기][8바이트]: 0 사용자, 1~15 내장, 16 BD, 17 HH/SD, 18 TOM/CYM
    private static readonly byte[,] Rom =
    {
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        { 0x71, 0x61, 0x1E, 0x17, 0xD0, 0x78, 0x00, 0x17 },   // 1 Violin
        { 0x13, 0x41, 0x1A, 0x0D, 0xD8, 0xF7, 0x23, 0x13 },   // 2 Guitar
        { 0x13, 0x01, 0x99, 0x00, 0xF2, 0xC4, 0x21, 0x23 },   // 3 Piano
        { 0x11, 0x61, 0x0E, 0x07, 0x8D, 0x64, 0x70, 0x27 },   // 4 Flute
        { 0x32, 0x21, 0x1E, 0x06, 0xE1, 0x76, 0x01, 0x28 },   // 5 Clarinet
        { 0x31, 0x22, 0x16, 0x05, 0xE0, 0x71, 0x00, 0x18 },   // 6 Oboe
        { 0x21, 0x61, 0x1D, 0x07, 0x82, 0x81, 0x11, 0x07 },   // 7 Trumpet
        { 0x33, 0x21, 0x2D, 0x13, 0xB0, 0x70, 0x00, 0x07 },   // 8 Organ
        { 0x61, 0x61, 0x1B, 0x06, 0x64, 0x65, 0x10, 0x17 },   // 9 Horn
        { 0x41, 0x61, 0x0B, 0x18, 0x85, 0xF0, 0x81, 0x07 },   // 10 Synthesizer
        { 0x33, 0x01, 0x83, 0x11, 0xEA, 0xEF, 0x10, 0x04 },   // 11 Harpsichord
        { 0x17, 0xC1, 0x24, 0x07, 0xF8, 0xF8, 0x22, 0x12 },   // 12 Vibraphone
        { 0x61, 0x50, 0x0C, 0x05, 0xD2, 0xF5, 0x40, 0x42 },   // 13 Synth Bass
        { 0x01, 0x01, 0x55, 0x03, 0xE9, 0x90, 0x03, 0x02 },   // 14 Acoustic Bass
        { 0x41, 0x41, 0x89, 0x03, 0xF1, 0xE4, 0xC0, 0x13 },   // 15 Electric Guitar
        { 0x01, 0x01, 0x18, 0x0F, 0xDF, 0xF8, 0x6A, 0x6D },   // BD
        { 0x01, 0x01, 0x00, 0x00, 0xC8, 0xD8, 0xA7, 0x68 },   // HH / SD
        { 0x05, 0x01, 0x00, 0x00, 0xF8, 0xAA, 0x59, 0x55 },   // TOM / CYM
    };

    public static readonly string[] InstrumentNames =
    {
        "User", "Violin", "Guitar", "Piano", "Flute", "Clarinet", "Oboe", "Trumpet", "Organ", "Horn",
        "Synthesizer", "Harpsichord", "Vibraphone", "Synth Bass", "Acoustic Bass", "Electric Guitar",
    };

    private static readonly double[] MulTab = { 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 12, 12, 15, 15 };
    private static readonly int[] KslRom = { 0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64 };

    private sealed class Slot
    {
        public bool Am, Pm, EgType, Kr, HalfSine;
        public int Mul, Ksl, Tl, Ar, Dr, Sl, Rr, Fb;
        public double Phase, Att = 511;
        public int Stage = 3;   // 0 공격 1 감쇠 2 지속 3 놓음
        public bool Key;
        public double Out0, Out1;
    }

    private readonly Slot[] slots = new Slot[18];
    private readonly byte[] reg = new byte[64];
    private readonly byte[] user = new byte[8];
    private readonly bool[] mute = new bool[14];
    private readonly double[] pitch = new double[9];
    private readonly double clock;
    private double amPhase, pmPhase;
    private int noise = 1;
    private bool rhythm;

    public Ym2413(double clock = DefaultClock)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = (int)Math.Round(this.clock / 72);
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
        Reset();
    }

    public string Name { get { return "YM2413 (OPLL)"; } }
    public int NativeRate { get; }
    /// <summary>멜로디 9 + 리듬 5 (BD · SD · TOM · CYM · HH).</summary>
    public int ChannelCount { get { return 14; } }
    private static readonly string[] DrumNames = { "BD", "SD", "TO", "CY", "HH" };
    public string ChannelLabel(int ch) { return ch < 9 ? "L" + (ch + 1) : DrumNames[ch - 9]; }

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        Array.Clear(user, 0, user.Length);
        foreach (var s in slots) { s.Phase = 0; s.Att = 511; s.Stage = 3; s.Key = false; s.Out0 = s.Out1 = 0; }
        for (int i = 0; i < 9; i++) pitch[i] = 1.0;
        rhythm = false; amPhase = pmPhase = 0; noise = 1;
        for (int ch = 0; ch < 9; ch++) LoadPatch(ch);
    }

    public void Write(int port, int r, int v)
    {
        r &= 0x3F; v &= 0xFF;
        int old = reg[r];
        reg[r] = (byte)v;
        if (r < 8)
        {
            user[r] = (byte)v;
            for (int ch = 0; ch < 9; ch++) if ((reg[0x30 + ch] >> 4) == 0 && !(rhythm && ch >= 6)) LoadPatch(ch);
            return;
        }
        if (r == 0x0E)
        {
            bool rm = (v & 0x20) != 0;
            if (rm != rhythm) { rhythm = rm; for (int ch = 6; ch < 9; ch++) LoadPatch(ch); }
            if (rhythm)
            {
                KeySlot(12, (v & 0x10) != 0); KeySlot(13, (v & 0x10) != 0);   // BD
                KeySlot(14, (v & 0x01) != 0);                                  // HH (ch7 M)
                KeySlot(15, (v & 0x08) != 0);                                  // SD (ch7 C)
                KeySlot(16, (v & 0x04) != 0);                                  // TOM (ch8 M)
                KeySlot(17, (v & 0x02) != 0);                                  // CYM (ch8 C)
            }
            return;
        }
        int c = r & 0x0F;
        if (c > 8) return;
        switch (r & 0xF0)
        {
            case 0x20:
                if (!(rhythm && c >= 6))
                {
                    bool key = (v & 0x10) != 0;
                    if (key != ((old & 0x10) != 0)) { KeySlot(c * 2, key); KeySlot(c * 2 + 1, key); }
                }
                break;
            case 0x30:
                if (!(rhythm && c >= 6)) LoadPatch(c);
                break;
        }
    }

    private void KeySlot(int i, bool on)
    {
        var s = slots[i];
        if (on && !s.Key) { s.Key = true; s.Stage = 0; s.Phase = 0; if (s.Ar >= 15) { s.Att = 0; s.Stage = 1; } }
        else if (!on && s.Key) { s.Key = false; s.Stage = 3; }
    }

    private byte PatchByte(int patch, int i) { return patch == 0 ? user[i] : Rom[patch, i]; }

    private void LoadPatch(int ch)
    {
        int patch = rhythm && ch >= 6 ? 16 + (ch - 6) : reg[0x30 + ch] >> 4;
        for (int op = 0; op < 2; op++)
        {
            var s = slots[ch * 2 + op];
            int b0 = PatchByte(patch, op);
            s.Am = (b0 & 0x80) != 0; s.Pm = (b0 & 0x40) != 0; s.EgType = (b0 & 0x20) != 0; s.Kr = (b0 & 0x10) != 0; s.Mul = b0 & 15;
            int b3 = PatchByte(patch, 3);
            if (op == 0) { s.Ksl = PatchByte(patch, 2) >> 6; s.Tl = PatchByte(patch, 2) & 0x3F; s.HalfSine = (b3 & 0x08) != 0; s.Fb = b3 & 7; }
            else { s.Ksl = b3 >> 6; s.HalfSine = (b3 & 0x10) != 0; s.Fb = 0; }
            int ad = PatchByte(patch, 4 + op), sr = PatchByte(patch, 6 + op);
            s.Ar = ad >> 4; s.Dr = ad & 15; s.Sl = sr >> 4; s.Rr = sr & 15;
        }
    }

    private int Fnum(int ch) { return reg[0x10 + ch] | ((reg[0x20 + ch] & 1) << 8); }
    private int Block(int ch) { return (reg[0x20 + ch] >> 1) & 7; }

    // ------------------------------------------------------------ 엔벨로프

    private static double EgStep(int rate)
    {
        if (rate <= 0) return 0;
        if (rate >= 60) return 8;
        return (4 + (rate & 3)) / 8.0 * Math.Pow(2, (rate >> 2) - 11);
    }

    private static readonly double[] EgTab = BuildEg();
    private static double[] BuildEg() { var t = new double[64]; for (int i = 0; i < 64; i++) t[i] = EgStep(i); return t; }

    private int Rate(int r, Slot s, int ch)
    {
        if (r == 0) return 0;
        int kr = (Block(ch) << 1) | (Fnum(ch) >> 8);
        return Math.Min(63, r * 4 + (s.Kr ? kr : kr >> 2));
    }

    private void Envelope(Slot s, int ch, bool sus)
    {
        switch (s.Stage)
        {
            case 0:
                {
                    int r = Rate(s.Ar, s, ch);
                    if (r >= 60) { s.Att = 0; s.Stage = 1; break; }
                    s.Att -= (s.Att + 1) * EgTab[r] / 8.0;
                    if (s.Att <= 0) { s.Att = 0; s.Stage = 1; }
                    break;
                }
            case 1:
                s.Att += EgTab[Rate(s.Dr, s, ch)] / 2;
                if (s.Att >= s.Sl * 32) { s.Att = Math.Max(s.Att, s.Sl * 32); s.Stage = 2; }
                break;
            case 2:
                if (!s.EgType) s.Att += EgTab[Rate(s.Rr, s, ch)] / 2;
                break;
            default:
                {
                    int rr = sus ? 5 : s.EgType ? s.Rr : 7;
                    s.Att += EgTab[Rate(rr, s, ch)] / 2;
                    break;
                }
        }
        if (s.Att > 511) s.Att = 511;
    }

    // ------------------------------------------------------------ 소리

    private static double Gain(double att) { return att >= 511 ? 0 : Math.Pow(10, -att * 0.1875 / 20.0); }

    private double SlotOut(Slot s, int ch, double mod, double tlAtt, double am)
    {
        int fnum = Fnum(ch), block = Block(ch);
        int ksl = KslRom[fnum >> 5] * 4 - (7 - block) * 32;
        double kslAtt = ksl <= 0 || s.Ksl == 0 ? 0 : s.Ksl == 1 ? ksl >> 1 : s.Ksl == 2 ? ksl >> 2 : ksl;
        double att = s.Att + tlAtt + kslAtt + (s.Am ? am : 0);
        if (att >= 511) return 0;
        double v = Math.Sin(2 * Math.PI * (s.Phase + mod));
        if (s.HalfSine && v < 0) v = 0;
        return v * Gain(att);
    }

    private void Advance(Slot s, int ch, double pm)
    {
        double hz = Fnum(ch) * (clock / 72.0) / Math.Pow(2, 19 - Block(ch));
        s.Phase += hz * MulTab[s.Mul] * (s.Pm ? pm : 1) * pitch[ch] / NativeRate;
        if (s.Phase >= 1) s.Phase -= Math.Floor(s.Phase);
    }

    public void Generate(out int left, out int right)
    {
        amPhase += 3.7 / NativeRate; if (amPhase >= 1) amPhase -= 1;
        pmPhase += 6.4 / NativeRate; if (pmPhase >= 1) pmPhase -= 1;
        double am = (amPhase < 0.5 ? amPhase * 2 : 2 - amPhase * 2) * 25.6;
        double pmTri = pmPhase < 0.25 ? 4 * pmPhase : pmPhase < 0.75 ? 2 - 4 * pmPhase : 4 * pmPhase - 4;
        double pm = Math.Pow(2, 7 * pmTri / 1200.0);
        int b = (noise ^ (noise >> 14)) & 1; noise = ((noise << 1) | b) & 0x7FFFFF;
        bool nbit = (noise & 1) != 0;

        double sum = 0;
        int melodic = rhythm ? 6 : 9;
        for (int ch = 0; ch < 9; ch++)
        {
            var m = slots[ch * 2]; var c = slots[ch * 2 + 1];
            bool sus = (reg[0x20 + ch] & 0x20) != 0;
            Envelope(m, ch, sus); Envelope(c, ch, sus);
            Advance(m, ch, pm); Advance(c, ch, pm);
            if (ch >= melodic) continue;
            double fb = m.Fb > 0 ? (m.Out0 + m.Out1) * Math.Pow(2, m.Fb - 7) : 0;
            double mo = SlotOut(m, ch, fb, m.Tl * 4, am);
            m.Out1 = m.Out0; m.Out0 = mo;
            double co = SlotOut(c, ch, mo * 4, (reg[0x30 + ch] & 15) * 16, am);
            if (!mute[ch]) sum += co;
        }
        if (rhythm)
        {
            // BD (채널 7)
            var bm = slots[12]; var bc = slots[13];
            double fb = bm.Fb > 0 ? (bm.Out0 + bm.Out1) * Math.Pow(2, bm.Fb - 7) : 0;
            double mo = SlotOut(bm, 6, fb, bm.Tl * 4, am);
            bm.Out1 = bm.Out0; bm.Out0 = mo;
            double bd = SlotOut(bc, 6, mo * 4, (reg[0x36] & 15) * 16, am);
            if (!mute[9]) sum += bd * 2;

            int p7 = (int)(slots[14].Phase * 1024) & 1023, p8 = (int)(slots[17].Phase * 1024) & 1023;
            bool r1 = ((((p7 >> 2) ^ (p7 >> 7)) & 1) | ((p7 >> 3) & 1)) != 0;
            bool r2 = (((p8 >> 3) ^ (p8 >> 5)) & 1) != 0;
            bool hiPh = r1 || r2;
            // HH (채널 8 M) · SD (채널 8 C) · TOM (채널 9 M) · CYM (채널 9 C)
            double hh = Drum(slots[14], 7, (hiPh ^ nbit) ? 1 : -1, (reg[0x37] >> 4) * 16, am);
            double sd = Drum(slots[15], 7, (((p7 >> 8) & 1) != 0) ^ nbit ? 1 : -1, (reg[0x37] & 15) * 16, am);
            double tom = SlotOut(slots[16], 8, 0, (reg[0x38] >> 4) * 16, am);
            double cym = Drum(slots[17], 8, hiPh ? 1 : -1, (reg[0x38] & 15) * 16, am);
            if (!mute[10]) sum += sd * 2;
            if (!mute[11]) sum += tom * 2;
            if (!mute[12]) sum += cym * 2;
            if (!mute[13]) sum += hh * 2;
        }
        left = right = SoundChips.Clip(sum * 4000);
    }

    private double Drum(Slot s, int ch, double sign, double tlAtt, double am)
    {
        double att = s.Att + tlAtt + (s.Am ? am : 0);
        return att >= 511 ? 0 : sign * Gain(att) * 0.5;
    }

    // ------------------------------------------------------------ 조절 · 표시

    public void SetMute(int ch, bool m) { if (ch >= 0 && ch < 14) mute[ch] = m; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 9) pitch[ch] = ratio > 0 ? ratio : 1; }

    public double ChannelFrequency(int ch)
    {
        if (ch < 0 || ch >= 9 || (rhythm && ch >= 6)) return 0;
        if ((reg[0x20 + ch] & 0x10) == 0) return 0;
        return Fnum(ch) * (clock / 72.0) / Math.Pow(2, 19 - Block(ch));
    }

    public float ChannelLevel(int ch)
    {
        if (ch < 0 || ch >= 14) return 0;
        if (ch < 9) return rhythm && ch >= 6 ? 0 : (float)Gain(slots[ch * 2 + 1].Att + (reg[0x30 + ch] & 15) * 16);
        int[] slot = { 13, 15, 16, 17, 14 };
        return rhythm ? (float)Gain(slots[slot[ch - 9]].Att) : 0;
    }

    private static readonly int[] DrumKeyBit = { 0x10, 0x08, 0x04, 0x02, 0x01 };
    public bool ChannelActive(int ch) { return ch >= 9 && ch < 14 && rhythm && (reg[0x0E] & DrumKeyBit[ch - 9]) != 0; }

    /// <summary>채널 ch의 지금 악기 이름.</summary>
    public string InstrumentOf(int ch) { return ch >= 0 && ch < 9 && !(rhythm && ch >= 6) ? InstrumentNames[reg[0x30 + ch] >> 4] : ""; }
}
