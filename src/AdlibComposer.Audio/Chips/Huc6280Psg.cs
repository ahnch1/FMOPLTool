using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// HuC6280 PSG (PC엔진, VGM 0xB9 aa dd: aa 0~9 = $0800~$0809). 채널 6개 = 32칸 5비트 파형 메모리,
/// 채널 5 · 6은 노이즈, DDA(직접 출력), 채널 2가 채널 1의 주파수를 흔드는 LFO.
/// <para>레지스터: 0 채널 고르기, 1 전체 좌우 볼륨, 2 · 3 주기(12비트), 4 켜기 · DDA · 볼륨(5비트), 5 채널 좌우 볼륨, 6 파형 · DDA 값,
/// 7 노이즈, 8 LFO 주기, 9 LFO 제어. 소리 = 3.58MHz / (32 × 주기).</para>
/// <para>볼륨은 로그 (1.5dB 칸): 채널 볼륨 5비트, 채널 · 전체 좌우 볼륨 4비트는 표로 칸 수로 바꿔 더한다. 31칸이면 소리 없음 (MAME c6280과 같은 관례).</para>
/// </summary>
public sealed class Huc6280Psg : ISoundChip
{
    public const double DefaultClock = 3579545;
    private readonly double clock;
    private readonly int rate;

    private sealed class Ch
    {
        public int Freq, Control, Balance = 0xFF, Dda, WaveIndex, Noise;
        public readonly byte[] Wave = new byte[32];
        public double Phase;   // 파형 위치 (칸, 0~32)
        public double NoisePhase; public int Lfsr = 1, NoiseOut;
        public bool On { get { return (Control & 0x80) != 0; } }
        public bool DdaMode { get { return (Control & 0x40) != 0; } }
        public int Volume { get { return Control & 0x1F; } }
    }

    private readonly Ch[] ch = new Ch[6];
    private int select, mainBalance, lfoFreq, lfoCtrl;
    private readonly bool[] mute = new bool[6];
    private readonly double[] pitch = { 1, 1, 1, 1, 1, 1 };
    private static readonly double[] Atten = new double[32];
    /// <summary>4비트 좌우 볼륨 → 1.5dB 칸 (MAME c6280 scale_tab).</summary>
    private static readonly int[] Scale = { 0x00, 0x03, 0x05, 0x07, 0x09, 0x0B, 0x0D, 0x0F, 0x10, 0x13, 0x15, 0x17, 0x19, 0x1B, 0x1D, 0x1F };

    static Huc6280Psg()
    {
        for (int i = 0; i < Atten.Length; i++) Atten[i] = Math.Pow(10, -1.5 * i / 20.0);   // 1.5dB 단위
    }

    public Huc6280Psg(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        this.rate = rate;
        for (int i = 0; i < 6; i++) ch[i] = new Ch();
    }

    public string Name { get { return "HuC6280 (PC Engine)"; } }
    public int NativeRate { get { return rate; } }
    public int ChannelCount { get { return 6; } }
    public string ChannelLabel(int c) { return "W" + (c + 1); }

    public void Reset()
    {
        for (int i = 0; i < 6; i++) ch[i] = new Ch();
        select = mainBalance = lfoFreq = lfoCtrl = 0;
    }

    public void Write(int port, int reg, int value)
    {
        value &= 0xFF;
        var c = ch[select];
        switch (reg & 0x0F)
        {
            case 0: select = value & 7; if (select > 5) select = 5; break;
            case 1: mainBalance = value; break;
            case 2: c.Freq = (c.Freq & 0xF00) | value; break;
            case 3: c.Freq = (c.Freq & 0xFF) | ((value & 0x0F) << 8); break;
            case 4:
                if ((c.Control & 0x40) != 0 && (value & 0x40) == 0) c.WaveIndex = 0;   // DDA를 끄면 파형 쓰기 위치를 처음으로
                c.Control = value;
                break;
            case 5: c.Balance = value; break;
            case 6:
                if (c.DdaMode) c.Dda = value & 0x1F;
                else { c.Wave[c.WaveIndex] = (byte)(value & 0x1F); c.WaveIndex = (c.WaveIndex + 1) & 31; }
                break;
            case 7: if (select >= 4) c.Noise = value; break;
            case 8: lfoFreq = value; break;
            case 9: lfoCtrl = value; break;
        }
    }

    private int Period(Ch c) { int p = c.Freq & 0xFFF; return p == 0 ? 0x1000 : p; }

    private bool NoiseOn(int i) { return i >= 4 && (ch[i].Noise & 0x80) != 0; }

    public void Generate(out int left, out int right)
    {
        double l = 0, r = 0;
        int ml = (mainBalance >> 4) & 15, mr = mainBalance & 15;
        bool lfo = (lfoCtrl & 3) != 0 && (lfoCtrl & 0x80) == 0;
        for (int i = 0; i < 6; i++)
        {
            var c = ch[i];
            if (!c.On && !c.DdaMode) continue;
            if (lfo && i == 1) { AdvanceWave(c, Period(c), pitch[i]); continue; }   // LFO 채널은 소리 내지 않고 흔들기만
            double sample;
            if (c.DdaMode) sample = c.Dda - 16;
            else if (NoiseOn(i))
            {
                int np = ((~c.Noise) & 0x1F) * 64 + 64;
                c.NoisePhase += clock / rate / np * 32 * pitch[i];
                while (c.NoisePhase >= 1) { c.NoisePhase -= 1; int fb = ((c.Lfsr >> 0) ^ (c.Lfsr >> 1) ^ (c.Lfsr >> 11) ^ (c.Lfsr >> 12) ^ (c.Lfsr >> 17)) & 1; c.Lfsr = (c.Lfsr >> 1) | (fb << 17); if (c.Lfsr == 0) c.Lfsr = 1; c.NoiseOut = (c.Lfsr & 1) != 0 ? 15 : -16; }
                sample = c.NoiseOut;
            }
            else
            {
                int per = Period(c);
                if (lfo && i == 0)
                {
                    var m = ch[1];
                    int shift = (lfoCtrl & 3) == 1 ? 0 : (lfoCtrl & 3) == 2 ? 4 : 8;
                    per += (m.Wave[(int)m.Phase & 31] - 16) << shift;
                    if (per < 1) per = 1;
                }
                sample = AdvanceWave(c, per, pitch[i]);
            }
            if (mute[i] || !c.On) continue;
            // MAME c6280과 같은 방법: 감쇠 칸(1.5dB) = (31 − 채널 볼륨) + (31 − 좌우 표) + (31 − 전체 좌우 표), 31칸 이상이면 소리 없음
            int baseAtt = 31 - c.Volume;
            int al = baseAtt + (31 - Scale[(c.Balance >> 4) & 15]) + (31 - Scale[ml]);
            int ar = baseAtt + (31 - Scale[c.Balance & 15]) + (31 - Scale[mr]);
            if (al < 31) l += sample * Atten[al];
            if (ar < 31) r += sample * Atten[ar];
        }
        left = SoundChips.Clip(l * 330);
        right = SoundChips.Clip(r * 330);
    }

    /// <summary>파형을 이 샘플 동안 나아가게 하고 지나간 칸들의 평균을 돌려준다 (−16~15).</summary>
    private double AdvanceWave(Ch c, int period, double ratio)
    {
        double step = clock / ((double)period * rate) * ratio;   // 샘플 하나에 나아가는 칸 수
        double p0 = c.Phase;
        double p1 = p0 + step;
        double sum;
        if (step >= 32) { sum = 0; for (int k = 0; k < 32; k++) sum += c.Wave[k]; sum = sum / 32 * step; }
        else
        {
            sum = 0;
            double p = p0;
            while (p < p1)
            {
                double next = Math.Min(Math.Floor(p) + 1, p1);
                sum += c.Wave[(int)p & 31] * (next - p);
                p = next;
            }
        }
        c.Phase = p1 % 32.0;
        return sum / step - 16;
    }

    public void SetMute(int c, bool on) { if (c >= 0 && c < 6) mute[c] = on; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 6) pitch[c] = ratio > 0 ? ratio : 1.0; }

    public double ChannelFrequency(int i)
    {
        var c = ch[i];
        if (!c.On || c.DdaMode || NoiseOn(i) || c.Volume == 0 || (c.Balance == 0)) return 0;
        return clock / (32.0 * Period(c));
    }

    public float ChannelLevel(int i) { var c = ch[i]; return c.On ? c.Volume / 31f : 0; }

    public bool ChannelActive(int i) { var c = ch[i]; return c.On && c.Volume > 0 && (c.DdaMode || NoiseOn(i)); }
}
