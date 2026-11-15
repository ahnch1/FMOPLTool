using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// BEEP: 8253 / 8254 타이머 카운터(방식 3, 사각파)로 울리는 스피커. PC-98 (2.4576MHz · 1.9968MHz) · PC/AT 스피커 (1.193182MHz).
/// 소리 높이 = 클럭 / 카운터. 사각파 한 칸을 출력 샘플 구간으로 평균 내어 (상자 거르기) 높은 음의 겹침(앨리어싱)을 줄인다.
/// <para>쓰기: reg 0 = 카운터 아래 바이트, reg 1 = 위 바이트 (0 = 65536), reg 2 = bit0 켜기.</para>
/// </summary>
public sealed class Beeper : ISoundChip
{
    public const double Pc98Clock = 2457600, Pc98Clock8MHz = 1996800, PcAtClock = 1193182;
    private readonly double clock;
    private readonly int rate;
    private int counter = 0x10000;
    private bool on, mute;
    private double phase, pitch = 1;

    public Beeper(double clock = Pc98Clock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : Pc98Clock;
        this.rate = rate > 0 ? rate : 44100;
        NativeRate = this.rate;
    }

    public string Name { get { return "BEEP (8253)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 1; } }
    public string ChannelLabel(int ch) { return "BEEP"; }
    public double Frequency { get { return clock / counter; } }

    public void Reset() { counter = 0x10000; on = false; phase = 0; }

    public void Write(int port, int reg, int v)
    {
        switch (reg)
        {
            case 0: counter = (counter & 0xFF00) | (v & 0xFF); if (counter == 0) counter = 0x10000; break;
            case 1: counter = ((v & 0xFF) << 8) | (counter & 0xFF); if (counter == 0) counter = 0x10000; break;
            case 2: on = (v & 1) != 0; break;
        }
    }

    /// <summary>사각파(+1 · −1, 한 주기 = 1)의 0~x 적분.</summary>
    private static double Integral(double x)
    {
        double n = Math.Floor(x), r = x - n;
        return r < 0.5 ? r : 1 - r;
    }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        double inc = Frequency * pitch / rate;
        double p0 = phase;
        phase += inc;
        if (phase >= 1) phase -= Math.Floor(phase);
        if (!on || mute) return;
        double avg = inc >= 1 ? 0 : (Integral(p0 + inc) - Integral(p0)) / inc;
        left = right = (int)(avg * 8000);
    }

    public void SetMute(int ch, bool m) { if (ch == 0) mute = m; }
    public void SetPitch(int ch, double ratio) { if (ch == 0) pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int ch) { return ch == 0 && on ? Frequency : 0; }
    public float ChannelLevel(int ch) { return ch == 0 && on ? 1f : 0f; }
    public bool ChannelActive(int ch) { return ch == 0 && on; }
}

/// <summary>
/// 8비트 DAC: 쓴 값을 그대로 낸다 (여러 아케이드 · 코코넛 등). reg 0 = 값 (부호 없음, 0x80 = 0), reg 1 = 1이면 부호 있는 값으로 읽기.
/// </summary>
public sealed class DacChip : ISoundChip
{
    private int value;
    private bool signed, mute;
    private double level;

    public DacChip(int rate = 44100) { NativeRate = rate > 0 ? rate : 44100; }

    public string Name { get { return "DAC (8비트)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 1; } }
    public string ChannelLabel(int ch) { return "DAC"; }
    public void Reset() { value = 0; level = 0; }

    public void Write(int port, int reg, int v)
    {
        if (reg == 1) { signed = (v & 1) != 0; return; }
        value = signed ? (sbyte)(byte)v : (v & 0xFF) - 0x80;
    }

    public void Generate(out int left, out int right)
    {
        level = Math.Max(level * 0.999, Math.Abs(value) / 128.0);
        left = right = mute ? 0 : value * 128;
    }

    public void SetMute(int ch, bool m) { if (ch == 0) mute = m; }
    public void SetPitch(int ch, double ratio) { }
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return ch == 0 ? (float)level : 0; }
    public bool ChannelActive(int ch) { return ch == 0 && level > 0.01; }
}

/// <summary>
/// 남코 WSG (팩맨 · 갤러그 등의 파형 소리): 목소리 3개, 파형 ROM(32칸 × 4비트 × 8가지)을 20비트 누산기로 읽는다.
/// 칩 속도 96kHz (3.072MHz / 32), 소리 높이 = 주파수 × 96000 / 2^20.
/// <para>레지스터 (팩맨 0x5040~0x505F, 니블 하나씩): 0x05 · 0x0A · 0x0F 파형, 0x10~0x14 · 0x16~0x19 · 0x1B~0x1E 주파수 (목소리 0만 맨 아래 니블이 있음),
/// 0x15 · 0x1A · 0x1F 음량. 파형 ROM(82s126, 256바이트)은 <see cref="LoadWaveRom"/>으로 — 없으면 시험용 기본 파형 (0 = 사인).
/// C30 (시스템 1 · 86, 8목소리 스테레오 · 노이즈)은 아직 없음.</para>
/// </summary>
public sealed class NamcoWsg : ISoundChip
{
    public const double ChipRate = 96000;
    private const int Voices = 3;
    private readonly byte[] regs = new byte[0x20];
    private readonly int[] freq = new int[Voices], wave = new int[Voices], vol = new int[Voices];
    private readonly double[] counter = new double[Voices];
    private readonly double[] pitch = { 1, 1, 1 };
    private readonly bool[] mute = new bool[Voices];
    private byte[] rom = DefaultRom();
    private readonly int rate;

    public NamcoWsg(int rate = 44100) { this.rate = rate > 0 ? rate : 44100; NativeRate = this.rate; }

    public string Name { get { return "Namco WSG"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return Voices; } }
    public string ChannelLabel(int ch) { return "W" + (ch + 1); }

    /// <summary>시험용 파형 8가지: 사인 · 세모 · 톱니 · 사각 · 25% · 12.5% · 사인 2배 · 섞음.</summary>
    private static byte[] DefaultRom()
    {
        var r = new byte[256];
        for (int i = 0; i < 32; i++)
        {
            double ph = i / 32.0;
            int[] w =
            {
                (int)Math.Round(7.5 + 7.5 * Math.Sin(2 * Math.PI * ph)),
                i < 16 ? i : 31 - i,
                i / 2,
                i < 16 ? 15 : 0,
                i < 8 ? 15 : 0,
                i < 4 ? 15 : 0,
                (int)Math.Round(7.5 + 7.5 * Math.Sin(4 * Math.PI * ph)),
                (int)Math.Round(7.5 + 5 * Math.Sin(2 * Math.PI * ph) + 2.5 * Math.Sin(6 * Math.PI * ph)),
            };
            for (int k = 0; k < 8; k++) r[k * 32 + i] = (byte)Math.Max(0, Math.Min(15, w[k]));
        }
        return r;
    }

    /// <summary>파형 ROM (82s126, 256바이트: 아래 4비트만 씀).</summary>
    public void LoadWaveRom(byte[] data)
    {
        if (data == null || data.Length < 256) return;
        rom = new byte[256];
        Array.Copy(data, rom, 256);
    }

    public void Reset()
    {
        Array.Clear(regs, 0, regs.Length);
        for (int i = 0; i < Voices; i++) { freq[i] = wave[i] = vol[i] = 0; counter[i] = 0; }
    }

    public void Write(int port, int offset, int data)
    {
        if (offset < 0 || offset >= 0x20) return;
        data &= 0x0F;
        regs[offset] = (byte)data;
        int c = offset < 0x10 ? (offset - 5) / 5 : offset == 0x10 ? 0 : (offset - 0x11) / 5;
        if (c < 0 || c >= Voices) return;
        switch (offset - c * 5)
        {
            case 0x05: wave[c] = data & 7; break;
            case 0x10: case 0x11: case 0x12: case 0x13: case 0x14:
                freq[c] = (c == 0 ? regs[0x10] : 0) | (regs[c * 5 + 0x11] << 4) | (regs[c * 5 + 0x12] << 8) | (regs[c * 5 + 0x13] << 12) | (regs[c * 5 + 0x14] << 16);
                break;
            case 0x15: vol[c] = data; break;
        }
    }

    public void Generate(out int left, out int right)
    {
        int sum = 0;
        double step = ChipRate / rate;
        for (int c = 0; c < Voices; c++)
        {
            counter[c] += freq[c] * pitch[c] * step;
            if (counter[c] >= 1 << 20) counter[c] %= 1 << 20;
            if (mute[c] || vol[c] == 0 || freq[c] == 0) continue;
            int idx = ((int)counter[c] >> 15) & 31;
            sum += ((rom[wave[c] * 32 + idx] & 15) - 8) * vol[c];
        }
        left = right = sum * 80;
    }

    public void SetMute(int ch, bool m) { if (ch >= 0 && ch < Voices) mute[ch] = m; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < Voices) pitch[ch] = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int ch) { return ch >= 0 && ch < Voices && vol[ch] > 0 ? freq[ch] * ChipRate / (1 << 20) : 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < Voices && freq[ch] > 0 ? vol[ch] / 15f : 0; }
    public bool ChannelActive(int ch) { return ChannelLevel(ch) > 0; }
}
