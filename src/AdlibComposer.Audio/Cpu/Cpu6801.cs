using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>6801 계열이 보는 메모리 · 포트. 내부 레지스터 (00h~1Fh)는 CPU가 직접 처리하고, 포트 1~4만 버스로 묻는다.</summary>
public interface ICpu6801Bus
{
    byte Read(int addr);
    void Write(int addr, byte value);
    /// <summary>포트 읽기 (port 1~4). 출력 방향 비트는 CPU가 래치 값으로 채운다.</summary>
    byte PortRead(int port);
    /// <summary>포트 쓰기 (port 1~4, 방향 레지스터를 거친 값).</summary>
    void PortWrite(int port, byte value);
}

/// <summary>
/// 모토로라 6801 / 6803 · 히타치 HD6301 / HD63701 (6800 + 6801 확장 + 6301 확장): 남코 System 86 · 아이렘 M62 사운드 CPU.
/// <para>6801 확장: LSRD · ASLD · PULX · PSHX · ABX · MUL · LDD · STD · ADDD · SUBD · BRN, CPX는 모든 깃발. 6301 확장: XGDX · SLP, AIM · OIM · EIM · TIM (바로 값 + 직접 / 색인).</para>
/// <para>내부: 00h~1Fh 레지스터 (포트 1~4 방향 · 자료, TCSR 08h, FRC 09h~0Ah, OCR 0Bh~0Ch, …), 타이머 = E 클럭마다 FRC +1, FRC = OCR → OCF (TCSR bit6, EOCI bit3 → 벡터 FFF4h),
/// 넘침 → TOF (bit5, ETOI bit2 → FFF2h). 벡터: 리셋 FFFEh, NMI FFFCh, SWI FFFAh, IRQ1 FFF8h, ICF FFF6h, OCF FFF4h, TOF FFF2h, SCI FFF0h, TRAP FFEEh.
/// 사이클은 HD6301 표 근사.</para>
/// </summary>
public sealed class Cpu6801
{
    private readonly ICpu6801Bus bus;

    public int A, B, X, SP, PC, CC = 0xC0 | FlagI;
    public long Cycles;
    /// <summary>WAI · SLP로 인터럽트를 기다리는 중.</summary>
    public bool Waiting;
    private bool waiStacked;
    public int UnknownOps;

    public const int FlagH = 0x20, FlagI = 0x10, FlagN = 0x08, FlagZ = 0x04, FlagV = 0x02, FlagC = 0x01;

    // 내부 레지스터
    private readonly byte[] ddr = new byte[5], latch = new byte[5];
    private int tcsr, frc, ocr = 0xFFFF;
    private bool tcsrRead;
    private long frcBase;   // FRC를 맞춘 사이클

    public Cpu6801(ICpu6801Bus bus) { this.bus = bus; }

    public int D { get { return (A << 8) | B; } set { A = (value >> 8) & 0xFF; B = value & 0xFF; } }

    public void Reset()
    {
        CC = 0xC0 | FlagI; Waiting = false; waiStacked = false;
        tcsr = 0; frc = 0; ocr = 0xFFFF; frcBase = Cycles;
        Array.Clear(ddr, 0, ddr.Length);
        PC = Read16(0xFFFE);
    }

    // ------------------------------------------------------------ 메모리 · 내부 레지스터

    private int Frc { get { return (int)((frc + (Cycles - frcBase)) & 0xFFFF); } }

    private int Read(int a)
    {
        a &= 0xFFFF;
        if (a < 0x20) return ReadInternal(a);
        return bus.Read(a);
    }

    private void Write(int a, int v)
    {
        a &= 0xFFFF; v &= 0xFF;
        if (a < 0x20) { WriteInternal(a, v); return; }
        bus.Write(a, (byte)v);
    }

    private static readonly int[] PortOf = { 1, 2, 1, 2, 3, 4, 3, 4 };   // 00h~07h → 포트

    private int ReadInternal(int a)
    {
        switch (a)
        {
            case 0x00: case 0x01: case 0x04: case 0x05: return ddr[PortOf[a]];
            case 0x02: case 0x03: case 0x06: case 0x07:
                {
                    int p = PortOf[a];
                    int input = bus.PortRead(p);
                    return (latch[p] & ddr[p]) | (input & ~ddr[p]) & 0xFF;
                }
            case 0x08: UpdateTimer(); tcsrRead = true; return tcsr;
            case 0x09: { UpdateTimer(); if (tcsrRead) tcsr &= ~0x20; return Frc >> 8; }
            case 0x0A: return Frc & 0xFF;
            case 0x0B: return ocr >> 8;
            case 0x0C: return ocr & 0xFF;
        }
        return 0;
    }

    private void WriteInternal(int a, int v)
    {
        switch (a)
        {
            case 0x00: case 0x01: case 0x04: case 0x05: ddr[PortOf[a]] = (byte)v; bus.PortWrite(PortOf[a], (byte)(latch[PortOf[a]] & v)); return;
            case 0x02: case 0x03: case 0x06: case 0x07:
                {
                    int p = PortOf[a];
                    latch[p] = (byte)v;
                    bus.PortWrite(p, (byte)((v & ddr[p]) | (~ddr[p] & 0xFF)));
                    return;
                }
            case 0x08: UpdateTimer(); tcsr = (tcsr & 0xE0) | (v & 0x1F); return;
            case 0x09: frc = 0xFFF8; frcBase = Cycles; return;   // 6801: FRC 위 바이트를 쓰면 FFF8h
            case 0x0B: UpdateTimer(); ocr = (v << 8) | (ocr & 0xFF); if (tcsrRead) tcsr &= ~0x40; return;
            case 0x0C: UpdateTimer(); ocr = (ocr & 0xFF00) | v; if (tcsrRead) tcsr &= ~0x40; return;
        }
    }

    /// <summary>FRC를 지금까지 세면서 OCR 일치 · 넘침 깃발을 세운다.</summary>
    private void UpdateTimer()
    {
        long n = Cycles - frcBase;
        if (n <= 0) return;
        long from = frc;
        long to = from + n;
        // OCR과 같아지는 때가 (from, to] 안에 있는가 (16비트 주기)
        long dist = ((ocr - from) & 0xFFFF);
        if (dist == 0) dist = 0x10000;
        if (dist <= n) tcsr |= 0x40;
        if (to > 0xFFFF) tcsr |= 0x20;
        frc = (int)(to & 0xFFFF);
        frcBase = Cycles;
    }

    /// <summary>다음 타이머 사건 (OCF · TOF)까지 남은 사이클 — 기계가 기다릴 때 건너뛰기용.</summary>
    public long CyclesToTimerEvent()
    {
        int f = Frc;
        long dOc = (ocr - f) & 0xFFFF; if (dOc == 0) dOc = 0x10000;
        long dOv = 0x10000 - f;
        return Math.Max(1, Math.Min(dOc, dOv));
    }

    private int Read16(int a) { return (Read(a) << 8) | Read(a + 1); }
    private void Write16(int a, int v) { Write(a, v >> 8); Write(a + 1, v); }
    private int Fetch() { int v = Read(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int v = Read16(PC); PC = (PC + 2) & 0xFFFF; return v; }
    private void Push(int v) { Write(SP, v); SP = (SP - 1) & 0xFFFF; }
    private void Push16(int v) { Push(v); Push(v >> 8); }
    private int Pull() { SP = (SP + 1) & 0xFFFF; return Read(SP); }
    private int Pull16() { int h = Pull(); return (h << 8) | Pull(); }

    // ------------------------------------------------------------ 인터럽트

    private void PushAll() { Push16(PC); Push16(X); Push(A); Push(B); Push(CC); }

    private void Vector(int v)
    {
        Waiting = false;
        if (!waiStacked) { PushAll(); Cycles += 12; }
        waiStacked = false;
        CC |= FlagI;
        PC = Read16(v);
    }

    public void Nmi() { Vector(0xFFFC); }

    /// <summary>바깥 IRQ1 줄: I가 꺼져 있으면 받고 true.</summary>
    public bool Irq()
    {
        if ((CC & FlagI) != 0) return false;
        Vector(0xFFF8);
        return true;
    }

    /// <summary>내부 타이머 인터럽트 (OCF · TOF): 깃발 · 허가가 있고 I가 꺼져 있으면 받는다.</summary>
    public bool TimerIrq()
    {
        UpdateTimer();
        if ((CC & FlagI) != 0) return false;
        if ((tcsr & 0x40) != 0 && (tcsr & 0x08) != 0) { Vector(0xFFF4); return true; }
        if ((tcsr & 0x20) != 0 && (tcsr & 0x04) != 0) { Vector(0xFFF2); return true; }
        return false;
    }

    // ------------------------------------------------------------ 깃발

    private void NZ8(int r) { CC &= ~(FlagN | FlagZ); if ((r & 0x80) != 0) CC |= FlagN; if ((r & 0xFF) == 0) CC |= FlagZ; }
    private void NZ16(int r) { CC &= ~(FlagN | FlagZ); if ((r & 0x8000) != 0) CC |= FlagN; if ((r & 0xFFFF) == 0) CC |= FlagZ; }
    private void Flag(int f, bool on) { if (on) CC |= f; else CC &= ~f; }
    private int Carry { get { return CC & FlagC; } }

    private int Add8(int a, int b, int c)
    {
        int r = a + b + c;
        CC &= ~(FlagH | FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b ^ r) & 0x10) != 0) CC |= FlagH;
        if (((a ^ r) & (b ^ r) & 0x80) != 0) CC |= FlagV;
        if ((r & 0x100) != 0) CC |= FlagC;
        NZ8(r);
        return r & 0xFF;
    }

    private int Sub8(int a, int b, int c)
    {
        int r = a - b - c;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b) & (a ^ r) & 0x80) != 0) CC |= FlagV;
        if ((r & 0x100) != 0) CC |= FlagC;
        NZ8(r);
        return r & 0xFF;
    }

    private int Add16(int a, int b)
    {
        int r = a + b;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ r) & (b ^ r) & 0x8000) != 0) CC |= FlagV;
        if ((r & 0x10000) != 0) CC |= FlagC;
        NZ16(r);
        return r & 0xFFFF;
    }

    private int Sub16(int a, int b)
    {
        int r = a - b;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b) & (a ^ r) & 0x8000) != 0) CC |= FlagV;
        if ((r & 0x10000) != 0) CC |= FlagC;
        NZ16(r);
        return r & 0xFFFF;
    }

    private int Logic(int r) { r &= 0xFF; CC &= ~FlagV; NZ8(r); return r; }

    /// <summary>한 자리 연산 (아래 니블): NEG COM LSR ROR ASR ASL ROL DEC INC TST CLR. 쓰지 않으면 −1.</summary>
    private int Unary(int op, int m)
    {
        int r;
        switch (op & 0x0F)
        {
            case 0x0: r = (-m) & 0xFF; CC &= ~(FlagN | FlagZ | FlagV | FlagC); if (m == 0x80) CC |= FlagV; if (m != 0) CC |= FlagC; NZ8(r); return r;
            case 0x3: r = (~m) & 0xFF; CC &= ~FlagV; CC |= FlagC; NZ8(r); return r;
            case 0x4: Flag(FlagC, (m & 1) != 0); r = m >> 1; NZ8(r); Flag(FlagV, (CC & FlagC) != 0); return r;   // V = N ^ C (N = 0)
            case 0x6: r = (m >> 1) | (Carry << 7); Flag(FlagC, (m & 1) != 0); NZ8(r); VNC(); return r;
            case 0x7: r = (m & 0x80) | (m >> 1); Flag(FlagC, (m & 1) != 0); NZ8(r); VNC(); return r;
            case 0x8: r = (m << 1) & 0xFF; Flag(FlagC, (m & 0x80) != 0); NZ8(r); VNC(); return r;
            case 0x9: r = ((m << 1) | Carry) & 0xFF; Flag(FlagC, (m & 0x80) != 0); NZ8(r); VNC(); return r;
            case 0xA: r = (m - 1) & 0xFF; Flag(FlagV, m == 0x80); NZ8(r); return r;
            case 0xC: r = (m + 1) & 0xFF; Flag(FlagV, m == 0x7F); NZ8(r); return r;
            case 0xD: CC &= ~(FlagV | FlagC); NZ8(m); return -1;
            case 0xF: CC &= ~(FlagN | FlagV | FlagC); CC |= FlagZ; return 0;
        }
        UnknownOps++;
        return -1;
    }

    /// <summary>자리 옮김 뒤 V = N ^ C (6800).</summary>
    private void VNC() { Flag(FlagV, ((CC & FlagN) != 0) != ((CC & FlagC) != 0)); }

    private bool Cond(int c)
    {
        bool n = (CC & FlagN) != 0, z = (CC & FlagZ) != 0, v = (CC & FlagV) != 0, cy = (CC & FlagC) != 0;
        switch (c & 15)
        {
            case 0x0: return true;
            case 0x1: return false;
            case 0x2: return !(cy || z);
            case 0x3: return cy || z;
            case 0x4: return !cy;
            case 0x5: return cy;
            case 0x6: return !z;
            case 0x7: return z;
            case 0x8: return !v;
            case 0x9: return v;
            case 0xA: return !n;
            case 0xB: return n;
            case 0xC: return n == v;
            case 0xD: return n != v;
            case 0xE: return !z && n == v;
            default: return z || n != v;
        }
    }

    // ------------------------------------------------------------ 실행

    public int Step()
    {
        long start = Cycles;
        if (Waiting) { Cycles += 1; return 1; }
        Exec(Fetch());
        return (int)(Cycles - start);
    }

    private void Exec(int op)
    {
        int ea, m, r;
        if (op >= 0x80) { Alu(op); return; }
        if (op >= 0x40)
        {
            int mode = op >> 4;   // 4 = A, 5 = B, 6 = 색인, 7 = 확장 (6301: 61 62 65 6B / 71 72 75 7B = 비트 연산)
            if (mode >= 6 && ((op & 15) == 1 || (op & 15) == 2 || (op & 15) == 5 || (op & 15) == 0xB)) { BitOp(op); return; }
            if (mode == 4 || mode == 5)
            {
                Cycles += 2;
                r = Unary(op, mode == 4 ? A : B);
                if (r >= 0) { if (mode == 4) A = r; else B = r; }
                return;
            }
            if ((op & 15) == 0xE) { PC = mode == 6 ? (X + Fetch()) & 0xFFFF : Fetch16(); Cycles += 3; return; }   // JMP
            ea = mode == 6 ? (X + Fetch()) & 0xFFFF : Fetch16();
            Cycles += 6;
            if ((op & 15) == 0xF) { Unary(op, 0); Write(ea, 0); return; }
            r = Unary(op, Read(ea));
            if (r >= 0) Write(ea, r);
            return;
        }
        if (op >= 0x20 && op < 0x30)
        {
            int o = (sbyte)Fetch();
            if (Cond(op)) PC = (PC + o) & 0xFFFF;
            Cycles += 3;
            return;
        }
        Cycles += 2;
        switch (op)
        {
            case 0x01: return;                                                                  // NOP
            case 0x04: { int d = D; Flag(FlagC, (d & 1) != 0); d >>= 1; D = d; NZ16(d); VNC(); Cycles += 1; return; }   // LSRD
            case 0x05: { int d = D; Flag(FlagC, (d & 0x8000) != 0); d = (d << 1) & 0xFFFF; D = d; NZ16(d); VNC(); Cycles += 1; return; }   // ASLD
            case 0x06: CC = 0xC0 | (A & 0x3F); return;                                          // TAP
            case 0x07: A = CC | 0xC0; return;                                                   // TPA
            case 0x08: X = (X + 1) & 0xFFFF; Flag(FlagZ, X == 0); return;                       // INX
            case 0x09: X = (X - 1) & 0xFFFF; Flag(FlagZ, X == 0); return;                       // DEX
            case 0x0A: CC &= ~FlagV; return;
            case 0x0B: CC |= FlagV; return;
            case 0x0C: CC &= ~FlagC; return;
            case 0x0D: CC |= FlagC; return;
            case 0x0E: CC &= ~FlagI; return;
            case 0x0F: CC |= FlagI; return;
            case 0x10: A = Sub8(A, B, 0); return;                                               // SBA
            case 0x11: Sub8(A, B, 0); return;                                                   // CBA
            case 0x16: B = Logic(A); return;                                                    // TAB
            case 0x17: A = Logic(B); return;                                                    // TBA
            case 0x18: { int t = X; X = D; D = t; return; }                                     // XGDX (6301)
            case 0x19:                                                                          // DAA
                {
                    int lo = A & 15, hi = A >> 4, cf = 0;
                    if (lo > 9 || (CC & FlagH) != 0) cf |= 0x06;
                    if (hi > 9 || (CC & FlagC) != 0 || (hi > 8 && lo > 9)) cf |= 0x60;
                    int t = A + cf;
                    if ((t & 0x100) != 0) CC |= FlagC;
                    A = t & 0xFF; NZ8(A); CC &= ~FlagV;
                    return;
                }
            case 0x1A: Waiting = true; waiStacked = false; return;                              // SLP (6301)
            case 0x1B: A = Add8(A, B, 0); return;                                               // ABA
            case 0x30: X = (SP + 1) & 0xFFFF; return;                                           // TSX
            case 0x31: SP = (SP + 1) & 0xFFFF; return;                                          // INS
            case 0x32: A = Pull(); Cycles += 2; return;                                         // PULA
            case 0x33: B = Pull(); Cycles += 2; return;                                         // PULB
            case 0x34: SP = (SP - 1) & 0xFFFF; return;                                          // DES
            case 0x35: SP = (X - 1) & 0xFFFF; return;                                           // TXS
            case 0x36: Push(A); Cycles += 1; return;                                            // PSHA
            case 0x37: Push(B); Cycles += 1; return;                                            // PSHB
            case 0x38: X = Pull16(); Cycles += 3; return;                                       // PULX
            case 0x39: PC = Pull16(); Cycles += 3; return;                                      // RTS
            case 0x3A: X = (X + B) & 0xFFFF; return;                                            // ABX
            case 0x3B: CC = Pull() | 0xC0; B = Pull(); A = Pull(); X = Pull16(); PC = Pull16(); Cycles += 8; return;   // RTI
            case 0x3C: Push16(X); Cycles += 2; return;                                          // PSHX
            case 0x3D: { int d = A * B; D = d; Flag(FlagC, (d & 0x80) != 0); Cycles += 5; return; }   // MUL
            case 0x3E: PushAll(); Waiting = true; waiStacked = true; Cycles += 7; return;       // WAI
            case 0x3F: PushAll(); CC |= FlagI; PC = Read16(0xFFFA); Cycles += 10; return;       // SWI
        }
        // 00h (6301 TRAP) 그 밖의 정의 안 된 명령
        UnknownOps++;
        PushAll(); CC |= FlagI; PC = Read16(0xFFEE);
    }

    /// <summary>6301 AIM · OIM · EIM · TIM: 바로 값 + 직접 (7xh) / 색인 (6xh).</summary>
    private void BitOp(int op)
    {
        int imm = Fetch();
        int ea = (op & 0xF0) == 0x60 ? (X + Fetch()) & 0xFFFF : Fetch();
        int m = Read(ea), r;
        switch (op & 15)
        {
            case 0x1: r = Logic(m & imm); Write(ea, r); break;
            case 0x2: r = Logic(m | imm); Write(ea, r); break;
            case 0x5: r = Logic(m ^ imm); Write(ea, r); break;
            default: Logic(m & imm); break;
        }
        Cycles += (op & 15) == 0xB ? 4 : 6;
    }

    /// <summary>80h~FFh: A (80h~BFh) · B (C0h~FFh), 칸 (bit4~5): 0 = 바로, 1 = 직접, 2 = 색인 (X + 8비트), 3 = 확장.</summary>
    private void Alu(int op)
    {
        bool isB = op >= 0xC0;
        int col = op & 15, mode = (op >> 4) & 3;
        bool wide = col == 3 || col == 0xC || col == 0xE;
        int ea;
        switch (mode)
        {
            case 0: ea = PC; PC = (PC + (wide ? 2 : 1)) & 0xFFFF; Cycles += 2; break;
            case 1: ea = Fetch(); Cycles += 3; break;
            case 2: ea = (X + Fetch()) & 0xFFFF; Cycles += 4; break;
            default: ea = Fetch16(); Cycles += 4; break;
        }
        if (wide)
        {
            int w = Read16(ea);
            Cycles += 1;
            if (col == 3) { if (isB) D = Add16(D, w); else D = Sub16(D, w); }          // ADDD · SUBD
            else if (col == 0xC) { if (isB) { D = w; CC &= ~FlagV; NZ16(w); } else Sub16(X, w); }   // LDD · CPX
            else { if (isB) X = w; else SP = w; CC &= ~FlagV; NZ16(w); }                 // LDX · LDS
            return;
        }
        if (col == 0xD)
        {
            if (!isB)
            {
                if (op == 0x8D) { int o = (sbyte)Read(ea); Push16(PC); PC = (PC + o) & 0xFFFF; Cycles += 4; return; }   // BSR
                Push16(PC); PC = ea; Cycles += 2; return;                                                              // JSR
            }
            if (mode == 0) { UnknownOps++; return; }
            Write16(ea, D); CC &= ~FlagV; NZ16(D); Cycles += 1; return;                                                 // STD
        }
        if (col == 0xF)
        {
            if (mode == 0) { UnknownOps++; return; }
            int v = isB ? X : SP;
            Write16(ea, v); CC &= ~FlagV; NZ16(v); Cycles += 1; return;                                                // STX · STS
        }
        if (col == 0x7)
        {
            if (mode == 0) { UnknownOps++; return; }
            Write(ea, Logic(isB ? B : A)); return;                                                                    // STA
        }
        int mval = Read(ea);
        int acc = isB ? B : A, res = acc;
        switch (col)
        {
            case 0x0: res = Sub8(acc, mval, 0); break;
            case 0x1: Sub8(acc, mval, 0); return;
            case 0x2: res = Sub8(acc, mval, Carry); break;
            case 0x4: res = Logic(acc & mval); break;
            case 0x5: Logic(acc & mval); return;
            case 0x6: res = Logic(mval); break;
            case 0x8: res = Logic(acc ^ mval); break;
            case 0x9: res = Add8(acc, mval, Carry); break;
            case 0xA: res = Logic(acc | mval); break;
            case 0xB: res = Add8(acc, mval, 0); break;
        }
        if (isB) B = res; else A = res;
    }
}
