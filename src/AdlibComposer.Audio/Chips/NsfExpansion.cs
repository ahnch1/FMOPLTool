using System;
using System.Collections.Generic;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// NSF 확장 음원 중 VGM에 명령이 없는 것: VRC6 · N163 · MMC5 (VGM 예약 명령 0xD7 cc aa dd로 이 프로그램 안에서만 쓴다:
/// cc 0 = VRC6 (aa 0~11 = $9000~$9003 · $A000~$A003 · $B000~$B003), 1 = N163 (aa = 내부 메모리 주소 0~127),
/// 3 = MMC5 (aa = $5000~$501F의 아래 5비트)). VRC7은 YM2413(<see cref="Ym2413"/>, VRC7 악기), 선소프트 5B는 YM2149, FDS는 <see cref="NesApu"/>.
/// <para>CPU 클럭으로 한 사이클씩 돌리고 출력 샘플 하나 동안 평균. 크기는 NES APU 사각파 한 칸(볼륨 1) ≈ 0.0099와 같은 단위로 맞춘다 (nesdev 위키 각 칩 문서).</para>
/// </summary>
public sealed class NsfExpansion : ISoundChip
{
    public const int Vrc6 = 0x01, Vrc7 = 0x02, Fds = 0x04, Mmc5 = 0x08, N163 = 0x10, Sunsoft5B = 0x20;
    /// <summary>VGM 0xD7의 칩 번호.</summary>
    public const int IdVrc6 = 0, IdN163 = 1, IdMmc5 = 3;

    private readonly double clock;
    private readonly int rate;
    private readonly bool hasVrc6, hasN163, hasMmc5;
    private readonly List<string> labels = new List<string>();
    private readonly int baseVrc6 = -1, baseN163 = -1, baseMmc5 = -1;
    private const double Unit = 0.0099;
    private double cycleFrac, hpPrev, hpOut;
    private readonly double hpA;
    private readonly bool[] mute = new bool[16];
    private readonly double[] pitch = new double[16];

    public NsfExpansion(int chips, double clock = NesApu.NtscClock, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : NesApu.NtscClock;
        this.rate = rate;
        hpA = Math.Exp(-2 * Math.PI * 30.0 / rate);
        hasVrc6 = (chips & Vrc6) != 0; hasN163 = (chips & N163) != 0; hasMmc5 = (chips & Mmc5) != 0;
        if (hasVrc6) { baseVrc6 = labels.Count; labels.AddRange(new[] { "V1", "V2", "VS" }); }
        if (hasN163) { baseN163 = labels.Count; for (int i = 1; i <= 8; i++) labels.Add("N" + i); }
        if (hasMmc5) { baseMmc5 = labels.Count; labels.AddRange(new[] { "M1", "M2", "MP" }); }
        for (int i = 0; i < pitch.Length; i++) pitch[i] = 1;
        Reset();
    }

    public string Name
    {
        get
        {
            var l = new List<string>();
            if (hasVrc6) l.Add("VRC6"); if (hasN163) l.Add("N163"); if (hasMmc5) l.Add("MMC5");
            return string.Join(" + ", l) + " (NES)";
        }
    }
    public int NativeRate { get { return rate; } }
    public int ChannelCount { get { return labels.Count; } }
    public string ChannelLabel(int ch) { return ch >= 0 && ch < labels.Count ? labels[ch] : "?"; }

    // ------------------------------------------------------------ VRC6

    private sealed class VPulse { public int Duty, Vol, Period, Step; public bool Mode, On; public double Timer; }
    private readonly VPulse[] vp = { new VPulse(), new VPulse() };
    private int sawRate, sawPeriod, sawAcc, sawStep; private bool sawOn, vrcHalt; private double sawTimer;

    // ------------------------------------------------------------ N163

    private readonly byte[] nRam = new byte[128];
    private readonly long[] nPhase = new long[8];
    private readonly double[] nOut = new double[8];
    private int nCycle, nCh;

    // ------------------------------------------------------------ MMC5

    private sealed class MPulse
    {
        public int DutyMode, Step, Period, Length, Vol, EnvDiv, EnvVol;
        public bool Halt, Const, EnvStart, Enabled;
        public double Timer;
        public int Volume { get { return Const ? Vol : EnvVol; } }
    }
    private readonly MPulse[] mp = { new MPulse(), new MPulse() };
    private int mPcm, mFrame;
    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    };
    private static readonly byte[,] Duty = { { 0, 1, 0, 0, 0, 0, 0, 0 }, { 0, 1, 1, 0, 0, 0, 0, 0 }, { 0, 1, 1, 1, 1, 0, 0, 0 }, { 1, 0, 0, 1, 1, 1, 1, 1 } };

    public void Reset()
    {
        foreach (var p in vp) { p.Duty = p.Vol = p.Period = p.Step = 0; p.Mode = p.On = false; p.Timer = 0; }
        sawRate = sawPeriod = sawAcc = sawStep = 0; sawOn = vrcHalt = false; sawTimer = 0;
        Array.Clear(nRam, 0, nRam.Length); Array.Clear(nPhase, 0, 8); Array.Clear(nOut, 0, 8); nCycle = nCh = 0;
        foreach (var p in mp) { p.DutyMode = p.Step = p.Period = p.Length = p.Vol = p.EnvDiv = p.EnvVol = 0; p.Halt = p.Const = p.EnvStart = p.Enabled = false; p.Timer = 0; }
        mPcm = mFrame = 0; cycleFrac = hpPrev = hpOut = 0;
    }

    public void Write(int port, int reg, int v)
    {
        v &= 0xFF;
        switch (port)
        {
            case IdVrc6: WriteVrc6(reg, v); break;
            case IdN163: nRam[reg & 0x7F] = (byte)v; break;
            case IdMmc5: WriteMmc5(reg & 0x1F, v); break;
        }
    }

    private void WriteVrc6(int r, int v)
    {
        int unit = r >> 2, sub = r & 3;
        if (unit < 2)
        {
            var p = vp[unit];
            if (sub == 0) { p.Mode = (v & 0x80) != 0; p.Duty = (v >> 4) & 7; p.Vol = v & 15; }
            else if (sub == 1) p.Period = (p.Period & 0xF00) | v;
            else if (sub == 2) { p.Period = (p.Period & 0xFF) | ((v & 15) << 8); p.On = (v & 0x80) != 0; if (!p.On) p.Step = 15; }
            else if (unit == 0) vrcHalt = (v & 1) != 0;   // $9003
            return;
        }
        if (sub == 0) sawRate = v & 0x3F;
        else if (sub == 1) sawPeriod = (sawPeriod & 0xF00) | v;
        else if (sub == 2) { sawPeriod = (sawPeriod & 0xFF) | ((v & 15) << 8); sawOn = (v & 0x80) != 0; if (!sawOn) { sawAcc = 0; sawStep = 0; } }
    }

    private void WriteMmc5(int r, int v)
    {
        if (r < 8)
        {
            var p = mp[r >> 2];
            switch (r & 3)
            {
                case 0: p.DutyMode = v >> 6; p.Halt = (v & 0x20) != 0; p.Const = (v & 0x10) != 0; p.Vol = v & 15; break;
                case 2: p.Period = (p.Period & 0x700) | v; break;
                case 3: p.Period = (p.Period & 0xFF) | ((v & 7) << 8); if (p.Enabled) p.Length = LengthTable[v >> 3]; p.Step = 0; p.EnvStart = true; break;
            }
            return;
        }
        if (r == 0x11) mPcm = v;
        else if (r == 0x15)
        {
            mp[0].Enabled = (v & 1) != 0; if (!mp[0].Enabled) mp[0].Length = 0;
            mp[1].Enabled = (v & 2) != 0; if (!mp[1].Enabled) mp[1].Length = 0;
        }
    }

    // ------------------------------------------------------------ 소리

    private int NCount { get { return ((nRam[0x7F] >> 4) & 7) + 1; } }

    public void Generate(out int left, out int right)
    {
        cycleFrac += clock / rate;
        int n = (int)cycleFrac;
        cycleFrac -= n;
        double sum = 0;
        for (int k = 0; k < n; k++) sum += Tick();
        double v = n > 0 ? sum / n : 0;
        double y = v - hpPrev + hpA * hpOut;
        hpPrev = v; hpOut = y;
        left = right = SoundChips.Clip(y * 36000);
    }

    private double Tick()
    {
        double outSum = 0;
        if (hasVrc6)
        {
            if (!vrcHalt)
            {
                for (int i = 0; i < 2; i++)
                {
                    var p = vp[i];
                    if (!p.On) continue;
                    p.Timer -= pitch[baseVrc6 + i];
                    while (p.Timer < 0) { p.Timer += p.Period + 1; p.Step = (p.Step - 1) & 15; }
                }
                if (sawOn)
                {
                    sawTimer -= pitch[baseVrc6 + 2];
                    while (sawTimer < 0)
                    {
                        sawTimer += sawPeriod + 1;
                        // 두 번 셀 때마다 누산, 일곱 번 더하면 (14칸) 처음으로
                        sawStep++;
                        if ((sawStep & 1) == 0) sawAcc = (sawAcc + sawRate) & 0xFF;
                        if (sawStep >= 14) { sawStep = 0; sawAcc = 0; }
                    }
                }
            }
            for (int i = 0; i < 2; i++)
            {
                var p = vp[i];
                if (!p.On || mute[baseVrc6 + i]) continue;
                if (p.Mode || p.Step <= p.Duty) outSum += p.Vol;
            }
            if (sawOn && !mute[baseVrc6 + 2]) outSum += sawAcc >> 3;
        }
        if (hasMmc5)
        {
            // 엔벨로프 · 길이 = 240Hz 고정
            if (++mFrame >= (int)(clock / 240)) { mFrame = 0; MmcFrame(); }
            for (int i = 0; i < 2; i++)
            {
                var p = mp[i];
                p.Timer -= 0.5 * pitch[baseMmc5 + i];
                while (p.Timer < 0) { p.Timer += p.Period + 1; p.Step = (p.Step - 1) & 7; }
                if (!mute[baseMmc5 + i] && p.Length > 0 && p.Period >= 8 && Duty[p.DutyMode, p.Step] != 0) outSum += p.Volume;
            }
            if (!mute[baseMmc5 + 2]) outSum += mPcm / 255.0 * 20;
        }
        double result = outSum * Unit;
        if (hasN163)
        {
            // 15 사이클마다 채널 하나를 갱신 (7번부터 아래로), 출력 = 켜진 채널의 평균 (칩은 번갈아 내보낸다)
            int count = NCount;
            if (++nCycle >= 15)
            {
                nCycle = 0;
                int ch = 7 - (nCh % count);
                nCh = (nCh + 1) % count;
                int b = 0x40 + ch * 8;
                long freq = nRam[b] | (nRam[b + 2] << 8) | ((nRam[b + 4] & 3) << 16);
                int len = 256 - (nRam[b + 4] & 0xFC);
                long phase = nRam[b + 1] | (nRam[b + 3] << 8) | (nRam[b + 5] << 16);
                phase = (phase + (long)(freq * pitch[baseN163 + 7 - ch])) % ((long)len << 16);
                nRam[b + 1] = (byte)phase; nRam[b + 3] = (byte)(phase >> 8); nRam[b + 5] = (byte)(phase >> 16);
                int addr = ((int)(phase >> 16) + nRam[b + 6]) & 0xFF;
                int sample = (nRam[addr >> 1] >> ((addr & 1) * 4)) & 15;
                nOut[ch] = mute[baseN163 + 7 - ch] ? 0 : (sample - 8) * (nRam[b + 7] & 15);
            }
            double s = 0;
            for (int i = 0; i < count; i++) s += nOut[7 - i];
            result += s / count * 0.0015;   // 한 채널 최대(±112) ≈ APU 사각파 최대의 2배쯤
        }
        return result;
    }

    private void MmcFrame()
    {
        foreach (var p in mp)
        {
            if (p.EnvStart) { p.EnvStart = false; p.EnvVol = 15; p.EnvDiv = p.Vol; }
            else if (p.EnvDiv > 0) p.EnvDiv--;
            else { p.EnvDiv = p.Vol; if (p.EnvVol > 0) p.EnvVol--; else if (p.Halt) p.EnvVol = 15; }
            if (!p.Halt && p.Length > 0) p.Length--;
        }
    }

    // ------------------------------------------------------------ 조절 · 표시

    public void SetMute(int ch, bool on) { if (ch >= 0 && ch < mute.Length) mute[ch] = on; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < pitch.Length) pitch[ch] = ratio > 0 ? ratio : 1.0; }

    /// <summary>표시 채널 → (칩, 칩 안 번호).</summary>
    private int Which(int ch, out int idx)
    {
        idx = -1;
        if (baseVrc6 >= 0 && ch >= baseVrc6 && ch < baseVrc6 + 3) { idx = ch - baseVrc6; return IdVrc6; }
        if (baseN163 >= 0 && ch >= baseN163 && ch < baseN163 + 8) { idx = ch - baseN163; return IdN163; }
        if (baseMmc5 >= 0 && ch >= baseMmc5 && ch < baseMmc5 + 3) { idx = ch - baseMmc5; return IdMmc5; }
        return -1;
    }

    /// <summary>N163 표시 채널 i (0부터) = 칩 채널 7 − i. 켜진 채널 수 밖이면 -1.</summary>
    private int N163Reg(int i) { return i < NCount ? 0x40 + (7 - i) * 8 : -1; }

    public double ChannelFrequency(int ch)
    {
        switch (Which(ch, out int i))
        {
            case IdVrc6:
                if (i < 2) return vp[i].On && vp[i].Vol > 0 && !vrcHalt ? clock / (16.0 * (vp[i].Period + 1)) : 0;
                return sawOn && sawRate > 0 ? clock / (14.0 * (sawPeriod + 1)) : 0;
            case IdN163:
                {
                    int b = N163Reg(i);
                    if (b < 0 || (nRam[b + 7] & 15) == 0) return 0;
                    long freq = nRam[b] | (nRam[b + 2] << 8) | ((nRam[b + 4] & 3) << 16);
                    int len = 256 - (nRam[b + 4] & 0xFC);
                    return freq == 0 ? 0 : clock * freq / (15.0 * 65536 * NCount * len);
                }
            case IdMmc5:
                if (i < 2) return mp[i].Length > 0 && mp[i].Period >= 8 && mp[i].Volume > 0 ? clock / (16.0 * (mp[i].Period + 1)) : 0;
                return 0;
        }
        return 0;
    }

    public float ChannelLevel(int ch)
    {
        switch (Which(ch, out int i))
        {
            case IdVrc6: return i < 2 ? (vp[i].On ? vp[i].Vol / 15f : 0) : (sawOn ? Math.Min(1f, sawRate / 42f) : 0);
            case IdN163: { int b = N163Reg(i); return b < 0 ? 0 : (nRam[b + 7] & 15) / 15f; }
            case IdMmc5: return i < 2 ? (mp[i].Length > 0 ? mp[i].Volume / 15f : 0) : mPcm / 255f;
        }
        return 0;
    }

    public bool ChannelActive(int ch)
    {
        return Which(ch, out int i) == IdMmc5 && i == 2 && mPcm != 0;
    }
}
