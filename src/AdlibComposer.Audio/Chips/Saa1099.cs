using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Philips SAA1099: 사각파 6 + 노이즈 2 + 엔벨로프 2, 채널마다 왼쪽 · 오른쪽 4비트 음량. Creative Game Blaster (C/MS) · SAM Coupé.
/// 소리 높이 = 클럭 / 512 × 2^옥타브 / (511 − 주파수). 셋씩 두 묶음 (0~2 · 3~5): 묶음마다 노이즈 하나 · 엔벨로프 하나 (채널 2 · 5에 걸림).
/// <para>레지스터: 0x00~0x05 음량 (아래 = 왼쪽, 위 = 오른쪽), 0x08~0x0D 주파수, 0x10~0x12 옥타브 (채널 둘씩), 0x14 사각파 켜기, 0x15 노이즈 켜기,
/// 0x16 노이즈 속도 (0 = 클럭/256, 1 = /512, 2 = /1024, 3 = 채널 0 · 3의 주파수), 0x18 · 0x19 엔벨로프, 0x1C bit0 전체 켜기 · bit1 리셋.
/// VGM 0xBD aa dd (aa bit7 = 둘째 칩). MAME saa1099를 따른다.</para>
/// </summary>
public sealed class Saa1099 : ISoundChip
{
    public const double DefaultClock = 8000000;

    // 엔벨로프 모양 8가지 × 64칸 (MAME 표)
    private static readonly byte[][] EnvTable = BuildEnv();

    private static byte[][] BuildEnv()
    {
        var t = new byte[8][];
        for (int m = 0; m < 8; m++) t[m] = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int down = 15 - (i & 15), up = i & 15;
            t[0][i] = 0;
            t[1][i] = 15;
            t[2][i] = (byte)(i < 16 ? down : 0);                          // 한 번 줄어듦
            t[3][i] = (byte)down;                                         // 되풀이 줄어듦
            t[4][i] = (byte)(i < 16 ? up : i < 32 ? down : 0);            // 한 번 세모
            t[5][i] = (byte)((i & 16) == 0 ? up : down);                  // 되풀이 세모
            t[6][i] = (byte)(i < 16 ? up : 0);                            // 한 번 커짐
            t[7][i] = (byte)up;                                           // 되풀이 커짐
        }
        return t;
    }

    private sealed class Ch
    {
        public int Freq, Octave;
        public int AmpL, AmpR;
        public int EnvL = 16, EnvR = 16;
        public bool FreqOn, NoiseOn;
        public double Counter;
        public int Level;
        public double Pitch = 1;
    }

    private readonly Ch[] ch = new Ch[6];
    private readonly double[] noiseCounter = new double[2];
    private readonly int[] noiseParam = new int[2];
    private readonly int[] noiseLevel = new int[2];
    private readonly bool[] envEnable = new bool[2], envReverse = new bool[2], envExt = new bool[2], env3Bit = new bool[2];
    private readonly int[] envMode = new int[2], envStep = new int[2];
    private readonly bool[] mute = new bool[6];
    private bool allOn;
    private readonly double clock;
    private readonly int rate;

    public Saa1099(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        this.rate = rate > 0 ? rate : 44100;
        NativeRate = this.rate;
        for (int i = 0; i < 6; i++) ch[i] = new Ch();
        Reset();
    }

    public string Name { get { return "Philips SAA1099"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 6; } }
    public string ChannelLabel(int c) { return "Q" + (c + 1); }

    public void Reset()
    {
        foreach (var c in ch) { c.Freq = c.Octave = c.AmpL = c.AmpR = 0; c.EnvL = c.EnvR = 16; c.FreqOn = c.NoiseOn = false; c.Counter = 0; c.Level = 0; }
        for (int i = 0; i < 2; i++) { noiseCounter[i] = 0; noiseParam[i] = 0; noiseLevel[i] = 1; envEnable[i] = false; envStep[i] = 0; envMode[i] = 0; }
        allOn = false;
    }

    private double ToneRate(Ch c) { return 2.0 * clock / 512.0 * (1 << c.Octave) / (511.0 - c.Freq); }   // 뒤집는 횟수 / 초

    public void Write(int port, int reg, int v)
    {
        reg &= 0x1F; v &= 0xFF;
        // 바깥 클럭 엔벨로프: 주소를 쓸 때마다 한 칸
        for (int e = 0; e < 2; e++) if (envExt[e]) EnvStep(e);
        switch (reg)
        {
            case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:
                ch[reg].AmpL = v & 15; ch[reg].AmpR = v >> 4; break;
            case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D:
                ch[reg - 8].Freq = v; break;
            case 0x10: case 0x11: case 0x12:
                ch[(reg - 0x10) * 2].Octave = v & 7;
                ch[(reg - 0x10) * 2 + 1].Octave = (v >> 4) & 7;
                break;
            case 0x14: for (int i = 0; i < 6; i++) ch[i].FreqOn = (v & (1 << i)) != 0; break;
            case 0x15: for (int i = 0; i < 6; i++) ch[i].NoiseOn = (v & (1 << i)) != 0; break;
            case 0x16: noiseParam[0] = v & 3; noiseParam[1] = (v >> 4) & 3; break;
            case 0x18: case 0x19:
                {
                    int e = reg - 0x18;
                    envReverse[e] = (v & 1) != 0;
                    envMode[e] = (v >> 1) & 7;
                    env3Bit[e] = (v & 0x10) != 0;
                    envExt[e] = (v & 0x20) != 0;
                    envEnable[e] = (v & 0x80) != 0;
                    envStep[e] = 0;
                    ApplyEnv(e);
                    break;
                }
            case 0x1C:
                allOn = (v & 1) != 0;
                if ((v & 2) != 0)
                {
                    foreach (var c in ch) { c.Level = 0; c.Counter = 0; }
                }
                break;
        }
    }

    private void EnvStep(int e)
    {
        if (!envEnable[e]) return;
        int s = envStep[e];
        envStep[e] = ((s + 1) & 0x3F) | (s & 0x20);   // 뒤 절반에 들어가면 그 안에서 되풀이
        ApplyEnv(e);
    }

    private void ApplyEnv(int e)
    {
        var c = ch[e * 3 + 2];
        if (!envEnable[e]) { c.EnvL = c.EnvR = 16; return; }
        int mask = env3Bit[e] ? 14 : 15;
        int l = EnvTable[envMode[e]][envStep[e]] & mask;
        c.EnvL = l;
        c.EnvR = envReverse[e] ? (15 - l) & mask : l;
    }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        if (!allOn) return;
        int ol = 0, or = 0;
        for (int i = 0; i < 6; i++)
        {
            var c = ch[i];
            c.Counter -= ToneRate(c) * c.Pitch;
            while (c.Counter < 0)
            {
                c.Counter += rate;
                c.Level ^= 1;
                if (i == 1 && !envExt[0]) EnvStep(0);   // 안 클럭 엔벨로프: 채널 1 · 4의 주파수로
                if (i == 4 && !envExt[1]) EnvStep(1);
            }
            if (mute[i]) continue;
            // 엔벨로프 채널은 음량 bit0을 쓰지 않는다
            int al = c.AmpL, ar = c.AmpR;
            bool env = c.EnvL < 16;
            if (env) { al &= 14; ar &= 14; }
            int el = env ? c.EnvL : 16, er = env ? c.EnvR : 16;
            if (c.FreqOn && c.Level != 0) { ol += al * el; or += ar * er; }
            if (c.NoiseOn && (noiseLevel[i / 3] & 1) != 0) { ol -= al * el / 2; or -= ar * er / 2; }
        }
        for (int n = 0; n < 2; n++)
        {
            double f = noiseParam[n] == 3 ? ToneRate(ch[n * 3]) : 2.0 * clock / (256 << noiseParam[n]);
            noiseCounter[n] -= f;
            while (noiseCounter[n] < 0)
            {
                noiseCounter[n] += rate;
                int lv = noiseLevel[n];
                noiseLevel[n] = ((lv & 0x4000) == 0) == ((lv & 0x0040) == 0) ? (lv << 1) | 1 : lv << 1;
                noiseLevel[n] &= 0x7FFF;
            }
        }
        // 한 채널 최대 15 × 16 = 240 → 6채널 ≈ 1440. 16비트로
        left = SoundChips.Clip(ol * 8.0);
        right = SoundChips.Clip(or * 8.0);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 6) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 6) ch[c].Pitch = ratio > 0 ? ratio : 1; }

    public double ChannelFrequency(int c)
    {
        if (c < 0 || c >= 6 || !allOn) return 0;
        var x = ch[c];
        if (!x.FreqOn || (x.AmpL | x.AmpR) == 0 || (x.EnvL == 0 && x.EnvR == 0)) return 0;
        return ToneRate(x) / 2;
    }

    public float ChannelLevel(int c)
    {
        if (c < 0 || c >= 6 || !allOn) return 0;
        var x = ch[c];
        if (!x.FreqOn && !x.NoiseOn) return 0;
        return Math.Max(x.AmpL * Math.Min(16, x.EnvL), x.AmpR * Math.Min(16, x.EnvR)) / 240f;
    }

    public bool ChannelActive(int c) { return ChannelLevel(c) > 0; }
}
