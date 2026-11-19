using System;

namespace AdlibComposer.Audio;

/// <summary>
/// 보드 흉내에서 OKIM6295 읽기(울리는 음성 비트)를 계산한다: 명령 바이트를 따라가며 구절 표(구절 × 8, 시작 · 끝 18비트)의 길이 ÷ (클럭 / 132 또는 165)로 끝 시각을 잡는다.
/// 소리 자체는 VGM 쪽 <see cref="Chips.Okim6295"/>가 낸다.
/// </summary>
public sealed class HootOkiVoices
{
    private readonly byte[] rom;
    private readonly double okiClock, cpuClock;
    private readonly long[] voiceEnd = new long[4];
    private int pending = -1;
    public bool Pin7 = true;
    public int Bank;
    public int Starts;

    public HootOkiVoices(byte[] rom, double okiClock, double cpuClock)
    {
        this.rom = rom ?? new byte[0]; this.okiClock = okiClock; this.cpuClock = cpuClock;
    }

    private int Byte(long a) { a += (long)Bank * 0x40000; return a >= 0 && a < rom.Length ? rom[a] : 0; }

    /// <summary>명령 바이트 하나 (now = CPU 사이클).</summary>
    public void Write(int d, long now)
    {
        if (pending >= 0)
        {
            int phrase = pending;
            pending = -1;
            int voices = (d >> 4) & 15;
            long t = phrase * 8;
            long start = ((Byte(t) << 16) | (Byte(t + 1) << 8) | Byte(t + 2)) & 0x3FFFF;
            long stop = ((Byte(t + 3) << 16) | (Byte(t + 4) << 8) | Byte(t + 5)) & 0x3FFFF;
            long nibbles = Math.Max(0, stop - start + 1) * 2;
            double rate = okiClock / (Pin7 ? 132.0 : 165.0);
            long cycles = (long)(nibbles / rate * cpuClock);
            for (int i = 0; i < 4; i++)
                if ((voices & (1 << i)) != 0 && now >= voiceEnd[i]) { voiceEnd[i] = now + cycles; Starts++; }
            return;
        }
        if ((d & 0x80) != 0) { pending = d & 0x7F; return; }
        int stopMask = (d >> 3) & 15;
        for (int i = 0; i < 4; i++) if ((stopMask & (1 << i)) != 0) voiceEnd[i] = 0;
    }

    /// <summary>읽기: F0h | 울리는 음성.</summary>
    public byte Status(long now)
    {
        int r = 0xF0;
        for (int i = 0; i < 4; i++) if (now < voiceEnd[i]) r |= 1 << i;
        return (byte)r;
    }
}
