using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 남코 C140 (System 2 · System 21): PCM 24목소리, 8비트 선형 또는 압축 (지수 + 가수) 표본, 선형 보간, 스테레오 음량. MAME c140 (고전 구현)을 따른다.
/// <para>레지스터 (목소리 × 16바이트, 000h~17Fh): +0 오른쪽 음량, +1 왼쪽 음량, +2 · +3 주파수, +4 뱅크, +5 모드 (bit7 키온, bit4 반복, bit3 압축), +6 · +7 시작, +8 · +9 끝, +A · +B 반복.
/// 주소 = 뱅크 &lt;&lt; 16 + 자리 → System 2: ((a &amp; 200000h) &gt;&gt; 2) | (a &amp; 7FFFFh), System 21: ((a &amp; 300000h) &gt;&gt; 1) | (a &amp; 7FFFFh).
/// 한 샘플마다 자리가 주파수 × 2 × 클럭 / 2^16 / 출력 속도만큼 (클럭 = 49.152MHz ÷ 2304 ≈ 21.3kHz). VGM 0xD4 pp aa dd (레지스터 = pp &lt;&lt; 8 | aa), ROM 블록 8Dh, 머리말 A8h 클럭 · 96h 형식.</para>
/// </summary>
public sealed class NamcoC140 : ISoundChip
{
    public const double DefaultClock = 49152000 / 2304.0;
    public const int Voices = 24;
    public enum Kind { System2 = 0, System21 = 1, Asic219 = 2 }

    private static readonly int[] PcmTable = BuildTable();
    private static int[] BuildTable() { var t = new int[8]; int seg = 0; for (int i = 0; i < 8; i++) { t[i] = seg; seg += 16 << i; } return t; }

    private sealed class Voice
    {
        public bool Key;
        public int Mode, Bank, Start, End, Loop;
        public long Pos;            // 표본 자리 (시작에서)
        public double Frac;         // 다음 표본까지 (0~1)
        public int Prev, Last;
        public double Pitch = 1;
    }

    private readonly byte[] reg = new byte[0x200];
    private readonly Voice[] v = new Voice[Voices];
    private readonly bool[] mute = new bool[Voices];
    private byte[] rom = new byte[0];
    private readonly double clock;
    public readonly Kind Type;

    public NamcoC140(double clock = DefaultClock, int rate = 44100, Kind type = Kind.System2)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        NativeRate = rate > 0 ? rate : 44100;
        Type = type;
        for (int i = 0; i < Voices; i++) v[i] = new Voice();
    }

    public string Name { get { return "Namco C140"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return Voices; } }
    public string ChannelLabel(int c) { return "C" + (c + 1); }

    public void Reset()
    {
        Array.Clear(reg, 0, reg.Length);
        foreach (var x in v) { x.Key = false; x.Pos = 0; x.Frac = 0; x.Prev = x.Last = 0; }
    }

    public void LoadRom(byte[] data, int start)
    {
        if (start + data.Length > rom.Length) Array.Resize(ref rom, start + data.Length);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    public void Write(int port, int r, int value)
    {
        r &= 0x1FF;
        if (r >= 0x1F8 && (r & 1) != 0) r -= 8;   // 뱅크 레지스터 거울 (MAME)
        reg[r] = (byte)value;
        if (r >= 0x180 || (r & 15) != 5) return;
        var x = v[r >> 4];
        int b = r & 0x1F0;
        if ((value & 0x80) != 0)
        {
            x.Key = true;
            x.Mode = value;
            x.Bank = reg[b + 4];
            x.Start = (reg[b + 6] << 8) | reg[b + 7];
            x.End = (reg[b + 8] << 8) | reg[b + 9];
            x.Loop = (reg[b + 10] << 8) | reg[b + 11];
            x.Pos = 0; x.Frac = 0; x.Prev = x.Last = 0;
        }
        else x.Key = false;
    }

    private int Address(int bank, long adr)
    {
        long a = ((long)bank << 16) + adr;
        switch (Type)
        {
            case Kind.System21: return (int)(((a & 0x300000) >> 1) | (a & 0x7FFFF));
            case Kind.Asic219: return (int)(a & 0x7FFFF);
            default: return (int)(((a & 0x200000) >> 2) | (a & 0x7FFFF));
        }
    }

    private int Fetch(Voice x)
    {
        int a = Address(x.Bank, x.Start + x.Pos);
        int b = rom.Length == 0 ? 0 : (sbyte)rom[a % rom.Length];
        if ((x.Mode & 0x08) == 0) return b << 4;   // 선형 8비트 (12비트 자리)
        int s = b >> 3;                              // 압축: 부호 있는 가수 · 지수 (아래 3비트)
        int e = b & 7;
        return s < 0 ? (s << e) - PcmTable[e] : (s << e) + PcmTable[e];
    }

    public void Generate(out int left, out int right)
    {
        int l = 0, r = 0;
        double baseStep = 2.0 * clock / 65536.0 / NativeRate;
        for (int i = 0; i < Voices; i++)
        {
            var x = v[i];
            if (!x.Key) continue;
            int b = i << 4;
            int freq = (reg[b + 2] << 8) | reg[b + 3];
            if (freq == 0) continue;
            x.Frac += freq * baseStep * x.Pitch;
            int size = x.End - x.Start;
            while (x.Frac >= 1)
            {
                x.Frac -= 1;
                x.Pos++;
                if (x.Pos >= size)
                {
                    if ((x.Mode & 0x10) != 0 && x.Loop >= x.Start && x.Loop < x.End) x.Pos = x.Loop - x.Start;
                    else { x.Key = false; break; }
                }
                x.Prev = x.Last;
                x.Last = Fetch(x);
            }
            if (!x.Key || mute[i]) continue;
            int dt = (int)(x.Prev + (x.Last - x.Prev) * x.Frac);
            int rv = reg[b] * 32 / Voices, lv = reg[b + 1] * 32 / Voices;
            l += (dt * lv) >> 10;
            r += (dt * rv) >> 10;
        }
        left = SoundChips.Clip(l * 4.0);
        right = SoundChips.Clip(r * 4.0);
    }

    public void SetMute(int c, bool m) { if (c >= 0 && c < Voices) mute[c] = m; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < Voices) v[c].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int c) { return 0; }   // PCM: 음높이 표시 없음
    public float ChannelLevel(int c) { return c < 0 || c >= Voices || !v[c].Key ? 0 : Math.Max(reg[c * 16], reg[c * 16 + 1]) / 255f; }
    public bool ChannelActive(int c) { return c >= 0 && c < Voices && v[c].Key; }
}
