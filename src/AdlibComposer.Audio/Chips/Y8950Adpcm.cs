using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 야마하 Y8950 (MSX-AUDIO)의 ADPCM 부분 (delta-T): ROM / RAM의 4비트 ADPCM 하나. MAME ymdeltat (Y8950 = 주소 32바이트 단위)을 따른다.
/// <para>레지스터 (칩 번호 그대로): 07h 제어 (bit7 시작, bit4 되풀이, bit0 리셋), 08h (bit0 ROM), 09h · 0Ah 시작, 0Bh · 0Ch 끝 (× 32바이트), 10h · 11h 델타-N (16비트), 12h 음량.
/// 속도 = 클럭 ÷ 72 × 델타-N ÷ 65536. FM 쪽은 OPL로 따로 낸다. VGM에는 이 프로그램 안 명령 DCh rr vv 00 (hoot SNK), ROM은 <c>VgmSong.Y8950Rom</c>.</para>
/// </summary>
public sealed class Y8950Adpcm : ISoundChip
{
    public const double DefaultClock = 4000000;

    private static readonly int[] StepScale = { 57, 57, 57, 57, 77, 102, 128, 153 };
    private readonly byte[] reg = new byte[0x20];
    private byte[] rom = new byte[0];
    private readonly double chipRate;
    private bool playing, mute;
    private double pos, pitch = 1;
    private int nibble, acc, prev, step = 127;
    private float level;

    public Y8950Adpcm(double clock = DefaultClock, int rate = 44100)
    {
        NativeRate = rate > 0 ? rate : 44100;
        chipRate = (clock > 0 ? clock : DefaultClock) / 72.0;
    }

    public string Name { get { return "Yamaha Y8950 ADPCM"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 1; } }
    public string ChannelLabel(int c) { return "ADPCM"; }
    public void Reset() { Array.Clear(reg, 0, reg.Length); playing = false; }

    public void LoadRom(byte[] data) { rom = data ?? new byte[0]; }

    private int Start { get { return (reg[0x09] | (reg[0x0A] << 8)) * 32; } }
    private int End { get { return ((reg[0x0B] | (reg[0x0C] << 8)) + 1) * 32; } }

    public void Write(int port, int r, int v)
    {
        r &= 0x1F; v &= 0xFF;
        reg[r] = (byte)v;
        if (r != 0x07) return;
        if ((v & 1) != 0) { playing = false; return; }
        if ((v & 0x80) != 0) { playing = true; pos = nibble = Start * 2; acc = prev = 0; step = 127; }
        else playing = false;
    }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        if (!playing) { level = 0; return; }
        int deltaN = reg[0x10] | (reg[0x11] << 8);
        pos += deltaN / 65536.0 * chipRate / NativeRate * pitch;
        int end = End * 2;
        while (nibble < (int)pos)
        {
            if (nibble >= end || (nibble >> 1) >= rom.Length)
            {
                if ((reg[0x07] & 0x10) != 0) { pos -= nibble - Start * 2; nibble = Start * 2; acc = 0; step = 127; continue; }
                playing = false; level = 0; return;
            }
            int b = rom[nibble >> 1];
            int n = (nibble & 1) == 0 ? b >> 4 : b & 15;
            prev = acc;
            int diff = ((2 * (n & 7) + 1) * step) >> 3;
            acc += (n & 8) != 0 ? -diff : diff;
            acc = Math.Max(-32768, Math.Min(32767, acc));
            step = Math.Max(127, Math.Min(24576, (step * StepScale[n & 7]) >> 6));
            nibble++;
        }
        double frac = pos - Math.Floor(pos);
        double s = (prev + (acc - prev) * frac) * reg[0x12] / 256.0 * 0.6;
        level = (float)Math.Min(1, Math.Abs(s) / 8192.0);
        if (mute) return;
        left = right = SoundChips.Clip((int)s);
    }

    public void SetMute(int c, bool m) { mute = m; }
    public void SetPitch(int c, double ratio) { pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return playing ? level : 0; }
    public bool ChannelActive(int c) { return playing; }
}
