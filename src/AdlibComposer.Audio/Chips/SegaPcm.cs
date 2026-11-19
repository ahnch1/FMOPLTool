using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 세가 PCM (315-5218, Out Run · Hang-On · X · Y 보드): 16채널 8bit PCM, 레지스터 = 256바이트 RAM (채널마다 8바이트 두 벌).
/// MAME segapcm.cpp와 같게: ch × 8 + 2 · 3 = 왼쪽 · 오른쪽 볼륨 (0~7Fh), 4 · 5 = 루프 주소 (위 16비트), 6 = 끝 주소 위 바이트 (+1에서 멈춤 · 되풀이), 7 = 속도,
/// 84h · 85h = 지금 주소, 86h = bit0 멈춤 · bit1 되풀이 안 함 · 나머지 = 뱅크 ((86h &amp; 마스크) &lt;&lt; 뱅크 자리).
/// 출력 속도 = 클럭 / 128. VGM: 클럭 38h, 인터페이스 3Ch (= 마스크 &lt;&lt; 16 | 뱅크 자리, 마스크 0이면 70h), 명령 C0h aaaa dd, ROM 블록 80h.
/// </summary>
public sealed class SegaPcm : ISoundChip
{
    public const double StandardClock = 4000000;
    /// <summary>MAME 상수: 뱅크 자리 11 (32KB) · 12 (64KB, BANK_512) · 13 (BANK_12M), 마스크 70h · F0h · F8h.</summary>
    public const int Bank512 = 0x0070000C, Bank12M = 0x00F8000D;

    public readonly byte[] Ram = new byte[0x100];
    private readonly int[] low = new int[16];
    private readonly bool[] muted = new bool[16];
    private readonly double[] pitch = new double[16];
    private byte[] rom = new byte[0];
    private readonly int bankShift, bankMask;
    private readonly double chipRate;
    private readonly int outRate;
    private double acc;
    private int curL, curR;
    private readonly int[] lastVol = new int[16];

    public SegaPcm(double clock, int intf, int rate = 44100)
    {
        outRate = rate;
        chipRate = (clock > 0 ? clock : StandardClock) / 128.0;
        bankShift = intf & 0x0F;
        if (bankShift == 0) bankShift = 12;
        bankMask = (intf >> 16) & 0xFF;
        if (bankMask == 0) bankMask = 0x70;
        for (int i = 0; i < 16; i++) pitch[i] = 1.0;
        Reset();
    }

    public string Name { get { return "Sega PCM"; } }
    public int NativeRate { get { return outRate; } }
    public int ChannelCount { get { return 16; } }
    public string ChannelLabel(int ch) { return "P" + (ch + 1); }
    public double ChipRate { get { return chipRate; } }

    public void Reset()
    {
        for (int i = 0; i < Ram.Length; i++) Ram[i] = 0xFF;   // MAME: 처음 RAM = FFh (모두 멈춤)
        Array.Clear(low, 0, low.Length);
        acc = 0;
    }

    public void LoadRom(byte[] data, int start)
    {
        if (start + data.Length > rom.Length) Array.Resize(ref rom, start + data.Length);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    public void Write(int port, int reg, int value) { Ram[reg & 0xFF] = (byte)value; }

    /// <summary>칩 샘플 하나 (레지스터도 실제처럼 바뀐다 — 보드 흉내에서 드라이버가 읽는다).</summary>
    public void ChipSample(out int l, out int r)
    {
        l = r = 0;
        for (int ch = 0; ch < 16; ch++)
        {
            int b = ch * 8;
            int flags = Ram[0x86 + b];
            if ((flags & 1) != 0) { lastVol[ch] = 0; continue; }
            int offset = (flags & bankMask) << bankShift;
            uint addr = (uint)((Ram[0x85 + b] << 16) | (Ram[0x84 + b] << 8) | low[ch]);
            uint loop = (uint)((Ram[0x05 + b] << 16) | (Ram[0x04 + b] << 8));
            int end = (Ram[6 + b] + 1) & 0xFF;
            if ((addr >> 16) == end)
            {
                if ((flags & 2) != 0) { Ram[0x86 + b] = (byte)(flags | 1); lastVol[ch] = 0; continue; }
                addr = loop;
            }
            int a = offset + (int)(addr >> 8);
            int v = (rom.Length > 0 ? rom[a % rom.Length] : 0x80) - 0x80;
            int vl = Ram[2 + b] & 0x7F, vr = Ram[3 + b] & 0x7F;
            lastVol[ch] = Math.Max(vl, vr);
            if (!muted[ch]) { l += v * vl; r += v * vr; }
            int step = (int)(Ram[7 + b] * pitch[ch]);
            addr = (uint)((addr + step) & 0xFFFFFF);
            Ram[0x84 + b] = (byte)(addr >> 8);
            Ram[0x85 + b] = (byte)(addr >> 16);
            low[ch] = (int)(addr & 0xFF);
        }
    }

    public void Generate(out int left, out int right)
    {
        acc += chipRate / outRate;
        while (acc >= 1.0) { acc -= 1.0; ChipSample(out curL, out curR); }
        left = Math.Max(-32768, Math.Min(32767, curL / 2));
        right = Math.Max(-32768, Math.Min(32767, curR / 2));
    }

    public void SetMute(int ch, bool mute) { if (ch >= 0 && ch < 16) muted[ch] = mute; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 16) pitch[ch] = ratio; }
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < 16 ? lastVol[ch] / 127f : 0f; }
    public bool ChannelActive(int ch) { return ch >= 0 && ch < 16 && (Ram[0x86 + ch * 8] & 1) == 0; }
}
