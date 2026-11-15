using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>Z80이 보는 메모리 · I/O.</summary>
public interface IZ80Bus
{
    byte Read(int addr);
    void Write(int addr, byte value);
    byte In(int port);
    void Out(int port, byte value);
}

/// <summary>
/// Zilog Z80 CPU (문서화된 명령 전부 + 자주 쓰는 비문서 명령: IXH/IXL, SLL, DDCB 결과 레지스터 복사).
/// 사운드 드라이버(KSS)를 돌리기 위한 것이라 T-state는 명령마다 대략 값 (<see cref="Cycles"/>). 인터럽트는 드라이버를 직접 CALL해서 대신한다.
/// 명령 해석은 x/y/z/p/q 비트 나눔 방식 ("Decoding Z80 Opcodes")을 따른다.
/// </summary>
public sealed class Z80
{
    public int A, F, B, C, D, E, H, L;
    public int A2, F2, B2, C2, D2, E2, H2, L2;
    public int IX, IY, SP, PC, I, R;
    public bool Iff1, Iff2, Halted;
    public int Im;
    /// <summary>지금까지 지난 대략의 T-state.</summary>
    public long Cycles;

    private readonly IZ80Bus bus;
    // 0 = HL, 1 = IX, 2 = IY (DD · FD 접두어)
    private int idx;
    private int disp;
    private bool dispRead;

    private const int FS = 0x80, FZ = 0x40, FY = 0x20, FH = 0x10, FX = 0x08, FPV = 0x04, FN = 0x02, FC = 0x01;
    private static readonly byte[] Sz = new byte[256], Szp = new byte[256];

    static Z80()
    {
        for (int i = 0; i < 256; i++)
        {
            int f = (i & 0x80) | (i & (FY | FX)) | (i == 0 ? FZ : 0);
            Sz[i] = (byte)f;
            int bits = 0; for (int b = 0; b < 8; b++) bits += (i >> b) & 1;
            Szp[i] = (byte)(f | ((bits & 1) == 0 ? FPV : 0));
        }
    }

    public Z80(IZ80Bus bus) { this.bus = bus; Reset(); }

    public void Reset()
    {
        A = F = B = C = D = E = H = L = 0xFF;
        A2 = F2 = B2 = C2 = D2 = E2 = H2 = L2 = 0;
        IX = IY = 0xFFFF; SP = 0xFFFF; PC = 0; I = R = 0;
        Iff1 = Iff2 = Halted = false; Im = 0; Cycles = 0;
    }

    // ------------------------------------------------------------ 레지스터 쌍

    public int BC { get { return (B << 8) | C; } set { B = (value >> 8) & 0xFF; C = value & 0xFF; } }
    public int DE { get { return (D << 8) | E; } set { D = (value >> 8) & 0xFF; E = value & 0xFF; } }
    public int HL { get { return (H << 8) | L; } set { H = (value >> 8) & 0xFF; L = value & 0xFF; } }
    public int AF { get { return (A << 8) | F; } set { A = (value >> 8) & 0xFF; F = value & 0xFF; } }

    /// <summary>접두어를 따른 HL / IX / IY.</summary>
    private int XHL
    {
        get { return idx == 0 ? HL : idx == 1 ? IX : IY; }
        set { if (idx == 0) HL = value; else if (idx == 1) IX = value & 0xFFFF; else IY = value & 0xFFFF; }
    }

    private int Rp(int p)
    {
        switch (p) { case 0: return BC; case 1: return DE; case 2: return XHL; default: return SP; }
    }

    private void SetRp(int p, int v)
    {
        v &= 0xFFFF;
        switch (p) { case 0: BC = v; break; case 1: DE = v; break; case 2: XHL = v; break; default: SP = v; break; }
    }

    private int Rp2(int p) { return p == 3 ? AF : Rp(p); }
    private void SetRp2(int p, int v) { if (p == 3) AF = v & 0xFFFF; else SetRp(p, v); }

    // ------------------------------------------------------------ 메모리

    private int Rd(int a) { return bus.Read(a & 0xFFFF); }
    private void Wr(int a, int v) { bus.Write(a & 0xFFFF, (byte)v); }
    private int Fetch() { int v = Rd(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int lo = Fetch(); return lo | (Fetch() << 8); }
    private int Rd16(int a) { return Rd(a) | (Rd(a + 1) << 8); }
    private void Wr16(int a, int v) { Wr(a, v & 0xFF); Wr(a + 1, (v >> 8) & 0xFF); }
    private void Push(int v) { SP = (SP - 2) & 0xFFFF; Wr16(SP, v); }
    private int Pop() { int v = Rd16(SP); SP = (SP + 2) & 0xFFFF; return v; }

    /// <summary>(HL) 또는 (IX+d) 주소. d는 한 명령에서 한 번만 읽는다.</summary>
    private int MemAddr()
    {
        if (idx == 0) return HL;
        if (!dispRead) { disp = (sbyte)Fetch(); dispRead = true; }
        return (XHL + disp) & 0xFFFF;
    }

    /// <summary>8비트 레지스터 r (6 = 메모리). useIdx = H · L을 IXH · IXL로 볼지 ((IX+d)와 함께 쓰는 명령은 아님).</summary>
    private int GetR(int r, bool useIdx = true)
    {
        switch (r)
        {
            case 0: return B; case 1: return C; case 2: return D; case 3: return E;
            case 4: return useIdx && idx != 0 ? (XHL >> 8) & 0xFF : H;
            case 5: return useIdx && idx != 0 ? XHL & 0xFF : L;
            case 6: return Rd(MemAddr());
            default: return A;
        }
    }

    private void SetR(int r, int v, bool useIdx = true)
    {
        v &= 0xFF;
        switch (r)
        {
            case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break;
            case 4: if (useIdx && idx != 0) XHL = (XHL & 0x00FF) | (v << 8); else H = v; break;
            case 5: if (useIdx && idx != 0) XHL = (XHL & 0xFF00) | v; else L = v; break;
            case 6: Wr(MemAddr(), v); break;
            default: A = v; break;
        }
    }

    // ------------------------------------------------------------ 산술

    private void Add8(int v, int carry)
    {
        int r = A + v + carry;
        F = Sz[r & 0xFF] | ((A ^ v ^ r) & FH) | (((A ^ ~v) & (A ^ r) & 0x80) != 0 ? FPV : 0) | (r > 0xFF ? FC : 0);
        A = r & 0xFF;
    }

    private int Sub8(int v, int carry, bool store)
    {
        int r = A - v - carry;
        int f = Sz[r & 0xFF] | ((A ^ v ^ r) & FH) | (((A ^ v) & (A ^ r) & 0x80) != 0 ? FPV : 0) | (r < 0 ? FC : 0) | FN;
        if (!store) f = (f & ~(FY | FX)) | (v & (FY | FX));
        F = f;
        if (store) A = r & 0xFF;
        return r & 0xFF;
    }

    private void Alu(int y, int v)
    {
        switch (y)
        {
            case 0: Add8(v, 0); break;
            case 1: Add8(v, F & FC); break;
            case 2: Sub8(v, 0, true); break;
            case 3: Sub8(v, F & FC, true); break;
            case 4: A &= v; F = Szp[A] | FH; break;
            case 5: A ^= v; F = Szp[A]; break;
            case 6: A |= v; F = Szp[A]; break;
            default: Sub8(v, 0, false); break;
        }
    }

    private int Inc8(int v)
    {
        int r = (v + 1) & 0xFF;
        F = (F & FC) | Sz[r] | (r == 0x80 ? FPV : 0) | ((r & 0x0F) == 0 ? FH : 0);
        return r;
    }

    private int Dec8(int v)
    {
        int r = (v - 1) & 0xFF;
        F = (F & FC) | Sz[r] | (r == 0x7F ? FPV : 0) | ((r & 0x0F) == 0x0F ? FH : 0) | FN;
        return r;
    }

    private int Add16(int a, int b)
    {
        int r = a + b;
        F = (F & (FS | FZ | FPV)) | (((a ^ b ^ r) >> 8) & FH) | ((r >> 8) & (FY | FX)) | (r > 0xFFFF ? FC : 0);
        return r & 0xFFFF;
    }

    private int Adc16(int a, int b)
    {
        int c = F & FC;
        int r = a + b + c;
        F = (((r & 0xFFFF) == 0) ? FZ : 0) | ((r >> 8) & (FS | FY | FX)) | (((a ^ b ^ r) >> 8) & FH)
            | (((a ^ ~b) & (a ^ r) & 0x8000) != 0 ? FPV : 0) | (r > 0xFFFF ? FC : 0);
        return r & 0xFFFF;
    }

    private int Sbc16(int a, int b)
    {
        int c = F & FC;
        int r = a - b - c;
        F = (((r & 0xFFFF) == 0) ? FZ : 0) | ((r >> 8) & (FS | FY | FX)) | (((a ^ b ^ r) >> 8) & FH)
            | (((a ^ b) & (a ^ r) & 0x8000) != 0 ? FPV : 0) | (r < 0 ? FC : 0) | FN;
        return r & 0xFFFF;
    }

    private int Rot(int y, int v)
    {
        int c, r;
        switch (y)
        {
            case 0: c = v >> 7; r = ((v << 1) | c) & 0xFF; break;                 // RLC
            case 1: c = v & 1; r = (v >> 1) | (c << 7); break;                     // RRC
            case 2: c = v >> 7; r = ((v << 1) | (F & FC)) & 0xFF; break;          // RL
            case 3: c = v & 1; r = (v >> 1) | ((F & FC) << 7); break;             // RR
            case 4: c = v >> 7; r = (v << 1) & 0xFF; break;                        // SLA
            case 5: c = v & 1; r = (v >> 1) | (v & 0x80); break;                   // SRA
            case 6: c = v >> 7; r = ((v << 1) | 1) & 0xFF; break;                  // SLL (비문서)
            default: c = v & 1; r = v >> 1; break;                                  // SRL
        }
        F = Szp[r] | c;
        return r;
    }

    private void Daa()
    {
        int a = A, cf = F & FC, hf = F & FH, nf = F & FN;
        int diff = 0;
        if (hf != 0 || (a & 0x0F) > 9) diff |= 0x06;
        if (cf != 0 || a > 0x99) diff |= 0x60;
        int r = nf != 0 ? a - diff : a + diff;
        int newH = nf != 0 ? (hf != 0 && (a & 0x0F) < 6 ? FH : 0) : ((a & 0x0F) > 9 ? FH : 0);
        F = Szp[r & 0xFF] | newH | nf | (cf != 0 || a > 0x99 ? FC : 0);
        A = r & 0xFF;
    }

    private bool Cond(int y)
    {
        switch (y)
        {
            case 0: return (F & FZ) == 0; case 1: return (F & FZ) != 0;
            case 2: return (F & FC) == 0; case 3: return (F & FC) != 0;
            case 4: return (F & FPV) == 0; case 5: return (F & FPV) != 0;
            case 6: return (F & FS) == 0; default: return (F & FS) != 0;
        }
    }

    // ------------------------------------------------------------ 실행

    /// <summary>명령 하나 실행. 반환 = 대략의 T-state.</summary>
    public int Step()
    {
        if (Halted) { Cycles += 4; return 4; }
        idx = 0; dispRead = false;
        int op = Fetch();
        R = (R & 0x80) | ((R + 1) & 0x7F);
        int t = 4;
        while (op == 0xDD || op == 0xFD)
        {
            idx = op == 0xDD ? 1 : 2;
            op = Fetch();
            t += 4;
        }
        if (op == 0xCB) t += ExecCb();
        else if (op == 0xED) { idx = 0; t += ExecEd(); }
        else t += ExecMain(op);
        Cycles += t;
        return t;
    }

    private int ExecMain(int op)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        switch (y)
                        {
                            case 0: return 0;                                                 // NOP
                            case 1: { int a = AF; AF = (A2 << 8) | F2; A2 = (a >> 8) & 0xFF; F2 = a & 0xFF; return 0; }
                            case 2: { int d = (sbyte)Fetch(); B = (B - 1) & 0xFF; if (B != 0) { PC = (PC + d) & 0xFFFF; return 9; } return 4; }
                            case 3: { int d = (sbyte)Fetch(); PC = (PC + d) & 0xFFFF; return 8; }
                            default: { int d = (sbyte)Fetch(); if (Cond(y - 4)) { PC = (PC + d) & 0xFFFF; return 8; } return 3; }
                        }
                    case 1:
                        if (q == 0) { SetRp(p, Fetch16()); return 6; }
                        XHL = Add16(XHL, Rp(p)); return 7;
                    case 2:
                        switch (y)
                        {
                            case 0: Wr(BC, A); return 3;
                            case 1: A = Rd(BC); return 3;
                            case 2: Wr(DE, A); return 3;
                            case 3: A = Rd(DE); return 3;
                            case 4: Wr16(Fetch16(), XHL); return 12;
                            case 5: XHL = Rd16(Fetch16()); return 12;
                            case 6: Wr(Fetch16(), A); return 9;
                            default: A = Rd(Fetch16()); return 9;
                        }
                    case 3:
                        SetRp(p, Rp(p) + (q == 0 ? 1 : -1)); return 2;
                    case 4:
                        if (y == 6) { int a = MemAddr(); Wr(a, Inc8(Rd(a))); return 7; }
                        SetR(y, Inc8(GetR(y))); return 0;
                    case 5:
                        if (y == 6) { int a = MemAddr(); Wr(a, Dec8(Rd(a))); return 7; }
                        SetR(y, Dec8(GetR(y))); return 0;
                    case 6:
                        if (y == 6) { int a = MemAddr(); Wr(a, Fetch()); return 6; }
                        SetR(y, Fetch()); return 3;
                    default:
                        switch (y)
                        {
                            case 0: { int c = A >> 7; A = ((A << 1) | c) & 0xFF; F = (F & (FS | FZ | FPV)) | (A & (FY | FX)) | c; return 0; }
                            case 1: { int c = A & 1; A = (A >> 1) | (c << 7); F = (F & (FS | FZ | FPV)) | (A & (FY | FX)) | c; return 0; }
                            case 2: { int c = A >> 7; A = ((A << 1) | (F & FC)) & 0xFF; F = (F & (FS | FZ | FPV)) | (A & (FY | FX)) | c; return 0; }
                            case 3: { int c = A & 1; A = (A >> 1) | ((F & FC) << 7); F = (F & (FS | FZ | FPV)) | (A & (FY | FX)) | c; return 0; }
                            case 4: Daa(); return 0;
                            case 5: A ^= 0xFF; F = (F & (FS | FZ | FPV | FC)) | FH | FN | (A & (FY | FX)); return 0;
                            case 6: F = (F & (FS | FZ | FPV)) | FC | (A & (FY | FX)); return 0;
                            default: F = (F & (FS | FZ | FPV)) | ((F & FC) != 0 ? FH : FC) | (A & (FY | FX)); return 0;
                        }
                }
            case 1:
                if (z == 6 && y == 6) { Halted = true; return 0; }
                // LD r, r' : (IX+d)가 있으면 다른 쪽 H · L은 그대로 H · L
                if (y == 6) { SetR(6, GetR(z, false)); return 3; }
                if (z == 6) { SetR(y, GetR(6), false); return 3; }
                SetR(y, GetR(z)); return 0;
            case 2:
                Alu(y, GetR(z)); return z == 6 ? 3 : 0;
            default:
                switch (z)
                {
                    case 0: if (Cond(y)) { PC = Pop(); return 7; } return 1;
                    case 1:
                        if (q == 0) { SetRp2(p, Pop()); return 6; }
                        switch (p)
                        {
                            case 0: PC = Pop(); return 6;
                            case 1: { Swap(ref B, ref B2); Swap(ref C, ref C2); Swap(ref D, ref D2); Swap(ref E, ref E2); Swap(ref H, ref H2); Swap(ref L, ref L2); return 0; }
                            case 2: PC = XHL; return 0;
                            default: SP = XHL; return 2;
                        }
                    case 2: { int a = Fetch16(); if (Cond(y)) PC = a; return 6; }
                    case 3:
                        switch (y)
                        {
                            case 0: PC = Fetch16(); return 6;
                            case 1: return 0;   // CB (위에서 처리)
                            case 2: bus.Out(Fetch() | (A << 8), (byte)A); return 7;
                            case 3: A = bus.In(Fetch() | (A << 8)); return 7;
                            case 4: { int v = Rd16(SP); Wr16(SP, XHL); XHL = v; return 15; }
                            case 5: { int d = DE; DE = HL; HL = d; return 0; }
                            case 6: Iff1 = Iff2 = false; return 0;
                            default: Iff1 = Iff2 = true; return 0;
                        }
                    case 4: { int a = Fetch16(); if (Cond(y)) { Push(PC); PC = a; return 13; } return 6; }
                    case 5:
                        if (q == 0) { Push(Rp2(p)); return 7; }
                        if (p == 0) { int a = Fetch16(); Push(PC); PC = a; return 13; }
                        return 0;   // DD · ED · FD (위에서 처리)
                    case 6: Alu(y, Fetch()); return 3;
                    default: Push(PC); PC = y * 8; return 7;
                }
        }
    }

    private static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }

    private int ExecCb()
    {
        int addr = 0;
        if (idx != 0) { disp = (sbyte)Fetch(); dispRead = true; addr = (XHL + disp) & 0xFFFF; }
        int op = Fetch();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        bool mem = idx != 0 || z == 6;
        if (mem && idx == 0) addr = HL;
        int v = mem ? Rd(addr) : GetR(z, false);
        int r;
        switch (x)
        {
            case 0: r = Rot(y, v); break;
            case 1:
                {
                    int bit = v & (1 << y);
                    F = (F & FC) | FH | (bit == 0 ? FZ | FPV : 0) | (y == 7 && bit != 0 ? FS : 0) | ((mem ? addr >> 8 : v) & (FY | FX));
                    return mem ? 8 : 4;
                }
            case 2: r = v & ~(1 << y); break;
            default: r = v | (1 << y); break;
        }
        if (mem) { Wr(addr, r); if (idx != 0 && z != 6) SetR(z, r, false); return 11; }
        SetR(z, r, false);
        return 4;
    }

    private int ExecEd()
    {
        int op = Fetch();
        R = (R & 0x80) | ((R + 1) & 0x7F);
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        if (x == 1)
        {
            switch (z)
            {
                case 0: { int v = bus.In(BC); if (y != 6) SetR(y, v, false); F = (F & FC) | Szp[v]; return 8; }
                case 1: bus.Out(BC, (byte)(y == 6 ? 0 : GetR(y, false))); return 8;
                case 2: HL = q == 0 ? Sbc16(HL, Rp(p)) : Adc16(HL, Rp(p)); return 11;
                case 3: { int a = Fetch16(); if (q == 0) Wr16(a, Rp(p)); else SetRp(p, Rd16(a)); return 16; }
                case 4: { int a = A; A = 0; Sub8(a, 0, true); return 4; }
                case 5: PC = Pop(); Iff1 = Iff2; return 10;
                case 6: Im = y & 3; Im = Im == 1 ? 0 : Im == 2 ? 1 : Im == 3 ? 2 : 0; return 4;
                default:
                    switch (y)
                    {
                        case 0: I = A; return 5;
                        case 1: R = A; return 5;
                        case 2: A = I; F = (F & FC) | Sz[A] | (Iff2 ? FPV : 0); return 5;
                        case 3: A = R; F = (F & FC) | Sz[A] | (Iff2 ? FPV : 0); return 5;
                        case 4:   // RRD
                            {
                                int m = Rd(HL);
                                Wr(HL, ((A << 4) | (m >> 4)) & 0xFF);
                                A = (A & 0xF0) | (m & 0x0F);
                                F = (F & FC) | Szp[A];
                                return 14;
                            }
                        case 5:   // RLD
                            {
                                int m = Rd(HL);
                                Wr(HL, ((m << 4) | (A & 0x0F)) & 0xFF);
                                A = (A & 0xF0) | (m >> 4);
                                F = (F & FC) | Szp[A];
                                return 14;
                            }
                        default: return 4;
                    }
            }
        }
        if (x == 2 && z <= 3 && y >= 4)
        {
            bool inc = (y & 1) == 0, rep = y >= 6;
            int d = inc ? 1 : -1;
            switch (z)
            {
                case 0:   // LDI LDD LDIR LDDR
                    {
                        int v = Rd(HL);
                        Wr(DE, v);
                        HL = (HL + d) & 0xFFFF; DE = (DE + d) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
                        int n = v + A;
                        F = (F & (FS | FZ | FC)) | (BC != 0 ? FPV : 0) | (n & FX) | ((n << 4) & FY);
                        if (rep && BC != 0) { PC = (PC - 2) & 0xFFFF; return 17; }
                        return 12;
                    }
                case 1:   // CPI CPD CPIR CPDR
                    {
                        int v = Rd(HL);
                        int r = (A - v) & 0xFF;
                        int h = (A ^ v ^ r) & FH;
                        HL = (HL + d) & 0xFFFF; BC = (BC - 1) & 0xFFFF;
                        int n = r - (h != 0 ? 1 : 0);
                        F = (F & FC) | FN | (Sz[r] & (FS | FZ)) | h | (BC != 0 ? FPV : 0) | (n & FX) | ((n << 4) & FY);
                        if (rep && BC != 0 && r != 0) { PC = (PC - 2) & 0xFFFF; return 17; }
                        return 12;
                    }
                case 2:   // INI IND INIR INDR
                    {
                        int v = bus.In(BC);
                        Wr(HL, v);
                        HL = (HL + d) & 0xFFFF; B = (B - 1) & 0xFF;
                        F = Sz[B] | FN;
                        if (rep && B != 0) { PC = (PC - 2) & 0xFFFF; return 17; }
                        return 12;
                    }
                default:  // OUTI OUTD OTIR OTDR
                    {
                        int v = Rd(HL);
                        B = (B - 1) & 0xFF;
                        bus.Out(BC, (byte)v);
                        HL = (HL + d) & 0xFFFF;
                        F = Sz[B] | FN;
                        if (rep && B != 0) { PC = (PC - 2) & 0xFFFF; return 17; }
                        return 12;
                    }
            }
        }
        return 4;   // 그 밖의 ED = NOP
    }
}
