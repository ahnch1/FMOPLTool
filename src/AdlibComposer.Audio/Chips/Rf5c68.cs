using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 리코 RF5C68 (세가 System 18 · System 32 · FM TOWNS · 메가 CD의 RF5C164): PCM 8채널, 웨이브 RAM 64KB, 부호-크기 8비트 표본, FFh = 반복 표시. MAME rf5c68을 따른다.
/// <para>레지스터 (지금 고른 채널): 0 = 음량 (ENV), 1 = 팬 (아래 니블 왼쪽 · 위 오른쪽), 2 · 3 = 걸음 (FD, 11비트 소수), 4 · 5 = 반복 자리 (LS), 6 = 시작 (ST, × 100h),
/// 7 = 제어 (bit7 소리 켬, bit6 = 1이면 아래 3비트로 채널 고름 · 0이면 아래 4비트로 메모리 창 뱅크), 8 = 채널 끔 (bit = 1이면 끔, 1→0이면 시작에서).</para>
/// <para>출력 속도 = 클럭 ÷ 384. VGM 0xB0 aa dd (레지스터), 0xC1 aaaa dd (메모리), RAM 블록 C0h, 머리말 40h.</para>
/// </summary>
public sealed class Rf5c68 : ISoundChip
{
    public const double DefaultClock = 10000000;

    private sealed class Channel
    {
        public bool Enable;
        public int Env, Pan, Step, LoopSt, Start;
        public long Addr;   // 11비트 소수 자리
        public double Pitch = 1;
    }

    private readonly Channel[] ch = new Channel[8];
    private readonly bool[] mute = new bool[8];
    /// <summary>웨이브 RAM 64KB.</summary>
    public readonly byte[] Ram = new byte[0x10000];
    private int cbank, wbank, enable;
    private readonly double clock;
    private double frac;

    public Rf5c68(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
        for (int i = 0; i < 8; i++) ch[i] = new Channel();
        for (int i = 0; i < Ram.Length; i++) Ram[i] = 0xFF;
    }

    public string Name { get { return "Ricoh RF5C68"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 8; } }
    public string ChannelLabel(int c) { return "R" + (c + 1); }
    public void Reset() { foreach (var c in ch) { c.Enable = false; c.Addr = 0; } enable = 0; }

    /// <summary>port 0 = 레지스터 (reg 0~8), port 1 = 웨이브 RAM (reg = 64KB 자리).</summary>
    public void Write(int port, int r, int v)
    {
        v &= 0xFF;
        if (port == 1) { Ram[r & 0xFFFF] = (byte)v; return; }
        var c = ch[cbank];
        switch (r & 0x0F)
        {
            case 0: c.Env = v; break;
            case 1: c.Pan = v; break;
            case 2: c.Step = (c.Step & 0xFF00) | v; break;
            case 3: c.Step = (c.Step & 0x00FF) | (v << 8); break;
            case 4: c.LoopSt = (c.LoopSt & 0xFF00) | v; break;
            case 5: c.LoopSt = (c.LoopSt & 0x00FF) | (v << 8); break;
            case 6: c.Start = v; if (!c.Enable) c.Addr = (long)v << (8 + 11); break;
            case 7:
                enable = (v >> 7) & 1;
                if ((v & 0x40) != 0) cbank = v & 7; else wbank = v & 15;
                break;
            case 8:
                for (int i = 0; i < 8; i++)
                {
                    bool on = (v & (1 << i)) == 0;
                    if (on && !ch[i].Enable) ch[i].Addr = (long)ch[i].Start << (8 + 11);
                    ch[i].Enable = on;
                }
                break;
        }
    }

    /// <summary>CPU 메모리 창 (4KB) 뱅크.</summary>
    public int WindowBank { get { return wbank; } }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        if (enable == 0) return;
        int l = 0, r = 0;
        // 칩 속도 (클럭/384)로 걸음 — 출력 속도와 다르면 걸음을 비율로
        double ratio = clock / 384.0 / NativeRate;
        for (int i = 0; i < 8; i++)
        {
            var c = ch[i];
            if (!c.Enable) continue;
            int lv = (c.Pan & 0x0F) * c.Env, rv = ((c.Pan >> 4) & 0x0F) * c.Env;
            int s = Ram[(int)((c.Addr >> 11) & 0xFFFF)];
            if (s == 0xFF)
            {
                c.Addr = (long)c.LoopSt << 11;
                s = Ram[c.LoopSt & 0xFFFF];
                if (s == 0xFF) continue;
            }
            c.Addr += (long)(c.Step * ratio * c.Pitch);
            if (mute[i]) continue;
            if ((s & 0x80) != 0) { s &= 0x7F; l += (s * lv) >> 5; r += (s * rv) >> 5; }
            else { l -= (s * lv) >> 5; r -= (s * rv) >> 5; }
        }
        left = SoundChips.Clip(l);
        right = SoundChips.Clip(r);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 8) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 8) ch[c].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return c < 0 || c >= 8 || !ch[c].Enable ? 0 : ch[c].Env / 255f; }
    public bool ChannelActive(int c) { return c >= 0 && c < 8 && ch[c].Enable; }
}
