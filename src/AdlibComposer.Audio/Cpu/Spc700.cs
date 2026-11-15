using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>SPC700이 보는 64KB 주소 공간 (I/O 레지스터 · IPL ROM은 기계가 처리).</summary>
public interface ISpcBus
{
    int Read(int addr);
    void Write(int addr, int value);
}

/// <summary>
/// Sony SPC700 (슈퍼 패미컴 사운드 CPU, 1.024MHz). 256개 명령 모두: 6502와 닮은 8비트 산술 · 논리, 다이렉트 페이지(P 깃발로 $00 / $100),
/// 16비트(MOVW · ADDW · SUBW · CMPW · INCW · DECW), 비트 명령(SET1 · CLR1 · BBS · BBC · TSET1 · TCLR1 · 1비트 메모리 연산), MUL · DIV · DAA · DAS · XCN,
/// TCALL · PCALL · BRK · RETI. 사이클 수는 명령 종류별 대표값 (타이머 · DSP는 이 사이클 수를 시계로 쓴다).
/// </summary>
public sealed class Spc700
{
    private readonly ISpcBus bus;
    public int A, X, Y, SP = 0xEF, PC;
    public bool N, V, P, B, H, I, Z, C;
    public long Cycles;
    /// <summary>SLEEP · STOP으로 멈춤.</summary>
    public bool Stopped;
    public int UnknownOps;

    public Spc700(ISpcBus bus) { this.bus = bus; }

    public int PSW
    {
        get { return (N ? 0x80 : 0) | (V ? 0x40 : 0) | (P ? 0x20 : 0) | (B ? 0x10 : 0) | (H ? 0x08 : 0) | (I ? 0x04 : 0) | (Z ? 0x02 : 0) | (C ? 0x01 : 0); }
        set { N = (value & 0x80) != 0; V = (value & 0x40) != 0; P = (value & 0x20) != 0; B = (value & 0x10) != 0; H = (value & 0x08) != 0; I = (value & 0x04) != 0; Z = (value & 0x02) != 0; C = (value & 0x01) != 0; }
    }

    // ------------------------------------------------------------ 메모리

    private int Rd(int a) { return bus.Read(a & 0xFFFF) & 0xFF; }
    private void Wr(int a, int v) { bus.Write(a & 0xFFFF, v & 0xFF); }
    private int Dp(int d) { return (P ? 0x100 : 0) | (d & 0xFF); }
    private int RdDp(int d) { return Rd(Dp(d)); }
    private void WrDp(int d, int v) { Wr(Dp(d), v); }
    private int Fetch() { int v = Rd(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int lo = Fetch(); return lo | (Fetch() << 8); }
    /// <summary>다이렉트 페이지 16비트 (윗바이트는 같은 페이지 안에서 감싼다).</summary>
    private int Rd16Dp(int d) { return RdDp(d) | (RdDp((d + 1) & 0xFF) << 8); }
    private void Push(int v) { Wr(0x100 | SP, v); SP = (SP - 1) & 0xFF; }
    private int Pop() { SP = (SP + 1) & 0xFF; return Rd(0x100 | SP); }
    private void PushPc() { Push(PC >> 8); Push(PC & 0xFF); }
    private void PopPc() { int lo = Pop(); PC = lo | (Pop() << 8); }

    // 주소 방식
    private int AIndX() { int t = (Fetch() + X) & 0xFF; return RdDp(t) | (RdDp((t + 1) & 0xFF) << 8); }   // [d+X]
    private int AIndY() { return (Rd16Dp(Fetch()) + Y) & 0xFFFF; }                                       // [d]+Y

    // ------------------------------------------------------------ 연산

    private void NZ(int v) { N = (v & 0x80) != 0; Z = (v & 0xFF) == 0; }

    private int Adc(int a, int b)
    {
        int r = a + b + (C ? 1 : 0);
        V = (~(a ^ b) & (a ^ r) & 0x80) != 0;
        H = ((a ^ b ^ r) & 0x10) != 0;
        C = r > 0xFF;
        r &= 0xFF;
        NZ(r);
        return r;
    }

    private int Sbc(int a, int b) { return Adc(a, b ^ 0xFF); }
    private void Cmp(int a, int b) { int r = a - b; C = r >= 0; NZ(r & 0xFF); }

    /// <summary>0 OR, 1 AND, 2 EOR, 3 CMP, 4 ADC, 5 SBC. CMP는 a를 그대로 돌려준다.</summary>
    private int Alu(int op, int a, int b)
    {
        switch (op)
        {
            case 0: a |= b; NZ(a); return a;
            case 1: a &= b; NZ(a); return a;
            case 2: a ^= b; NZ(a); return a;
            case 3: Cmp(a, b); return a;
            case 4: return Adc(a, b);
            default: return Sbc(a, b);
        }
    }

    /// <summary>0 ASL, 1 ROL, 2 LSR, 3 ROR, 4 DEC, 5 INC.</summary>
    private int Shift(int op, int v)
    {
        switch (op)
        {
            case 0: C = (v & 0x80) != 0; v = (v << 1) & 0xFF; break;
            case 1: { int c = C ? 1 : 0; C = (v & 0x80) != 0; v = ((v << 1) | c) & 0xFF; break; }
            case 2: C = (v & 1) != 0; v >>= 1; break;
            case 3: { int c = C ? 0x80 : 0; C = (v & 1) != 0; v = (v >> 1) | c; break; }
            case 4: v = (v - 1) & 0xFF; break;
            default: v = (v + 1) & 0xFF; break;
        }
        NZ(v);
        return v;
    }

    private void Branch(bool cond)
    {
        int rel = (sbyte)Fetch();
        if (cond) { PC = (PC + rel) & 0xFFFF; Cycles += 2; }
    }

    // ------------------------------------------------------------ 한 명령

    /// <summary>명령 하나를 실행하고 쓴 사이클 수.</summary>
    public int Step()
    {
        if (Stopped) { Cycles += 2; return 2; }
        long start = Cycles;
        int op = Fetch();
        int row = op >> 4, col = op & 0x0F;
        // ---- 산술 · 논리 묶음 (줄 0 · 2 · 4 · 6 · 8 · A와 그 다음 줄, 칸 4~9)
        if (row < 0xC && col >= 4 && col <= 9)
        {
            int alu = row >> 1;
            bool odd = (row & 1) != 0;
            if (!odd)
            {
                switch (col)
                {
                    case 4: A = Alu(alu, A, RdDp(Fetch())); Cycles += 3; break;
                    case 5: A = Alu(alu, A, Rd(Fetch16())); Cycles += 4; break;
                    case 6: A = Alu(alu, A, RdDp(X)); Cycles += 3; break;
                    case 7: A = Alu(alu, A, Rd(AIndX())); Cycles += 6; break;
                    case 8: A = Alu(alu, A, Fetch()); Cycles += 2; break;
                    case 9:   // dd, ds (원본이 먼저)
                        {
                            int src = RdDp(Fetch());
                            int dst = Dp(Fetch());
                            int r = Alu(alu, Rd(dst), src);
                            if (alu != 3) Wr(dst, r);
                            Cycles += 6;
                            break;
                        }
                }
            }
            else
            {
                switch (col)
                {
                    case 4: A = Alu(alu, A, RdDp(Fetch() + X)); Cycles += 4; break;
                    case 5: A = Alu(alu, A, Rd(Fetch16() + X)); Cycles += 5; break;
                    case 6: A = Alu(alu, A, Rd(Fetch16() + Y)); Cycles += 5; break;
                    case 7: A = Alu(alu, A, Rd(AIndY())); Cycles += 6; break;
                    case 8:   // d, #i
                        {
                            int imm = Fetch();
                            int dst = Dp(Fetch());
                            int r = Alu(alu, Rd(dst), imm);
                            if (alu != 3) Wr(dst, r);
                            Cycles += 5;
                            break;
                        }
                    case 9:   // (X), (Y)
                        {
                            int src = RdDp(Y);
                            int dst = Dp(X);
                            int r = Alu(alu, Rd(dst), src);
                            if (alu != 3) Wr(dst, r);
                            Cycles += 5;
                            break;
                        }
                }
            }
            return (int)(Cycles - start);
        }
        // ---- 칸 1: TCALL n, 칸 2: SET1 / CLR1 d.b, 칸 3: BBS / BBC d.b,r
        if (col == 1)
        {
            PushPc();
            int v = 0xFFDE - 2 * row;
            PC = Rd(v) | (Rd(v + 1) << 8);
            Cycles += 8;
            return (int)(Cycles - start);
        }
        if (col == 2)
        {
            int d = Fetch(), bit = 1 << (row >> 1);
            int m = RdDp(d);
            WrDp(d, (row & 1) == 0 ? m | bit : m & ~bit);
            Cycles += 4;
            return (int)(Cycles - start);
        }
        if (col == 3)
        {
            int d = Fetch(), bit = 1 << (row >> 1);
            bool set = (RdDp(d) & bit) != 0;
            Cycles += 5;
            Branch((row & 1) == 0 ? set : !set);
            return (int)(Cycles - start);
        }
        // ---- 자리 옮김 · 늘이기 · 줄이기 (칸 B · C, 줄 0~B)
        if (row < 0xC && (col == 0xB || col == 0xC))
        {
            int sh = row >> 1;   // 0 ASL 1 ROL 2 LSR 3 ROR 4 DEC 5 INC
            bool odd = (row & 1) != 0;
            if (col == 0xB)
            {
                int d = Fetch();
                int addr = odd ? Dp(d + X) : Dp(d);
                Wr(addr, Shift(sh, Rd(addr)));
                Cycles += odd ? 5 : 4;
            }
            else if (!odd)
            {
                int addr = Fetch16();
                Wr(addr, Shift(sh, Rd(addr)));
                Cycles += 5;
            }
            else
            {
                A = Shift(sh, A);
                Cycles += 2;
            }
            return (int)(Cycles - start);
        }
        Exec(op);
        return (int)(Cycles - start);
    }

    private void Exec(int op)
    {
        int a, d, t, w;
        switch (op)
        {
            case 0x00: Cycles += 2; break;                                                              // NOP
            case 0x0F:                                                                                  // BRK
                PushPc(); Push(PSW); B = true; I = false;
                PC = Rd(0xFFDE) | (Rd(0xFFDF) << 8); Cycles += 8; break;
            case 0x0A: case 0x2A: case 0x4A: case 0x6A: case 0x8A: case 0xAA: case 0xCA: case 0xEA:   // 1비트 메모리
                {
                    w = Fetch16(); a = w & 0x1FFF; int bit = w >> 13;
                    bool m = ((Rd(a) >> bit) & 1) != 0;
                    switch (op)
                    {
                        case 0x0A: C |= m; Cycles += 5; break;
                        case 0x2A: C |= !m; Cycles += 5; break;
                        case 0x4A: C &= m; Cycles += 4; break;
                        case 0x6A: C &= !m; Cycles += 4; break;
                        case 0x8A: C ^= m; Cycles += 5; break;
                        case 0xAA: C = m; Cycles += 4; break;
                        case 0xCA: { int v = Rd(a); Wr(a, C ? v | (1 << bit) : v & ~(1 << bit)); Cycles += 6; break; }
                        default: Wr(a, Rd(a) ^ (1 << bit)); Cycles += 5; break;
                    }
                    break;
                }
            case 0x0D: Push(PSW); Cycles += 4; break;
            case 0x2D: Push(A); Cycles += 4; break;
            case 0x4D: Push(X); Cycles += 4; break;
            case 0x6D: Push(Y); Cycles += 4; break;
            case 0x8E: PSW = Pop(); Cycles += 4; break;
            case 0xAE: A = Pop(); Cycles += 4; break;
            case 0xCE: X = Pop(); Cycles += 4; break;
            case 0xEE: Y = Pop(); Cycles += 4; break;
            case 0x0E: case 0x4E:                                                                       // TSET1 · TCLR1 !a
                a = Fetch16(); t = Rd(a);
                NZ((A - t) & 0xFF);
                Wr(a, op == 0x0E ? t | A : t & ~A);
                Cycles += 6; break;

            // ---- 가지
            case 0x10: Cycles += 2; Branch(!N); break;
            case 0x30: Cycles += 2; Branch(N); break;
            case 0x50: Cycles += 2; Branch(!V); break;
            case 0x70: Cycles += 2; Branch(V); break;
            case 0x90: Cycles += 2; Branch(!C); break;
            case 0xB0: Cycles += 2; Branch(C); break;
            case 0xD0: Cycles += 2; Branch(!Z); break;
            case 0xF0: Cycles += 2; Branch(Z); break;
            case 0x2F: Cycles += 2; Branch(true); break;                                               // BRA
            case 0x2E: d = Fetch(); t = RdDp(d); Cycles += 5; Branch(A != t); break;                   // CBNE d,r
            case 0xDE: d = Fetch(); t = RdDp(d + X); Cycles += 6; Branch(A != t); break;               // CBNE d+X,r
            case 0x6E: d = Fetch(); t = (RdDp(d) - 1) & 0xFF; WrDp(d, t); Cycles += 5; Branch(t != 0); break;   // DBNZ d,r
            case 0xFE: Y = (Y - 1) & 0xFF; Cycles += 4; Branch(Y != 0); break;                         // DBNZ Y,r

            // ---- 뛰기 · 부르기
            case 0x5F: PC = Fetch16(); Cycles += 3; break;
            case 0x1F: a = (Fetch16() + X) & 0xFFFF; PC = Rd(a) | (Rd(a + 1) << 8); Cycles += 6; break;
            case 0x3F: a = Fetch16(); PushPc(); PC = a; Cycles += 8; break;
            case 0x4F: a = Fetch(); PushPc(); PC = 0xFF00 | a; Cycles += 6; break;                     // PCALL
            case 0x6F: PopPc(); Cycles += 5; break;
            case 0x7F: PSW = Pop(); PopPc(); Cycles += 6; break;

            // ---- 깃발
            case 0x20: P = false; Cycles += 2; break;
            case 0x40: P = true; Cycles += 2; break;
            case 0x60: C = false; Cycles += 2; break;
            case 0x80: C = true; Cycles += 2; break;
            case 0xA0: I = true; Cycles += 3; break;
            case 0xC0: I = false; Cycles += 3; break;
            case 0xE0: V = false; H = false; Cycles += 2; break;
            case 0xED: C = !C; Cycles += 3; break;
            case 0xEF: case 0xFF: Stopped = true; Cycles += 3; break;

            // ---- 16비트
            case 0x1A: case 0x3A:                                                                       // DECW · INCW d
                d = Fetch(); w = Rd16Dp(d);
                w = (w + (op == 0x3A ? 1 : -1)) & 0xFFFF;
                WrDp(d, w & 0xFF); WrDp((d + 1) & 0xFF, w >> 8);
                N = (w & 0x8000) != 0; Z = w == 0; Cycles += 6; break;
            case 0x5A:                                                                                  // CMPW YA,d
                { w = Rd16Dp(Fetch()); int r = ((Y << 8) | A) - w; C = r >= 0; N = (r & 0x8000) != 0; Z = (r & 0xFFFF) == 0; Cycles += 4; break; }
            case 0x7A: case 0x9A:                                                                       // ADDW · SUBW YA,d
                {
                    w = Rd16Dp(Fetch());
                    if (op == 0x7A) { C = false; A = Adc(A, w & 0xFF); Y = Adc(Y, w >> 8); }
                    else { C = true; A = Sbc(A, w & 0xFF); Y = Sbc(Y, w >> 8); }
                    Z = A == 0 && Y == 0; N = (Y & 0x80) != 0;
                    Cycles += 5; break;
                }
            case 0xBA: w = Rd16Dp(Fetch()); A = w & 0xFF; Y = w >> 8; N = (Y & 0x80) != 0; Z = w == 0; Cycles += 5; break;   // MOVW YA,d
            case 0xDA: d = Fetch(); WrDp(d, A); WrDp((d + 1) & 0xFF, Y); Cycles += 5; break;          // MOVW d,YA
            case 0xCF: w = Y * A; A = w & 0xFF; Y = w >> 8; NZ(Y); Cycles += 9; break;                 // MUL YA
            case 0x9E:                                                                                  // DIV YA,X
                {
                    int ya = (Y << 8) | A;
                    V = Y >= X;
                    H = (Y & 15) >= (X & 15);
                    if (Y < X * 2) { if (X != 0) { A = ya / X; Y = ya - A * X; } else { A = 0xFF; Y = ya & 0xFF; } }
                    else { A = 255 - (ya - X * 0x200) / (256 - X); Y = X + (ya - X * 0x200) % (256 - X); }
                    A &= 0xFF; Y &= 0xFF;
                    NZ(A);
                    Cycles += 12; break;
                }
            case 0xDF:                                                                                  // DAA
                if (C || A > 0x99) { A += 0x60; C = true; }
                if (H || (A & 0x0F) > 9) A += 6;
                A &= 0xFF; NZ(A); Cycles += 3; break;
            case 0xBE:                                                                                  // DAS
                if (!C || A > 0x99) { A -= 0x60; C = false; }
                if (!H || (A & 0x0F) > 9) A -= 6;
                A &= 0xFF; NZ(A); Cycles += 3; break;
            case 0x9F: A = ((A >> 4) | (A << 4)) & 0xFF; NZ(A); Cycles += 5; break;                    // XCN

            // ---- X · Y 비교 · 늘이기
            case 0xC8: Cmp(X, Fetch()); Cycles += 2; break;
            case 0x3E: Cmp(X, RdDp(Fetch())); Cycles += 3; break;
            case 0x1E: Cmp(X, Rd(Fetch16())); Cycles += 4; break;
            case 0xAD: Cmp(Y, Fetch()); Cycles += 2; break;
            case 0x7E: Cmp(Y, RdDp(Fetch())); Cycles += 3; break;
            case 0x5E: Cmp(Y, Rd(Fetch16())); Cycles += 4; break;
            case 0x3D: X = (X + 1) & 0xFF; NZ(X); Cycles += 2; break;
            case 0x1D: X = (X - 1) & 0xFF; NZ(X); Cycles += 2; break;
            case 0xFC: Y = (Y + 1) & 0xFF; NZ(Y); Cycles += 2; break;
            case 0xDC: Y = (Y - 1) & 0xFF; NZ(Y); Cycles += 2; break;

            // ---- MOV: 읽기 (N · Z)
            case 0xE8: A = Fetch(); NZ(A); Cycles += 2; break;
            case 0xE6: A = RdDp(X); NZ(A); Cycles += 3; break;
            case 0xBF: A = RdDp(X); X = (X + 1) & 0xFF; NZ(A); Cycles += 4; break;
            case 0xE4: A = RdDp(Fetch()); NZ(A); Cycles += 3; break;
            case 0xF4: A = RdDp(Fetch() + X); NZ(A); Cycles += 4; break;
            case 0xE5: A = Rd(Fetch16()); NZ(A); Cycles += 4; break;
            case 0xF5: A = Rd(Fetch16() + X); NZ(A); Cycles += 5; break;
            case 0xF6: A = Rd(Fetch16() + Y); NZ(A); Cycles += 5; break;
            case 0xE7: A = Rd(AIndX()); NZ(A); Cycles += 6; break;
            case 0xF7: A = Rd(AIndY()); NZ(A); Cycles += 6; break;
            case 0xCD: X = Fetch(); NZ(X); Cycles += 2; break;
            case 0xF8: X = RdDp(Fetch()); NZ(X); Cycles += 3; break;
            case 0xF9: X = RdDp(Fetch() + Y); NZ(X); Cycles += 4; break;
            case 0xE9: X = Rd(Fetch16()); NZ(X); Cycles += 4; break;
            case 0x8D: Y = Fetch(); NZ(Y); Cycles += 2; break;
            case 0xEB: Y = RdDp(Fetch()); NZ(Y); Cycles += 3; break;
            case 0xFB: Y = RdDp(Fetch() + X); NZ(Y); Cycles += 4; break;
            case 0xEC: Y = Rd(Fetch16()); NZ(Y); Cycles += 4; break;

            // ---- MOV: 쓰기 (깃발 그대로)
            case 0xC6: WrDp(X, A); Cycles += 4; break;
            case 0xAF: WrDp(X, A); X = (X + 1) & 0xFF; Cycles += 4; break;
            case 0xC4: WrDp(Fetch(), A); Cycles += 4; break;
            case 0xD4: WrDp(Fetch() + X, A); Cycles += 5; break;
            case 0xC5: Wr(Fetch16(), A); Cycles += 5; break;
            case 0xD5: Wr(Fetch16() + X, A); Cycles += 6; break;
            case 0xD6: Wr(Fetch16() + Y, A); Cycles += 6; break;
            case 0xC7: Wr(AIndX(), A); Cycles += 7; break;
            case 0xD7: Wr(AIndY(), A); Cycles += 7; break;
            case 0xD8: WrDp(Fetch(), X); Cycles += 4; break;
            case 0xD9: WrDp(Fetch() + Y, X); Cycles += 5; break;
            case 0xC9: Wr(Fetch16(), X); Cycles += 5; break;
            case 0xCB: WrDp(Fetch(), Y); Cycles += 4; break;
            case 0xDB: WrDp(Fetch() + X, Y); Cycles += 5; break;
            case 0xCC: Wr(Fetch16(), Y); Cycles += 5; break;
            case 0xFA: { int s = RdDp(Fetch()); WrDp(Fetch(), s); Cycles += 5; break; }                // MOV dd,ds
            case 0x8F: { int imm = Fetch(); WrDp(Fetch(), imm); Cycles += 5; break; }                   // MOV d,#i

            // ---- MOV: 레지스터끼리
            case 0x7D: A = X; NZ(A); Cycles += 2; break;
            case 0xDD: A = Y; NZ(A); Cycles += 2; break;
            case 0x5D: X = A; NZ(X); Cycles += 2; break;
            case 0xFD: Y = A; NZ(Y); Cycles += 2; break;
            case 0x9D: X = SP; NZ(X); Cycles += 2; break;
            case 0xBD: SP = X; Cycles += 2; break;

            default: UnknownOps++; Cycles += 2; break;
        }
    }
}
