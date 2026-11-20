using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>6809가 보는 메모리 (16비트 주소).</summary>
public interface ICpu6809Bus
{
    byte Read(int addr);
    void Write(int addr, byte value);
}

/// <summary>
/// 모토로라 6809 (HD6309의 확장 명령은 없음): 남코 System 1 · 86 · 매피 계열, 데이터 이스트 brkthru 같은 아케이드 사운드 CPU.
/// <para>레지스터 A · B (= D), X · Y · U · S, DP, CC (E F H I N Z V C). 방식: 바로 · 직접 (DP:nn) · 확장 · 색인 (포스트바이트: 5비트 · 8 · 16비트 오프셋, A · B · D,R, ,R+ · ,R++ · ,-R · ,--R, PC 기준, [간접]) · 상대 8 · 16.
/// 앞 바이트 10h · 11h 페이지 (긴 분기, CMPD · CMPY · LDY · LDS · CMPU · CMPS, SWI2 · SWI3).</para>
/// <para>인터럽트: NMI (전부 쌓음, FFFCh), FIRQ (PC · CC만, FFF6h), IRQ (전부, FFF8h), CWAI · SYNC는 인터럽트까지 기다림. 사이클은 모토로라 표 (색인 덧 사이클 포함).</para>
/// </summary>
public sealed class Cpu6809
{
    private readonly ICpu6809Bus bus;

    public int A, B, X, Y, U, S, PC, DP, CC;
    public long Cycles;
    /// <summary>CWAI · SYNC로 인터럽트를 기다리는 중.</summary>
    public bool Waiting;
    /// <summary>CWAI로 이미 레지스터를 쌓아 둠 (인터럽트 때 다시 쌓지 않음).</summary>
    private bool cwaiStacked;
    /// <summary>알 수 없는 명령 수 (시험 · 로그용).</summary>
    public int UnknownOps;

    public const int FlagE = 0x80, FlagF = 0x40, FlagH = 0x20, FlagI = 0x10, FlagN = 0x08, FlagZ = 0x04, FlagV = 0x02, FlagC = 0x01;

    public Cpu6809(ICpu6809Bus bus) { this.bus = bus; }

    public int D { get { return (A << 8) | B; } set { A = (value >> 8) & 0xFF; B = value & 0xFF; } }

    /// <summary>리셋: DP = 0, FIRQ · IRQ 막음, PC = (FFFEh).</summary>
    public void Reset()
    {
        DP = 0; CC = FlagI | FlagF; Waiting = false; cwaiStacked = false;
        PC = Read16(0xFFFE);
    }

    // ------------------------------------------------------------ 메모리

    private int Read(int a) { return bus.Read(a & 0xFFFF); }
    private void Write(int a, int v) { bus.Write(a & 0xFFFF, (byte)v); }
    private int Read16(int a) { return (Read(a) << 8) | Read(a + 1); }
    private void Write16(int a, int v) { Write(a, v >> 8); Write(a + 1, v); }
    private int Fetch() { int v = Read(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int v = Read16(PC); PC = (PC + 2) & 0xFFFF; return v; }

    private void PushS(int v) { S = (S - 1) & 0xFFFF; Write(S, v); }
    private void PushS16(int v) { PushS(v); PushS(v >> 8); }
    private int PullS() { int v = Read(S); S = (S + 1) & 0xFFFF; return v; }
    private int PullS16() { int h = PullS(); return (h << 8) | PullS(); }
    private void PushU(int v) { U = (U - 1) & 0xFFFF; Write(U, v); }
    private int PullU() { int v = Read(U); U = (U + 1) & 0xFFFF; return v; }

    // ------------------------------------------------------------ 인터럽트

    private void PushAll()
    {
        PushS16(PC); PushS16(U); PushS16(Y); PushS16(X); PushS(DP); PushS(B); PushS(A); PushS(CC);
    }

    /// <summary>NMI (막을 수 없음).</summary>
    public void Nmi()
    {
        Waiting = false;
        if (!cwaiStacked) { CC |= FlagE; PushAll(); Cycles += 19; }
        cwaiStacked = false;
        CC |= FlagI | FlagF;
        PC = Read16(0xFFFC);
    }

    /// <summary>IRQ 줄: I가 꺼져 있으면 받고 true. SYNC 중이면 막혀 있어도 깨어나기만 한다.</summary>
    public bool Irq()
    {
        if ((CC & FlagI) != 0) { if (Waiting && !cwaiStacked) Waiting = false; return false; }
        Waiting = false;
        if (!cwaiStacked) { CC |= FlagE; PushAll(); Cycles += 19; }
        cwaiStacked = false;
        CC |= FlagI;
        PC = Read16(0xFFF8);
        return true;
    }

    /// <summary>FIRQ 줄: F가 꺼져 있으면 받고 true (PC · CC만 쌓음).</summary>
    public bool Firq()
    {
        if ((CC & FlagF) != 0) { if (Waiting && !cwaiStacked) Waiting = false; return false; }
        Waiting = false;
        if (!cwaiStacked) { CC &= ~FlagE; PushS16(PC); PushS(CC); Cycles += 10; }
        cwaiStacked = false;
        CC |= FlagI | FlagF;
        PC = Read16(0xFFF6);
        return true;
    }

    // ------------------------------------------------------------ 깃발

    private void SetNZ8(int r) { CC &= ~(FlagN | FlagZ); if ((r & 0x80) != 0) CC |= FlagN; if ((r & 0xFF) == 0) CC |= FlagZ; }
    private void SetNZ16(int r) { CC &= ~(FlagN | FlagZ); if ((r & 0x8000) != 0) CC |= FlagN; if ((r & 0xFFFF) == 0) CC |= FlagZ; }
    private int Carry { get { return CC & FlagC; } }
    private void Flag(int f, bool on) { if (on) CC |= f; else CC &= ~f; }

    private int Add8(int a, int b, int c)
    {
        int r = a + b + c;
        CC &= ~(FlagH | FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b ^ r) & 0x10) != 0) CC |= FlagH;
        if (((a ^ r) & (b ^ r) & 0x80) != 0) CC |= FlagV;
        if ((r & 0x100) != 0) CC |= FlagC;
        SetNZ8(r);
        return r & 0xFF;
    }

    private int Sub8(int a, int b, int c)
    {
        int r = a - b - c;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b) & (a ^ r) & 0x80) != 0) CC |= FlagV;
        if ((r & 0x100) != 0) CC |= FlagC;
        SetNZ8(r);
        return r & 0xFF;
    }

    private int Add16(int a, int b)
    {
        int r = a + b;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ r) & (b ^ r) & 0x8000) != 0) CC |= FlagV;
        if ((r & 0x10000) != 0) CC |= FlagC;
        SetNZ16(r);
        return r & 0xFFFF;
    }

    private int Sub16(int a, int b)
    {
        int r = a - b;
        CC &= ~(FlagN | FlagZ | FlagV | FlagC);
        if (((a ^ b) & (a ^ r) & 0x8000) != 0) CC |= FlagV;
        if ((r & 0x10000) != 0) CC |= FlagC;
        SetNZ16(r);
        return r & 0xFFFF;
    }

    private int Logic8(int r) { r &= 0xFF; CC &= ~FlagV; SetNZ8(r); return r; }
    private int Ld16(int r) { r &= 0xFFFF; CC &= ~FlagV; SetNZ16(r); return r; }

    // 읽고-바꾸고-쓰기 (NEG COM LSR ROR ASR ASL ROL DEC INC TST CLR): 칸 번호 = 명령 아래 니블
    private int Rmw(int op, int m)
    {
        int r;
        switch (op & 0x0F)
        {
            case 0x0: r = (-m) & 0xFF; CC &= ~(FlagN | FlagZ | FlagV | FlagC); if (m == 0x80) CC |= FlagV; if (m != 0) CC |= FlagC; SetNZ8(r); return r;   // NEG
            case 0x3: r = (~m) & 0xFF; CC &= ~FlagV; CC |= FlagC; SetNZ8(r); return r;                                                                    // COM
            case 0x4: Flag(FlagC, (m & 1) != 0); r = m >> 1; SetNZ8(r); return r;                                                                          // LSR
            case 0x6: r = (m >> 1) | (Carry << 7); Flag(FlagC, (m & 1) != 0); SetNZ8(r); return r;                                                         // ROR
            case 0x7: r = (m & 0x80) | (m >> 1); Flag(FlagC, (m & 1) != 0); SetNZ8(r); return r;                                                           // ASR
            case 0x8: r = (m << 1) & 0xFF; Flag(FlagC, (m & 0x80) != 0); Flag(FlagV, ((m ^ (m << 1)) & 0x80) != 0); SetNZ8(r); return r;                    // ASL
            case 0x9: r = ((m << 1) | Carry) & 0xFF; Flag(FlagC, (m & 0x80) != 0); Flag(FlagV, ((m ^ (m << 1)) & 0x80) != 0); SetNZ8(r); return r;          // ROL
            case 0xA: r = (m - 1) & 0xFF; Flag(FlagV, m == 0x80); SetNZ8(r); return r;                                                                     // DEC
            case 0xC: r = (m + 1) & 0xFF; Flag(FlagV, m == 0x7F); SetNZ8(r); return r;                                                                     // INC
            case 0xD: CC &= ~FlagV; SetNZ8(m); return -1;                                                                                                  // TST (안 씀)
            case 0xF: CC &= ~(FlagN | FlagV | FlagC); CC |= FlagZ; return 0;                                                                               // CLR
        }
        UnknownOps++;
        return -1;
    }

    // ------------------------------------------------------------ 주소

    private int Direct() { return (DP << 8) | Fetch(); }

    private int GetReg(int r) { switch (r) { case 0: return X; case 1: return Y; case 2: return U; default: return S; } }
    private void SetReg(int r, int v) { v &= 0xFFFF; switch (r) { case 0: X = v; break; case 1: Y = v; break; case 2: U = v; break; default: S = v; break; } }

    /// <summary>색인 주소 (포스트바이트를 읽고 덧 사이클을 더함).</summary>
    private int Indexed()
    {
        int post = Fetch();
        int r = (post >> 5) & 3;
        int ea;
        if ((post & 0x80) == 0)
        {
            int off = post & 0x1F; if (off >= 16) off -= 32;
            Cycles += 1;
            return (GetReg(r) + off) & 0xFFFF;
        }
        switch (post & 0x0F)
        {
            case 0x0: ea = GetReg(r); SetReg(r, ea + 1); Cycles += 2; break;
            case 0x1: ea = GetReg(r); SetReg(r, ea + 2); Cycles += 3; break;
            case 0x2: SetReg(r, GetReg(r) - 1); ea = GetReg(r); Cycles += 2; break;
            case 0x3: SetReg(r, GetReg(r) - 2); ea = GetReg(r); Cycles += 3; break;
            case 0x4: ea = GetReg(r); break;
            case 0x5: ea = GetReg(r) + (sbyte)B; Cycles += 1; break;
            case 0x6: ea = GetReg(r) + (sbyte)A; Cycles += 1; break;
            case 0x8: ea = GetReg(r) + (sbyte)Fetch(); Cycles += 1; break;
            case 0x9: ea = GetReg(r) + (short)Fetch16(); Cycles += 4; break;
            case 0xB: ea = GetReg(r) + (short)D; Cycles += 4; break;
            case 0xC: { int o = (sbyte)Fetch(); ea = PC + o; Cycles += 1; break; }
            case 0xD: { int o = (short)Fetch16(); ea = PC + o; Cycles += 5; break; }
            case 0xF: ea = Fetch16(); Cycles += 2; break;
            default: UnknownOps++; ea = GetReg(r); break;
        }
        ea &= 0xFFFF;
        if ((post & 0x10) != 0) { ea = Read16(ea); Cycles += 3; }
        return ea;
    }

    /// <summary>80h~FFh 묶음의 주소: 칸 (bit4~5) 0 = 바로 (자리 = PC), 1 = 직접, 2 = 색인, 3 = 확장.</summary>
    private int Ea(int op, int immSize)
    {
        switch ((op >> 4) & 3)
        {
            case 0: { int a = PC; PC = (PC + immSize) & 0xFFFF; return a; }
            case 1: Cycles += 2; return Direct();
            case 2: Cycles += 2; return Indexed();
            default: Cycles += 3; return Fetch16();
        }
    }

    // ------------------------------------------------------------ 분기

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

    // ------------------------------------------------------------ TFR · EXG

    private int RegValue(int code)
    {
        switch (code)
        {
            case 0x0: return D; case 0x1: return X; case 0x2: return Y; case 0x3: return U; case 0x4: return S; case 0x5: return PC;
            case 0x8: return A | 0xFF00; case 0x9: return B | 0xFF00; case 0xA: return CC | 0xFF00; case 0xB: return DP | 0xFF00;
        }
        return 0xFFFF;
    }

    private void SetRegValue(int code, int v)
    {
        switch (code)
        {
            case 0x0: D = v & 0xFFFF; break; case 0x1: X = v & 0xFFFF; break; case 0x2: Y = v & 0xFFFF; break;
            case 0x3: U = v & 0xFFFF; break; case 0x4: S = v & 0xFFFF; break; case 0x5: PC = v & 0xFFFF; break;
            case 0x8: A = v & 0xFF; break; case 0x9: B = v & 0xFF; break; case 0xA: CC = v & 0xFF; break; case 0xB: DP = v & 0xFF; break;
        }
    }

    // ------------------------------------------------------------ 실행

    /// <summary>명령 하나를 실행하고 쓴 사이클을 돌려준다. 기다리는 중이면 1사이클만 흘려보낸다.</summary>
    public int Step()
    {
        long start = Cycles;
        if (Waiting) { Cycles += 1; return 1; }
        int op = Fetch();
        if (op == 0x10) Page2(Fetch());
        else if (op == 0x11) Page3(Fetch());
        else Page1(op);
        return (int)(Cycles - start);
    }

    private void Page1(int op)
    {
        int ea, m, r;
        // 00h~0Fh 직접, 40h A, 50h B, 60h 색인, 70h 확장: 읽고-바꾸고-쓰기 · JMP
        if (op < 0x10 || (op >= 0x40 && op < 0x80))
        {
            int mode = op >> 4;
            if (mode == 4 || mode == 5)
            {
                Cycles += 2;
                m = mode == 4 ? A : B;
                r = Rmw(op, m);
                if (r >= 0) { if (mode == 4) A = r; else B = r; }
                return;
            }
            if ((op & 0x0F) == 0x0E)   // JMP
            {
                Cycles += mode == 0 ? 3 : mode == 6 ? 3 : 4;
                PC = mode == 0 ? Direct() : mode == 6 ? Indexed() : Fetch16();
                return;
            }
            Cycles += mode == 0 ? 6 : mode == 6 ? 6 : 7;
            ea = mode == 0 ? Direct() : mode == 6 ? Indexed() : Fetch16();
            if ((op & 0x0F) == 0x0F) { Rmw(op, 0); Write(ea, 0); return; }   // CLR (읽지 않는 셈)
            r = Rmw(op, Read(ea));
            if (r >= 0) Write(ea, r);
            return;
        }
        if (op >= 0x80) { Alu(op); return; }
        switch (op)
        {
            case 0x12: Cycles += 2; return;                                                 // NOP
            case 0x13: Cycles += 4; Waiting = true; cwaiStacked = false; return;            // SYNC
            case 0x16: { int o = Fetch16(); PC = (PC + o) & 0xFFFF; Cycles += 5; return; }  // LBRA
            case 0x17: { int o = Fetch16(); PushS16(PC); PC = (PC + o) & 0xFFFF; Cycles += 9; return; }   // LBSR
            case 0x19:                                                                      // DAA
                {
                    int lo = A & 0x0F, hi = A >> 4, cf = 0;
                    if (lo > 9 || (CC & FlagH) != 0) cf |= 0x06;
                    if (hi > 9 || (CC & FlagC) != 0 || (hi > 8 && lo > 9)) cf |= 0x60;
                    int t = A + cf;
                    CC &= ~(FlagN | FlagZ | FlagV);
                    if ((t & 0x100) != 0) CC |= FlagC;
                    A = t & 0xFF;
                    SetNZ8(A);
                    Cycles += 2;
                    return;
                }
            case 0x1A: CC |= Fetch(); Cycles += 3; return;                                  // ORCC
            case 0x1C: CC &= Fetch(); Cycles += 3; return;                                  // ANDCC
            case 0x1D: A = (B & 0x80) != 0 ? 0xFF : 0; SetNZ16(D); CC &= ~FlagV; Cycles += 2; return;   // SEX
            case 0x1E:                                                                      // EXG
                {
                    int p = Fetch(), r1 = p >> 4, r2 = p & 15;
                    int v1 = RegValue(r1), v2 = RegValue(r2);
                    SetRegValue(r1, v2); SetRegValue(r2, v1);
                    Cycles += 8;
                    return;
                }
            case 0x1F: { int p = Fetch(); SetRegValue(p & 15, RegValue(p >> 4)); Cycles += 6; return; }   // TFR
            case 0x30: X = Indexed(); Flag(FlagZ, X == 0); Cycles += 4; return;             // LEAX
            case 0x31: Y = Indexed(); Flag(FlagZ, Y == 0); Cycles += 4; return;             // LEAY
            case 0x32: S = Indexed(); Cycles += 4; return;                                  // LEAS
            case 0x33: U = Indexed(); Cycles += 4; return;                                  // LEAU
            case 0x34: Psh(Fetch(), true); Cycles += 5; return;                             // PSHS
            case 0x35: Pul(Fetch(), true); Cycles += 5; return;                             // PULS
            case 0x36: Psh(Fetch(), false); Cycles += 5; return;                            // PSHU
            case 0x37: Pul(Fetch(), false); Cycles += 5; return;                            // PULU
            case 0x39: PC = PullS16(); Cycles += 5; return;                                 // RTS
            case 0x3A: X = (X + B) & 0xFFFF; Cycles += 3; return;                           // ABX
            case 0x3B:                                                                      // RTI
                CC = PullS();
                if ((CC & FlagE) != 0)
                {
                    A = PullS(); B = PullS(); DP = PullS(); X = PullS16(); Y = PullS16(); U = PullS16();
                    Cycles += 15;
                }
                else Cycles += 6;
                PC = PullS16();
                return;
            case 0x3C: CC &= Fetch(); CC |= FlagE; PushAll(); Waiting = true; cwaiStacked = true; Cycles += 20; return;   // CWAI
            case 0x3D: { int d = A * B; D = d; Flag(FlagZ, d == 0); Flag(FlagC, (d & 0x80) != 0); Cycles += 11; return; }   // MUL
            case 0x3F: Swi(0xFFFA, true); return;                                           // SWI
        }
        if (op >= 0x20 && op < 0x30)                                                        // 짧은 분기
        {
            int o = (sbyte)Fetch();
            if (Cond(op)) PC = (PC + o) & 0xFFFF;
            Cycles += 3;
            return;
        }
        UnknownOps++;
        Cycles += 2;
    }

    private void Swi(int vector, bool maskFirq)
    {
        CC |= FlagE;
        PushAll();
        CC |= FlagI;
        if (maskFirq) CC |= FlagF;
        PC = Read16(vector);
        Cycles += 19;
    }

    private void Psh(int post, bool s)
    {
        Action<int> p8 = s ? (Action<int>)PushS : PushU;
        Action<int> p16 = v => { p8(v); p8(v >> 8); };
        if ((post & 0x80) != 0) { p16(PC); Cycles += 2; }
        if ((post & 0x40) != 0) { p16(s ? U : S); Cycles += 2; }
        if ((post & 0x20) != 0) { p16(Y); Cycles += 2; }
        if ((post & 0x10) != 0) { p16(X); Cycles += 2; }
        if ((post & 0x08) != 0) { p8(DP); Cycles += 1; }
        if ((post & 0x04) != 0) { p8(B); Cycles += 1; }
        if ((post & 0x02) != 0) { p8(A); Cycles += 1; }
        if ((post & 0x01) != 0) { p8(CC); Cycles += 1; }
    }

    private void Pul(int post, bool s)
    {
        Func<int> g8 = s ? (Func<int>)PullS : PullU;
        Func<int> g16 = () => { int h = g8(); return (h << 8) | g8(); };
        if ((post & 0x01) != 0) { CC = g8(); Cycles += 1; }
        if ((post & 0x02) != 0) { A = g8(); Cycles += 1; }
        if ((post & 0x04) != 0) { B = g8(); Cycles += 1; }
        if ((post & 0x08) != 0) { DP = g8(); Cycles += 1; }
        if ((post & 0x10) != 0) { X = g16(); Cycles += 2; }
        if ((post & 0x20) != 0) { Y = g16(); Cycles += 2; }
        if ((post & 0x40) != 0) { if (s) U = g16(); else S = g16(); Cycles += 2; }
        if ((post & 0x80) != 0) { PC = g16(); Cycles += 2; }
    }

    /// <summary>80h~FFh: A (80h~BFh) · B (C0h~FFh) 산술 · 불러오기 · 저장, 16비트 (SUBD · ADDD · CMPX · LDX · LDD · LDU …), BSR · JSR.</summary>
    private void Alu(int op)
    {
        bool isB = op >= 0xC0;
        int col = op & 0x0F;
        int ea;
        Cycles += 2;
        switch (col)
        {
            case 0x3: case 0xC: case 0xE:   // 16비트 읽기
                {
                    ea = Ea(op, 2);
                    int m = Read16(ea);
                    Cycles += 2;
                    if (col == 0x3) { if (isB) D = Add16(D, m); else D = Sub16(D, m); }      // ADDD · SUBD
                    else if (col == 0xC) { if (isB) D = Ld16(m); else Sub16(X, m); }        // LDD · CMPX
                    else { if (isB) U = Ld16(m); else X = Ld16(m); }                       // LDU · LDX
                    return;
                }
            case 0xD:
                if (!isB)   // BSR · JSR
                {
                    if (op == 0x8D) { int o = (sbyte)Fetch(); PushS16(PC); PC = (PC + o) & 0xFFFF; Cycles += 5; return; }
                    ea = Ea(op, 0);
                    PushS16(PC); PC = ea; Cycles += 3;
                    return;
                }
                if (op == 0xCD) { UnknownOps++; return; }
                ea = Ea(op, 0); Write16(ea, Ld16(D)); Cycles += 1; return;                 // STD
            case 0xF:
                if (op == 0x8F || op == 0xCF) { UnknownOps++; return; }
                ea = Ea(op, 0); Write16(ea, Ld16(isB ? U : X)); Cycles += 1; return;       // STU · STX
            case 0x7:
                if (op == 0x87 || op == 0xC7) { UnknownOps++; return; }
                ea = Ea(op, 0); Write(ea, Logic8(isB ? B : A)); return;                   // STA · STB
        }
        ea = Ea(op, 1);
        int v = Read(ea);
        int acc = isB ? B : A;
        int res = acc;
        switch (col)
        {
            case 0x0: res = Sub8(acc, v, 0); break;               // SUB
            case 0x1: Sub8(acc, v, 0); return;                    // CMP
            case 0x2: res = Sub8(acc, v, Carry); break;           // SBC
            case 0x4: res = Logic8(acc & v); break;               // AND
            case 0x5: Logic8(acc & v); return;                    // BIT
            case 0x6: res = Logic8(v); break;                     // LD
            case 0x8: res = Logic8(acc ^ v); break;               // EOR
            case 0x9: res = Add8(acc, v, Carry); break;           // ADC
            case 0xA: res = Logic8(acc | v); break;               // OR
            case 0xB: res = Add8(acc, v, 0); break;               // ADD
        }
        if (isB) B = res; else A = res;
    }

    /// <summary>10h 페이지: 긴 분기, CMPD · CMPY · LDY · STY · LDS · STS, SWI2.</summary>
    private void Page2(int op)
    {
        if (op >= 0x21 && op < 0x30)
        {
            int o = Fetch16();
            if (Cond(op)) { PC = (PC + o) & 0xFFFF; Cycles += 6; } else Cycles += 5;
            return;
        }
        if (op == 0x3F) { Swi(0xFFF4, false); return; }
        int col = op & 0x0F;
        if (op >= 0x80 && (col == 0x3 || col == 0xC || col == 0xE || col == 0xF))
        {
            bool isB = op >= 0xC0;
            Cycles += 3;
            if (col == 0xF)
            {
                if (op == 0x8F || op == 0xCF) { UnknownOps++; return; }
                int ea2 = Ea(op, 0); Write16(ea2, Ld16(isB ? S : Y)); Cycles += 1; return;   // STS · STY
            }
            int ea = Ea(op, 2);
            int m = Read16(ea);
            Cycles += 2;
            if (col == 0x3 && !isB) Sub16(D, m);              // CMPD
            else if (col == 0xC && !isB) Sub16(Y, m);         // CMPY
            else if (col == 0xE) { if (isB) S = Ld16(m); else Y = Ld16(m); }   // LDS · LDY
            else UnknownOps++;
            return;
        }
        UnknownOps++;
        Cycles += 2;
    }

    /// <summary>11h 페이지: CMPU · CMPS, SWI3.</summary>
    private void Page3(int op)
    {
        if (op == 0x3F) { Swi(0xFFF2, false); return; }
        int col = op & 0x0F;
        if (op >= 0x80 && op < 0xC0 && (col == 0x3 || col == 0xC))
        {
            Cycles += 3;
            int ea = Ea(op, 2);
            int m = Read16(ea);
            Cycles += 2;
            Sub16(col == 0x3 ? U : S, m);
            return;
        }
        UnknownOps++;
        Cycles += 2;
    }
}
