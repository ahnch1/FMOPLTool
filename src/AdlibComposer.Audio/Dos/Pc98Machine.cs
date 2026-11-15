using System;
using System.Collections.Generic;
using AdlibComposer.Audio.Cpu;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// PC-9801 + MS-DOS (hoot pc98dos처럼): <see cref="DosMachine"/>에 PC-98 입출력 · 인터럽트를 더한다.
/// <para>PIC 8259 둘 (주 00h/02h · 벡터 08h~, 종 08h/0Ah · 벡터 10h~, 종 = 주 IRQ7), PIT 8253 (71h~77h · 3FD9h~3FDFh, 2.4576MHz: 0번 → IRQ0, 1번 = BEEP 높이),
/// VSYNC 56.42Hz → IRQ2 (64h에 쓰면 다음 한 번 받음), GDC 상태 60h · A0h bit5 = 수직 귀선, BEEP 켜기 37h (6 켬 · 7 끔),
/// 사운드 보드: OPN (26K, YM2203 3.9936MHz) · OPNA (86, YM2608 7.9872MHz) 188h/18Ah (+ 18Ch/18Eh), 타이머 A · B → INT0 (IRQ3, 벡터 0Bh: SSG I/O 포트로 읽히는 점퍼 = 0), 86 ID A460h.</para>
/// <para>hoot 전용: 7E0h 인터럽트 요인 (0 재생 · 2 정지), 7E2h~7E5h 곡 코드, 7E8h 0x80 · 0x81 hoot 인터럽트 끔 · 켬. hoot 인터럽트 = funcvect 벡터를 직접 (PIC 없이,
/// IF가 켜져 있을 때). 재생 = 곡 코드 번호의 핸들을 표준 입력에 이중화하고 요인 0 (<see cref="Play"/>).</para>
/// <para>칩 쓰기는 시간 (CPU 클럭)과 함께 <see cref="Log"/>에 모은다 (VGM으로 바꾸기는 바깥에서).</para>
/// </summary>
public sealed class Pc98Machine : DosMachine
{
    public enum Board { None, Opn, Opna }

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

    /// <summary>칩 쓰기: 시간 (CPU 클럭), 종류 (0 OPN/OPNA 포트 0, 1 OPNA 포트 1, 2 BEEP 켜기, 3 BEEP 카운터), 레지스터, 값.</summary>
    public readonly List<(long t, int kind, int reg, int val)> Log = new List<(long, int, int, int)>();
    public int UnknownPortReads, UnknownPortWrites;
    public readonly HashSet<int> UnknownPorts = new HashSet<int>();

    public Pc98Machine(DosFileSystem fs, Board sound, int clockMul = 8, int memoryTop = 0xA000) : base(fs, memoryTop)
    {
        Sound = sound;
        CpuHz = PitClock * Math.Max(1, clockMul);
        vsyncPeriod = (long)(CpuHz / VsyncHz);
        nextVsync = vsyncPeriod;
        // BIOS: 시스템 공통 영역 (0:0500~) 일부 — 86 / 26K가 있다고 알리는 값은 없음 (드라이버가 포트로 찾는다)
    }

    // ------------------------------------------------------------ PIC 8259

    private sealed class Pic
    {
        public int Irr, Isr, Imr = 0xFF, Base;
        public int IcwStep;
        public bool NeedIcw4, ReadIsr, AutoEoi;

        public void WriteCmd(int v)
        {
            if ((v & 0x10) != 0) { IcwStep = 1; NeedIcw4 = (v & 1) != 0; Imr = 0; Isr = 0; Irr = 0; return; }   // ICW1
            if ((v & 0x18) == 0)   // OCW2
            {
                int cmd = v >> 5;
                if (cmd == 1) { for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { Isr &= ~(1 << i); break; } }   // 정하지 않은 EOI
                else if (cmd == 3) Isr &= ~(1 << (v & 7));   // 정한 EOI
                return;
            }
            if ((v & 0x18) == 0x08) { if ((v & 2) != 0) ReadIsr = (v & 1) != 0; }   // OCW3
        }

        public void WriteData(int v)
        {
            switch (IcwStep)
            {
                case 1: Base = v & 0xF8; IcwStep = 2; return;
                case 2: IcwStep = NeedIcw4 ? 3 : 0; return;
                case 3: AutoEoi = (v & 2) != 0; IcwStep = 0; return;
            }
            Imr = v;
        }

        public int ReadCmd() { return ReadIsr ? Isr : Irr; }

        /// <summary>받을 IRQ (가장 높은 우선순위, 들어가 있는 것보다 높음), 없으면 −1.</summary>
        public int Pending()
        {
            int req = Irr & ~Imr;
            for (int i = 0; i < 8; i++)
            {
                if ((Isr & (1 << i)) != 0) return -1;
                if ((req & (1 << i)) != 0) return i;
            }
            return -1;
        }
    }

    // BIOS가 남기는 마스크: 주 = 키보드 · 종(IRQ7)만 열림, 종 = 모두 닫힘 (드라이버가 자기 IRQ를 연다)
    private readonly Pic master = new Pic { Base = 0x08, Imr = 0x7D }, slave = new Pic { Base = 0x10, Imr = 0xFF };

    private void RaiseIrq(int irq)
    {
        if (irq < 8) master.Irr |= 1 << irq;
        else { slave.Irr |= 1 << (irq - 8); master.Irr |= 1 << 7; }
    }

    private void LowerIrq(int irq)
    {
        if (irq < 8) master.Irr &= ~(1 << irq);
        else { slave.Irr &= ~(1 << (irq - 8)); if ((slave.Irr & ~slave.Imr) == 0) master.Irr &= ~(1 << 7); }
    }

    /// <summary>CPU에 넣을 수 있는 인터럽트가 있으면 넣는다.</summary>
    private bool DeliverIrq()
    {
        if (!Cpu.GetFlag(X86.IF)) return false;
        int m = master.Pending();
        if (m < 0) return false;
        int vector;
        if (m == 7)
        {
            int s = slave.Pending();
            if (s < 0) { master.Irr &= ~0x80; return false; }
            vector = slave.Base + s;
            if (!Cpu.HardwareInterrupt(vector)) return false;
            if (Trace) Count(TraceVectors, vector);
            slave.Irr &= ~(1 << s); if (!slave.AutoEoi) slave.Isr |= 1 << s;
            master.Irr &= ~0x80; if (!master.AutoEoi) master.Isr |= 0x80;
            Interrupts++;
            return true;
        }
        vector = master.Base + m;
        if (!Cpu.HardwareInterrupt(vector)) return false;
        if (Trace) Count(TraceVectors, vector);
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
    private int a460 = 0x40;

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
        return opnRegs[port * 0x100 + reg];
    }

    private void TimerOverflow(int which)
    {
        bool enabled = (timerCtl & (which == 0 ? 4 : 8)) != 0;
        if (enabled)
        {
            bool was = timerFlags != 0;
            timerFlags |= which == 0 ? 1 : 2;
            if (!was) RaiseIrq(SoundIrq);
        }
    }

    // ------------------------------------------------------------ VSYNC · BEEP · hoot

    private readonly long vsyncPeriod;
    private long nextVsync;
    private bool vsyncArmed;
    private int hootCause = 2, hootCode;
    // hoot 인터럽트는 처음부터 켜져 있다 (MMP_HOOT 같은 셸은 7E8h를 쓰지 않는다, PMD_98은 준비하는 동안만 80h로 끔)
    private bool hootEnabled = true, hootPending;
    public bool HootInterruptEnabled { get { return hootEnabled; } }

    private bool InVsync { get { return Cpu.Cycles % vsyncPeriod < vsyncPeriod / 16; } }

    /// <summary>곡 재생 요청 (hoot): 곡 코드 번호의 핸들을 표준 입력에 이중화하고 요인 0으로 hoot 인터럽트.</summary>
    public void Play(int code)
    {
        if (HandleOpen(code & 0xFFFF)) Duplicate(code & 0xFFFF, 0);
        hootCode = code; hootCause = 0; hootPending = true;
    }

    public void Stop() { hootCause = 2; hootPending = true; }

    // ------------------------------------------------------------ 포트

    // ------------------------------------------------------------ 진단 (Trace = true일 때만 센다)

    /// <summary>진단: 넣은 하드웨어 인터럽트 벡터별 수 · 포트 읽기 / 쓰기 수 (쓰기는 포트 | 0x10000).</summary>
    public bool Trace;
    public readonly Dictionary<int, int> TraceVectors = new Dictionary<int, int>(), TracePorts = new Dictionary<int, int>();
    private static void Count(Dictionary<int, int> d, int k) { d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1; }

    /// <summary>진단 한 줄: PIC 마스크 · ISR, PIT 0, OPN 타이머, CPU 위치.</summary>
    public string Describe()
    {
        return "PIC m IMR " + master.Imr.ToString("X2") + " ISR " + master.Isr.ToString("X2") + " IRR " + master.Irr.ToString("X2") + ", s IMR " + slave.Imr.ToString("X2") + " ISR " + slave.Isr.ToString("X2") +
            ", PIT0 " + (pit[0].Running ? pit[0].Reload.ToString() + " m" + pit[0].Mode : "off") + ", OPN ctl " + timerCtl.ToString("X2") + " A " + timerA + " B " + timerB + " flags " + timerFlags +
            ", IF " + (Cpu.GetFlag(X86.IF) ? 1 : 0) + ", at " + Cpu.CS.ToString("X4") + ":" + Cpu.IP.ToString("X4") + (Cpu.Halted ? " HLT" : "");
    }

    public override byte In(int port)
    {
        if (Trace) Count(TracePorts, port);
        switch (port)
        {
            case 0x00: return (byte)master.ReadCmd();
            case 0x02: return (byte)master.Imr;
            case 0x08: return (byte)slave.ReadCmd();
            case 0x0A: return (byte)slave.Imr;
            case 0x71: case 0x3FD9: return (byte)PitRead(0);
            case 0x73: case 0x3FDB: return (byte)PitRead(1);
            case 0x75: case 0x3FDD: return (byte)PitRead(2);
            case 0x60: case 0xA0: return (byte)(0x04 | (InVsync ? 0x20 : 0));   // GDC 상태: FIFO 빔, 수직 귀선
            case 0x31: case 0x33: case 0x35: return 0x00;
            case 0x42: return 0xDF;   // 프린터 8255 포트 B: bit5 = 0 → 5 · 10MHz 계열 (PIT 2.4576MHz)
            case 0x7FD9: return 0;    // 마우스: 움직임 없음
            case 0x7FDB: return 0xFF; // 버튼 안 눌림
            case 0x7FDD: return (byte)mouseC;
            case 0x7E0: return (byte)hootCause;
            case 0x7E1: return 0;
            case 0x7E2: return (byte)hootCode; case 0x7E3: return (byte)(hootCode >> 8);
            case 0x7E4: return (byte)(hootCode >> 16); case 0x7E5: return (byte)(hootCode >> 24);
        }
        if (Sound != Board.None)
        {
            switch (port)
            {
                case 0x188: return (byte)OpnStatus(false);
                case 0x18A: return (byte)OpnReadData(0, opnAddr0);
                case 0x18C: if (Sound == Board.Opna) return (byte)OpnStatus(true); break;
                case 0x18E: if (Sound == Board.Opna) return (byte)OpnReadData(1, opnAddr1); break;
                case 0xA460: if (Sound == Board.Opna) return (byte)a460; break;
            }
        }
        UnknownPortReads++; UnknownPorts.Add(port);
        return 0xFF;
    }

    public override void Out(int port, byte value)
    {
        if (Trace) Count(TracePorts, port | 0x10000);
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
            case 0x7FDD: mouseC = v; MouseTimerChanged(); return;
            case 0x7FDF:
                if ((v & 0x80) == 0) { int bit = (v >> 1) & 7; mouseC = (v & 1) != 0 ? mouseC | (1 << bit) : mouseC & ~(1 << bit); }
                else mouseC = 0x10;   // 방식 설정: 포트 C 처음으로 (인터럽트 끔)
                MouseTimerChanged();
                return;
            case 0xBFDB: mouseHz = MouseRates[v & 3]; MouseTimerChanged(); return;
            case 0x7E8:
                if (v == 0x80) hootEnabled = false;
                else if (v == 0x81) hootEnabled = true;
                return;
        }
        if (Sound != Board.None)
        {
            switch (port)
            {
                case 0x188: opnAddr0 = v; return;
                case 0x18A: OpnWrite(0, opnAddr0, v); return;
                case 0x18C: if (Sound == Board.Opna) { opnAddr1 = v; return; } break;
                case 0x18E: if (Sound == Board.Opna) { OpnWrite(1, opnAddr1, v); return; } break;
                case 0xA460: if (Sound == Board.Opna) { a460 = 0x40 | (v & 3); return; } break;
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

    private long NextEvent { get { return Math.Min(Math.Min(Math.Min(nextTimer0, nextVsync), Math.Min(nextTimerA, nextTimerB)), nextMouse); } }

    // ------------------------------------------------------------ BIOS (기본 벡터 처리기)

    protected override bool HaltIsIdle { get { return true; } }

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
            if (hootPending && hootEnabled && Cpu.GetFlag(X86.IF)) { hootPending = false; Cpu.Vector(FuncVector); Interrupts++; HootDelivered++; }
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
            do { Cpu.Step(); } while (Cpu.Cycles < until && !Cpu.Halted && !HostReturned && ++guard < 64);
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
