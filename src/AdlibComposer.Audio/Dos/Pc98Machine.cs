using System;
using System.Collections.Generic;
using AdlibComposer.Audio.Cpu;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// PC-9801 + MS-DOS (hoot pc98dos처럼): <see cref="DosMachine"/>에 PC-98 입출력 · 인터럽트를 더한다.
/// <para>PIC 8259 둘 (주 00h/02h · 벡터 08h~, 종 08h/0Ah · 벡터 10h~, 종 = 주 IRQ7), PIT 8253 (71h~77h · 3FD9h~3FDFh, 2.4576MHz: 0번 → IRQ0, 1번 = BEEP 높이),
/// VSYNC 56.42Hz → IRQ2 (64h에 쓰면 다음 한 번 받음), GDC 상태 60h · A0h bit5 = 수직 귀선, BEEP 켜기 37h (6 켬 · 7 끔),
/// 사운드 보드: OPN (26K, YM2203 3.9936MHz) · OPNA (86, YM2608 7.9872MHz) 188h/18Ah (+ 18Ch/18Eh), 타이머 A · B → INT5 (IRQ12, 벡터 14h — SoundIrq, SSG 포트 A로 읽히는 점퍼), 86 ID A460h.</para>
/// <para>hoot 전용: 7E0h 인터럽트 요인 (0 재생 · 2 정지), 7E2h~7E5h 곡 코드, 7E8h 0x80 · 0x81 hoot 인터럽트 끔 · 켬. hoot 인터럽트 = funcvect 벡터를 직접 (PIC 없이,
/// IF가 켜져 있을 때). 재생 = 곡 코드 번호의 핸들을 표준 입력에 이중화하고 요인 0 (<see cref="Play"/>).</para>
/// <para>칩 쓰기는 시간 (CPU 클럭)과 함께 <see cref="Log"/>에 모은다 (VGM으로 바꾸기는 바깥에서).</para>
/// </summary>
public sealed class Pc98Machine : DosMachine, IHootMachine
{
    // ------------------------------------------------------------ IHootMachine
    List<(long t, int kind, int reg, int val)> IHootMachine.ChipLog { get { return Log; } }
    double IHootMachine.ClockHz { get { return CpuHz; } }
    int IHootMachine.HootVector { get { return FuncVector; } set { FuncVector = value; } }
    bool IHootMachine.KeepRunning { get { return AutoIdle; } set { AutoIdle = value; } }
    int IHootMachine.HootCount { get { return HootDelivered; } }

    /// <summary>사운드 보드: 없음 · 26K (OPN) · 86 (OPNA) · PC-98용 Sound Blaster 16 (OPL3 20D2h · 21D2h · 22D2h · 23D2h, OPN 없음).</summary>
    /// SB16 포트 = 20D2h + (SB 오프셋 × 100h): OPL3 0~3, 믹서 4 · 5, DSP 리셋 6 · 읽기 A · 쓰기 C · 상태 E, 그리고 81D2h (bit6 = 0 → 있음). PCM DMA는 없음.
    public enum Board { None, Opn, Opna, Sb16 }

    // ------------------------------------------------------------ OPL3 (PC-98 Sound Blaster 16)

    private int oplAddr0, oplAddr1, oplT1, oplT2, oplCtl, oplFlags, sbMixerAddr;
    private bool sbReset;
    private readonly int[] sbMixer = new int[256];
    private readonly Queue<int> sbDspOut = new Queue<int>();
    private long oplStart1 = long.MaxValue, oplStart2 = long.MaxValue;

    private void OplWrite(int port, int reg, int v)
    {
        if (port == 0)
        {
            switch (reg)
            {
                case 0x02: oplT1 = v; return;
                case 0x03: oplT2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { oplFlags = 0; return; }   // IRQ 깃발 지움
                    oplCtl = v;
                    oplStart1 = (v & 1) != 0 ? Cpu.Cycles : long.MaxValue;
                    oplStart2 = (v & 2) != 0 ? Cpu.Cycles : long.MaxValue;
                    return;
            }
        }
        Log.Add((Cpu.Cycles, port, reg & 0xFF, v));
    }

    /// <summary>OPL3 상태: 타이머가 넘쳤으면 bit6 (T1) · bit5 (T2) + bit7 (드라이버의 OPL 찾기용, IRQ는 내지 않음).</summary>
    private int OplStatus()
    {
        long now = Cpu.Cycles;
        if (oplStart1 != long.MaxValue && now - oplStart1 >= (long)(80e-6 * (256 - oplT1) * CpuHz) && (oplCtl & 0x40) == 0) oplFlags |= 0x40;
        if (oplStart2 != long.MaxValue && now - oplStart2 >= (long)(320e-6 * (256 - oplT2) * CpuHz) && (oplCtl & 0x20) == 0) oplFlags |= 0x20;
        return oplFlags != 0 ? oplFlags | 0x80 : 0;
    }

    public const double PitClock = 2457600, VsyncHz = 56.4229, OpnBaseClock = 3993600;
    public readonly Board Sound;
    /// <summary>CPU 클럭 (Hz) = 2.4576MHz × clockmul.</summary>
    public readonly double CpuHz;
    /// <summary>hoot 인터럽트 벡터 (xml funcvect, 기본 0x7F).</summary>
    public int FuncVector = 0x7F;
    /// <summary>
    /// 사운드 보드 인터럽트 줄 (IRQ): 기본 INT5 = IRQ12 (벡터 14h) — hoot(NP2)의 기본 점퍼 (snd26opt 0xD1). INT5로 고정해 둔 드라이버가 많다.
    /// 점퍼는 SSG 포트 A (레지스터 0x0E) bit6~7로 읽힌다: 00 INT0 (IRQ3) · 01 INT6 (IRQ13) · 10 INT41 (IRQ10) · 11 INT5 (IRQ12).
    /// </summary>
    public int SoundIrq = 12;

    private int JumperBits { get { return SoundIrq == 13 ? 0x40 : SoundIrq == 10 ? 0x80 : SoundIrq == 12 ? 0xC0 : 0x00; } }

    /// <summary>MPU-PC98 (E0D0h 데이터 · E0D2h 상태 / 명령): 나간 MIDI 바이트는 기록 종류 4.</summary>
    public readonly Mpu401 Mpu = new Mpu401();
    /// <summary>MPU-PC98 보드가 꽂혀 있는가 (false = E0D0h · E0D2h가 빈 버스 FFh: MIDI 판이 아닌 PC-98generic 항목 — 드라이버가 MPU를 찾으면 MIDI로만 낸다).</summary>
    public bool MpuPresent = true;
    /// <summary>MPU-PC98 인터럽트: INT2 = IRQ6 (벡터 0Eh, 보드 기본).</summary>
    public int MpuIrq = 6;

    /// <summary>칩 쓰기: 시간 (CPU 클럭), 종류 (0 OPN/OPNA 포트 0, 1 OPNA 포트 1, 2 BEEP 켜기, 3 BEEP 카운터, 4 MIDI 바이트), 레지스터, 값.</summary>
    public readonly List<(long t, int kind, int reg, int val)> Log = new List<(long, int, int, int)>();
    public int UnknownPortReads, UnknownPortWrites;
    public readonly HashSet<int> UnknownPorts = new HashSet<int>();

    public Pc98Machine(DosFileSystem fs, Board sound, int clockMul = 8, int memoryTop = 0xA000) : base(fs, memoryTop)
    {
        Sound = sound;
        CpuHz = PitClock * Math.Max(1, clockMul);
        vsyncPeriod = (long)(CpuHz / VsyncHz);
        Mpu.MidiOut = b => Log.Add((Cpu.Cycles, 4, 0, b));
        nextVsync = vsyncPeriod;
        // BIOS: 시스템 공통 영역 (0:0500~) 일부 — 86 / 26K가 있다고 알리는 값은 없음 (드라이버가 포트로 찾는다)
    }

    // ------------------------------------------------------------ PIC 8259

    // BIOS가 남기는 마스크: 주 = 키보드 · 종(IRQ7)만 열림, 종 = 모두 닫힘 (드라이버가 자기 IRQ를 연다)
    private readonly Pic8259 master = new Pic8259 { Base = 0x08, Imr = 0x7D }, slave = new Pic8259 { Base = 0x10, Imr = 0xFF };

    private void RaiseIrq(int irq)
    {
        if (irq < 8) master.Irr |= 1 << irq;
        else slave.Irr |= 1 << (irq - 8);   // 주 IRQ7 (종 연결)은 DeliverIrq가 종의 열린 요청으로 그때그때 정한다
    }

    private void LowerIrq(int irq)
    {
        if (irq < 8) master.Irr &= ~(1 << irq);
        else slave.Irr &= ~(1 << (irq - 8));
    }

    /// <summary>CPU에 넣을 수 있는 인터럽트가 있으면 넣는다.</summary>
    private bool DeliverIrq()
    {
        if (!Cpu.GetFlag(X86.IF)) return false;
        // 종 INT 출력 = 열린 요청이 있는가: 종이 막은 동안 온 요청도 나중에 열면 살아난다 (예전엔 주 IRQ7을 지워 영영 잃었다)
        if (slave.Pending() >= 0) master.Irr |= 0x80; else master.Irr &= ~0x80;
        int m = master.Pending();
        if (m < 0) return false;
        int vector;
        if (m == 7)
        {
            int s = slave.Pending();
            if (s < 0) return false;
            vector = slave.Base + s;
            if (!Cpu.HardwareInterrupt(vector)) return false;
            if (Trace) { Count(TraceVectors, vector); TraceIoAdd("IRQ>" + vector.ToString("X2")); }
            slave.Irr &= ~(1 << s); if (!slave.AutoEoi) slave.Isr |= 1 << s;
            master.Irr &= ~0x80; if (!master.AutoEoi) master.Isr |= 0x80;
            Interrupts++;
            return true;
        }
        vector = master.Base + m;
        if (!Cpu.HardwareInterrupt(vector)) return false;
        if (Trace) { Count(TraceVectors, vector); TraceIoAdd("IRQ>" + vector.ToString("X2")); }
        master.Irr &= ~(1 << m); if (!master.AutoEoi) master.Isr |= 1 << m;
        Interrupts++;
        return true;
    }

    public long Interrupts { get; private set; }
    /// <summary>재생 중 (셸을 다 돌린 뒤): 프로그램이 끝나 셸로 돌아와도 멈추지 않고 쉬는 고리에서 인터럽트를 계속 받는다.</summary>
    public bool AutoIdle;
    /// <summary>넣은 hoot 인터럽트 수 (진단).</summary>
    public int HootDelivered { get; private set; }
    /// <summary>hoot 인터럽트를 기다리는 중 (요청은 있었지만 꺼져 있거나 IF = 0).</summary>
    public bool HootPending { get { return hootPending; } }

    // ------------------------------------------------------------ PIT 8253

    private sealed class Counter
    {
        public int Reload = 0x10000, Mode = 3, Rw = 3;
        public bool LowNext = true, Latched;
        public int LatchValue, WriteLow;
        public long Start;     // 다시 넣은 때 (CPU 클럭)
        public bool Running;
    }

    private readonly Counter[] pit = { new Counter(), new Counter(), new Counter() };
    private long nextTimer0 = long.MaxValue;

    private long PitTicksToCycles(long ticks) { return (long)(ticks * CpuHz / PitClock); }

    private int PitCount(Counter c)
    {
        if (!c.Running) return c.Reload & 0xFFFF;
        long ticks = (long)((Cpu.Cycles - c.Start) * PitClock / CpuHz);
        int r = c.Reload - (int)(ticks % c.Reload);
        return r & 0xFFFF;
    }

    private void PitWrite(int ch, int v)
    {
        var c = pit[ch];
        bool done;
        if (c.Rw == 1) { c.Reload = v; done = true; }
        else if (c.Rw == 2) { c.Reload = v << 8; done = true; }
        else if (c.LowNext) { c.WriteLow = v; c.LowNext = false; done = false; }
        else { c.Reload = c.WriteLow | (v << 8); c.LowNext = true; done = true; }
        if (!done) return;
        if (c.Reload == 0) c.Reload = 0x10000;
        c.Start = Cpu.Cycles; c.Running = true;
        if (ch == 0) nextTimer0 = Cpu.Cycles + Math.Max(1, PitTicksToCycles(c.Reload));
        if (ch == 1) Log.Add((Cpu.Cycles, 3, 0, c.Reload));
    }

    private int PitRead(int ch)
    {
        var c = pit[ch];
        int v = c.Latched ? c.LatchValue : PitCount(c);
        if (c.Rw == 1) { c.Latched = false; return v & 0xFF; }
        if (c.Rw == 2) { c.Latched = false; return v >> 8; }
        if (c.LowNext) { c.LowNext = false; return v & 0xFF; }
        c.LowNext = true; c.Latched = false;
        return v >> 8;
    }

    private void PitControl(int v)
    {
        int ch = v >> 6;
        if (ch == 3) return;
        var c = pit[ch];
        int rw = (v >> 4) & 3;
        if (rw == 0) { c.LatchValue = PitCount(c); c.Latched = true; return; }
        c.Rw = rw; c.Mode = (v >> 1) & 7; c.LowNext = true; c.Running = false;
        if (ch == 0) nextTimer0 = long.MaxValue;
    }

    // ------------------------------------------------------------ OPN / OPNA

    private int opnAddr0, opnAddr1;
    private readonly byte[] opnRegs = new byte[0x200];
    private int timerA, timerB, timerCtl, timerFlags;
    private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
    private int a460 = 0x40, a468, a46a;

    private long TimerAPeriod { get { return Math.Max(1, (long)(72.0 * (1024 - timerA) / OpnBaseClock * CpuHz)); } }
    private long TimerBPeriod { get { return Math.Max(1, (long)(1152.0 * (256 - timerB) / OpnBaseClock * CpuHz)); } }

    private void OpnWrite(int port, int reg, int v)
    {
        opnRegs[port * 0x100 + reg] = (byte)v;
        if (port == 0)
        {
            switch (reg)
            {
                case 0x24: timerA = (timerA & 3) | (v << 2); return;
                case 0x25: timerA = (timerA & 0x3FC) | (v & 3); return;
                case 0x26: timerB = v; return;
                case 0x27:
                    {
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 0x10) != 0) timerFlags &= ~1;
                        if ((v & 0x20) != 0) timerFlags &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                        if (timerFlags == 0) LowerIrq(SoundIrq);
                        Log.Add((Cpu.Cycles, 0, reg, v & 0xC0));   // CSM · 채널 3 모드만 칩에
                        return;
                    }
            }
        }
        Log.Add((Cpu.Cycles, port, reg, v));
    }

    private int OpnStatus(bool ext)
    {
        int s = timerFlags & 3;
        if (ext) s |= 0x08;   // ADPCM BRDY = 늘 준비됨 (PMDB2 등이 ADPCM RAM을 쓰고 읽으며 기다린다), EOS · 리듬 상태 없음
        return s;
    }

    private int OpnReadData(int port, int reg)
    {
        if (port == 0 && reg == 0xFF) return Sound == Board.Opna ? 0x01 : 0x00;   // YM2608 ID
        if (port == 0 && reg == 0x0E) return (opnRegs[reg] & 0x3F) | JumperBits;   // SSG 포트 A = 보드 INT 점퍼
        if (port == 0 && reg < 0x10) return opnRegs[reg];
        return 0;   // fmgen (hoot) GetReg: FM 레지스터는 읽을 수 없음 = 0
    }

    private void TimerOverflow(int which)
    {
        // hoot ssFMTimer · ssSoundDriverManager: 플래그는 LOAD만으로 선다 (ENABLE 없이 188h를 읽어 기다리는 보드 찾기),
        // IRQ는 ENABLE일 때 넘칠 때마다 한 번씩 (플래그를 안 지우는 드라이버도 계속 틱을 받는다)
        timerFlags |= which == 0 ? 1 : 2;
        if ((timerCtl & (which == 0 ? 4 : 8)) != 0 && !(va && vaSoundMasked)) RaiseIrq(SoundIrq);
    }

    // ------------------------------------------------------------ PC-88VA (hoot pc88va): 포트만 다른 PC-98 기계

    /// <summary>
    /// PC-88VA 포트 배치 (MAME pc88va · 사운드 BIOS로 확인): OPN · OPNA 44h~47h, 8259 주 188h · 18Ah · 종 184h · 186h, 8253 1A0h~1A6h,
    /// 32h bit7 = 사운드 인터럽트 막음 (SINTM). 나머지 (hoot 7E0h~ · 7D0h~)는 PC-98 기계 그대로. 사운드 IRQ = 종 IRQ4 (IRQ12, PC-98과 같음).
    /// </summary>
    public bool Va { get { return va; } set { va = value; if (value) slave.Imr &= ~0x10; } }   // VA BIOS가 사운드 (종 IRQ4)를 열어 둔 채로 넘긴다 (R-Type은 스스로 열지 않음)
    private bool va, vaSoundMasked;
    private int va32;

    private static int VaPort(int port)
    {
        switch (port)
        {
            case 0x44: return 0x188; case 0x45: return 0x18A; case 0x46: return 0x18C; case 0x47: return 0x18E;
            case 0xA8: return 0x188; case 0xA9: return 0x18A; case 0xAC: return 0x18C; case 0xAD: return 0x18E;   // hoot PC-88 드라이버: 사운드 보드 II
            case 0x188: return 0x00; case 0x18A: return 0x02; case 0x184: return 0x08; case 0x186: return 0x0A;
            case 0x1A0: return 0x71; case 0x1A2: return 0x73; case 0x1A4: return 0x75; case 0x1A6: return 0x77;
        }
        return port;
    }

    // ------------------------------------------------------------ VSYNC · BEEP · hoot

    private readonly long vsyncPeriod;
    private long nextVsync;
    private bool vsyncArmed;
    private int hootCause = 2, hootCode;
    // hoot 인터럽트는 처음부터 켜져 있다 (MMP_HOOT 같은 셸은 7E8h를 쓰지 않는다, PMD_98은 준비하는 동안만 80h로 끔)
    private bool hootEnabled = true, hootPending;
    public bool HootInterruptEnabled { get { return hootEnabled; } }
    /// <summary>프로그램이 7E8h에 81h (hoot 인터럽트 켬)를 쓴 적이 있다 (PC-98generic 부팅이 끝난 표시).</summary>
    public bool HootArmed { get; private set; }

    private bool InVsync { get { return Cpu.Cycles % vsyncPeriod < vsyncPeriod / 16; } }
    private bool InHblank { get { long h = Math.Max(2, (long)(CpuHz / 24830)); return Cpu.Cycles % h < h / 5; } }

    /// <summary>
    /// 곡 재생 요청 (hoot). pc98dos: 곡 코드 번호의 핸들을 표준 입력에 이중화하고 요인 0으로 hoot 인터럽트.
    /// pc98vx · pc98 (<see cref="Generic"/>): 요인 0으로 hoot 인터럽트 → 처리기가 돌아오면 코드 아래 16비트 번호의 bgm / bgm2를 dataaddr에 넣고 → 넣었으면 요인 1로 한 번 더.
    /// </summary>
    public void Play(int code)
    {
        if (Generic == null) SelectSongHandle(code & 0xFFFF);
        hootCode = code; hootCause = 0; hootPending = true;
        genericLoadAfter = Generic != null;
    }

    public void Stop() { hootCause = 2; hootPending = true; genericLoadAfter = false; }

    // ------------------------------------------------------------ hoot PC-98generic (pc98vx · pc98: DOS 없이)

    /// <summary>
    /// hoot PC-98generic 항목의 곡 자료 (pc98vx · pc98): bgm / bgm2 (번호 → 파일 내용), strlist (번호 → 글자), 넣을 주소 · 크기 (0 = 넣지 않음).
    /// 있으면 hoot 인터럽트를 F000의 함정을 거쳐 넣어 처리기가 돌아온 때를 알고, I/O 7D0h~7D7h (기능 · 인자)를 받는다.
    /// </summary>
    public sealed class GenericData
    {
        public Func<int, byte[]> Bgm = _ => null, Bgm2 = _ => null;
        public Func<int, byte[]> Str = _ => null;
        /// <summary>넣을 곳 (선형 주소, 0 = 넣지 않음): [0] 주 (dataaddr) · [1] 둘째 (data2addr).</summary>
        public readonly int[] Addr = new int[2];
        /// <summary>넣을 크기 한도 (filesize · file2size, 0 = 파일 전부).</summary>
        public readonly int[] Size = new int[2];
        /// <summary>요인 1로 넣은 횟수 (진단 · 시험).</summary>
        public int Loads;
    }

    /// <summary>PC-98generic 항목 (null = pc98dos).</summary>
    public GenericData Generic;
    private bool genericLoadAfter, inHootHandler;
    private long hootHandlerStart;
    private int genericStatus = 0xFF;
    private readonly byte[] genericParams = new byte[6];   // 7D2h~7D7h
    private const int HootReturnOff = 0x0420, HootReturnTrap = 0x0201;

    /// <summary>pc98 (옛 방식): PIC 자동 EOI (드라이버가 EOI를 보내지 않아도 다음 인터럽트를 받는다).</summary>
    public void SetAutoEoi(bool on) { master.AutoEoi = on; slave.AutoEoi = on; }

    /// <summary>bootcs:bootip에서 시작 (DOS 없이: 파일은 이미 메모리에).</summary>
    public void StartAt(int cs, int ip)
    {
        Poke(StubSeg, HootReturnOff, 0xF1); Poke16(StubSeg, HootReturnOff + 1, HootReturnTrap); Poke(StubSeg, HootReturnOff + 3, 0xCF);
        Cpu.CS = cs & 0xFFFF; Cpu.IP = ip & 0xFFFF; Cpu.Halted = false;
    }

    private int GenericParam(int i) { return genericParams[i * 2] | (genericParams[i * 2 + 1] << 8); }
    private void SetGenericParam(int i, int v) { genericParams[i * 2] = (byte)v; genericParams[i * 2 + 1] = (byte)(v >> 8); }

    private void CopyIn(int linear, byte[] d, int from, int count)
    {
        for (int i = 0; i < count && from + i < d.Length; i++)
        {
            int a = (linear + i) & 0xFFFFF;
            if (a >= 0xF0000) break;   // 함정 · BIOS 자리는 지키기
            Mem[a] = d[from + i];
        }
    }

    /// <summary>hoot 인터럽트 처리기가 돌아옴 (F000:0420 함정): 재생 요청이면 bgm / bgm2를 넣고 요인 1로.</summary>
    protected override void MachineTrap(int id)
    {
        if (id != HootReturnTrap) return;
        inHootHandler = false;
        if (!genericLoadAfter || Generic == null) return;
        genericLoadAfter = false;
        int n = hootCode & 0xFFFF;
        bool loaded = false;
        for (int k = 0; k < 2; k++)
        {
            int at = Generic.Addr[k];
            if (at == 0) continue;
            var d = k == 0 ? Generic.Bgm(n) : Generic.Bgm2(n);
            if (d == null) continue;
            CopyIn(at, d, 0, Generic.Size[k] > 0 ? Math.Min(d.Length, Generic.Size[k]) : d.Length);
            loaded = true;
        }
        if (loaded) { hootCause = 1; hootPending = true; Generic.Loads++; }
    }

    private void DeliverHoot()
    {
        hootPending = false;
        Cpu.Vector(FuncVector);
        Interrupts++; HootDelivered++;
        if (Generic == null) return;
        // 처리기의 IRET이 F000:0420 함정으로 오게: 원래 틀 위에 하나 더 (함정 뒤 IRET이 원래 자리로)
        int flags = PeekWord(Cpu.SS, Cpu.SP + 4) & ~X86.IF;
        Cpu.Push(flags); Cpu.Push(StubSeg); Cpu.Push(HootReturnOff);
        inHootHandler = true; hootHandlerStart = Cpu.Cycles;
    }

    /// <summary>7D0h 기능 (PC98.TXT "PC-98generic"): 10h · 11h dataaddr 설정 · 얻기, 18h strlist, 20h · 21h bgm 넣기, 30h · 31h 넣기 (인자 블록).</summary>
    private void GenericFunction(int f)
    {
        var g = Generic;
        int p1 = GenericParam(0), p2 = GenericParam(1), p3 = GenericParam(2);
        int at = X86.Linear(p3, p2);
        genericStatus = 0xFF;
        switch (f)
        {
            case 0x10:
                if ((p1 & 0xFF) > 1) return;
                g.Addr[p1 & 1] = at; genericStatus = 0; return;
            case 0x11:
                if ((p1 & 0xFF) > 1) return;
                SetGenericParam(1, g.Addr[p1 & 1] & 0xF); SetGenericParam(2, g.Addr[p1 & 1] >> 4); genericStatus = 0; return;
            case 0x18:
                {
                    var s = g.Str(p1);
                    if (s == null) return;
                    CopyIn(at, s, 0, s.Length); CopyIn(at + s.Length, new byte[1], 0, 1);
                    genericStatus = 0; return;
                }
            case 0x20: case 0x21:
                {
                    var d = f == 0x20 ? g.Bgm(p1) : g.Bgm2(p1);
                    if (d == null) return;
                    CopyIn(at, d, 0, d.Length); genericStatus = 0; return;
                }
            case 0x30: case 0x31:
                {
                    var d = f == 0x30 ? g.Bgm(p1) : g.Bgm2(p1);
                    if (d == null) return;
                    int off = PeekWord(p3, p2), seg = PeekWord(p3, p2 + 2);
                    long pos = (uint)(PeekWord(p3, p2 + 4) | (PeekWord(p3, p2 + 6) << 16)), size = (uint)(PeekWord(p3, p2 + 8) | (PeekWord(p3, p2 + 10) << 16));
                    if (pos > d.Length) return;
                    CopyIn(X86.Linear(seg, off), d, (int)pos, (int)Math.Min(size, d.Length - pos));
                    genericStatus = 0; return;
                }
        }
    }

    // ------------------------------------------------------------ 포트

    // ------------------------------------------------------------ 진단 (Trace = true일 때만 센다)

    /// <summary>진단: 넣은 하드웨어 인터럽트 벡터별 수 · 포트 읽기 / 쓰기 수 (쓰기는 포트 | 0x10000).</summary>
    public bool Trace;
    /// <summary>진단: 명령마다 부름 (아주 느림, 시험 도구만).</summary>
    public static Action<Pc98Machine> DebugStep;
    public readonly Dictionary<int, int> TraceVectors = new Dictionary<int, int>(), TracePorts = new Dictionary<int, int>();
    private static void Count(Dictionary<int, int> d, int k) { d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1; }

    /// <summary>진단 한 줄: PIC 마스크 · ISR, PIT 0, OPN 타이머, CPU 위치.</summary>
    public string Describe()
    {
        return "PIC m IMR " + master.Imr.ToString("X2") + " ISR " + master.Isr.ToString("X2") + " IRR " + master.Irr.ToString("X2") + ", s IMR " + slave.Imr.ToString("X2") + " ISR " + slave.Isr.ToString("X2") +
            ", PIT0 " + (pit[0].Running ? pit[0].Reload.ToString() + " m" + pit[0].Mode : "off") + ", OPN ctl " + timerCtl.ToString("X2") + " A " + timerA + " B " + timerB + " flags " + timerFlags +
            ", IF " + (Cpu.GetFlag(X86.IF) ? 1 : 0) + ", at " + Cpu.CS.ToString("X4") + ":" + Cpu.IP.ToString("X4") + (Cpu.Halted ? " HLT" : "");
    }

    /// <summary>진단: MPU · PIC 포트 입출력 순서 (Trace일 때 600개까지).</summary>
    public readonly List<string> TraceIo = new List<string>();
    private void TraceIoAdd(string s) { if (Trace && TraceIo.Count < 600) TraceIo.Add(s); }

    public override byte In(int port)
    {
        if (va)
        {
            if (port == 0x32 || port == 0xAA) return (byte)va32;
            if (port == 0x18C || port == 0x18E || port == 0x00 || port == 0x02 || port == 0x08 || port == 0x0A || (port >= 0x71 && port <= 0x77)) return 0xFF;   // PC-98 자리는 VA에 없음
            port = VaPort(port);
        }
        if (Trace) Count(TracePorts, port);
        if (Trace && MpuPresent && (port == 0xE0D0 || port == 0xE0D2))
        {
            byte r = port == 0xE0D0 ? (byte)Mpu.ReadData() : (byte)Mpu.ReadStatus();
            TraceIoAdd("r" + port.ToString("X") + "=" + r.ToString("X2") + (Cpu.GetFlag(X86.IF) ? "" : "!IF") + "@" + Cpu.CS.ToString("X4") + ":" + Cpu.IP.ToString("X4"));
            return r;
        }
        if (Trace && (port == 0x00 || port == 0x02 || port == 0x08 || port == 0x0A || port == 0x60 || port == 0xA0 || port == 0x71) && TraceIo.Count >= 0 && TraceIo.Count < 600)
        {
            string last = TraceIo.Count > 0 ? TraceIo[TraceIo.Count - 1] : "";
            string tag = "r" + port.ToString("X");
            if (last.StartsWith(tag + "x")) { int n = int.Parse(last.Substring(tag.Length + 1)); TraceIo[TraceIo.Count - 1] = tag + "x" + (n + 1); }
            else TraceIo.Add(tag + "x1");
        }
        switch (port)
        {
            case 0x00: return (byte)master.ReadCmd();
            case 0x02: return (byte)master.Imr;
            case 0x08: return (byte)slave.ReadCmd();
            case 0x0A: return (byte)slave.Imr;
            case 0x71: case 0x3FD9: return (byte)PitRead(0);
            case 0x73: case 0x3FDB: return (byte)PitRead(1);
            case 0x75: case 0x3FDD: return (byte)PitRead(2);
            case 0x60: case 0xA0: return (byte)(0x04 | (InVsync ? 0x20 : 0) | (InHblank ? 0x40 : 0));   // GDC 상태: FIFO 빔, 수직 귀선, 수평 귀선 (24.83kHz)
            case 0x31: case 0x33: case 0x35: return 0x00;
            case 0xE0D0: return MpuPresent ? (byte)Mpu.ReadData() : (byte)0xFF;
            case 0xE0D2: return MpuPresent ? (byte)Mpu.ReadStatus() : (byte)0xFF;
            case 0x42: return 0xDF;   // 프린터 8255 포트 B: bit5 = 0 → 5 · 10MHz 계열 (PIT 2.4576MHz)
            case 0x7FD9: return 0;    // 마우스: 움직임 없음
            case 0x7FDB: return 0xFF; // 버튼 안 눌림
            case 0x7FDD: return (byte)mouseC;
            case 0x7E0: return (byte)hootCause;
            case 0x7E1: return 0;
            case 0x7E2: return (byte)hootCode; case 0x7E3: return (byte)(hootCode >> 8);
            case 0x7E4: return (byte)(hootCode >> 16); case 0x7E5: return (byte)(hootCode >> 24);
        }
        if (Generic != null && port >= 0x7D0 && port <= 0x7D7)
        {
            if (port == 0x7D0) return (byte)genericStatus;
            if (port >= 0x7D2) return genericParams[port - 0x7D2];
            return 0;
        }
        if (Sound == Board.Sb16)
        {
            switch (port)
            {
                case 0x20D2: case 0x22D2: return (byte)OplStatus();
                case 0x21D2: case 0x23D2: return 0xFF;
                case 0x25D2: return (byte)sbMixer[sbMixerAddr];
                case 0x2AD2: return (byte)(sbDspOut.Count > 0 ? sbDspOut.Dequeue() : 0xFF);   // DSP 읽기
                case 0x2CD2: return 0x00;                                                         // DSP 쓰기 준비됨
                case 0x2ED2: case 0x2FD2: return (byte)(sbDspOut.Count > 0 ? 0x80 : 0x00);         // DSP 읽을 것 있음
                case 0x81D2: return 0x00;                                                         // bit6 = 0: 카드 있음 (드라이버의 찾기)
            }
        }
        if (Sound == Board.Opn || Sound == Board.Opna)
        {
            switch (port)
            {
                case 0x188: return (byte)OpnStatus(false);
                case 0x18A: return (byte)OpnReadData(0, opnAddr0);
                case 0x18C: if (Sound == Board.Opna) return (byte)OpnStatus(true); break;
                case 0x18E: if (Sound == Board.Opna) return (byte)OpnReadData(1, opnAddr1); break;
                case 0xA460: if (Sound == Board.Opna) return (byte)a460; break;
                // 86 PCM (소리는 아직 없음): A466h bit0 = 표본 클럭 깜빡임, A468h = FIFO 제어 (bit4 IRQ 깃발 = 늘 0), A46Ah 모드, A46Ch FIFO
                case 0xA466: if (Sound == Board.Opna) return (byte)((Cpu.Cycles / 64) & 1); break;
                case 0xA468: if (Sound == Board.Opna) return (byte)(a468 & ~0x10); break;
                case 0xA46A: if (Sound == Board.Opna) return (byte)a46a; break;
                case 0xA46C: if (Sound == Board.Opna) return 0; break;
            }
        }
        UnknownPortReads++; UnknownPorts.Add(port);
        return 0xFF;
    }

    public override void Out(int port, byte value)
    {
        if (va)
        {
            if (port == 0x32 || port == 0xAA) { va32 = value; vaSoundMasked = (value & 0x80) != 0; return; }
            if (port == 0x197 || port == 0x18C || port == 0x18E || port == 0x00 || port == 0x02 || port == 0x08 || port == 0x0A || (port >= 0x71 && port <= 0x77)) return;
            port = VaPort(port);
        }
        if (Trace) Count(TracePorts, port | 0x10000);
        if (Trace && (port == 0xE0D0 || port == 0xE0D2 || port == 0x02 || port == 0x00 || port == 0x08 || port == 0x0A)) TraceIoAdd("w" + port.ToString("X") + "=" + value.ToString("X2") + (Cpu.GetFlag(X86.IF) ? "" : "!IF") + "@" + Cpu.CS.ToString("X4") + ":" + Cpu.IP.ToString("X4"));
        int v = value;
        switch (port)
        {
            case 0x00: master.WriteCmd(v); return;
            case 0x02: master.WriteData(v); return;
            case 0x08: slave.WriteCmd(v); return;
            case 0x0A: slave.WriteData(v); return;
            case 0x71: case 0x3FD9: PitWrite(0, v); return;
            case 0x73: case 0x3FDB: PitWrite(1, v); return;
            case 0x75: case 0x3FDD: PitWrite(2, v); return;
            case 0x77: case 0x3FDF: PitControl(v); return;
            case 0x37: if ((v & 0x0E) == 0x06) Log.Add((Cpu.Cycles, 2, 0, (v & 1) == 0 ? 1 : 0)); return;   // 8255 C bit3: 0 = BEEP 켬
            case 0x35: return;
            case 0x64: vsyncArmed = true; return;
            case 0x60: case 0x62: case 0xA0: case 0xA2: case 0x68: case 0x6A: case 0x7C: case 0x7E: return;   // GDC · 표시 (무시)
            case 0x5F: return;   // 기다림
            case 0xE0D0: if (MpuPresent) Mpu.WriteData(v); return;
            case 0xE0D2: if (MpuPresent) Mpu.WriteCommand(v); return;
            case 0x7FDD: mouseC = v; MouseTimerChanged(); return;
            case 0x7FDF:
                if ((v & 0x80) == 0) { int bit = (v >> 1) & 7; mouseC = (v & 1) != 0 ? mouseC | (1 << bit) : mouseC & ~(1 << bit); }
                else mouseC = 0x10;   // 방식 설정: 포트 C 처음으로 (인터럽트 끔)
                MouseTimerChanged();
                return;
            case 0xBFDB: mouseHz = MouseRates[v & 3]; MouseTimerChanged(); return;
            case 0x7E8:
                if (v == 0x80) hootEnabled = false;
                else if (v == 0x81) { hootEnabled = true; HootArmed = true; }
                return;
        }
        if (Generic != null && port >= 0x7D0 && port <= 0x7D7)
        {
            if (port == 0x7D0) GenericFunction(v);
            else if (port >= 0x7D2) genericParams[port - 0x7D2] = value;
            return;
        }
        if (Sound == Board.Sb16)
        {
            switch (port)
            {
                case 0x20D2: oplAddr0 = v; return;
                case 0x21D2: OplWrite(0, oplAddr0, v); return;
                case 0x22D2: oplAddr1 = v; return;
                case 0x23D2: OplWrite(1, oplAddr1 | 0x100, v); return;
                case 0x24D2: sbMixerAddr = v; return;
                case 0x25D2: sbMixer[sbMixerAddr] = v; return;
                case 0x26D2:   // DSP 리셋: 1 → 0이면 AAh
                    if ((v & 1) != 0) sbReset = true;
                    else if (sbReset) { sbReset = false; sbDspOut.Clear(); sbDspOut.Enqueue(0xAA); }
                    return;
                case 0x2CD2:   // DSP 명령: 판 (E1h → 4.05)만, 그 밖은 버림 (PCM 없음)
                    if (v == 0xE1) { sbDspOut.Enqueue(4); sbDspOut.Enqueue(5); }
                    return;
            }
        }
        if (Sound == Board.Opn || Sound == Board.Opna)
        {
            switch (port)
            {
                case 0x188: opnAddr0 = v; return;
                case 0x18A: OpnWrite(0, opnAddr0, v); return;
                case 0x18C: if (Sound == Board.Opna) { opnAddr1 = v; return; } break;
                case 0x18E: if (Sound == Board.Opna) { OpnWrite(1, opnAddr1, v); return; } break;
                case 0xA460: if (Sound == Board.Opna) { a460 = 0x40 | (v & 3); return; } break;
                case 0xA466: if (Sound == Board.Opna) return; break;
                case 0xA468: if (Sound == Board.Opna) { a468 = v; return; } break;
                case 0xA46A: if (Sound == Board.Opna) { a46a = v; return; } break;
                case 0xA46C: if (Sound == Board.Opna) return; break;
            }
        }
        if (port >= 0x4A0 && port <= 0x4AF) return;   // EGC (그래픽)
        if (port >= 0xA4 && port <= 0xAE && (port & 1) == 0) return;   // 팔레트 · 표시 페이지
        UnknownPortWrites++; UnknownPorts.Add(port | 0x10000);
    }

    // ------------------------------------------------------------ 마우스 인터페이스 타이머 (IRQ13)

    private static readonly int[] MouseRates = { 120, 60, 30, 15 };
    private int mouseC = 0x10, mouseHz = 120;
    private long nextMouse = long.MaxValue;
    private long MousePeriod { get { return Math.Max(1, (long)(CpuHz / mouseHz)); } }

    /// <summary>포트 C bit4 (INT#) = 0이면 마우스 타이머 인터럽트 (IRQ13, 벡터 15h)가 120 · 60 · 30 · 15Hz로 (BFDBh). usd_98 같은 셸이 템포에 쓴다.</summary>
    private void MouseTimerChanged()
    {
        bool on = (mouseC & 0x10) == 0;
        if (!on) { nextMouse = long.MaxValue; LowerIrq(13); }
        else if (nextMouse == long.MaxValue) nextMouse = Cpu.Cycles + MousePeriod;
    }

    private long NextEvent { get { return Math.Min(Math.Min(Math.Min(Math.Min(nextTimer0, nextVsync), Math.Min(nextTimerA, nextTimerB)), nextMouse), nextMpu); } }
    // ------------------------------------------------------------ MPU 클럭 · 인터럽트

    private long nextMpu = long.MaxValue;
    private bool mpuIrqUp;
    private long MpuPeriod { get { return Math.Max(1, (long)(Mpu.ClockSeconds * CpuHz)); } }


    // ------------------------------------------------------------ BIOS (기본 벡터 처리기)

    protected override bool HaltIsIdle { get { return true; } }

    /// <summary>NEC MS-DOS: 안 쓴 사용자 벡터 = 0060:xxxx ("ＦＭ音源ドライバー" MUSIC.COM은 INT 48h 세그먼트가 0060h가 아니고 INT 0Ah와 같으면 이미 상주한 것으로 본다).</summary>
    protected override int UserVectorSeg { get { return 0x0060; } }

    protected override void Interrupt(int n)
    {
        if (Trace) Count(TraceVectors, 0x10000 + (n << 8) + Cpu.AH);   // 진단: 기본 처리기로 온 BIOS 호출 (벡터 · AH)
        if (n >= 0x08 && n <= 0x0F) { master.WriteCmd(0x60 | (n - 0x08)); return; }          // 하드웨어 IRQ: 처리기가 없으면 EOI만
        if (n >= 0x10 && n <= 0x17) { slave.WriteCmd(0x60 | (n - 0x10)); master.WriteCmd(0x67); return; }
        switch (n)
        {
            case 0x18:   // 키보드 · 표시 BIOS: 누른 키 없음
                if (Cpu.AH == 0x00) Cpu.AX = 0;
                else if (Cpu.AH == 0x01) { Cpu.BH = 0; }
                else if (Cpu.AH == 0x02) Cpu.AL = 0;
                return;
            case 0x1A: case 0x1B: case 0x1C: case 0x1F: return;
        }
        base.Interrupt(n);
    }

    // ------------------------------------------------------------ 돌리기

    /// <summary>CPU · 타이머 · 인터럽트를 cycles만큼. HLT면 다음 사건까지 건너뛴다.</summary>
    public override void Run(long cycles)
    {
        long end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            if (HostReturned)
            {
                if (!AutoIdle) break;
                Idle();   // 재생 중에 셸 프로그램이 끝남: 상주한 드라이버는 계속 (인터럽트를 받는다)
            }
            long now = Cpu.Cycles;
            if (now >= nextTimer0) { RaiseIrq(0); var c = pit[0]; nextTimer0 = c.Mode == 0 ? long.MaxValue : nextTimer0 + Math.Max(1, PitTicksToCycles(c.Reload)); }
            if (now >= nextVsync) { if (vsyncArmed) { RaiseIrq(2); vsyncArmed = false; } nextVsync += vsyncPeriod; }
            if (now >= nextTimerA) { TimerOverflow(0); nextTimerA += TimerAPeriod; }
            if (now >= nextTimerB) { TimerOverflow(1); nextTimerB += TimerBPeriod; }
            if (now >= nextMouse) { RaiseIrq(13); nextMouse += MousePeriod; }
            if (Mpu.Running) { if (nextMpu == long.MaxValue) nextMpu = now + MpuPeriod; }
            else nextMpu = long.MaxValue;
            if (now >= nextMpu) { Mpu.Clock(); nextMpu += MpuPeriod; }
            if (Mpu.IrqPending != mpuIrqUp) { mpuIrqUp = Mpu.IrqPending; if (mpuIrqUp) RaiseIrq(MpuIrq); else LowerIrq(MpuIrq); }
            else if (mpuIrqUp) RaiseIrq(MpuIrq);
            if (inHootHandler && now - hootHandlerStart > (long)CpuHz) MachineTrap(HootReturnTrap);   // 1초 넘게 돌아오지 않는 처리기: 돌아온 것으로
            if (hootPending && hootEnabled && !inHootHandler && Cpu.GetFlag(X86.IF)) DeliverHoot();
            else DeliverIrq();
            if (Cpu.Halted)
            {
                // 다음 사건까지 쉰다
                long next = NextEvent;
                if (next == long.MaxValue || next > end) { Cpu.Cycles = Math.Max(Cpu.Cycles, end); break; }
                Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                continue;
            }
            // 사건 사이를 한 번에 (인터럽트 검사를 줄인다)
            long until = Math.Min(end, NextEvent);
            int guard = 0;
            do { DebugStep?.Invoke(this); Cpu.Step(); } while (Cpu.Cycles < until && !Cpu.Halted && !HostReturned && Mpu.IrqPending == mpuIrqUp && ++guard < 64);   // MPU가 읽을 데이터를 내면 바로 인터럽트 (드라이버가 ACK를 인터럽트로 기다림)
        }
    }

    public double Seconds { get { return Cpu.Cycles / CpuHz; } }

    /// <summary>
    /// 칩 쓰기 기록 → VGM (YM2203 0x55 · YM2608 0x56/0x57). from(CPU 클럭) 앞의 쓰기는 기다림 없이 처음에 (음색 · 설정), 그 뒤는 시간대로 seconds초까지.
    /// BEEP은 VGM에 칩이 없어 빠진다.
    /// </summary>
    public byte[] ToVgm(long from, double seconds, string title = "")
    {
        var w = new AdlibComposer.Formats.VgmWriter { System = "PC-9801", Title = title, Notes = "PC-98 DOS 흉내 (" + Sound + ")" };
        bool opna = Sound == Board.Opna;
        if (opna) w.SetClock(AdlibComposer.Formats.VgmWriter.ClockYm2608, OpnBaseClock * 2);
        else w.SetClock(AdlibComposer.Formats.VgmWriter.ClockYm2203, OpnBaseClock);
        long total = (long)(seconds * 44100), at = 0;
        foreach (var e in Log)
        {
            if (e.kind > 1) continue;
            if (e.t >= from)
            {
                long ts = (long)((e.t - from) / CpuHz * 44100);
                if (ts >= total) break;
                if (ts > at) { w.Wait(ts - at); at = ts; }
            }
            w.Write((byte)(opna ? 0x56 + e.kind : 0x55), e.reg, e.val);
        }
        if (total > at) w.Wait(total - at);
        return w.ToArray();
    }
}
