using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// NES 2A03 APU (VGM 0xB4 aa dd: aa 0x00~0x17 = $4000~$4017, DPCM 메모리 = 데이터 블록 0xC2).
/// 채널 = 사각파 2 · 삼각파 · 노이즈 · DPCM. CPU 클럭으로 한 사이클씩 돌리고 출력 샘플 하나 동안 평균 (상자 필터).
/// 섞기는 NES의 비선형 공식 (nesdev 위키 "APU Mixer"), 직류는 약 30Hz 하이패스로 뺀다.
/// <para>프레임 시퀀서: 4단계(7457 · 14913 · 22371 · 29829 사이클) / 5단계, 엔벨로프 · 선형 카운터 = 1/4 프레임, 길이 · 스윕 = 1/2 프레임.</para>
/// </summary>
public sealed class NesApu : ISoundChip
{
    public const double NtscClock = 1789773;
    private readonly double clock;
    private readonly int rate;

    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    };
    private static readonly byte[,] Duty = { { 0, 1, 0, 0, 0, 0, 0, 0 }, { 0, 1, 1, 0, 0, 0, 0, 0 }, { 0, 1, 1, 1, 1, 0, 0, 0 }, { 1, 0, 0, 1, 1, 1, 1, 1 } };
    private static readonly byte[] TriSeq = { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly int[] NoisePeriod = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
    /// <summary>섞기 표 (nesdev "APU Mixer" 표 근사): 사각파 합 0~30, 삼각 · 노이즈 · DPCM = 3t + 2n + d (0~202).</summary>
    private static readonly double[] PulseTable = new double[31], TndTable = new double[203];

    static NesApu()
    {
        for (int i = 1; i < PulseTable.Length; i++) PulseTable[i] = 95.52 / (8128.0 / i + 100);
        for (int i = 1; i < TndTable.Length; i++) TndTable[i] = 163.67 / (24329.0 / i + 100);
    }

    private static readonly int[] DmcPeriod = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54 };

    private sealed class Pulse
    {
        public int DutyMode, Step, Period, Length, Vol, EnvDiv, EnvVol;
        public bool Halt, Const, EnvStart, Enabled;
        public bool SweepOn, SweepNeg, SweepReload; public int SweepPeriod, SweepShift, SweepDiv;
        public double Timer;
        public int Volume { get { return Const ? Vol : EnvVol; } }
    }

    private readonly Pulse[] pulse = { new Pulse(), new Pulse() };
    // 삼각파
    private int triPeriod, triStep, triLength, triLinear, triReload;
    private bool triControl, triReloadFlag, triEnabled;
    private double triTimer;
    // 노이즈
    private int noiseLength, noiseVol, noiseEnvDiv, noiseEnvVol, noisePeriodIdx, lfsr = 1;
    private bool noiseHalt, noiseConst, noiseEnvStart, noiseMode, noiseEnabled;
    private double noiseTimer;
    // DPCM
    private int dmcRateIdx, dmcOut, dmcAddr, dmcLen, dmcCurAddr, dmcBytesLeft, dmcShift, dmcBits = 8;
    private bool dmcLoop, dmcSilent = true, dmcHasBuffer; private int dmcBuffer;
    private double dmcTimer;
    private readonly byte[] mem = new byte[0x10000];
    // 프레임 시퀀서
    private bool fiveStep;
    private int frameCycle;

    // 시간 · 출력
    private double cycleFrac;
    private double hpPrev, hpOut;
    private readonly double hpA;
    private readonly bool[] mute = new bool[6];
    private readonly double[] pitch = { 1, 1, 1, 1, 1, 1 };

    // ------------------------------------------------------------ FDS (패미컴 디스크 시스템, VGM: NES 클럭 bit31 + 0xB4 aa 0x20~0x7F)
    /// <summary>FDS 음원이 있는가: 64칸 6비트 파형 + 볼륨 엔벨로프 + 주파수 변조 (nesdev 위키 "FDS audio").</summary>
    public readonly bool Fds;
    private readonly byte[] fdsWave = new byte[64];
    private readonly byte[] fdsModTable = new byte[32];
    private int fdsFreq, fdsVolGain, fdsVolSpeed, fdsModGain, fdsModSpeed, fdsModFreq, fdsModCounter, fdsModPos, fdsMaster, fdsEnvMaster = 0xE8;
    private bool fdsVolDirect = true, fdsVolUp, fdsModDirect = true, fdsModUp, fdsWaveHalt = true, fdsEnvHalt, fdsModHalt = true, fdsWriteMode;
    private long fdsWaveAcc, fdsModAcc;
    private int fdsVolTimer, fdsModTimer, fdsOut;
    private double fdsLp;
    private readonly double fdsLpA;
    private static readonly double[] FdsMaster = { 1.0, 2.0 / 3, 2.0 / 4, 2.0 / 5 };
    private static readonly int[] FdsModStep = { 0, 1, 2, 4, 0, -4, -2, -1 };
    private readonly byte[] regs = new byte[0x20];

    public NesApu(double clock = NtscClock, int rate = 44100, bool fds = false)
    {
        Fds = fds;
        fdsLpA = 1 - Math.Exp(-2 * Math.PI * 2000.0 / rate);   // FDS 출력의 RC 로우패스 (약 2kHz)
        this.clock = clock > 0 ? clock : NtscClock;
        this.rate = rate;
        hpA = Math.Exp(-2 * Math.PI * 30.0 / rate);
        Reset();
    }

    public string Name { get { return Fds ? "2A03 + FDS (NES)" : "2A03 (NES APU)"; } }
    public int NativeRate { get { return rate; } }
    public int ChannelCount { get { return Fds ? 6 : 5; } }
    private static readonly string[] Labels = { "P1", "P2", "TR", "NO", "DM", "FD" };
    public string ChannelLabel(int ch) { return Labels[ch]; }

    public void Reset()
    {
        foreach (var p in pulse) { p.DutyMode = p.Step = p.Period = p.Length = p.Vol = p.EnvDiv = p.EnvVol = 0; p.Halt = p.Const = p.EnvStart = p.Enabled = false; p.SweepOn = p.SweepNeg = p.SweepReload = false; p.SweepPeriod = p.SweepShift = p.SweepDiv = 0; p.Timer = 0; }
        triPeriod = triStep = triLength = triLinear = triReload = 0; triControl = triReloadFlag = triEnabled = false; triTimer = 0;
        noiseLength = noiseVol = noiseEnvDiv = noiseEnvVol = noisePeriodIdx = 0; lfsr = 1; noiseHalt = noiseConst = noiseEnvStart = noiseMode = noiseEnabled = false; noiseTimer = 0;
        dmcRateIdx = dmcOut = dmcAddr = dmcLen = dmcCurAddr = dmcBytesLeft = dmcShift = 0; dmcBits = 8; dmcLoop = false; dmcSilent = true; dmcHasBuffer = false; dmcTimer = 0;
        fiveStep = false; frameCycle = 0; cycleFrac = 0; hpPrev = hpOut = 0;
        Array.Clear(regs, 0, regs.Length);
        Array.Clear(fdsWave, 0, 64); Array.Clear(fdsModTable, 0, 32);
        fdsFreq = fdsVolGain = fdsVolSpeed = fdsModGain = fdsModSpeed = fdsModFreq = fdsModCounter = fdsModPos = fdsMaster = 0; fdsEnvMaster = 0xE8;
        fdsVolDirect = fdsModDirect = fdsWaveHalt = fdsModHalt = true; fdsVolUp = fdsModUp = fdsEnvHalt = fdsWriteMode = false;
        fdsWaveAcc = fdsModAcc = 0; fdsVolTimer = fdsModTimer = fdsOut = 0; fdsLp = 0;
    }

    /// <summary>DPCM이 읽는 메모리 (VGM 데이터 블록 0xC2: 시작 주소 + 바이트).</summary>
    public void LoadRam(int start, byte[] data, int offset, int count)
    {
        for (int i = 0; i < count; i++) mem[(start + i) & 0xFFFF] = data[offset + i];
    }

    // ------------------------------------------------------------ 레지스터

    public void Write(int port, int reg, int value)
    {
        reg &= 0x7F; value &= 0xFF;
        if (reg >= 0x20) { if (Fds) FdsWrite(reg, value); return; }
        if (reg < 0x18) regs[reg] = (byte)value;
        switch (reg)
        {
            case 0x00: case 0x04: { var p = pulse[reg >> 2]; p.DutyMode = value >> 6; p.Halt = (value & 0x20) != 0; p.Const = (value & 0x10) != 0; p.Vol = value & 15; break; }
            case 0x01: case 0x05: { var p = pulse[reg >> 2]; p.SweepOn = (value & 0x80) != 0; p.SweepPeriod = (value >> 4) & 7; p.SweepNeg = (value & 8) != 0; p.SweepShift = value & 7; p.SweepReload = true; break; }
            case 0x02: case 0x06: { var p = pulse[reg >> 2]; p.Period = (p.Period & 0x700) | value; break; }
            case 0x03: case 0x07:
                {
                    var p = pulse[reg >> 2];
                    p.Period = (p.Period & 0xFF) | ((value & 7) << 8);
                    if (p.Enabled) p.Length = LengthTable[value >> 3];
                    p.Step = 0; p.EnvStart = true;
                    break;
                }
            case 0x08: triControl = (value & 0x80) != 0; triReload = value & 0x7F; break;
            case 0x0A: triPeriod = (triPeriod & 0x700) | value; break;
            case 0x0B: triPeriod = (triPeriod & 0xFF) | ((value & 7) << 8); if (triEnabled) triLength = LengthTable[value >> 3]; triReloadFlag = true; break;
            case 0x0C: noiseHalt = (value & 0x20) != 0; noiseConst = (value & 0x10) != 0; noiseVol = value & 15; break;
            case 0x0E: noiseMode = (value & 0x80) != 0; noisePeriodIdx = value & 15; break;
            case 0x0F: if (noiseEnabled) noiseLength = LengthTable[value >> 3]; noiseEnvStart = true; break;
            case 0x10: dmcRateIdx = value & 15; dmcLoop = (value & 0x40) != 0; break;
            case 0x11: dmcOut = value & 0x7F; break;
            case 0x12: dmcAddr = 0xC000 | (value << 6); break;
            case 0x13: dmcLen = (value << 4) | 1; break;
            case 0x15:
                pulse[0].Enabled = (value & 1) != 0; if (!pulse[0].Enabled) pulse[0].Length = 0;
                pulse[1].Enabled = (value & 2) != 0; if (!pulse[1].Enabled) pulse[1].Length = 0;
                triEnabled = (value & 4) != 0; if (!triEnabled) triLength = 0;
                noiseEnabled = (value & 8) != 0; if (!noiseEnabled) noiseLength = 0;
                if ((value & 0x10) == 0) dmcBytesLeft = 0;
                else if (dmcBytesLeft == 0) { dmcCurAddr = dmcAddr; dmcBytesLeft = dmcLen; }
                break;
            case 0x17:
                fiveStep = (value & 0x80) != 0;
                frameCycle = 0;
                if (fiveStep) { QuarterFrame(); HalfFrame(); }
                break;
        }
    }

    // ------------------------------------------------------------ 프레임 시퀀서

    private void QuarterFrame()
    {
        foreach (var p in pulse)
        {
            if (p.EnvStart) { p.EnvStart = false; p.EnvVol = 15; p.EnvDiv = p.Vol; }
            else if (p.EnvDiv > 0) p.EnvDiv--;
            else { p.EnvDiv = p.Vol; if (p.EnvVol > 0) p.EnvVol--; else if (p.Halt) p.EnvVol = 15; }
        }
        if (noiseEnvStart) { noiseEnvStart = false; noiseEnvVol = 15; noiseEnvDiv = noiseVol; }
        else if (noiseEnvDiv > 0) noiseEnvDiv--;
        else { noiseEnvDiv = noiseVol; if (noiseEnvVol > 0) noiseEnvVol--; else if (noiseHalt) noiseEnvVol = 15; }
        if (triReloadFlag) triLinear = triReload;
        else if (triLinear > 0) triLinear--;
        if (!triControl) triReloadFlag = false;
    }

    private void HalfFrame()
    {
        for (int i = 0; i < 2; i++)
        {
            var p = pulse[i];
            if (!p.Halt && p.Length > 0) p.Length--;
            int target = SweepTarget(i);
            if (p.SweepDiv == 0 && p.SweepOn && p.SweepShift > 0 && p.Period >= 8 && target <= 0x7FF) p.Period = target;
            if (p.SweepDiv == 0 || p.SweepReload) { p.SweepDiv = p.SweepPeriod; p.SweepReload = false; }
            else p.SweepDiv--;
        }
        if (!triControl && triLength > 0) triLength--;
        if (!noiseHalt && noiseLength > 0) noiseLength--;
    }

    private int SweepTarget(int i)
    {
        var p = pulse[i];
        int c = p.Period >> p.SweepShift;
        if (p.SweepNeg) return i == 0 ? p.Period - c - 1 : p.Period - c;
        return p.Period + c;
    }

    private bool PulseAudible(int i)
    {
        var p = pulse[i];
        return p.Length > 0 && p.Period >= 8 && SweepTarget(i) <= 0x7FF && p.Volume > 0;
    }

    // ------------------------------------------------------------ 소리

    public void Generate(out int left, out int right)
    {
        cycleFrac += clock / rate;
        int n = (int)cycleFrac;
        cycleFrac -= n;
        double sum = 0;
        double fdsSum = 0;
        for (int k = 0; k < n; k++)
        {
            Tick(); sum += Mix();
            if (Fds) { FdsTick(); fdsSum += fdsOut; }
        }
        if (Fds && n > 0)
        {
            // FDS: 0~2016 → 사각파 하나 최대쯤의 2배 (패미컴 디스크 음원은 본체 소리보다 크게 섞인다), 로우패스
            double f = mute.Length > 5 && mute[5] ? 0 : fdsSum / n / 2016.0 * 0.3;
            fdsLp += (f - fdsLp) * fdsLpA;
            sum += fdsLp * n;
        }
        double v = n > 0 ? sum / n : Mix();
        // 직류 빼기
        double y = v - hpPrev + hpA * hpOut;
        hpPrev = v; hpOut = y;
        int s = SoundChips.Clip(y * 36000);
        left = right = s;
    }

    private void Tick()
    {
        // 프레임 시퀀서 (CPU 사이클)
        frameCycle++;
        if (frameCycle == 7457 || frameCycle == 22371) QuarterFrame();
        else if (frameCycle == 14913) { QuarterFrame(); HalfFrame(); }
        else if (!fiveStep && frameCycle == 29829) { QuarterFrame(); HalfFrame(); frameCycle = 0; }
        else if (fiveStep && frameCycle == 37281) { QuarterFrame(); HalfFrame(); frameCycle = 0; }

        // 사각파: 타이머는 2 CPU 사이클마다 (APU 사이클) → 한 CPU 사이클에 0.5 칸
        for (int i = 0; i < 2; i++)
        {
            var p = pulse[i];
            p.Timer -= 0.5 * pitch[i];
            while (p.Timer < 0) { p.Timer += p.Period + 1; p.Step = (p.Step - 1) & 7; }
        }
        // 삼각파: CPU 사이클마다, 길이 · 선형 카운터가 0이면 멈춤 (출력은 그대로), 너무 높은 음(주기 < 2)은 멈춤
        if (triLength > 0 && triLinear > 0 && triPeriod >= 2)
        {
            triTimer -= pitch[2];
            while (triTimer < 0) { triTimer += triPeriod + 1; triStep = (triStep + 1) & 31; }
        }
        // 노이즈
        noiseTimer -= pitch[3];
        while (noiseTimer < 0)
        {
            noiseTimer += NoisePeriod[noisePeriodIdx];
            int fb = (lfsr & 1) ^ ((lfsr >> (noiseMode ? 6 : 1)) & 1);
            lfsr = (lfsr >> 1) | (fb << 14);
        }
        // DPCM
        dmcTimer -= 1;
        while (dmcTimer < 0)
        {
            dmcTimer += DmcPeriod[dmcRateIdx];
            if (!dmcHasBuffer && dmcBytesLeft > 0)
            {
                dmcBuffer = mem[dmcCurAddr]; dmcHasBuffer = true;
                dmcCurAddr = dmcCurAddr == 0xFFFF ? 0x8000 : dmcCurAddr + 1;
                if (--dmcBytesLeft == 0 && dmcLoop) { dmcCurAddr = dmcAddr; dmcBytesLeft = dmcLen; }
            }
            if (!dmcSilent)
            {
                if ((dmcShift & 1) != 0) { if (dmcOut <= 125) dmcOut += 2; }
                else if (dmcOut >= 2) dmcOut -= 2;
            }
            dmcShift >>= 1;
            if (--dmcBits == 0)
            {
                dmcBits = 8;
                if (dmcHasBuffer) { dmcSilent = false; dmcShift = dmcBuffer; dmcHasBuffer = false; }
                else dmcSilent = true;
            }
        }
    }

    private double Mix()
    {
        int p1 = !mute[0] && PulseAudible(0) && Duty[pulse[0].DutyMode, pulse[0].Step] != 0 ? pulse[0].Volume : 0;
        int p2 = !mute[1] && PulseAudible(1) && Duty[pulse[1].DutyMode, pulse[1].Step] != 0 ? pulse[1].Volume : 0;
        int t = mute[2] ? 0 : TriSeq[triStep];
        int nz = !mute[3] && noiseLength > 0 && (lfsr & 1) == 0 ? (noiseConst ? noiseVol : noiseEnvVol) : 0;
        int d = mute[4] ? 0 : dmcOut;
        return PulseTable[p1 + p2] + TndTable[3 * t + 2 * nz + d];
    }

    // ------------------------------------------------------------ 조절 · 표시

    public void SetMute(int ch, bool on) { if (ch >= 0 && ch < 6) mute[ch] = on; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 6 && ch != 4) pitch[ch] = ratio > 0 ? ratio : 1.0; }

    // ------------------------------------------------------------ FDS

    /// <summary>VGM 0xB4 aa: 0x20~0x3E = $4080~$409E, 0x3F = $4023, 0x40~0x7F = $4040~$407F (파형 메모리).</summary>
    private void FdsWrite(int reg, int v)
    {
        if (reg >= 0x40) { if (fdsWriteMode) fdsWave[reg - 0x40] = (byte)(v & 0x3F); return; }
        switch (reg - 0x20)
        {
            case 0x0:   // $4080 볼륨 엔벨로프
                fdsVolDirect = (v & 0x80) != 0; fdsVolUp = (v & 0x40) != 0; fdsVolSpeed = v & 0x3F;
                if (fdsVolDirect) fdsVolGain = v & 0x3F;
                fdsVolTimer = 0;
                break;
            case 0x2: fdsFreq = (fdsFreq & 0xF00) | v; break;
            case 0x3:
                fdsFreq = (fdsFreq & 0xFF) | ((v & 0x0F) << 8);
                fdsWaveHalt = (v & 0x80) != 0; fdsEnvHalt = (v & 0x40) != 0;
                if (fdsWaveHalt) fdsWaveAcc = 0;
                break;
            case 0x4:   // $4084 변조 엔벨로프
                fdsModDirect = (v & 0x80) != 0; fdsModUp = (v & 0x40) != 0; fdsModSpeed = v & 0x3F;
                if (fdsModDirect) fdsModGain = v & 0x3F;
                fdsModTimer = 0;
                break;
            case 0x5: fdsModCounter = ((v & 0x7F) ^ 0x40) - 0x40; break;   // 7비트 부호
            case 0x6: fdsModFreq = (fdsModFreq & 0xF00) | v; break;
            case 0x7:
                fdsModFreq = (fdsModFreq & 0xFF) | ((v & 0x0F) << 8);
                fdsModHalt = (v & 0x80) != 0;
                if (fdsModHalt) fdsModAcc = 0;
                break;
            case 0x8:   // $4088 변조 표 (멈춰 있을 때 한 칸씩)
                if (fdsModHalt) { fdsModTable[(fdsModPos >> 1) & 31] = (byte)(v & 7); fdsModPos = (fdsModPos + 2) & 63; }
                break;
            case 0x9: fdsWriteMode = (v & 0x80) != 0; fdsMaster = v & 3; break;
            case 0xA: fdsEnvMaster = v; break;
        }
    }

    private void FdsTick()
    {
        // 엔벨로프: 8 × (속도 + 1) × 전체 속도($408A) 사이클마다 한 칸
        if (!fdsEnvHalt && !fdsWaveHalt && fdsEnvMaster > 0)
        {
            if (!fdsVolDirect && ++fdsVolTimer >= 8 * (fdsVolSpeed + 1) * fdsEnvMaster)
            {
                fdsVolTimer = 0;
                if (fdsVolUp) { if (fdsVolGain < 32) fdsVolGain++; } else if (fdsVolGain > 0) fdsVolGain--;
            }
            if (!fdsModDirect && ++fdsModTimer >= 8 * (fdsModSpeed + 1) * fdsEnvMaster)
            {
                fdsModTimer = 0;
                if (fdsModUp) { if (fdsModGain < 32) fdsModGain++; } else if (fdsModGain > 0) fdsModGain--;
            }
        }
        // 변조 장치: 표 한 칸마다 카운터를 움직인다
        if (!fdsModHalt && fdsModFreq > 0)
        {
            fdsModAcc += fdsModFreq;
            while (fdsModAcc >= 65536)
            {
                fdsModAcc -= 65536;
                int e = fdsModTable[(fdsModPos >> 1) & 31];
                fdsModCounter = e == 4 ? 0 : (((fdsModCounter + FdsModStep[e]) + 64) & 127) - 64;
                fdsModPos = (fdsModPos + 1) & 63;
            }
        }
        // 파형
        if (!fdsWaveHalt)
        {
            int f = fdsFreq;
            if (!fdsModHalt && fdsModGain != 0)
            {
                // nesdev "FDS audio" 주파수 변조 계산
                int temp = fdsModCounter * fdsModGain;
                int rem = temp & 0x0F;
                temp >>= 4;
                if (rem > 0 && (temp & 0x80) == 0) temp += fdsModCounter < 0 ? -1 : 2;
                if (temp >= 192) temp -= 256; else if (temp < -64) temp += 256;
                temp = fdsFreq * temp;
                rem = temp & 0x3F;
                temp >>= 6;
                if (rem >= 32) temp += 1;
                f = fdsFreq + temp;
                if (f < 0) f = 0;
            }
            fdsWaveAcc += (long)(f * pitch[5]);
        }
        if (!fdsWriteMode)
        {
            int pos = (int)((fdsWaveAcc >> 16) & 63);
            fdsOut = (int)(fdsWave[pos] * Math.Min(fdsVolGain, 32) * FdsMaster[fdsMaster]);
        }
    }

    public double ChannelFrequency(int ch)
    {
        switch (ch)
        {
            case 0: case 1: return PulseAudible(ch) ? clock / (16.0 * (pulse[ch].Period + 1)) : 0;
            case 2: return triLength > 0 && triLinear > 0 && triPeriod >= 2 ? clock / (32.0 * (triPeriod + 1)) : 0;
            case 5: return Fds && !fdsWaveHalt && fdsVolGain > 0 && fdsFreq > 0 ? clock * fdsFreq / (64.0 * 65536) : 0;
        }
        return 0;
    }

    public float ChannelLevel(int ch)
    {
        switch (ch)
        {
            case 0: case 1: return PulseAudible(ch) ? pulse[ch].Volume / 15f : 0;
            case 2: return triLength > 0 && triLinear > 0 ? 1f : 0;
            case 3: return noiseLength > 0 ? (noiseConst ? noiseVol : noiseEnvVol) / 15f : 0;
            case 4: return dmcBytesLeft > 0 || !dmcSilent ? 1f : 0;
            case 5: return Fds && !fdsWaveHalt ? Math.Min(fdsVolGain, 32) / 32f : 0;
        }
        return 0;
    }

    public bool ChannelActive(int ch)
    {
        if (ch == 3) return noiseLength > 0 && (noiseConst ? noiseVol : noiseEnvVol) > 0;
        if (ch == 4) return dmcBytesLeft > 0;
        if (ch == 5) return Fds && !fdsWaveHalt && fdsVolGain > 0 && fdsFreq > 0;
        return false;
    }
}
