using System;
using AdlibComposer.Audio.Cpu;

namespace AdlibComposer.Audio.Chips;

/// <summary>스스로 시간을 움직이는 칩 (CPU를 품은 칩): 앞으로 감기 · 쓴 채널 찾기를 칩이 직접 한다.</summary>
public interface ISelfRunningChip
{
    /// <summary>소리를 버리고 seconds초 앞으로.</summary>
    void Skip(double seconds);
    /// <summary>지금까지 소리를 낸 채널 (비트).</summary>
    int VoicesUsed { get; }
}

/// <summary>
/// 슈퍼 패미컴 사운드 부분 (SPC 파일 하나 = 이것의 순간 상태): SPC700 CPU(<see cref="Spc700"/>) + 64KB RAM + IPL ROM + 타이머 3개 + S-DSP.
/// <para>S-DSP (32kHz, 8 보이스): BRR 샘플 풀기 (9바이트 블록, 필터 4종), 4탭 가우스 보간, ADSR · GAIN 엔벨로프, 노이즈, 피치 변조(PMON),
/// 에코 (RAM 안 버퍼, 8탭 FIR, 되먹임). 풀기 · 섞기 · 에코 순서와 비트 폭은 blargg의 snes_spc 문서를 따른다.
/// 가우스 표는 실제 칩 표 대신 같은 모양의 가우스 곡선(σ = 0.63, 가운데 1301)으로 만든 근사.</para>
/// <para>CPU 1.024MHz, DSP 한 샘플 = 32 사이클. 타이머 0 · 1 = 8kHz(128 사이클), 타이머 2 = 64kHz(16 사이클).</para>
/// </summary>
public sealed class SnesSpc : ISoundChip, ISelfRunningChip, ISpcBus
{
    public const int Rate = 32000;
    private const int CyclesPerSample = 32;

    private readonly byte[] ram = new byte[0x10000];
    private readonly byte[] regs = new byte[128];
    private readonly Spc700 cpu;
    private readonly byte[] image;

    private static readonly byte[] IplRom =
    {
        0xCD, 0xEF, 0xBD, 0xE8, 0x00, 0xC6, 0x1D, 0xD0, 0xFC, 0x8F, 0xAA, 0xF4, 0x8F, 0xBB, 0xF5, 0x78,
        0xCC, 0xF4, 0xD0, 0xFB, 0x2F, 0x19, 0xEB, 0xF4, 0xD0, 0xFC, 0x7E, 0xF4, 0xD0, 0x0B, 0xE4, 0xF5,
        0xCB, 0xF4, 0xD7, 0x00, 0xFC, 0xD0, 0xF3, 0xAB, 0x01, 0x10, 0xEF, 0x7E, 0xF4, 0x10, 0xEB, 0xBA,
        0xF6, 0xDA, 0x00, 0xBA, 0xF4, 0xC4, 0xF4, 0xDD, 0x5D, 0xD0, 0xDB, 0x1F, 0x00, 0x00, 0xC0, 0xFF,
    };

    // ------------------------------------------------------------ I/O
    private int dspAddr;
    private readonly byte[] portIn = new byte[4];
    private bool ipl;
    private readonly bool[] tOn = new bool[3];
    private readonly int[] tTarget = new int[3], tCount = new int[3], tOut = new int[3];
    private readonly long[] tAcc = new long[3];
    private long timerAt;
    private long nextSample;

    // ------------------------------------------------------------ DSP 보이스
    private sealed class Voice
    {
        public int Addr;          // 지금 BRR 블록 주소
        public int Offset;        // 블록 안 다음에 풀 샘플 (0~15)
        public int Header;
        public int Pos;           // 보간 위치 (12비트 소수)
        public readonly int[] Hist = new int[4];   // 0 = 가장 오래된
        public int P1, P2;        // BRR 필터 앞 두 샘플 (×2 된 값)
        public int Env;           // 0~0x7FF
        public int Mode;          // 0 공격 1 감쇠 2 지속 3 놓음
        public int EnvCounter;
        public int KonDelay;
        public bool On;
        public int Out;           // 마지막 출력 (PMON · OUTX)
    }

    private readonly Voice[] v = new Voice[8];
    private int pendingKon;
    private int noise = 0x4000, noiseCounter;
    private int echoPos;
    private readonly int[,] echoHist = new int[2, 8];
    private int echoHistPos;
    private readonly bool[] mute = new bool[8];
    private readonly double[] pitchMul = { 1, 1, 1, 1, 1, 1, 1, 1 };
    private int used;

    /// <summary>엔벨로프 · 노이즈 빠르기 (샘플 수, 0 = 멈춤).</summary>
    private static readonly int[] RatePeriod =
    {
        0, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1,
    };

    private static readonly int[] Gauss = BuildGauss();

    private static int[] BuildGauss()
    {
        // gauss[i]: i = 256~511 → 거리 (511 − i) / 256, i = 0~255 → 거리 1 + (255 − i) / 256. K(d) = A·exp(−d² / 2σ²), 네 탭 합 ≈ 2048
        const double sigma = 0.63;
        double k(double dist) { return Math.Exp(-dist * dist / (2 * sigma * sigma)); }
        double a = 2048 / (1 + 2 * k(1) + k(2));
        var g = new int[512];
        for (int i = 0; i < 512; i++)
        {
            double dist = i >= 256 ? (511 - i) / 256.0 : 1 + (255 - i) / 256.0;
            g[i] = (int)Math.Round(a * k(dist));
        }
        return g;
    }

    // ------------------------------------------------------------ 만들기 · 읽어 들이기

    /// <param name="spcFile">.spc 파일 전체 (0x100 RAM 64KB · 0x10100 DSP 128 · 0x101C0 IPL 아래 RAM 64).</param>
    public SnesSpc(byte[] spcFile)
    {
        image = spcFile;
        cpu = new Spc700(this);
        for (int i = 0; i < 8; i++) v[i] = new Voice();
        Reset();
    }

    // ------------------------------------------------------------ DSP만 (CPU 없이 바깥이 레지스터 · RAM을 쓴다)

    private bool dspOnly;

    /// <summary>SPC700 프로그램 없이 S-DSP만: 바깥(8-bit 신디)이 <see cref="WriteDsp"/> · <see cref="LoadRam"/>로 바로 쓴다. 처음 RAM · 레지스터는 모두 0.</summary>
    public static SnesSpc DspOnly()
    {
        var img = new byte[0x10200];
        img[0x10100 + 0x6C] = 0x60;   // FLG: 음소거 · 에코 쓰기 끔 (바깥이 켠다)
        return new SnesSpc(img) { dspOnly = true };
    }

    /// <summary>DSP 레지스터 쓰기 (0~7Fh).</summary>
    public void WriteDsp(int r, int value) { DspWrite(r & 0x7F, value & 0xFF); }

    /// <summary>64KB RAM에 바이트들 (샘플 · 디렉터리).</summary>
    public void LoadRam(int at, byte[] data) { Array.Copy(data, 0, ram, at, Math.Min(data.Length, 0x10000 - at)); }

    public void Reset()
    {
        var f = image;
        if (f == null || f.Length < 0x10180) throw new ArgumentException("SPC 파일이 너무 짧습니다.");
        Array.Copy(f, 0x100, ram, 0, 0x10000);
        Array.Copy(f, 0x10100, regs, 0, 128);
        if (f.Length >= 0x10200) Array.Copy(f, 0x101C0, ram, 0xFFC0, 64);
        cpu.PC = f[0x25] | (f[0x26] << 8);
        cpu.A = f[0x27]; cpu.X = f[0x28]; cpu.Y = f[0x29]; cpu.PSW = f[0x2A]; cpu.SP = f[0x2B];
        cpu.Cycles = 0; cpu.Stopped = false;
        // I/O 상태는 RAM 사본의 $F0~$FF에서
        int ctl = ram[0xF1];
        ipl = (ctl & 0x80) != 0;
        for (int i = 0; i < 3; i++)
        {
            tOn[i] = (ctl & (1 << i)) != 0;
            tTarget[i] = ram[0xFA + i];
            tOut[i] = ram[0xFD + i] & 15;
            tCount[i] = 0; tAcc[i] = 0;
        }
        dspAddr = ram[0xF2];
        for (int i = 0; i < 4; i++) portIn[i] = ram[0xF4 + i];
        timerAt = 0; nextSample = CyclesPerSample;
        foreach (var x in v) { x.On = false; x.Env = 0; x.Mode = 3; x.Pos = 0; x.KonDelay = 0; x.Out = 0; Array.Clear(x.Hist, 0, 4); }
        pendingKon = 0; noise = 0x4000; noiseCounter = 0; echoPos = 0; echoHistPos = 0; Array.Clear(echoHist, 0, echoHist.Length);
        used = 0;
    }

    // ------------------------------------------------------------ 버스

    private void CatchUpTimers()
    {
        long now = cpu.Cycles;
        long dt = now - timerAt;
        if (dt <= 0) return;
        timerAt = now;
        for (int i = 0; i < 3; i++)
        {
            if (!tOn[i]) continue;
            int period = i == 2 ? 16 : 128;
            tAcc[i] += dt;
            while (tAcc[i] >= period)
            {
                tAcc[i] -= period;
                int target = tTarget[i] == 0 ? 256 : tTarget[i];
                if (++tCount[i] >= target) { tCount[i] = 0; tOut[i] = (tOut[i] + 1) & 15; }
            }
        }
    }

    public int Read(int a)
    {
        if (a >= 0xF0 && a <= 0xFF)
        {
            CatchUpTimers();
            switch (a)
            {
                case 0xF2: return dspAddr;
                case 0xF3: return DspRead(dspAddr & 0x7F);
                case 0xF4: case 0xF5: case 0xF6: case 0xF7: return portIn[a - 0xF4];
                case 0xFD: case 0xFE: case 0xFF: { int t = tOut[a - 0xFD]; tOut[a - 0xFD] = 0; return t; }
                case 0xFA: case 0xFB: case 0xFC: case 0xF0: case 0xF1: return 0;
            }
            return ram[a];
        }
        if (a >= 0xFFC0 && ipl) return IplRom[a - 0xFFC0];
        return ram[a];
    }

    public void Write(int a, int value)
    {
        if (a >= 0xF0 && a <= 0xFF)
        {
            CatchUpTimers();
            switch (a)
            {
                case 0xF1:
                    for (int i = 0; i < 3; i++)
                    {
                        bool on = (value & (1 << i)) != 0;
                        if (on && !tOn[i]) { tCount[i] = 0; tOut[i] = 0; tAcc[i] = 0; }
                        tOn[i] = on;
                    }
                    if ((value & 0x10) != 0) { portIn[0] = portIn[1] = 0; }
                    if ((value & 0x20) != 0) { portIn[2] = portIn[3] = 0; }
                    ipl = (value & 0x80) != 0;
                    break;
                case 0xF2: dspAddr = value; break;
                case 0xF3: if ((dspAddr & 0x80) == 0) DspWrite(dspAddr, value); break;
                case 0xFA: case 0xFB: case 0xFC: tTarget[a - 0xFA] = value; break;
                case 0xF4: case 0xF5: case 0xF6: case 0xF7: return;   // SNES 쪽으로 (재생에는 쓰지 않음)
            }
            ram[a] = (byte)value;
            return;
        }
        ram[a] = (byte)value;
    }

    private int DspRead(int r)
    {
        int vi = r >> 4;
        if ((r & 0x0F) == 0x08) return (v[vi].Env >> 4) & 0x7F;        // ENVX
        if ((r & 0x0F) == 0x09) return (v[vi].Out >> 8) & 0xFF;        // OUTX
        return regs[r];
    }

    private void DspWrite(int r, int value)
    {
        regs[r] = (byte)value;
        if (r == 0x4C) pendingKon |= value;                             // KON
        else if (r == 0x7C) regs[r] = 0;                                // ENDX: 쓰면 지워진다
    }

    // ------------------------------------------------------------ DSP

    private int Reg(int vi, int r) { return regs[(vi << 4) | r]; }
    private static int S8(int b) { return (sbyte)(byte)b; }
    private static int Clamp16(int s) { return s > 32767 ? 32767 : s < -32768 ? -32768 : s; }

    private int DirEntry(int srcn, int which)
    {
        int at = ((regs[0x5D] << 8) + srcn * 4 + which * 2) & 0xFFFF;
        return ram[at] | (ram[(at + 1) & 0xFFFF] << 8);
    }

    private void KeyOn(int vi)
    {
        var x = v[vi];
        x.Addr = DirEntry(Reg(vi, 4), 0);
        x.Header = ram[x.Addr];
        x.Offset = 0;
        x.Pos = 0;
        x.P1 = x.P2 = 0;
        Array.Clear(x.Hist, 0, 4);
        x.Env = 0;
        x.Mode = 0;
        x.EnvCounter = 0;
        x.KonDelay = 5;
        x.On = true;
        regs[0x7C] &= (byte)~(1 << vi);
    }

    /// <summary>BRR 샘플 하나를 풀어 보간 기록에 넣는다 (블록 끝이면 다음 블록 · 반복 · 끝).</summary>
    private void DecodeNext(int vi)
    {
        var x = v[vi];
        if (x.Offset >= 16)
        {
            // 블록 끝: END면 ENDX, LOOP면 반복 주소로, 아니면 소리 끝
            if ((x.Header & 1) != 0)
            {
                regs[0x7C] |= (byte)(1 << vi);
                if ((x.Header & 2) != 0) x.Addr = DirEntry(Reg(vi, 4), 1);
                else { x.Mode = 3; x.Env = 0; x.On = false; }
            }
            else x.Addr = (x.Addr + 9) & 0xFFFF;
            x.Header = ram[x.Addr];
            x.Offset = 0;
        }
        int b = ram[(x.Addr + 1 + (x.Offset >> 1)) & 0xFFFF];
        int nib = (x.Offset & 1) == 0 ? b >> 4 : b & 0x0F;
        x.Offset++;
        int s = (nib ^ 8) - 8;                                          // 부호 있는 4비트
        int shift = x.Header >> 4;
        s = (s << shift) >> 1;
        if (shift >= 0xD) s = s < 0 ? -2048 : 0;
        int filter = x.Header & 0x0C;
        int p1 = x.P1, p2 = x.P2 >> 1;
        if (filter >= 8)
        {
            s += p1;
            s -= p2;
            if (filter == 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }
            else { s += (p1 * -13) >> 7; s += (p2 * 3) >> 4; }
        }
        else if (filter != 0) { s += p1 >> 1; s += (-p1) >> 5; }
        s = Clamp16(s);
        s = (short)(s * 2);
        x.P2 = x.P1; x.P1 = s;
        x.Hist[0] = x.Hist[1]; x.Hist[1] = x.Hist[2]; x.Hist[2] = x.Hist[3]; x.Hist[3] = s;
    }

    private void StepEnvelope(int vi)
    {
        var x = v[vi];
        if (x.Mode == 3) { x.Env -= 8; if (x.Env < 0) x.Env = 0; return; }   // 놓음: 샘플마다 −8
        int adsr1 = Reg(vi, 5), adsr2 = Reg(vi, 6), gain = Reg(vi, 7);
        int rate;
        int step = 0;
        if ((adsr1 & 0x80) != 0)
        {
            switch (x.Mode)
            {
                case 0: rate = (adsr1 & 0x0F) * 2 + 1; step = rate == 31 ? 1024 : 32; break;
                case 1: rate = ((adsr1 >> 4) & 7) * 2 + 16; step = -(((x.Env - 1) >> 8) + 1); break;
                default: rate = adsr2 & 0x1F; step = -(((x.Env - 1) >> 8) + 1); break;
            }
        }
        else
        {
            if ((gain & 0x80) == 0) { x.Env = (gain & 0x7F) << 4; return; }   // 직접
            rate = gain & 0x1F;
            switch ((gain >> 5) & 3)
            {
                case 0: step = -32; break;
                case 1: step = -(((x.Env - 1) >> 8) + 1); break;
                case 2: step = 32; break;
                default: step = x.Env < 0x600 ? 32 : 8; break;
            }
        }
        int period = RatePeriod[rate];
        if (period == 0) return;
        if (++x.EnvCounter < period) return;
        x.EnvCounter = 0;
        x.Env += step;
        if ((adsr1 & 0x80) != 0)
        {
            if (x.Mode == 0 && x.Env >= 0x7E0) { if (x.Env > 0x7FF) x.Env = 0x7FF; x.Mode = 1; }
            else if (x.Mode == 1 && (x.Env >> 8) <= ((adsr2 >> 5) & 7)) x.Mode = 2;
        }
        if (x.Env < 0) x.Env = 0;
        if (x.Env > 0x7FF) x.Env = 0x7FF;
    }

    /// <summary>DSP 32kHz 샘플 하나.</summary>
    private void DspSample(out int outL, out int outR)
    {
        int flg = regs[0x6C];
        // 키 온 / 오프
        if (pendingKon != 0)
        {
            for (int i = 0; i < 8; i++) if ((pendingKon & (1 << i)) != 0) KeyOn(i);
            pendingKon = 0;
        }
        int koff = regs[0x5C];
        if ((flg & 0x80) != 0) koff = 0xFF;   // 리셋
        // 노이즈
        int nrate = RatePeriod[flg & 0x1F];
        if (nrate != 0 && ++noiseCounter >= nrate)
        {
            noiseCounter = 0;
            int fb = (noise << 13) ^ (noise << 14);
            noise = (fb & 0x4000) ^ (noise >> 1);
        }
        int pmon = regs[0x2D], non = regs[0x3D], eon = regs[0x4D];
        int mainL = 0, mainR = 0, echoL = 0, echoR = 0;
        int prevOut = 0;
        for (int i = 0; i < 8; i++)
        {
            var x = v[i];
            if ((koff & (1 << i)) != 0 && x.KonDelay == 0) x.Mode = 3;
            if (!x.On && x.Env == 0) { x.Out = 0; prevOut = 0; continue; }
            if (x.KonDelay > 0)
            {
                // 키 온 뒤 5샘플: 앞 블록을 풀어 두는 동안 소리 없음
                if (--x.KonDelay == 0) { DecodeNext(i); DecodeNext(i); DecodeNext(i); DecodeNext(i); }
                x.Out = 0; prevOut = 0;
                continue;
            }
            // 피치
            int pitch = (Reg(i, 2) | (Reg(i, 3) << 8)) & 0x3FFF;
            if ((pmon & (1 << i)) != 0 && i > 0) pitch += ((prevOut >> 5) * pitch) >> 10;
            if (pitchMul[i] != 1.0) pitch = (int)(pitch * pitchMul[i]);
            if (pitch < 0) pitch = 0; if (pitch > 0x3FFF) pitch = 0x3FFF;
            // 샘플 (노이즈 또는 가우스 보간)
            int s;
            if ((non & (1 << i)) != 0) s = (short)(noise * 2);
            else
            {
                int f = (x.Pos >> 4) & 0xFF;
                int o = (Gauss[255 - f] * x.Hist[0]) >> 11;
                o += (Gauss[511 - f] * x.Hist[1]) >> 11;
                o += (Gauss[256 + f] * x.Hist[2]) >> 11;
                o = (short)o;
                o += (Gauss[f] * x.Hist[3]) >> 11;
                s = Clamp16(o) & ~1;
            }
            StepEnvelope(i);
            int outv = (s * x.Env) >> 11 & ~1;
            x.Out = outv;
            prevOut = outv;
            if (x.Env > 0) used |= 1 << i;
            // 위치 나아가기
            x.Pos += pitch;
            while (x.Pos >= 0x1000) { x.Pos -= 0x1000; if (x.On) DecodeNext(i); }
            if (mute[i]) continue;
            int l = (outv * S8(Reg(i, 0))) >> 7, r = (outv * S8(Reg(i, 1))) >> 7;
            mainL = Clamp16(mainL + l); mainR = Clamp16(mainR + r);
            if ((eon & (1 << i)) != 0) { echoL = Clamp16(echoL + l); echoR = Clamp16(echoR + r); }
        }
        // 에코
        int esa = regs[0x6D] << 8;
        int edl = regs[0x7D] & 0x0F;
        int echoLen = edl == 0 ? 4 : edl * 0x800;
        int ea = (esa + echoPos) & 0xFFFF;
        int inL = (short)(ram[ea] | (ram[(ea + 1) & 0xFFFF] << 8));
        int inR = (short)(ram[(ea + 2) & 0xFFFF] | (ram[(ea + 3) & 0xFFFF] << 8));
        echoHistPos = (echoHistPos + 1) & 7;
        echoHist[0, echoHistPos] = inL >> 1;
        echoHist[1, echoHistPos] = inR >> 1;
        int firL = 0, firR = 0;
        for (int k = 0; k < 8; k++)
        {
            int c = S8(regs[(k << 4) | 0x0F]);
            int h = (echoHistPos + 1 + k) & 7;
            firL += (echoHist[0, h] * c) >> 6;
            firR += (echoHist[1, h] * c) >> 6;
        }
        firL = Clamp16(firL) & ~1; firR = Clamp16(firR) & ~1;
        outL = Clamp16(((mainL * S8(regs[0x0C])) >> 7) + ((firL * S8(regs[0x2C])) >> 7));
        outR = Clamp16(((mainR * S8(regs[0x1C])) >> 7) + ((firR * S8(regs[0x3C])) >> 7));
        if ((flg & 0x20) == 0)
        {
            int efb = S8(regs[0x0D]);
            int wl = Clamp16(echoL + ((firL * efb) >> 7)) & ~1;
            int wr = Clamp16(echoR + ((firR * efb) >> 7)) & ~1;
            ram[ea] = (byte)wl; ram[(ea + 1) & 0xFFFF] = (byte)(wl >> 8);
            ram[(ea + 2) & 0xFFFF] = (byte)wr; ram[(ea + 3) & 0xFFFF] = (byte)(wr >> 8);
        }
        echoPos += 4;
        if (echoPos >= echoLen) echoPos = 0;
        if ((flg & 0x40) != 0) { outL = 0; outR = 0; }   // 음소거
    }

    // ------------------------------------------------------------ ISoundChip

    public string Name { get { return "SPC700 + S-DSP (SNES)"; } }
    public int NativeRate { get { return Rate; } }
    public int ChannelCount { get { return 8; } }
    public string ChannelLabel(int ch) { return "V" + (ch + 1); }
    public void Write(int port, int reg, int value) { }

    public void Generate(out int left, out int right)
    {
        if (dspOnly) cpu.Cycles = nextSample;
        while (cpu.Cycles < nextSample) cpu.Step();
        CatchUpTimers();
        nextSample += CyclesPerSample;
        DspSample(out int l, out int r);
        // SPC는 16비트를 꽉 채워 낸다: 다른 칩과 비슷한 크기로
        left = l >> 1; right = r >> 1;
    }

    public void Skip(double seconds)
    {
        long n = (long)(seconds * Rate);
        for (long i = 0; i < n; i++) Generate(out _, out _);
    }

    public int VoicesUsed { get { return used; } }

    public void SetMute(int ch, bool on) { if (ch >= 0 && ch < 8) mute[ch] = on; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 8) pitchMul[ch] = ratio > 0 ? ratio : 1.0; }
    /// <summary>음 높이는 샘플마다 달라 (샘플이 어느 음인지 모름) 0. 소리 남은 <see cref="ChannelActive"/>로.</summary>
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < 8 ? v[ch].Env / 2047f : 0; }
    public bool ChannelActive(int ch) { return ch >= 0 && ch < 8 && v[ch].Env > 0 && (v[ch].On || v[ch].Mode == 3); }

    /// <summary>시험 · 진단용: CPU 알 수 없는 명령 수 · 멈춤.</summary>
    public int UnknownOps { get { return cpu.UnknownOps; } }
    public bool CpuStopped { get { return cpu.Stopped; } }
}
