using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Capcom QSound (CPS-2 · ZN · CPS-1 일부) — DSP 내부 ROM (dl-1425) 없이 예전 MAME qsound.cpp의 HLE 방식으로 근사:
/// 16채널 8bit PCM, 채널마다 뱅크 · 주소 · 속도 (12비트 소수, 1000h = 한 샘플) · 루프 길이 · 끝 · 볼륨, 팬 (80h~8Fh, 제곱근 표).
/// 3D 효과 · 반향 · 필터 (진짜 DSP)는 없다. 레지스터: 0 뱅크 (다음 채널에 걸림 — 원래 칩의 버릇), 1 주소, 2 속도, 4 루프, 5 끝, 6 볼륨 (0 = 끔, 0이 아니면 켬).
/// 출력 속도 = 클럭 / 166 (4MHz → 약 24kHz). VGM: 클럭 B4h, 명령 C4h mm ll rr, ROM 블록 8Fh.
/// </summary>
public sealed class QSound : ISoundChip
{
    public const double StandardClock = 4000000;

    private sealed class Ch
    {
        public int Bank, Address, Freq, Loop, End, Vol, Pan;
        public int Lvol, Rvol;
        public bool On, Muted;
        public int StepPtr;
        public int Last;
        public double Pitch = 1.0;
    }

    private readonly Ch[] ch = new Ch[16];
    private readonly int[] panTable = new int[33];
    private byte[] rom = new byte[0];
    private readonly double chipRate;
    private readonly int outRate;
    private double acc;
    private int curL, curR;

    public QSound(double clock, int rate = 44100)
    {
        outRate = rate;
        chipRate = (clock > 0 ? clock : StandardClock) / 166.0;
        for (int i = 0; i < 16; i++) ch[i] = new Ch();
        for (int i = 0; i < 33; i++) panTable[i] = (int)(256 / Math.Sqrt(32.0) * Math.Sqrt(i));
        Reset();
    }

    public string Name { get { return "Capcom QSound (HLE)"; } }
    public int NativeRate { get { return outRate; } }
    public int ChannelCount { get { return 16; } }
    public string ChannelLabel(int c) { return "Q" + (c + 1); }

    public void Reset()
    {
        foreach (var c in ch) { c.On = false; c.StepPtr = 0; c.Lvol = c.Rvol = panTable[16]; }
        acc = 0;
    }

    public void LoadRom(byte[] data, int start)
    {
        if (start + data.Length > rom.Length) Array.Resize(ref rom, start + data.Length);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    /// <summary>reg = QSound 레지스터 (0~FFh), value = 16비트 값.</summary>
    public void Write(int port, int reg, int value)
    {
        int c = 0, r;
        if (reg < 0x80) { c = reg >> 3; r = reg & 7; }
        else if (reg < 0x90) { c = reg & 0x0F; r = 8; }
        else return;
        var x = ch[c];
        switch (r)
        {
            case 0: ch[(c + 1) & 15].Bank = (value & 0x7F) << 16; break;   // 다음 채널의 뱅크 (MAME 옛 HLE)
            case 1: x.Address = value & 0xFFFF; break;
            case 2: x.Freq = value & 0xFFFF; if (value == 0) x.On = false; break;
            case 4: x.Loop = value & 0xFFFF; break;
            case 5: x.End = value & 0xFFFF; break;
            case 6:
                if (value == 0) x.On = false;
                else if (!x.On) { x.On = true; x.StepPtr = 0; }
                x.Vol = value & 0xFFFF;
                break;
            case 8:
                {
                    int p = (value - 0x10) & 0x3F;
                    if (p > 32) p = 32;
                    x.Rvol = panTable[p];
                    x.Lvol = panTable[32 - p];
                    x.Pan = value;
                    break;
                }
        }
    }

    private void ChipSample(out int l, out int r)
    {
        l = r = 0;
        if (rom.Length == 0) return;
        foreach (var c in ch)
        {
            if (!c.On) continue;
            int freq = (int)(c.Freq * c.Pitch);
            int advance = c.StepPtr >> 12;
            c.StepPtr &= 0xFFF;
            c.StepPtr += freq;
            for (int j = 0; j < advance; j++)
            {
                c.Address = (c.Address + 1) & 0xFFFF;
                if (c.Address >= c.End)
                {
                    if (c.Loop == 0) { c.On = false; break; }
                    c.Address = (c.End - c.Loop) & 0xFFFF;
                }
            }
            if (!c.On) continue;
            int s = (sbyte)rom[(c.Bank + c.Address) % rom.Length];
            c.Last = s;
            if (c.Muted) continue;
            l += (s * c.Lvol * c.Vol) >> 14;
            r += (s * c.Rvol * c.Vol) >> 14;
        }
    }

    public void Generate(out int left, out int right)
    {
        acc += chipRate / outRate;
        while (acc >= 1.0) { acc -= 1.0; ChipSample(out curL, out curR); }
        left = Math.Max(-32768, Math.Min(32767, curL));
        right = Math.Max(-32768, Math.Min(32767, curR));
    }

    public void SetMute(int c, bool mute) { if (c >= 0 && c < 16) ch[c].Muted = mute; }
    public void SetPitch(int c, double ratio) { if (c >= 0 && c < 16) ch[c].Pitch = ratio; }
    public double ChannelFrequency(int c) { return 0; }
    public float ChannelLevel(int c) { return c >= 0 && c < 16 && ch[c].On ? Math.Min(1f, ch[c].Vol / 8192f) : 0f; }
    public bool ChannelActive(int c) { return c >= 0 && c < 16 && ch[c].On; }
}
