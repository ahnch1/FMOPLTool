using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// YM2151 (OPM): 8채널 4-op FM, LFO(톱니 · 사각 · 삼각 · 노이즈), 채널 8 노이즈. 아케이드 · X68000 · MSX SFG-01.
/// 샘플레이트 = 클럭 / 64 (3.579545MHz → 55930Hz). 레지스터는 포트 하나 (주소 · 값).
/// </summary>
public sealed class Ym2151 : ISoundChip
{
    public const double DefaultClock = 3579545;
    private readonly FmEngine fm;
    private readonly byte[] reg = new byte[256];
    private readonly double clock;

    public Ym2151(double clock = DefaultClock)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = (int)Math.Round(this.clock / 64);
        fm = new FmEngine(FmEngine.Kind.Opm, 8, NativeRate);
        Reset();
    }

    public string Name { get { return "YM2151 (OPM)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 8; } }
    public string ChannelLabel(int ch) { return "M" + (ch + 1); }

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        fm.Reset();
        fm.LfoOn = true;
    }

    // 음 번호 (KC 아래 4비트) → 반음 (C#=0 … C=11). 3 · 7 · 11 · 15는 쓰지 않는 칸 (앞 칸과 같게)
    private static readonly int[] NoteMap = { 0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11 };

    public void Write(int port, int r, int v)
    {
        r &= 0xFF; v &= 0xFF;
        reg[r] = (byte)v;
        if (r < 0x20)
        {
            switch (r)
            {
                case 0x08:
                    {
                        int ch = v & 7;
                        fm.KeyOn(ch, 0, (v & 0x08) != 0);   // M1
                        fm.KeyOn(ch, 2, (v & 0x10) != 0);   // C1
                        fm.KeyOn(ch, 1, (v & 0x20) != 0);   // M2
                        fm.KeyOn(ch, 3, (v & 0x40) != 0);   // C2
                        break;
                    }
                case 0x0F:
                    fm.Ch[7].Noise = (v & 0x80) != 0;
                    fm.NoiseHz = clock / (64.0 * Math.Max(1, 32 - (v & 0x1F))) * 2;
                    break;
                case 0x18: fm.LfoHz = 0.0081 * Math.Pow(52.9 / 0.0081, v / 255.0); break;
                case 0x19: if ((v & 0x80) != 0) fm.Pmd = v & 0x7F; else fm.Amd = v & 0x7F; break;
                case 0x1B: fm.LfoWave = v & 3; break;
            }
            return;
        }
        if (r < 0x40)
        {
            int ch = r & 7;
            var c = fm.Ch[ch];
            switch (r & 0xF8)
            {
                case 0x20: c.Right = (v & 0x80) != 0; c.Left = (v & 0x40) != 0; c.Fb = (v >> 3) & 7; c.Algo = v & 7; break;
                case 0x28: case 0x30: UpdateFreq(ch); break;
                case 0x38: c.Pms = (v >> 4) & 7; c.Ams = v & 3; break;
            }
            return;
        }
        {
            int ch = r & 7, op = (r >> 3) & 3;
            var o = fm.Ch[ch].Ops[op];
            switch (r & 0xE0)
            {
                case 0x40: o.Dt1 = (v >> 4) & 7; o.Mul = v & 15; fm.UpdateIncrements(ch); break;
                case 0x60: o.Tl = v & 0x7F; break;
                case 0x80: o.Ks = v >> 6; o.Ar = v & 0x1F; break;
                case 0xA0: o.AmOn = (v & 0x80) != 0; o.D1r = v & 0x1F; break;
                case 0xC0: o.Dt2 = v >> 6; o.D2r = v & 0x1F; fm.UpdateIncrements(ch); break;
                case 0xE0: o.D1l = v >> 4; o.Rr = v & 15; break;
            }
        }
    }

    private void UpdateFreq(int ch)
    {
        int kc = reg[0x28 + ch], kf = reg[0x30 + ch] >> 2;
        int oct = (kc >> 4) & 7, note = kc & 15;
        double semis = oct * 12 + NoteMap[note] + kf / 64.0 - (4 * 12 + 8);   // A4 = KC 0x4A
        var c = fm.Ch[ch];
        c.Hz = 440.0 * Math.Pow(2, semis / 12.0) * clock / DefaultClock;
        c.KeyCode = (kc >> 2) & 31;
        fm.UpdateIncrements(ch);
    }

    public void Generate(out int left, out int right)
    {
        fm.Generate(out double l, out double r);
        left = SoundChips.Clip(l * 6000);
        right = SoundChips.Clip(r * 6000);
    }

    public void SetMute(int ch, bool mute) { if (ch >= 0 && ch < 8) fm.Ch[ch].Mute = mute; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 8) fm.Ch[ch].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int ch) { return ch >= 0 && ch < 8 && fm.Keyed(ch) && !fm.Ch[ch].Noise ? fm.Ch[ch].Hz : 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < 8 ? fm.Level(ch) : 0; }
    public bool ChannelActive(int ch) { return ch == 7 && fm.Ch[7].Noise && fm.Keyed(7); }
}
