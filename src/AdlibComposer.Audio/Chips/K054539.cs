using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 코나미 K054539 (Xexex · X-Men · Mystic Warriors · Run and Gun · GX …): PCM 8채널, 8비트 · 16비트 · 4비트 DPCM, 반복, 역재생, 팬. 잔향은 뺌. MAME k054539를 따른다.
/// <para>채널 레지스터 (ch × 20h): +0~2 피치 (24비트, 16.16 한 샘플 걸음), +3 감쇠 (−36dB / 40h), +4 잔향 음량, +5 팬 (81h~8Fh 또는 11h~1Fh, 그 밖 = 가운데), +8~A 반복 자리, +C~E 시작 자리.
/// 200h + ch × 2: +0 bit2~3 형식 (0 = 8비트, 4 = 16비트, 8 = DPCM), bit5 = 역재생, +1 bit0 = 반복. 214h = 키온 비트, 215h = 키오프 비트, 22Ch = 재생 중 (읽기), 22Fh bit0 = 켬 · bit7 = 키온 막음.</para>
/// <para>끝 표시: 8비트 80h, 16비트 8000h, DPCM 88h → 반복이면 반복 자리로, 아니면 키오프. 출력 속도 = 클럭 ÷ 384 (48kHz). VGM 0xD3 pp aa dd (pp bit7 = 둘째 칩), ROM 블록 8Ch, 머리말 A0h · 95h.</para>
/// </summary>
public sealed class K054539 : ISoundChip
{
    public const double DefaultClock = 18432000;
    private static readonly int[] Dpcm = { 0 << 8, 1 << 8, 4 << 8, 9 << 8, 16 << 8, 25 << 8, 36 << 8, 49 << 8, -64 << 8, -49 << 8, -36 << 8, -25 << 8, -16 << 8, -9 << 8, -4 << 8, -1 << 8 };
    private static readonly double[] VolTab = BuildVol(), PanTab = BuildPan();
    private static double[] BuildVol() { var t = new double[256]; for (int i = 0; i < 256; i++) t[i] = Math.Pow(10.0, (-36.0 * i / 0x40) / 20.0) / 4.0; return t; }
    private static double[] BuildPan() { var t = new double[0xF]; for (int i = 0; i < 0xF; i++) t[i] = Math.Sqrt(i) / Math.Sqrt(0xE); return t; }

    private sealed class Channel
    {
        public long Pos;      // 바이트 자리 (DPCM은 니블 자리)
        public long Frac;     // 16.16 아래
        public int Val, Prev;
        public double Pitch = 1;
    }

    private readonly byte[] reg = new byte[0x230];
    private readonly Channel[] ch = new Channel[8];
    private readonly bool[] mute = new bool[8];
    private byte[] rom = new byte[0];
    private readonly double clock;

    public K054539(double clock = DefaultClock)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = (int)Math.Round(this.clock / 384);
        for (int i = 0; i < 8; i++) ch[i] = new Channel();
    }

    public string Name { get { return "Konami K054539"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 8; } }
    public string ChannelLabel(int c) { return "K" + (c + 1); }
    public void Reset() { Array.Clear(reg, 0, reg.Length); foreach (var c in ch) { c.Pos = 0; c.Frac = 0; c.Val = c.Prev = 0; } }

    public void LoadRom(byte[] data, int start)
    {
        if (start + data.Length > rom.Length) Array.Resize(ref rom, start + data.Length);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    /// <summary>재생 중 비트 (22Ch).</summary>
    public int Active { get { return reg[0x22C]; } }

    private int Rom(long a) { return rom.Length == 0 ? 0 : rom[(int)(a % rom.Length)]; }
    private long Adr(int b, int o) { return reg[b + o] | (reg[b + o + 1] << 8) | (reg[b + o + 2] << 16); }

    public void Write(int port, int r, int v)
    {
        r &= 0x3FF;
        if (r >= reg.Length) return;
        v &= 0xFF;
        switch (r)
        {
            case 0x214:
                if ((reg[0x22F] & 0x80) == 0)
                    for (int i = 0; i < 8; i++)
                        if ((v & (1 << i)) != 0) KeyOn(i);
                break;
            case 0x215:
                if ((reg[0x22F] & 0x80) == 0) reg[0x22C] &= (byte)~v;
                break;
        }
        if (r != 0x214 && r != 0x215 && r != 0x22C) reg[r] = (byte)v;
    }

    private void KeyOn(int i)
    {
        reg[0x22C] |= (byte)(1 << i);
        var c = ch[i];
        int b = i * 0x20;
        c.Pos = Adr(b, 0x0C);
        if ((reg[0x200 + i * 2] & 0x0C) == 0x08) c.Pos <<= 1;   // DPCM: 니블 자리
        c.Frac = 0; c.Val = 0; c.Prev = 0;
    }

    public void Generate(out int left, out int right)
    {
        double l = 0, r = 0;
        if ((reg[0x22F] & 1) == 0) { left = right = 0; return; }   // 칩 꺼짐
        for (int i = 0; i < 8; i++)
        {
            if ((reg[0x22C] & (1 << i)) == 0) continue;
            var c = ch[i];
            int b = i * 0x20, b2 = 0x200 + i * 2;
            long delta = (long)((reg[b] | (reg[b + 1] << 8) | (reg[b + 2] << 16)) * c.Pitch);
            int vol = reg[b + 3];
            int pan = reg[b + 5];
            if (pan >= 0x81 && pan <= 0x8F) pan -= 0x81;
            else if (pan >= 0x11 && pan <= 0x1F) pan -= 0x11;
            else pan = 0x18 - 0x11;
            double lv = Math.Min(1.8, VolTab[vol] * PanTab[pan]), rv = Math.Min(1.8, VolTab[vol] * PanTab[0xE - pan]);
            bool reverse = (reg[b2] & 0x20) != 0, loop = (reg[b2 + 1] & 1) != 0;
            int type = reg[b2] & 0x0C;
            int step = reverse ? -1 : 1;
            c.Frac += delta;
            bool off = false;
            while (c.Frac >= 0x10000)
            {
                c.Frac -= 0x10000;
                c.Prev = c.Val;
                if (type == 0x04)   // 16비트
                {
                    c.Pos += 2 * step;
                    int s = (short)(Rom(c.Pos) | (Rom(c.Pos + 1) << 8));
                    if (s == -32768 && loop) { c.Pos = Adr(b, 0x08); s = (short)(Rom(c.Pos) | (Rom(c.Pos + 1) << 8)); }
                    if (s == -32768) { off = true; break; }
                    c.Val = s;
                }
                else if (type == 0x08)   // 4비트 DPCM (니블 자리)
                {
                    c.Pos += step;
                    int d = Rom(c.Pos >> 1);
                    if (d == 0x88 && loop) { c.Pos = Adr(b, 0x08) << 1; d = Rom(c.Pos >> 1); }
                    if (d == 0x88) { off = true; break; }
                    d = (c.Pos & 1) != 0 ? d >> 4 : d & 15;
                    c.Val = Math.Max(-32768, Math.Min(32767, c.Val + Dpcm[d]));
                }
                else   // 8비트
                {
                    c.Pos += step;
                    int s = (sbyte)Rom(c.Pos) << 8;
                    if (s == -32768 && loop) { c.Pos = Adr(b, 0x08); s = (sbyte)Rom(c.Pos) << 8; }
                    if (s == -32768) { off = true; break; }
                    c.Val = s;
                }
            }
            if (off) { reg[0x22C] &= (byte)~(1 << i); c.Val = 0; continue; }
            if (mute[i]) continue;
            l += c.Val * lv;
            r += c.Val * rv;
        }
        left = SoundChips.Clip(l);
        right = SoundChips.Clip(r);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 8) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 8) ch[c].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return c < 0 || c >= 8 || (reg[0x22C] & (1 << c)) == 0 ? 0 : (float)VolTab[reg[c * 0x20 + 3]] * 4; }
    public bool ChannelActive(int c) { return c >= 0 && c < 8 && (reg[0x22C] & (1 << c)) != 0; }
}
