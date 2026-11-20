using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 코나미 K053260 (Parodius · TMNT 2 · Sunset Riders · Vendetta · Simpsons …): PCM 4목소리, 8비트 부호 PCM 또는 KADPCM (4비트 차분), 반복, 3비트 팬. MAME k053260을 따른다.
/// <para>레지스터: 00h~03h = 메인 · 사운드 CPU 사이 통신 (소리 없음), 08h + 목소리 × 8: +0 · +1 피치 (12비트), +2 · +3 길이, +4 · +5 · +6 시작 (21비트), +7 음량 (7비트),
/// 28h = 키온 (bit0~3, 0→1 시작), 2Ah = 반복 (bit0~3) · KADPCM (bit4~7), 2Ch · 2Dh = 팬 (목소리마다 3비트), 2Fh = bit1 소리 켬.</para>
/// <para>클럭 ÷ 64마다 카운터 += 64, 1000h를 넘으면 카운터 = 카운터 − 1000h + 피치, 한 칸 (KADPCM은 니블, 아래 먼저). VGM 0xBA aa dd, ROM 블록 8Eh, 머리말 ACh.</para>
/// </summary>
public sealed class K053260 : ISoundChip
{
    public const double DefaultClock = 3579545;
    private static readonly int[] Kadpcm = { 0, 1, 2, 4, 8, 16, 32, 64, -128, -64, -32, -16, -8, -4, -2, -1 };
    private static readonly double[,] Pan = BuildPan();

    private static double[,] BuildPan()
    {
        var p = new double[8, 2];
        for (int i = 1; i < 8; i++) { double a = (i - 1) / 6.0 * Math.PI / 2; p[i, 0] = Math.Cos(a); p[i, 1] = Math.Sin(a); }   // 1 = 왼쪽 끝, 4 = 가운데, 7 = 오른쪽 끝, 0 = 없음
        return p;
    }

    private sealed class Voice
    {
        public int Pitch, Length, Start, Volume, Pan;
        public bool Loop, Adpcm, Playing;
        public long Position;
        public double Counter;
        public int Output;
        public double PitchRatio = 1;
    }

    private readonly byte[] reg = new byte[0x30];
    private readonly Voice[] v = { new Voice(), new Voice(), new Voice(), new Voice() };
    private readonly bool[] mute = new bool[4];
    private byte[] rom = new byte[0];
    private int keyOn, mode;
    private readonly double clock;

    public K053260(double clock = DefaultClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
    }

    public string Name { get { return "Konami K053260"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 4; } }
    public string ChannelLabel(int c) { return "K" + (c + 1); }

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        keyOn = mode = 0;
        foreach (var x in v) { x.Playing = false; x.Output = 0; x.Counter = 0; x.Position = 0; }
    }

    public void LoadRom(byte[] data, int start)
    {
        if (start + data.Length > rom.Length) Array.Resize(ref rom, start + data.Length);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    /// <summary>재생 중인 목소리 (bit0~3) — 상태 레지스터 29h.</summary>
    public int Status { get { int s = 0; for (int i = 0; i < 4; i++) if (v[i].Playing) s |= 1 << i; return s; } }

    public void Write(int port, int r, int value)
    {
        r &= 0x3F;
        if (r >= reg.Length) return;
        value &= 0xFF;
        reg[r] = (byte)value;
        if (r >= 0x08 && r < 0x28)
        {
            var x = v[(r - 8) >> 3];
            switch ((r - 8) & 7)
            {
                case 0: x.Pitch = (x.Pitch & 0xF00) | value; break;
                case 1: x.Pitch = (x.Pitch & 0xFF) | ((value & 0x0F) << 8); break;
                case 2: x.Length = (x.Length & 0xFF00) | value; break;
                case 3: x.Length = (x.Length & 0xFF) | (value << 8); break;
                case 4: x.Start = (x.Start & 0x1FFF00) | value; break;
                case 5: x.Start = (x.Start & 0x1F00FF) | (value << 8); break;
                case 6: x.Start = (x.Start & 0xFFFF) | ((value & 0x1F) << 16); break;
                case 7: x.Volume = value & 0x7F; break;
            }
            return;
        }
        switch (r)
        {
            case 0x28:
                {
                    int rising = value & ~keyOn;
                    int falling = keyOn & ~value;
                    for (int i = 0; i < 4; i++)
                    {
                        if ((rising & (1 << i)) != 0)
                        {
                            var x = v[i];
                            x.Playing = true;
                            x.Position = x.Adpcm ? 1 : 0;   // KADPCM은 니블 단위
                            x.Counter = 0x1000 - 64;
                            x.Output = 0;
                        }
                        else if ((falling & (1 << i)) != 0) v[i].Playing = false;
                    }
                    keyOn = value & 0x0F;
                    break;
                }
            case 0x2A:
                for (int i = 0; i < 4; i++) { v[i].Loop = (value & (1 << i)) != 0; v[i].Adpcm = (value & (0x10 << i)) != 0; }
                break;
            case 0x2C: v[0].Pan = value & 7; v[1].Pan = (value >> 3) & 7; break;
            case 0x2D: v[2].Pan = value & 7; v[3].Pan = (value >> 3) & 7; break;
            case 0x2F: mode = value; break;
        }
    }

    private int RomByte(long a) { return rom.Length == 0 ? 0 : rom[(int)(a % rom.Length)]; }

    public void Generate(out int left, out int right)
    {
        double l = 0, r = 0;
        double cycles = clock / NativeRate;
        for (int i = 0; i < 4; i++)
        {
            var x = v[i];
            if (!x.Playing) continue;
            x.Counter += cycles * x.PitchRatio;
            while (x.Counter >= 0x1000)
            {
                x.Counter = x.Counter - 0x1000 + x.Pitch;
                long bytePos = ++x.Position >> (x.Adpcm ? 1 : 0);
                if (bytePos > x.Length)
                {
                    if (x.Loop) { x.Position = 0; x.Output = 0; bytePos = 0; }
                    else { x.Playing = false; break; }
                }
                int d = RomByte(x.Start + bytePos);
                if (x.Adpcm)
                {
                    if ((x.Position & 1) != 0) d >>= 4;   // 아래 니블 먼저
                    x.Output = (sbyte)(x.Output + Kadpcm[d & 15]);
                }
                else x.Output = (sbyte)d;
            }
            if (!x.Playing || mute[i]) continue;
            double s = x.Output * x.Volume;
            l += s * Pan[x.Pan, 0];
            r += s * Pan[x.Pan, 1];
        }
        if ((mode & 2) == 0) { left = right = 0; return; }   // 소리 꺼짐
        left = SoundChips.Clip(l);
        right = SoundChips.Clip(r);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < 4) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 4) v[c].PitchRatio = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return c < 0 || c >= 4 || !v[c].Playing ? 0 : v[c].Volume / 127f; }
    public bool ChannelActive(int c) { return c >= 0 && c < 4 && v[c].Playing; }
}
