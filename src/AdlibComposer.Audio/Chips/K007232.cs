using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 코나미 K007232 (Super Contra · Thunder Cross · Salamander · TMNT · Twin16 …): PCM 2채널, 7비트 표본 (bit7 = 끝 표시), 반복, 채널마다 128KB 뱅크. MAME k007232 (옛 구현)을 따른다.
/// <para>레지스터: 채널 0 = 0~5, 채널 1 = 6~11 (+0 · +1 피치 9비트, +2 · +3 · +4 시작 17비트, +5 쓰기 = 시작), 13 = 반복 (bit0 · bit1).
/// 음량 · 뱅크는 보드가 바깥에서 정함 → 이 프로그램 안 가짜 레지스터 10h~13h (채널 0 왼 · 오, 채널 1 왼 · 오, 0~255), 14h · 15h (채널 0 · 1 뱅크).</para>
/// <para>걸음: 칩 표본 (클럭 ÷ 128)마다 32 / (512 − 피치) 표본. 출력 = ((표본 &amp; 7Fh) − 40h) × 음량 × 2. VGM에 없는 칩 — 이 프로그램 안 명령 DBh rr vv 00.</para>
/// </summary>
public sealed class K007232 : ISoundChip
{
    public const double DefaultClock = 3579545;

    private sealed class Ch { public bool Play; public int Start, Bank; public double Addr; public int VolL, VolR; public double Pitch = 1; }
    private readonly Ch[] ch = { new Ch(), new Ch() };
    private readonly byte[] reg = new byte[0x20];
    private readonly bool[] mute = new bool[2];
    private byte[] rom = new byte[0];
    private readonly double clock;

    public K007232(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
    }

    public string Name { get { return "Konami K007232"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 2; } }
    public string ChannelLabel(int c) { return "P" + (c + 1); }
    public void Reset() { Array.Clear(reg, 0, reg.Length); foreach (var c in ch) c.Play = false; }

    public void LoadRom(byte[] data) { rom = data ?? new byte[0]; }

    public void Write(int port, int r, int v)
    {
        r &= 0x1F; v &= 0xFF;
        reg[r] = (byte)v;
        switch (r)
        {
            case 0x05: case 0x0B:
                {
                    int i = r == 5 ? 0 : 1, b = i * 6;
                    var c = ch[i];
                    c.Start = ((reg[b + 4] & 1) << 16) | (reg[b + 3] << 8) | reg[b + 2];
                    c.Play = true; c.Addr = 0;
                    break;
                }
            case 0x10: ch[0].VolL = v; break;
            case 0x11: ch[0].VolR = v; break;
            case 0x12: ch[1].VolL = v; break;
            case 0x13: ch[1].VolR = v; break;
            case 0x14: ch[0].Bank = v; break;
            case 0x15: ch[1].Bank = v; break;
        }
    }

    private int Sample(Ch c, int pos)
    {
        if (rom.Length == 0) return 0x80;
        long a = (long)c.Bank * 0x20000 + ((c.Start + pos) & 0x1FFFF);
        return a < rom.Length ? rom[a] : 0x80;
    }

    public void Generate(out int left, out int right)
    {
        int l = 0, r = 0;
        double chipPerOut = clock / 128.0 / NativeRate;
        for (int i = 0; i < 2; i++)
        {
            var c = ch[i];
            if (!c.Play) continue;
            int b = i * 6;
            int f = reg[b] | ((reg[b + 1] & 1) << 8);
            double step = 32.0 / (0x200 - f) * chipPerOut * c.Pitch;
            int oldPos = (int)c.Addr;
            c.Addr += step;
            int pos = (int)c.Addr;
            bool stop = false;
            for (int p = oldPos; p <= pos; p++)
            {
                if ((Sample(c, p) & 0x80) == 0) continue;
                if ((reg[13] & (1 << i)) != 0) { c.Addr = 0; pos = 0; break; }   // 반복: 처음부터
                stop = true; break;
            }
            if (stop) { c.Play = false; continue; }
            if (mute[i]) continue;
            int s = (Sample(c, pos) & 0x7F) - 0x40;
            l += s * c.VolL * 2; r += s * c.VolR * 2;
        }
        left = SoundChips.Clip(l);
        right = SoundChips.Clip(r);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 2) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 2) ch[c].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return c < 0 || c >= 2 || !ch[c].Play ? 0 : Math.Max(ch[c].VolL, ch[c].VolR) / 255f; }
    public bool ChannelActive(int c) { return c >= 0 && c < 2 && ch[c].Play; }
}
