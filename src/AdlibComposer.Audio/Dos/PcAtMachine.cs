using System;
using System.Collections.Generic;
using AdlibComposer.Audio.Cpu;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// IBM PC/AT + MS-DOS (hoot pcatdos처럼): <see cref="DosMachine"/>에 AT 입출력 · 인터럽트 · 사운드 카드를 더한다.
/// <para>PIC 8259 둘 (주 20h/21h · 벡터 08h~, 종 A0h/A1h · 벡터 70h~, 종 = 주 IRQ2), PIT 8253 (40h~43h, 1.193182MHz: 0번 → IRQ0, 처음 18.2Hz, 2번 = 스피커 높이),
/// 스피커 61h (bit0 게이트 · bit1 출력), 키보드 60h · 64h (누른 키 없음), CMOS 70h · 71h.</para>
/// <para>BIOS: INT 08h = 진짜 x86 처리기 (0040:006C 틱 올리고 INT 1Ch 부른 뒤 EOI — 1Ch에 매달린 드라이버가 많다), INT 10h · 11h · 12h · 16h · 1Ah 최소.</para>
/// <para>사운드: AdLib (OPL2 388h/389h, 타이머 1 · 2 상태 = 카드 찾기), Sound Blaster 16 (OPL3 220h~223h · 388h~38Bh, OPL2 228h/229h, DSP 226h 리셋 → AAh · E1h 판 4.05).
/// hoot 포트 · 인터럽트는 PC-98과 같다 (7E0h 요인 · 7E2h~ 곡 코드 · 7E8h 켜기 / 끄기, funcvect 기본 7Fh).</para>
/// <para>CPU 클럭 = 1.193182MHz × clockmul (hoot pcatdos: clockmul 4 = 4.77MHz XT).</para>
/// </summary>
public sealed class PcAtMachine : DosMachine, IHootMachine
{
    // ------------------------------------------------------------ IHootMachine
    List<(long t, int kind, int reg, int val)> IHootMachine.ChipLog { get { return Log; } }
    double IHootMachine.ClockHz { get { return CpuHz; } }
    int IHootMachine.HootVector { get { return FuncVector; } set { FuncVector = value; } }
    bool IHootMachine.KeepRunning { get { return AutoIdle; } set { AutoIdle = value; } }
    int IHootMachine.HootCount { get { return HootDelivered; } }

    /// <summary>GameBlaster = SAA1099 × 2 (220h 데이터 · 221h 주소, 222h · 223h 둘째 칩 → 쓰기 종류 0 · 1), Tandy = SN76489 (C0h → 종류 0).</summary>
    public enum Card { AdLib, SoundBlaster16, Beep, GameBlaster, Tandy }

    public const double PitClock = 1193182;
    public readonly Card Sound;
    public readonly double CpuHz;
    public int FuncVector = 0x7F;
    public bool AutoIdle;

    /// <summary>MPU-401 (330h 데이터 · 331h 상태 / 명령): 나간 MIDI 바이트는 기록 종류 4.</summary>
    public readonly Mpu401 Mpu = new Mpu401();
    /// <summary>MPU-401 인터럽트: IRQ2 = AT에서 IRQ9 (벡터 71h, 카드 기본).</summary>
    public int MpuIrq = 9;

    /// <summary>칩 쓰기: 시간 (CPU 클럭), 종류 (0 OPL 포트 0 · SAA1099 #1 · SN76489, 1 OPL3 포트 1 · SAA1099 #2, 2 스피커 켜기, 3 스피커 카운터, 4 MIDI 바이트), 레지스터, 값.</summary>
    public readonly List<(long t, int kind, int reg, int val)> Log = new List<(long, int, int, int)>();
    public readonly HashSet<int> UnknownPorts = new HashSet<int>();
    public long Interrupts { get; private set; }
    public int HootDelivered { get; private set; }

    private const int BiosSeg = 0xF000, Int08Off = 0x0500;

    public PcAtMachine(DosFileSystem fs, Card sound, int clockMul = 4, int memoryTop = 0xA000) : base(fs, memoryTop)
    {
        Sound = sound;
        CpuHz = PitClock * Math.Max(1, clockMul);
        if (sound == Card.Tandy) { Poke(BiosSeg, 0xFFFE, 0xFF); Poke(BiosSeg, 0xC000, 0x21); }   // Tandy 1000 BIOS: 모델 바이트 FFh + F000:C000 = 21h (드라이버의 Tandy 확인)
        Mpu.MidiOut = b => Log.Add((Cpu.Cycles, 4, 0, b));
        // BIOS 데이터 (0040:xx): 장비 · 메모리 크기 · 틱
        Poke16(0x40, 0x10, 0x0021); Poke16(0x40, 0x13, 640);
        // INT 08h: PUSH AX; PUSH DS; MOV AX,40h; MOV DS,AX; INC WORD [6Ch]; JNZ +4; INC WORD [6Eh]; INT 1Ch; MOV AL,20h; OUT 20h,AL; POP DS; POP AX; IRET
        byte[] int08 = { 0x50, 0x1E, 0xB8, 0x40, 0x00, 0x8E, 0xD8, 0xFF, 0x06, 0x6C, 0x00, 0x75, 0x04, 0xFF, 0x06, 0x6E, 0x00, 0xCD, 0x1C, 0xB0, 0x20, 0xE6, 0x20, 0x1F, 0x58, 0xCF };
        for (int i = 0; i < int08.Length; i++) Poke(BiosSeg, Int08Off + i, int08[i]);
        Poke16(0, 0x08 * 4, Int08Off); Poke16(0, 0x08 * 4 + 2, BiosSeg);
        // INT 1Ch 기본 = IRET 하나 (스텁 F1 1C 00 CF는 호스트를 거치므로 그대로 둬도 되지만 빠르게)
        Poke(BiosSeg, 0x1C * 4, 0xCF);
        // BIOS가 PIT 0을 18.2Hz로
        pit[0].Reload = 0x10000; pit[0].Mode = 3; pit[0].Running = true; pit[0].Start = 0;
        nextTimer0 = PitTicksToCycles(0x10000);
    }

    // ------------------------------------------------------------ PIC

    // AT BIOS: 주 = 타이머 · 키보드 · 종(IRQ2)만 열림
    private readonly Pic8259 master = new Pic8259 { Base = 0x08, Imr = 0xF8 }, slave = new Pic8259 { Base = 0x70, Imr = 0xFF };

    private void RaiseIrq(int irq)
    {
        if (irq < 8) master.Irr |= 1 << irq;
        else { slave.Irr |= 1 << (irq - 8); master.Irr |= 1 << 2; }
    }

    private void LowerIrq(int irq)
    {
        if (irq < 8) master.Irr &= ~(1 << irq);
        else { slave.Irr &= ~(1 << (irq - 8)); if ((slave.Irr & ~slave.Imr) == 0) master.Irr &= ~0x04; }
    }

    private bool DeliverIrq()
    {
        if (!Cpu.GetFlag(X86.IF)) return false;
        int m = master.Pending();
        if (m < 0) return false;
        if (m == 2)
        {
            int s = slave.Pending();
            if (s < 0) { master.Irr &= ~0x04; return false; }
            if (!Cpu.HardwareInterrupt(slave.Base + s)) return false;
            slave.Irr &= ~(1 << s); if (!slave.AutoEoi) slave.Isr |= 1 << s;
            master.Irr &= ~0x04; if (!master.AutoEoi) master.Isr |= 0x04;
            Interrupts++;
            return true;
        }
        if (!Cpu.HardwareInterrupt(master.Base + m)) return false;
        master.Irr &= ~(1 << m); if (!master.AutoEoi) master.Isr |= 1 << m;
        Interrupts++;
        return true;
    }

    // ------------------------------------------------------------ PIT

    private sealed class Counter
    {
        public int Reload = 0x10000, Mode = 3, Rw = 3;
        public bool LowNext = true, Latched, Running;
        public int LatchValue, WriteLow;
        public long Start;
    }

    private readonly Counter[] pit = { new Counter(), new Counter(), new Counter() };
    private long nextTimer0 = long.MaxValue;

    private long PitTicksToCycles(long ticks) { return (long)(ticks * CpuHz / PitClock); }

    private int PitCount(Counter c)
    {
        if (!c.Running) return c.Reload & 0xFFFF;
        long ticks = (long)((Cpu.Cycles - c.Start) * PitClock / CpuHz);
        return (c.Reload - (int)(ticks % c.Reload)) & 0xFFFF;
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
        if (ch == 2) Log.Add((Cpu.Cycles, 3, 0, c.Reload & 0xFFFF));
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

    // ------------------------------------------------------------ OPL (AdLib · SB16)

    private int oplAddr0, oplAddr1;
    private int oplT1, oplT2, oplCtl, oplFlags;
    private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;
    private long T1Period { get { return Math.Max(1, (long)(80e-6 * (256 - oplT1) * CpuHz)); } }
    private long T2Period { get { return Math.Max(1, (long)(320e-6 * (256 - oplT2) * CpuHz)); } }
    private bool Opl3 { get { return Sound == Card.SoundBlaster16; } }

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
                    nextT1 = (v & 1) != 0 ? Cpu.Cycles + T1Period : long.MaxValue;
                    nextT2 = (v & 2) != 0 ? Cpu.Cycles + T2Period : long.MaxValue;
                    return;
            }
        }
        Log.Add((Cpu.Cycles, port, reg, v));
    }

    private int OplStatus()
    {
        int s = oplFlags;
        if (s != 0) s |= 0x80;
        return Opl3 ? s : s | 0x06;   // YM3812: 아래 비트 110
    }

    // ------------------------------------------------------------ SB16 DSP

    private int saaAddr0, saaAddr1;
    private readonly Queue<int> dspOut = new Queue<int>();
    private int dspResetStep, dspArgs, mixerAddr;
    private readonly byte[] mixer = new byte[256];

    private void DspWrite(int v)
    {
        if (dspArgs > 0) { dspArgs--; return; }
        switch (v)
        {
            case 0xE1: dspOut.Enqueue(4); dspOut.Enqueue(5); break;   // 판 4.05
            case 0xE0: dspArgs = 1; dspOut.Enqueue(0xFF); break;       // 반전 (값은 대충)
            case 0x40: case 0x10: case 0xE4: case 0x38: dspArgs = 1; break;
            case 0x41: case 0x42: case 0x14: case 0x48: case 0x80: dspArgs = 2; break;
            case 0xB0: case 0xB2: case 0xB4: case 0xB6: case 0xC0: case 0xC2: case 0xC4: case 0xC6: dspArgs = 3; break;
            case 0xE8: dspArgs = 1; dspOut.Enqueue(0); break;
        }
    }

    // ------------------------------------------------------------ hoot

    private int hootCause = 2, hootCode;
    private bool hootEnabled = true, hootPending;
    public bool HootInterruptEnabled { get { return hootEnabled; } }
    public bool HootPending { get { return hootPending; } }

    public void Play(int code)
    {
        if (HandleOpen(code & 0xFFFF)) Duplicate(code & 0xFFFF, 0);
        hootCode = code; hootCause = 0; hootPending = true;
    }

    public void Stop() { hootCause = 2; hootPending = true; }

    // ------------------------------------------------------------ 포트

    private int speaker;   // 61h

    public override byte In(int port)
    {
        switch (port)
        {
            case 0x20: return (byte)master.ReadCmd();
            case 0x21: return (byte)master.Imr;
            case 0xA0: return (byte)slave.ReadCmd();
            case 0xA1: return (byte)slave.Imr;
            case 0x40: return (byte)PitRead(0);
            case 0x41: return (byte)PitRead(1);
            case 0x42: return (byte)PitRead(2);
            case 0x61: return (byte)((speaker & 0x0F) | (PitCount(pit[2]) < pit[2].Reload / 2 ? 0x20 : 0) | ((Cpu.Cycles / 64) % 2 == 0 ? 0x10 : 0));   // bit4 = 새로고침 토글
            case 0x60: return 0;
            case 0x64: return 0x1C;   // 입력 버퍼 빔
            case 0x71: return 0;
            case 0x330: return (byte)Mpu.ReadData();
            case 0x331: return (byte)Mpu.ReadStatus();
            case 0x201: return 0xF0;  // 조이스틱 없음
            case 0x3DA: return (byte)((Cpu.Cycles / (long)(CpuHz / 60 / 16)) % 16 == 0 ? 0x09 : 0x00);   // CGA/VGA 수직 귀선
            case 0x7E0: return (byte)hootCause;
            case 0x7E1: return 0;
            case 0x7E2: return (byte)hootCode; case 0x7E3: return (byte)(hootCode >> 8);
            case 0x7E4: return (byte)(hootCode >> 16); case 0x7E5: return (byte)(hootCode >> 24);
        }
        if (Sound == Card.AdLib || Sound == Card.SoundBlaster16)
        {
            switch (port)
            {
                case 0x388: return (byte)OplStatus();
                case 0x389: case 0x38B: return 0xFF;
                case 0x38A: if (Opl3) return (byte)OplStatus(); break;
            }
        }
        if (Sound == Card.SoundBlaster16)
        {
            switch (port)
            {
                case 0x220: case 0x228: return (byte)OplStatus();
                case 0x222: return (byte)OplStatus();
                case 0x225: return mixerAddr == 0x80 ? (byte)0x02 : mixerAddr == 0x81 ? (byte)0x22 : mixer[mixerAddr];   // IRQ 5 · DMA 1 / 5
                case 0x22A: return (byte)(dspOut.Count > 0 ? dspOut.Dequeue() : 0xFF);
                case 0x22C: return 0x00;   // 쓰기 가능
                case 0x22E: case 0x22F: return (byte)(dspOut.Count > 0 ? 0x80 : 0x00);
            }
        }
        UnknownPorts.Add(port);
        return 0xFF;
    }

    public override void Out(int port, byte value)
    {
        int v = value;
        switch (port)
        {
            case 0x20: master.WriteCmd(v); return;
            case 0x21: master.WriteData(v); return;
            case 0xA0: slave.WriteCmd(v); return;
            case 0xA1: slave.WriteData(v); return;
            case 0x40: PitWrite(0, v); return;
            case 0x41: PitWrite(1, v); return;
            case 0x42: PitWrite(2, v); return;
            case 0x43: PitControl(v); return;
            case 0x61:
                {
                    bool was = (speaker & 3) == 3, now = (v & 3) == 3;
                    speaker = v;
                    if (was != now) Log.Add((Cpu.Cycles, 2, 0, now ? 1 : 0));
                    return;
                }
            case 0x60: case 0x64: case 0x70: case 0x80: case 0x3D4: case 0x3D5: case 0x3C8: case 0x3C9: case 0x3CE: case 0x3CF: case 0x3C4: case 0x3C5: return;
            case 0x330: Mpu.WriteData(v); return;
            case 0x331: Mpu.WriteCommand(v); return;
            case 0x7E8:
                if (v == 0x80) hootEnabled = false;
                else if (v == 0x81) hootEnabled = true;
                return;
        }
        if (Sound == Card.GameBlaster && port >= 0x220 && port <= 0x22F)
        {
            switch (port)
            {
                case 0x220: Log.Add((Cpu.Cycles, 0, saaAddr0, v)); return;
                case 0x221: saaAddr0 = v & 0x1F; return;
                case 0x222: Log.Add((Cpu.Cycles, 1, saaAddr1, v)); return;
                case 0x223: saaAddr1 = v & 0x1F; return;
            }
            return;   // 검출 래치 (226h · 227h) 등
        }
        if (Sound == Card.Tandy && port >= 0xC0 && port <= 0xC7) { Log.Add((Cpu.Cycles, 0, 0, v)); return; }
        if (Sound == Card.AdLib || Sound == Card.SoundBlaster16)
        {
            switch (port)
            {
                case 0x388: oplAddr0 = v; return;
                case 0x389: OplWrite(0, oplAddr0, v); return;
                case 0x38A: if (Opl3) { oplAddr1 = v; return; } break;
                case 0x38B: if (Opl3) { OplWrite(1, oplAddr1, v); return; } break;
            }
        }
        if (Sound == Card.SoundBlaster16)
        {
            switch (port)
            {
                case 0x220: case 0x228: oplAddr0 = v; return;
                case 0x221: case 0x229: OplWrite(0, oplAddr0, v); return;
                case 0x222: oplAddr1 = v; return;
                case 0x223: OplWrite(1, oplAddr1, v); return;
                case 0x224: mixerAddr = v; return;
                case 0x225: mixer[mixerAddr] = (byte)v; return;
                case 0x226:
                    if ((v & 1) != 0) dspResetStep = 1;
                    else if (dspResetStep == 1) { dspResetStep = 0; dspOut.Clear(); dspArgs = 0; dspOut.Enqueue(0xAA); }
                    return;
                case 0x22C: DspWrite(v); return;
            }
            if ((port >= 0x00 && port <= 0x1F) || (port >= 0x80 && port <= 0x8F) || (port >= 0xC0 && port <= 0xDF)) return;   // DMA (PCM은 아직)
        }
        UnknownPorts.Add(port | 0x10000);
    }

    // ------------------------------------------------------------ BIOS

    protected override bool HaltIsIdle { get { return true; } }

    protected override void Interrupt(int n)
    {
        if (n >= 0x08 && n <= 0x0F) { master.WriteCmd(0x60 | (n - 0x08)); return; }
        if (n >= 0x70 && n <= 0x77) { slave.WriteCmd(0x60 | (n - 0x70)); master.WriteCmd(0x62); return; }
        switch (n)
        {
            case 0x10:   // 화면: 모드 3, 나머지는 무시
                if (Cpu.AH == 0x0F) { Cpu.AL = 3; Cpu.AH = 80; Cpu.BH = 0; }
                else if (Cpu.AH == 0x1A) { Cpu.AL = 0x1A; Cpu.BL = 8; }
                else if (Cpu.AH == 0x0E) ConsoleOutByte(Cpu.AL);
                return;
            case 0x11: Cpu.AX = 0x0021; return;
            case 0x12: Cpu.AX = 640; return;
            case 0x15: if (Cpu.AH == 0x88) Cpu.AX = 0; SetStackFlag(X86.CF, Cpu.AH != 0x88); return;
            case 0x16:   // 키보드: 누른 키 없음
                if (Cpu.AH == 0x01 || Cpu.AH == 0x11) { SetStackFlag(X86.ZF, true); Cpu.AX = 0; }
                else if (Cpu.AH == 0x02 || Cpu.AH == 0x12) Cpu.AL = 0;
                else if (Cpu.AH == 0x00 || Cpu.AH == 0x10) Cpu.AX = 0;
                return;
            case 0x1A:
                if (Cpu.AH == 0x00) { Cpu.CX = PeekWord(0x40, 0x6E); Cpu.DX = PeekWord(0x40, 0x6C); Cpu.AL = 0; }
                return;
            case 0x13: case 0x14: case 0x17: case 0x1B: case 0x1C: case 0x1D: case 0x1E: case 0x1F: return;
        }
        base.Interrupt(n);
    }

    private void ConsoleOutByte(int c) { ConsoleBytes.Add((byte)c); }

    // ------------------------------------------------------------ 돌리기

    private long NextEvent { get { return Math.Min(Math.Min(nextTimer0, Math.Min(nextT1, nextT2)), nextMpu); } }
    // ------------------------------------------------------------ MPU 클럭 · 인터럽트

    private long nextMpu = long.MaxValue;
    private bool mpuIrqUp;
    private long MpuPeriod { get { return Math.Max(1, (long)(Mpu.ClockSeconds * CpuHz)); } }


    public override void Run(long cycles)
    {
        long end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            if (HostReturned)
            {
                if (!AutoIdle) break;
                Idle();
            }
            long now = Cpu.Cycles;
            if (now >= nextTimer0) { RaiseIrq(0); var c = pit[0]; nextTimer0 = c.Mode == 0 ? long.MaxValue : nextTimer0 + Math.Max(1, PitTicksToCycles(c.Reload)); }
            if (now >= nextT1) { if ((oplCtl & 0x40) == 0) oplFlags |= 0x40; nextT1 += T1Period; }
            if (now >= nextT2) { if ((oplCtl & 0x20) == 0) oplFlags |= 0x20; nextT2 += T2Period; }
            if (Mpu.Running) { if (nextMpu == long.MaxValue) nextMpu = now + MpuPeriod; }
            else nextMpu = long.MaxValue;
            if (now >= nextMpu) { Mpu.Clock(); nextMpu += MpuPeriod; }
            if (Mpu.IrqPending != mpuIrqUp) { mpuIrqUp = Mpu.IrqPending; if (mpuIrqUp) RaiseIrq(MpuIrq); else LowerIrq(MpuIrq); }
            else if (mpuIrqUp) RaiseIrq(MpuIrq);
            if (hootPending && hootEnabled && Cpu.GetFlag(X86.IF)) { hootPending = false; Cpu.Vector(FuncVector); Interrupts++; HootDelivered++; }
            else DeliverIrq();
            if (Cpu.Halted)
            {
                long next = NextEvent;
                if (next == long.MaxValue || next > end) { Cpu.Cycles = Math.Max(Cpu.Cycles, end); break; }
                Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                continue;
            }
            long until = Math.Min(end, NextEvent);
            int guard = 0;
            do { Cpu.Step(); } while (Cpu.Cycles < until && !Cpu.Halted && !HostReturned && Mpu.IrqPending == mpuIrqUp && ++guard < 64);   // MPU가 읽을 데이터를 내면 바로 인터럽트 (드라이버가 ACK를 인터럽트로 기다림)
        }
    }

    public double Seconds { get { return Cpu.Cycles / CpuHz; } }
}
