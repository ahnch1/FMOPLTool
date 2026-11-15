using System;
using System.Linq;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Yamaha OPN 계열: YM2203 (OPN, FM 3 + SSG 3) · YM2608 (OPNA, FM 6 + SSG 3 + 리듬 6 + ADPCM-B) · YM2612 (OPN2, FM 6 + DAC) ·
/// YM2610 / YM2610B (OPNB, 네오지오 · 타이토: FM 4 / 6 + SSG 3 + ADPCM-A 6 + ADPCM-B).
/// PC-88 · PC-98 · 메가 드라이브 · 네오지오 소리. FM 샘플레이트 = 클럭 / 72 (OPN) · 클럭 / 144 (OPNA · OPN2 · OPNB), 기본 프리스케일러로 본다.
/// <para>YM2610 레지스터: 포트 0 00~0F = SSG, 10~1C = ADPCM-B (OPNA ADPCM-B 레지스터 − 0x10, 주소 256바이트 단위), 포트 1 00~2D = ADPCM-A.
/// YM2610에는 FM 채널 1 · 4가 없다 (YM2610B는 6채널 모두).</para>
/// <para>OPNA 리듬 음원은 칩 안 ROM 소리라 넣지 않고 짧은 합성음으로 흉내 낸다 (베이스 드럼 · 스네어 · 심벌 · 하이햇 · 탐 · 림샷).
/// SSG-EG · 타이머 · CSM은 소리에 쓰지 않는다.</para>
/// </summary>
public sealed class OpnChip : ISoundChip
{
    public enum Variant { Ym2203, Ym2608, Ym2612, Ym2610, Ym2610B }

    public const double Ym2203Clock = 3993600, Ym2608Clock = 7987200, Ym2612Clock = 7670453, Ym2610Clock = 8000000;

    public readonly Variant Type;
    private readonly double clock;
    private readonly FmEngine fm;
    private readonly PsgEmulator ssg;
    private readonly Rhythm rhythm;
    private readonly AdpcmB adpcm;
    private readonly AdpcmA adpcmA;
    private readonly int fmCh;
    /// <summary>믹서에 보이는 FM 채널 → 엔진 채널 (YM2610은 1 · 2 · 4 · 5번만).</summary>
    private readonly int[] fmShown;
    private bool IsOpnb { get { return Type == Variant.Ym2610 || Type == Variant.Ym2610B; } }
    private readonly byte[,] reg = new byte[2, 256];
    private readonly int[] fnum = new int[6], block = new int[6];
    private readonly int[] latch = new int[6];
    private readonly int[] spFnum = new int[3], spBlock = new int[3];
    private int spLatch;
    private bool special;

    public OpnChip(Variant type, double clock = 0)
    {
        Type = type;
        this.clock = clock > 0 ? clock : type == Variant.Ym2203 ? Ym2203Clock : type == Variant.Ym2608 ? Ym2608Clock : IsOpnb ? Ym2610Clock : Ym2612Clock;
        NativeRate = (int)Math.Round(this.clock / (type == Variant.Ym2203 ? 72 : 144));
        fmCh = type == Variant.Ym2203 ? 3 : 6;
        fmShown = type == Variant.Ym2610 ? new[] { 1, 2, 4, 5 } : Enumerable.Range(0, fmCh).ToArray();
        fm = new FmEngine(FmEngine.Kind.Opn, fmCh, NativeRate);
        if (type != Variant.Ym2612) ssg = new PsgEmulator(this.clock / (type == Variant.Ym2203 ? 2 : 4), NativeRate, true);
        if (type == Variant.Ym2608) { rhythm = new Rhythm(NativeRate); adpcm = new AdpcmB(NativeRate); }
        if (IsOpnb) { adpcmA = new AdpcmA(this.clock, NativeRate); adpcm = new AdpcmB(NativeRate) { Unit256 = true }; }
        Reset();
    }

    public string Name
    {
        get
        {
            switch (Type)
            {
                case Variant.Ym2203: return "YM2203 (OPN)";
                case Variant.Ym2608: return "YM2608 (OPNA)";
                case Variant.Ym2610: return "YM2610 (OPNB)";
                case Variant.Ym2610B: return "YM2610B (OPNB)";
                default: return "YM2612 (OPN2)";
            }
        }
    }
    public int NativeRate { get; }
    /// <summary>FM → SSG → 리듬 6 + ADPCM (OPNA) / ADPCM-A 6 + ADPCM-B (OPNB).</summary>
    public int ChannelCount { get { return fmShown.Length + (ssg != null ? 3 : 0) + (rhythm != null || adpcmA != null ? 6 + 1 : 0); } }

    private static readonly string[] RhythmNames = { "BD", "SD", "CY", "HH", "TO", "RI" };

    public string ChannelLabel(int ch)
    {
        if (ch < fmShown.Length) return "F" + (ch + 1);
        ch -= fmShown.Length;
        if (ssg != null) { if (ch < 3) return ((char)('A' + ch)).ToString(); ch -= 3; }
        if (rhythm != null) { if (ch < 6) return RhythmNames[ch]; return "PCM"; }
        if (adpcmA != null) { if (ch < 6) return "A" + (ch + 1); return "AB"; }
        return "?";
    }

    /// <summary>ADPCM-B 메모리 (VGM 데이터 블록 0x81 · YM2610 0x83을 여기로).</summary>
    public void LoadAdpcm(byte[] data, int start) { adpcm?.Load(data, start); }

    /// <summary>YM2610 ADPCM-A ROM (VGM 데이터 블록 0x82).</summary>
    public void LoadAdpcmA(byte[] data, int start) { adpcmA?.Load(data, start); }

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        fm.Reset();
        ssg?.Reset();
        rhythm?.Reset();
        adpcm?.Reset();
        adpcmA?.Reset();
        Array.Clear(fnum, 0, 6); Array.Clear(block, 0, 6); Array.Clear(latch, 0, 6);
        Array.Clear(spFnum, 0, 3); Array.Clear(spBlock, 0, 3);
        spLatch = 0; special = false;
        for (int c = 0; c < fmCh; c++) { fm.Ch[c].Left = fm.Ch[c].Right = true; }
        if (Type == Variant.Ym2610) { fm.Ch[0].Mute = true; fm.Ch[3].Mute = true; }   // 없는 채널
    }

    // fm.c opn_fktable: F-number 위 4비트 → 키코드 아래 2비트
    private static readonly int[] FkTable = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };
    private static readonly double[] LfoHzTab = { 3.98, 5.56, 6.02, 6.37, 6.88, 9.63, 48.1, 72.2 };

    public void Write(int port, int r, int v)
    {
        port &= 1; r &= 0xFF; v &= 0xFF;
        if (fmCh == 3) port = 0;
        reg[port, r] = (byte)v;
        if (port == 0 && r < 0x10) { ssg?.Write(r, v); return; }
        if (IsOpnb)
        {
            if (port == 0 && r >= 0x10 && r < 0x20) { if (r <= 0x1B) adpcm.Write(r - 0x10, v); return; }   // ADPCM-B
            if (port == 1 && r < 0x30) { adpcmA.Write(r, v); return; }                                    // ADPCM-A
        }
        if (port == 0 && r >= 0x10 && r < 0x20) { rhythm?.Write(r, v); return; }
        if (port == 1 && r < 0x20) { adpcm?.Write(r, v); return; }
        if (port == 0 && r < 0x30)
        {
            switch (r)
            {
                case 0x22: if (Type != Variant.Ym2203) { fm.LfoOn = (v & 8) != 0; fm.LfoHz = LfoHzTab[v & 7]; } break;
                case 0x27:
                    {
                        bool sp = (v & 0xC0) != 0;
                        if (sp != special) { special = sp; ApplyFreq(2); }
                        break;
                    }
                case 0x28:
                    {
                        int c = v & 3;
                        if (c == 3) break;
                        if ((v & 4) != 0) { if (fmCh < 6) break; c += 3; }
                        fm.KeyOn(c, 0, (v & 0x10) != 0);
                        fm.KeyOn(c, 2, (v & 0x20) != 0);
                        fm.KeyOn(c, 1, (v & 0x40) != 0);
                        fm.KeyOn(c, 3, (v & 0x80) != 0);
                        break;
                    }
                case 0x2A: if (Type == Variant.Ym2612) fm.Ch[5].DacValue = (v - 128) / 128.0; break;
                case 0x2B: if (Type == Variant.Ym2612) fm.Ch[5].Dac = (v & 0x80) != 0; break;
            }
            return;
        }
        if (r < 0x30) return;
        int chn = r & 3;
        if (chn == 3) return;
        int ch = chn + port * 3;
        if (ch >= fmCh) return;
        var cc = fm.Ch[ch];
        if (r < 0xA0)
        {
            var o = cc.Ops[(r >> 2) & 3];
            switch (r & 0xF0)
            {
                case 0x30: o.Dt1 = (v >> 4) & 7; o.Mul = v & 15; fm.UpdateIncrements(ch); break;
                case 0x40: o.Tl = v & 0x7F; break;
                case 0x50: o.Ks = v >> 6; o.Ar = v & 0x1F; break;
                case 0x60: o.AmOn = (v & 0x80) != 0 && Type != Variant.Ym2203; o.D1r = v & 0x1F; break;
                case 0x70: o.D2r = v & 0x1F; break;
                case 0x80: o.D1l = v >> 4; o.Rr = v & 15; break;
            }
            return;
        }
        switch (r & 0xFC)
        {
            case 0xA0: fnum[ch] = ((latch[ch] & 7) << 8) | v; block[ch] = (latch[ch] >> 3) & 7; ApplyFreq(ch); break;
            case 0xA4: latch[ch] = v; break;
            case 0xA8: if (port == 0) { spFnum[chn] = ((spLatch & 7) << 8) | v; spBlock[chn] = (spLatch >> 3) & 7; if (special) ApplyFreq(2); } break;
            case 0xAC: if (port == 0) spLatch = v; break;
            case 0xB0: cc.Fb = (v >> 3) & 7; cc.Algo = v & 7; break;
            case 0xB4:
                if (Type != Variant.Ym2203) { cc.Left = (v & 0x80) != 0; cc.Right = (v & 0x40) != 0; cc.Ams = (v >> 4) & 3; cc.Pms = v & 7; }
                break;
        }
    }

    private double Hz(int f, int b) { return ((f << b) >> 1) * (double)NativeRate / 1048576.0; }
    private static int KeyCode(int f, int b) { return (b << 2) | FkTable[(f >> 7) & 15]; }

    private void ApplyFreq(int ch)
    {
        var c = fm.Ch[ch];
        c.Hz = Hz(fnum[ch], block[ch]);
        c.KeyCode = KeyCode(fnum[ch], block[ch]);
        for (int k = 0; k < 4; k++) c.Ops[k].OwnHz = -1;
        if (ch == 2 && special)
        {
            // fm.c: M1 = A9, C1 = AA, M2 = A8, C2 = 채널 (A2)
            SetOwn(c.Ops[0], 1); SetOwn(c.Ops[2], 2); SetOwn(c.Ops[1], 0);
        }
        fm.UpdateIncrements(ch);
    }

    private void SetOwn(FmEngine.Op o, int i) { o.OwnHz = Hz(spFnum[i], spBlock[i]); o.OwnKeyCode = KeyCode(spFnum[i], spBlock[i]); }

    // ------------------------------------------------------------ 소리

    public void Generate(out int left, out int right)
    {
        fm.Generate(out double l, out double r);
        l *= 6000; r *= 6000;
        if (ssg != null) { double s = ssg.Generate() * 0.5; l += s; r += s; }
        if (rhythm != null) { rhythm.Generate(out double rl, out double rr); l += rl; r += rr; }
        if (adpcm != null) { adpcm.Generate(out double al, out double ar); l += al; r += ar; }
        if (adpcmA != null) { adpcmA.Generate(out double al, out double ar); l += al; r += ar; }
        left = SoundChips.Clip(l);
        right = SoundChips.Clip(r);
    }

    // ------------------------------------------------------------ 조절 · 표시

    private bool Map(int ch, out int kind, out int i)
    {
        kind = 0; i = ch;
        if (ch < 0) return false;
        if (ch < fmShown.Length) { i = fmShown[ch]; return true; }
        i = ch - fmShown.Length;
        if (ssg != null) { if (i < 3) { kind = 1; return true; } i -= 3; }
        if (rhythm != null) { if (i < 6) { kind = 2; return true; } if (i == 6) { kind = 3; return true; } }
        if (adpcmA != null) { if (i < 6) { kind = 4; return true; } if (i == 6) { kind = 3; return true; } }
        return false;
    }

    public void SetMute(int ch, bool mute)
    {
        if (!Map(ch, out int k, out int i)) return;
        if (k == 0) fm.Ch[i].Mute = mute;
        else if (k == 1) ssg.SetMute(i, mute);
        else if (k == 2) rhythm.Mute[i] = mute;
        else if (k == 4) adpcmA.Mute[i] = mute;
        else adpcm.Mute = mute;
    }

    public void SetPitch(int ch, double ratio)
    {
        if (!Map(ch, out int k, out int i)) return;
        if (k == 0) fm.Ch[i].Pitch = ratio > 0 ? ratio : 1;
        else if (k == 1) ssg.SetPitch(i, ratio);
        else if (k == 3) adpcm.Pitch = ratio > 0 ? ratio : 1;
        else if (k == 4) adpcmA.Pitch[i] = ratio > 0 ? ratio : 1;
    }

    public double ChannelFrequency(int ch)
    {
        if (!Map(ch, out int k, out int i)) return 0;
        if (k == 0) return fm.Keyed(i) && !fm.Ch[i].Dac ? fm.Ch[i].Hz : 0;
        if (k == 1) return ssg.ChannelFrequency(i);
        return 0;
    }

    public float ChannelLevel(int ch)
    {
        if (!Map(ch, out int k, out int i)) return 0;
        if (k == 0) return fm.Level(i);
        if (k == 1) return ssg.ChannelLevel(i);
        if (k == 2) return rhythm.Level(i);
        if (k == 4) return adpcmA.Level(i);
        return adpcm.Playing ? adpcm.Volume : 0;
    }

    public bool ChannelActive(int ch)
    {
        if (!Map(ch, out int k, out int i)) return false;
        if (k == 0) return fm.Ch[i].Dac;
        if (k == 1) return ssg.ChannelActive(i);
        if (k == 2) return rhythm.Level(i) > 0.02f;
        if (k == 4) return adpcmA.Playing(i);
        return adpcm.Playing;
    }

    // ================================================================ OPNA 리듬 (합성)

    private sealed class Rhythm
    {
        private readonly int rate;
        private readonly double[] env = new double[6], phase = new double[6], hz = new double[6];
        private readonly int[] level = new int[6];
        private readonly bool[] l = new bool[6], r = new bool[6];
        public readonly bool[] Mute = new bool[6];
        private int total = 63;
        private int lfsr = 1;
        private double hp;
        // 줄어드는 시간 (초), 기본 음높이
        private static readonly double[] Decay = { 0.18, 0.14, 0.45, 0.06, 0.25, 0.03 };
        private static readonly double[] BaseHz = { 62, 190, 0, 0, 140, 1100 };

        public Rhythm(int rate) { this.rate = rate; Reset(); }

        public void Reset()
        {
            for (int i = 0; i < 6; i++) { env[i] = 0; phase[i] = 0; level[i] = 0; l[i] = r[i] = true; }
            total = 0;
        }

        public void Write(int a, int v)
        {
            if (a == 0x10)
            {
                if ((v & 0x80) != 0) { for (int i = 0; i < 6; i++) if ((v & (1 << i)) != 0) env[i] = 0; return; }
                for (int i = 0; i < 6; i++) if ((v & (1 << i)) != 0) { env[i] = 1; phase[i] = 0; hz[i] = BaseHz[i]; }
            }
            else if (a == 0x11) total = v & 0x3F;
            else if (a >= 0x18 && a <= 0x1D) { int i = a - 0x18; level[i] = v & 0x1F; l[i] = (v & 0x80) != 0; r[i] = (v & 0x40) != 0; }
        }

        public float Level(int i) { return (float)env[i]; }

        public void Generate(out double ol, out double or)
        {
            ol = or = 0;
            int b = (lfsr ^ (lfsr >> 3)) & 1; lfsr = (lfsr >> 1) | (b << 16);
            double noise = (lfsr & 1) != 0 ? 1 : -1;
            double hpn = noise - hp; hp = noise;   // 높은 쪽 노이즈
            double gTotal = Math.Pow(10, -(63 - total) * 0.75 / 20.0);
            for (int i = 0; i < 6; i++)
            {
                if (env[i] <= 0.0005) { env[i] = 0; continue; }
                double s;
                switch (i)
                {
                    case 0: hz[i] = Math.Max(38, hz[i] * (1 - 3.0 / rate)); s = Math.Sin(2 * Math.PI * phase[i]); break;              // BD
                    case 1: s = 0.45 * Math.Sin(2 * Math.PI * phase[i]) + 0.55 * noise; break;                                       // SD
                    case 2: s = hpn * 0.5; break;                                                                                     // CY
                    case 3: s = hpn * 0.5; break;                                                                                     // HH
                    case 4: hz[i] = Math.Max(90, hz[i] * (1 - 1.5 / rate)); s = Math.Sin(2 * Math.PI * phase[i]); break;              // TOM
                    default: s = Math.Sin(2 * Math.PI * phase[i]) * 0.7 + noise * 0.3; break;                                        // RIM
                }
                phase[i] += hz[i] / rate; if (phase[i] >= 1) phase[i] -= 1;
                double g = env[i] * Math.Pow(10, -(31 - level[i]) * 0.75 / 20.0) * gTotal * 9000;
                env[i] *= Math.Exp(-1.0 / (Decay[i] * rate / 4.6));
                if (Mute[i]) continue;
                if (l[i]) ol += s * g;
                if (r[i]) or += s * g;
            }
        }
    }

    // ================================================================ ADPCM-B (Yamaha delta-T)

    private sealed class AdpcmB
    {
        private readonly int rate;
        private byte[] mem = new byte[0x40000];
        private int ctrl1, ctrl2, start, stop, deltaN, vol, writeAt;
        private bool playing;
        private double pos;     // 니블 위치
        private int nibble, acc, step = 127, prev;
        public bool Mute;
        public double Pitch = 1.0;
        private bool l = true, r = true;

        private static readonly int[] StepScale = { 57, 57, 57, 57, 77, 102, 128, 153 };

        public AdpcmB(int rate) { this.rate = rate; }

        public bool Playing { get { return playing; } }
        public float Volume { get { return vol / 255f; } }

        public void Reset() { ctrl1 = ctrl2 = start = stop = deltaN = vol = writeAt = 0; playing = false; }

        public void Load(byte[] data, int at)
        {
            if (at < 0) return;
            int need = at + data.Length;
            if (need > mem.Length) Array.Resize(ref mem, Math.Min(16 << 20, need));
            Array.Copy(data, 0, mem, at, Math.Min(data.Length, mem.Length - at));
        }

        /// <summary>YM2610: 주소 = 256바이트 단위.</summary>
        public bool Unit256;
        private int Unit { get { return Unit256 ? 256 : (ctrl2 & 2) != 0 ? 32 : 4; } }

        public void Write(int a, int v)
        {
            switch (a)
            {
                case 0x00:
                    ctrl1 = v;
                    if ((v & 1) != 0) { playing = false; break; }
                    if ((v & 0x60) == 0x60) writeAt = start * Unit;   // 메모리 쓰기 준비
                    if ((v & 0x80) != 0 && (v & 0x40) == 0) { playing = true; pos = start * Unit * 2; nibble = (int)pos; acc = 0; prev = 0; step = 127; }
                    else if ((v & 0x80) == 0) playing = false;
                    break;
                case 0x01: ctrl2 = v; l = (v & 0x80) != 0; r = (v & 0x40) != 0; break;
                case 0x02: start = (start & 0xFF00) | v; break;
                case 0x03: start = (start & 0x00FF) | (v << 8); break;
                case 0x04: stop = (stop & 0xFF00) | v; break;
                case 0x05: stop = (stop & 0x00FF) | (v << 8); break;
                case 0x08:
                    if ((ctrl1 & 0x60) == 0x60 && writeAt >= 0)
                    {
                        if (writeAt >= mem.Length && writeAt < (16 << 20)) Array.Resize(ref mem, Math.Min(16 << 20, writeAt * 2));
                        if (writeAt < mem.Length) mem[writeAt] = (byte)v;
                        writeAt++;
                    }
                    break;
                case 0x09: deltaN = (deltaN & 0xFF00) | v; break;
                case 0x0A: deltaN = (deltaN & 0x00FF) | (v << 8); break;
                case 0x0B: vol = v; break;
            }
        }

        public void Generate(out double ol, out double or)
        {
            ol = or = 0;
            if (!playing) return;
            pos += deltaN / 65536.0 * Pitch;
            int endNibble = ((stop + 1) * Unit) * 2;
            while (nibble < (int)pos)
            {
                if (nibble >= endNibble || (nibble >> 1) >= mem.Length)
                {
                    if ((ctrl1 & 0x10) != 0) { pos -= nibble - start * Unit * 2; nibble = start * Unit * 2; acc = 0; step = 127; continue; }
                    playing = false; return;
                }
                int b = mem[nibble >> 1];
                int n = (nibble & 1) == 0 ? b >> 4 : b & 15;
                prev = acc;
                int diff = ((2 * (n & 7) + 1) * step) >> 3;
                acc += (n & 8) != 0 ? -diff : diff;
                acc = Math.Max(-32768, Math.Min(32767, acc));
                step = Math.Max(127, Math.Min(24576, (step * StepScale[n & 7]) >> 6));
                nibble++;
            }
            if (Mute) return;
            double frac = pos - Math.Floor(pos);
            double s = (prev + (acc - prev) * frac) * vol / 256.0 * 0.6;
            if (l) ol = s;
            if (r) or = s;
        }
    }

    // ================================================================ ADPCM-A (YM2610 · 6채널)

    /// <summary>
    /// YM2610 ADPCM-A: 채널 6개, 클럭 / 432 (8MHz → 18.5kHz) 고정 속도, 4비트 → 12비트 누산 (단계 표 49칸).
    /// 레지스터: 00 키 (bit7 = 끄기, bit0~5 채널), 01 전체 감쇠 (6비트), 08~0D 채널 좌 · 우 · 감쇠 (5비트), 10~15 · 18~1D 시작, 20~25 · 28~2D 끝 (256바이트 단위).
    /// 크기: 감쇠 칸 = (채널 ^ 0x1F) + (전체 ^ 0x3F), 63 이상 소리 없음, 곱 15 − (칸 & 7), 나눗셈 2^(5 + 칸 >> 3) (ymfm과 같은 셈).
    /// </summary>
    private sealed class AdpcmA
    {
        private static readonly int[] Steps =
        {
            16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
            279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        };
        private static readonly int[] StepInc = { -1, -1, -1, -1, 2, 5, 7, 9 };

        private byte[] mem = new byte[0x10000];
        private readonly double step;
        private readonly int[] start = new int[6], end = new int[6], level = new int[6], acc = new int[6], idx = new int[6], cur = new int[6], outv = new int[6];
        private readonly bool[] on = new bool[6], l = new bool[6], r = new bool[6];
        private readonly double[] phase = new double[6];
        public readonly bool[] Mute = new bool[6];
        public readonly double[] Pitch = { 1, 1, 1, 1, 1, 1 };
        private int total;

        public AdpcmA(double clock, int rate) { step = clock / 432.0 / rate; Reset(); }

        public void Reset()
        {
            for (int c = 0; c < 6; c++) { start[c] = end[c] = level[c] = acc[c] = idx[c] = cur[c] = outv[c] = 0; on[c] = false; l[c] = r[c] = true; phase[c] = 0; }
            total = 0;
        }

        public void Load(byte[] data, int at)
        {
            if (at < 0) return;
            int need = at + data.Length;
            if (need > mem.Length) Array.Resize(ref mem, Math.Min(16 << 20, need));
            Array.Copy(data, 0, mem, at, Math.Min(data.Length, mem.Length - at));
        }

        public void Write(int a, int v)
        {
            if (a == 0x00)
            {
                for (int c = 0; c < 6; c++)
                {
                    if ((v & (1 << c)) == 0) continue;
                    if ((v & 0x80) != 0) on[c] = false;
                    else { on[c] = true; cur[c] = (start[c] << 8) * 2; acc[c] = 0; idx[c] = 0; phase[c] = 0; outv[c] = 0; }
                }
                return;
            }
            if (a == 0x01) { total = v & 0x3F; return; }
            int ch = a & 7;
            if (ch > 5) return;
            switch (a & 0xF8)
            {
                case 0x08: l[ch] = (v & 0x80) != 0; r[ch] = (v & 0x40) != 0; level[ch] = v & 0x1F; break;
                case 0x10: start[ch] = (start[ch] & 0xFF00) | v; break;
                case 0x18: start[ch] = (start[ch] & 0x00FF) | (v << 8); break;
                case 0x20: end[ch] = (end[ch] & 0xFF00) | v; break;
                case 0x28: end[ch] = (end[ch] & 0x00FF) | (v << 8); break;
            }
        }

        public bool Playing(int c) { return on[c]; }

        public float Level(int c)
        {
            if (!on[c]) return 0;
            int vol = (level[c] ^ 0x1F) + (total ^ 0x3F);
            return vol >= 63 ? 0 : 1f - vol / 63f;
        }

        public void Generate(out double ol, out double or)
        {
            ol = or = 0;
            for (int c = 0; c < 6; c++)
            {
                if (!on[c]) continue;
                phase[c] += step * Pitch[c];
                while (phase[c] >= 1)
                {
                    phase[c] -= 1;
                    int last = ((end[c] << 8) | 0xFF) * 2 + 1;
                    if (cur[c] > last || (cur[c] >> 1) >= mem.Length) { on[c] = false; break; }
                    int b = mem[cur[c] >> 1];
                    int n = (cur[c] & 1) == 0 ? b >> 4 : b & 15;
                    cur[c]++;
                    int delta = (2 * (n & 7) + 1) * Steps[idx[c]] / 8;
                    if ((n & 8) != 0) delta = -delta;
                    acc[c] = (acc[c] + delta) & 0xFFF;
                    idx[c] = Math.Max(0, Math.Min(48, idx[c] + StepInc[n & 7]));
                    int vol = (level[c] ^ 0x1F) + (total ^ 0x3F);
                    if (vol >= 63) { outv[c] = 0; continue; }
                    int mul = 15 - (vol & 7), shift = 4 + 1 + (vol >> 3);
                    outv[c] = (((short)(acc[c] << 4)) * mul >> shift) & ~3;
                }
                if (Mute[c]) continue;
                double s = outv[c] * 0.5;
                if (l[c]) ol += s;
                if (r[c]) or += s;
            }
        }
    }
}
