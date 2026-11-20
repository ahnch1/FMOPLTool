using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 남코 CUS30 (System 1 · System 86): 파형 RAM 8목소리, 스테레오, 노이즈. MAME namco_audio (namcos1 모드)를 따른다.
/// <para>메모리 (쓰기 자리 0~13Fh): 000h~0FFh = 파형 RAM (바이트마다 4비트 둘, 위 먼저 → 32칸 × 16가지), 100h + 목소리 × 8 = 레지스터:
/// +0 왼쪽 음량 (아래 4비트), +1 bit4~7 파형 · bit0~3 주파수 위, +2 · +3 주파수 (20비트), +4 오른쪽 음량 · bit7 = <b>다음</b> 목소리의 노이즈 켬.</para>
/// <para>소리 높이 = 주파수 × 클럭 / 2^20 (클럭 = 49.152MHz ÷ 2048 = 24kHz). 노이즈 = 17비트 LFSR, 초당 주파수 아래 바이트 × 187.5번.
/// VGM에 없는 칩: 이 프로그램 안에서만 명령 0xD8 aa bb dd (자리 = aa &lt;&lt; 8 | bb).</para>
/// <para><b>15XX 모드</b> (매피 · 슈퍼 팩맨 · 갤럭시안 계열 6809 보드): 레지스터 00h~3Fh = 목소리 × 8 (+3 음량 (모노), +4 · +5 · +6 bit0~3 주파수 20비트, +6 bit4~6 파형),
/// 파형 = PROM 256바이트 (아래 4비트, 32칸 × 8가지 — 자리 200h~2FFh로 쓴다), 노이즈 없음. 클럭 18.432MHz ÷ 768 = 24kHz.</para>
/// </summary>
public sealed class NamcoCus30 : ISoundChip
{
    public const double DefaultClock = 49152000 / 2048.0;
    public const int Voices = 8;

    private readonly byte[] mem = new byte[0x300];
    /// <summary>15XX 모드 (매피 계열): 레지스터 배치 · 파형 PROM이 다름.</summary>
    public readonly bool Is15xx;
    private readonly int[] freq = new int[Voices], wave = new int[Voices], volL = new int[Voices], volR = new int[Voices];
    private readonly bool[] noise = new bool[Voices], mute = new bool[Voices];
    private readonly double[] phase = new double[Voices], noiseAcc = new double[Voices], pitch = { 1, 1, 1, 1, 1, 1, 1, 1 };
    private readonly int[] noiseSeed = new int[Voices], noiseState = new int[Voices];
    private readonly double clock;

    public NamcoCus30(double clock = DefaultClock, int rate = 44100, bool is15xx = false)
    {
        Is15xx = is15xx;
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
        Reset();
    }

    public string Name { get { return Is15xx ? "Namco 15XX" : "Namco CUS30"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return Voices; } }
    public string ChannelLabel(int c) { return "W" + (c + 1); }

    public void Reset()
    {
        Array.Clear(mem, 0, mem.Length);
        for (int v = 0; v < Voices; v++) { freq[v] = wave[v] = volL[v] = volR[v] = 0; noise[v] = false; phase[v] = noiseAcc[v] = 0; noiseSeed[v] = 1; noiseState[v] = 0; }
    }

    public void Write(int port, int reg, int value)
    {
        int o = reg & 0x3FF;
        if (o >= mem.Length) return;
        int d = value & 0xFF;
        mem[o] = (byte)d;
        if (Is15xx)
        {
            if (o >= 0x40) return;
            int c = o >> 3;
            switch (o & 7)
            {
                case 3: volL[c] = volR[c] = d & 15; break;
                case 6: wave[c] = (d >> 4) & 7; goto case 4;
                case 4: case 5: freq[c] = mem[c * 8 + 4] | (mem[c * 8 + 5] << 8) | ((mem[c * 8 + 6] & 15) << 16); break;
            }
            return;
        }
        if (o < 0x100 || o >= 0x140) return;
        int r = o - 0x100, ch = r >> 3;
        switch (r & 7)
        {
            case 0: volL[ch] = d & 15; break;
            case 1: wave[ch] = (d >> 4) & 15; goto case 3;
            case 2: case 3:
                freq[ch] = ((mem[0x100 + ch * 8 + 1] & 15) << 16) | (mem[0x100 + ch * 8 + 2] << 8) | mem[0x100 + ch * 8 + 3];
                break;
            case 4:
                volR[ch] = d & 15;
                noise[(ch + 1) % Voices] = (d & 0x80) != 0;
                break;
        }
    }

    private int Sample(int v, int idx)
    {
        if (Is15xx) return (mem[0x200 + ((wave[v] * 32 + idx) & 0xFF)] & 15) - 8;
        int b = mem[(wave[v] * 16 + (idx >> 1)) & 0xFF];
        return ((idx & 1) == 0 ? b >> 4 : b & 15) - 8;
    }

    public void Generate(out int left, out int right)
    {
        int l = 0, r = 0;
        for (int v = 0; v < Voices; v++)
        {
            int lv = volL[v], rv = volR[v];
            if (noise[v])
            {
                int f = freq[v] & 0xFF;
                if ((lv | rv) == 0 || f == 0) continue;
                noiseAcc[v] += 187.5 * f * (clock / DefaultClock) * pitch[v] / NativeRate;
                while (noiseAcc[v] >= 1)
                {
                    noiseAcc[v] -= 1;
                    if (((noiseSeed[v] + 1) & 2) != 0) noiseState[v] ^= 1;
                    if ((noiseSeed[v] & 1) != 0) noiseSeed[v] ^= 0x28000;
                    noiseSeed[v] >>= 1;
                }
                if (mute[v]) continue;
                int s = noiseState[v] != 0 ? 7 : -7;
                l += s * (lv >> 1) * 2; r += s * (rv >> 1) * 2;
                continue;
            }
            if (freq[v] == 0 || (lv | rv) == 0) continue;
            phase[v] += freq[v] * clock / 32768.0 * pitch[v] / NativeRate;
            if (phase[v] >= 32) phase[v] %= 32;
            if (mute[v]) continue;
            int w = Sample(v, (int)phase[v]);
            l += w * lv; r += w * rv;
        }
        // 목소리 최대 8 × 15 = 120 → 8목소리 960. 16비트로 (YM2151과 섞을 자리)
        left = SoundChips.Clip(l * 24.0);
        right = SoundChips.Clip(r * 24.0);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < Voices) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < Voices) pitch[c] = ratio > 0 ? ratio : 1; }

    public double ChannelFrequency(int c)
    {
        if (c < 0 || c >= Voices || noise[c] || (volL[c] | volR[c]) == 0) return 0;
        return freq[c] * clock / 1048576.0;
    }

    public float ChannelLevel(int c) { return c < 0 || c >= Voices ? 0 : Math.Max(volL[c], volR[c]) / 15f; }
    public bool ChannelActive(int c) { return c >= 0 && c < Voices && (volL[c] | volR[c]) != 0 && (noise[c] ? (freq[c] & 0xFF) != 0 : freq[c] != 0); }
}
