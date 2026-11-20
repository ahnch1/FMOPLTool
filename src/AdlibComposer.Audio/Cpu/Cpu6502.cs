using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>6502 계열 CPU가 보는 메모리 (주소 = 실제 주소: 2A03은 16비트, HuC6280은 MPR로 바꾼 21비트).</summary>
public interface ICpu6502Bus
{
    byte Read(int addr);
    void Write(int addr, byte value);
}

/// <summary>
/// 6502 계열 CPU 하나로 두 가지:
/// <list type="bullet">
/// <item><b>2A03</b> (NES · NSF): NMOS 6502, 10진 모드 없음. 자주 쓰는 비공식 명령(LAX · SAX · DCP · ISB · SLO · RLA · SRE · RRA · NOP 변형)도 실행</item>
/// <item><b>HuC6280</b> (PC엔진 · HES): 65C02 + 메모리 매퍼(MPR 8개, 8KB 페이지 → 21비트 주소), 블록 전송(TII · TDD · TIN · TIA · TAI),
/// ST0/1/2(VDC), SAX · SAY · SXY · CLA · CLX · CLY · BSR · TST · TAM · TMA · CSL · CSH, T 깃발(SET: 다음 ADC · AND · EOR · ORA가 (zp,X) 메모리에)</item>
/// </list>
/// 사이클 수는 6502 표를 따른다 (HuC6280도 같은 표 — 음악 드라이버 시간에는 충분).
/// </summary>
public sealed class Cpu6502
{
    public enum Variant { Nes2A03, HuC6280 }

    public readonly Variant Kind;
    private readonly ICpu6502Bus bus;
    private readonly bool huc;

    public int A, X, Y, S = 0xFD, PC;
    // 깃발
    public bool C, Z, I = true, D, V, N, T;
    public long Cycles;
    /// <summary>HuC6280 MPR (8KB 페이지 번호). 2A03은 쓰지 않는다.</summary>
    public readonly byte[] Mpr = new byte[8];
    /// <summary>알 수 없는 명령 수 (시험 · 로그용).</summary>
    public int UnknownOps;
    /// <summary>JAM(KIL) 등으로 멈춤.</summary>
    public bool Jammed;
    /// <summary>NMOS 6502 10진 모드 (ADC · SBC, SED 뒤) — 2A03은 없어서 기본 꺼짐, 아케이드 6502는 켠다.</summary>
    public bool DecimalMode;
    /// <summary>명령 바이트 풀기 (주소, 바이트 → 명령): 데이터 이스트 DECO222처럼 명령 바이트만 암호인 CPU. null = 그대로.</summary>
    public Func<int, int, int> OpcodeMap;

    public Cpu6502(ICpu6502Bus bus, Variant kind)
    {
        this.bus = bus;
        Kind = kind;
        huc = kind == Variant.HuC6280;
    }

    // ------------------------------------------------------------ 메모리

    public int Physical(int a)
    {
        a &= 0xFFFF;
        return huc ? (Mpr[a >> 13] << 13) | (a & 0x1FFF) : a;
    }

    public byte Read(int a) { return bus.Read(Physical(a)); }
    public void Write(int a, int v) { bus.Write(Physical(a), (byte)v); }
    private int Read16(int a) { return Read(a) | (Read((a + 1) & 0xFFFF) << 8); }
    /// <summary>0페이지 (HuC6280은 $2000 페이지).</summary>
    private int ZpBase { get { return huc ? 0x2000 : 0; } }
    private int StackBase { get { return huc ? 0x2100 : 0x100; } }
    private byte Zp(int a) { return Read(ZpBase | (a & 0xFF)); }
    private int Zp16(int a) { return Zp(a) | (Zp((a + 1) & 0xFF) << 8); }

    public void Push(int v) { Write(StackBase | S, v); S = (S - 1) & 0xFF; }
    public int Pop() { S = (S + 1) & 0xFF; return Read(StackBase | S); }

    public int P
    {
        get
        {
            return (C ? 1 : 0) | (Z ? 2 : 0) | (I ? 4 : 0) | (D ? 8 : 0) | (huc ? (T ? 0x20 : 0) : 0x20) | (V ? 0x40 : 0) | (N ? 0x80 : 0) | 0x10;
        }
        set
        {
            C = (value & 1) != 0; Z = (value & 2) != 0; I = (value & 4) != 0; D = (value & 8) != 0;
            V = (value & 0x40) != 0; N = (value & 0x80) != 0;
            if (huc) T = (value & 0x20) != 0;
        }
    }

    /// <summary>인터럽트 (IRQ · NMI): PC · P를 쌓고 벡터로. B 깃발은 0.</summary>
    public void Interrupt(int vector)
    {
        Push(PC >> 8); Push(PC & 0xFF);
        Push(P & ~0x10);
        I = true; D = huc ? false : D; T = false;
        PC = Read16(vector);
        Cycles += 7;
    }

    // ------------------------------------------------------------ 주소 방식

    private int Imm() { int a = PC; PC = (PC + 1) & 0xFFFF; return a; }
    private int Fetch() { int v = Read(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int v = Read16(PC); PC = (PC + 2) & 0xFFFF; return v; }
    private int AZp() { return ZpBase | Fetch(); }
    private int AZpX() { return ZpBase | ((Fetch() + X) & 0xFF); }
    private int AZpY() { return ZpBase | ((Fetch() + Y) & 0xFF); }
    private int AAbs() { return Fetch16(); }
    private int AAbsX(bool penalty = true) { int b = Fetch16(); int a = (b + X) & 0xFFFF; if (penalty && ((a ^ b) & 0xFF00) != 0) Cycles++; return a; }
    private int AAbsY(bool penalty = true) { int b = Fetch16(); int a = (b + Y) & 0xFFFF; if (penalty && ((a ^ b) & 0xFF00) != 0) Cycles++; return a; }
    private int AIndX() { return Zp16((Fetch() + X) & 0xFF); }
    private int AIndY(bool penalty = true) { int b = Zp16(Fetch()); int a = (b + Y) & 0xFFFF; if (penalty && ((a ^ b) & 0xFF00) != 0) Cycles++; return a; }
    private int AInd() { return Zp16(Fetch()); }   // 65C02 (zp)

    // ------------------------------------------------------------ 연산

    private void NZ(int v) { Z = (v & 0xFF) == 0; N = (v & 0x80) != 0; }

    private void Adc(int m)
    {
        if (D && (huc || DecimalMode))
        {
            int lo = (A & 0x0F) + (m & 0x0F) + (C ? 1 : 0);
            int hi = (A >> 4) + (m >> 4);
            if (lo > 9) { lo += 6; hi++; }
            if (hi > 9) hi += 6;
            C = hi > 15;
            A = ((hi << 4) | (lo & 0x0F)) & 0xFF;
            NZ(A);
            Cycles++;
            return;
        }
        int r = A + m + (C ? 1 : 0);
        V = (~(A ^ m) & (A ^ r) & 0x80) != 0;
        C = r > 0xFF;
        A = r & 0xFF;
        NZ(A);
    }

    private void Sbc(int m)
    {
        if (D && (huc || DecimalMode))
        {
            int lo = (A & 0x0F) - (m & 0x0F) - (C ? 0 : 1);
            int hi = (A >> 4) - (m >> 4);
            if (lo < 0) { lo -= 6; hi--; }
            if (hi < 0) hi -= 6;
            C = hi >= 0;
            A = ((hi << 4) | (lo & 0x0F)) & 0xFF;
            NZ(A);
            Cycles++;
            return;
        }
        Adc(m ^ 0xFF);
    }

    private void Cmp(int r, int m) { int t = r - m; C = t >= 0; NZ(t); }
    private void Bit(int m) { Z = (A & m) == 0; N = (m & 0x80) != 0; V = (m & 0x40) != 0; }
    private int Asl(int v) { C = (v & 0x80) != 0; v = (v << 1) & 0xFF; NZ(v); return v; }
    private int Lsr(int v) { C = (v & 1) != 0; v >>= 1; NZ(v); return v; }
    private int Rol(int v) { int c = C ? 1 : 0; C = (v & 0x80) != 0; v = ((v << 1) | c) & 0xFF; NZ(v); return v; }
    private int Ror(int v) { int c = C ? 0x80 : 0; C = (v & 1) != 0; v = (v >> 1) | c; NZ(v); return v; }

    private void Branch(bool cond)
    {
        int off = (sbyte)Fetch();
        if (!cond) return;
        int t = (PC + off) & 0xFFFF;
        Cycles += ((t ^ PC) & 0xFF00) != 0 ? 2 : 1;
        PC = t;
    }

    /// <summary>읽고-고치고-쓰기: 0 ASL, 1 LSR, 2 ROL, 3 ROR, 4 INC, 5 DEC (델리게이트 없이).</summary>
    private void Rmw(int a, int kind)
    {
        int v = Read(a);
        switch (kind)
        {
            case 0: v = Asl(v); break;
            case 1: v = Lsr(v); break;
            case 2: v = Rol(v); break;
            case 3: v = Ror(v); break;
            case 4: v = (v + 1) & 0xFF; NZ(v); break;
            default: v = (v - 1) & 0xFF; NZ(v); break;
        }
        Write(a, v);
    }

    // T 깃발 (HuC6280): ADC · AND · EOR · ORA가 A 대신 (zp,X) 메모리를 고친다
    private void Logic(int m, int op)
    {
        if (huc && T)
        {
            int at = ZpBase | X;
            int a0 = Read(at), r;
            if (op == 0) r = a0 & m; else if (op == 1) r = a0 | m; else r = a0 ^ m;
            Write(at, r); NZ(r); Cycles += 3;
            return;
        }
        if (op == 0) A &= m; else if (op == 1) A |= m; else A ^= m;
        NZ(A);
    }

    private void AdcT(int m)
    {
        if (huc && T)
        {
            int at = ZpBase | X;
            int saved = A;
            A = Read(at); Adc(m); Write(at, A); A = saved;
            Cycles += 3;
            return;
        }
        Adc(m);
    }

    // ------------------------------------------------------------ 한 명령

    private static readonly byte[] BaseCycles =
    {
        7,6,2,8,3,3,5,5,3,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,3,2,2,2,3,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,5,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4, 2,6,2,6,4,4,4,4,2,5,2,5,5,5,5,5,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4, 2,5,2,5,4,4,4,4,2,4,2,4,4,4,4,4,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
    };

    /// <summary>명령 하나를 실행하고 쓴 사이클을 돌려준다.</summary>
    public int Step()
    {
        if (Jammed) { Cycles += 2; return 2; }
        long start = Cycles;
        int opAt = PC;
        int op = Fetch();
        if (OpcodeMap != null) op = OpcodeMap(opAt, op) & 0xFF;
        Cycles += BaseCycles[op];
        bool wasT = T;
        T = false;
        if (huc) { if (!ExecHuc(op, wasT)) Exec(op, wasT); }
        else Exec(op, false);
        return (int)(Cycles - start);
    }

    private void Exec(int op, bool t)
    {
        T = t;   // Logic · AdcT가 본다
        ExecCore(op);
        T = false;
    }

    private void ExecCore(int op)
    {
        int a;
        switch (op)
        {
            // ---- 불러오기 · 저장
            case 0xA9: A = Read(Imm()); NZ(A); break;
            case 0xA5: A = Read(AZp()); NZ(A); break;
            case 0xB5: A = Read(AZpX()); NZ(A); break;
            case 0xAD: A = Read(AAbs()); NZ(A); break;
            case 0xBD: A = Read(AAbsX()); NZ(A); break;
            case 0xB9: A = Read(AAbsY()); NZ(A); break;
            case 0xA1: A = Read(AIndX()); NZ(A); break;
            case 0xB1: A = Read(AIndY()); NZ(A); break;
            case 0xA2: X = Read(Imm()); NZ(X); break;
            case 0xA6: X = Read(AZp()); NZ(X); break;
            case 0xB6: X = Read(AZpY()); NZ(X); break;
            case 0xAE: X = Read(AAbs()); NZ(X); break;
            case 0xBE: X = Read(AAbsY()); NZ(X); break;
            case 0xA0: Y = Read(Imm()); NZ(Y); break;
            case 0xA4: Y = Read(AZp()); NZ(Y); break;
            case 0xB4: Y = Read(AZpX()); NZ(Y); break;
            case 0xAC: Y = Read(AAbs()); NZ(Y); break;
            case 0xBC: Y = Read(AAbsX()); NZ(Y); break;
            case 0x85: Write(AZp(), A); break;
            case 0x95: Write(AZpX(), A); break;
            case 0x8D: Write(AAbs(), A); break;
            case 0x9D: Write(AAbsX(false), A); break;
            case 0x99: Write(AAbsY(false), A); break;
            case 0x81: Write(AIndX(), A); break;
            case 0x91: Write(AIndY(false), A); break;
            case 0x86: Write(AZp(), X); break;
            case 0x96: Write(AZpY(), X); break;
            case 0x8E: Write(AAbs(), X); break;
            case 0x84: Write(AZp(), Y); break;
            case 0x94: Write(AZpX(), Y); break;
            case 0x8C: Write(AAbs(), Y); break;

            // ---- 옮기기
            case 0xAA: X = A; NZ(X); break;
            case 0xA8: Y = A; NZ(Y); break;
            case 0x8A: A = X; NZ(A); break;
            case 0x98: A = Y; NZ(A); break;
            case 0xBA: X = S; NZ(X); break;
            case 0x9A: S = X; break;

            // ---- 스택
            case 0x48: Push(A); break;
            case 0x68: A = Pop(); NZ(A); break;
            case 0x08: Push(P | 0x10); break;
            case 0x28: P = Pop(); break;

            // ---- 산술 · 논리
            case 0x69: AdcT(Read(Imm())); break;
            case 0x65: AdcT(Read(AZp())); break;
            case 0x75: AdcT(Read(AZpX())); break;
            case 0x6D: AdcT(Read(AAbs())); break;
            case 0x7D: AdcT(Read(AAbsX())); break;
            case 0x79: AdcT(Read(AAbsY())); break;
            case 0x61: AdcT(Read(AIndX())); break;
            case 0x71: AdcT(Read(AIndY())); break;
            case 0xE9: case 0xEB: Sbc(Read(Imm())); break;
            case 0xE5: Sbc(Read(AZp())); break;
            case 0xF5: Sbc(Read(AZpX())); break;
            case 0xED: Sbc(Read(AAbs())); break;
            case 0xFD: Sbc(Read(AAbsX())); break;
            case 0xF9: Sbc(Read(AAbsY())); break;
            case 0xE1: Sbc(Read(AIndX())); break;
            case 0xF1: Sbc(Read(AIndY())); break;
            case 0x29: Logic(Read(Imm()), 0); break;
            case 0x25: Logic(Read(AZp()), 0); break;
            case 0x35: Logic(Read(AZpX()), 0); break;
            case 0x2D: Logic(Read(AAbs()), 0); break;
            case 0x3D: Logic(Read(AAbsX()), 0); break;
            case 0x39: Logic(Read(AAbsY()), 0); break;
            case 0x21: Logic(Read(AIndX()), 0); break;
            case 0x31: Logic(Read(AIndY()), 0); break;
            case 0x09: Logic(Read(Imm()), 1); break;
            case 0x05: Logic(Read(AZp()), 1); break;
            case 0x15: Logic(Read(AZpX()), 1); break;
            case 0x0D: Logic(Read(AAbs()), 1); break;
            case 0x1D: Logic(Read(AAbsX()), 1); break;
            case 0x19: Logic(Read(AAbsY()), 1); break;
            case 0x01: Logic(Read(AIndX()), 1); break;
            case 0x11: Logic(Read(AIndY()), 1); break;
            case 0x49: Logic(Read(Imm()), 2); break;
            case 0x45: Logic(Read(AZp()), 2); break;
            case 0x55: Logic(Read(AZpX()), 2); break;
            case 0x4D: Logic(Read(AAbs()), 2); break;
            case 0x5D: Logic(Read(AAbsX()), 2); break;
            case 0x59: Logic(Read(AAbsY()), 2); break;
            case 0x41: Logic(Read(AIndX()), 2); break;
            case 0x51: Logic(Read(AIndY()), 2); break;
            case 0xC9: Cmp(A, Read(Imm())); break;
            case 0xC5: Cmp(A, Read(AZp())); break;
            case 0xD5: Cmp(A, Read(AZpX())); break;
            case 0xCD: Cmp(A, Read(AAbs())); break;
            case 0xDD: Cmp(A, Read(AAbsX())); break;
            case 0xD9: Cmp(A, Read(AAbsY())); break;
            case 0xC1: Cmp(A, Read(AIndX())); break;
            case 0xD1: Cmp(A, Read(AIndY())); break;
            case 0xE0: Cmp(X, Read(Imm())); break;
            case 0xE4: Cmp(X, Read(AZp())); break;
            case 0xEC: Cmp(X, Read(AAbs())); break;
            case 0xC0: Cmp(Y, Read(Imm())); break;
            case 0xC4: Cmp(Y, Read(AZp())); break;
            case 0xCC: Cmp(Y, Read(AAbs())); break;
            case 0x24: Bit(Read(AZp())); break;
            case 0x2C: Bit(Read(AAbs())); break;

            // ---- 늘이기 · 줄이기
            case 0xE6: Rmw(AZp(), 4); break;
            case 0xF6: Rmw(AZpX(), 4); break;
            case 0xEE: Rmw(AAbs(), 4); break;
            case 0xFE: Rmw(AAbsX(false), 4); break;
            case 0xC6: Rmw(AZp(), 5); break;
            case 0xD6: Rmw(AZpX(), 5); break;
            case 0xCE: Rmw(AAbs(), 5); break;
            case 0xDE: Rmw(AAbsX(false), 5); break;
            case 0xE8: X = (X + 1) & 0xFF; NZ(X); break;
            case 0xCA: X = (X - 1) & 0xFF; NZ(X); break;
            case 0xC8: Y = (Y + 1) & 0xFF; NZ(Y); break;
            case 0x88: Y = (Y - 1) & 0xFF; NZ(Y); break;

            // ---- 자리 옮김
            case 0x0A: A = Asl(A); break;
            case 0x06: Rmw(AZp(), 0); break;
            case 0x16: Rmw(AZpX(), 0); break;
            case 0x0E: Rmw(AAbs(), 0); break;
            case 0x1E: Rmw(AAbsX(false), 0); break;
            case 0x4A: A = Lsr(A); break;
            case 0x46: Rmw(AZp(), 1); break;
            case 0x56: Rmw(AZpX(), 1); break;
            case 0x4E: Rmw(AAbs(), 1); break;
            case 0x5E: Rmw(AAbsX(false), 1); break;
            case 0x2A: A = Rol(A); break;
            case 0x26: Rmw(AZp(), 2); break;
            case 0x36: Rmw(AZpX(), 2); break;
            case 0x2E: Rmw(AAbs(), 2); break;
            case 0x3E: Rmw(AAbsX(false), 2); break;
            case 0x6A: A = Ror(A); break;
            case 0x66: Rmw(AZp(), 3); break;
            case 0x76: Rmw(AZpX(), 3); break;
            case 0x6E: Rmw(AAbs(), 3); break;
            case 0x7E: Rmw(AAbsX(false), 3); break;

            // ---- 뛰기
            case 0x4C: PC = Fetch16(); break;
            case 0x6C:
                a = Fetch16();
                // NMOS: 페이지 끝을 넘지 못하는 버그, 65C02/HuC6280: 바르게
                PC = huc ? Read16(a) : Read(a) | (Read((a & 0xFF00) | ((a + 1) & 0xFF)) << 8);
                break;
            case 0x20: a = Fetch16(); { int ret = (PC - 1) & 0xFFFF; Push(ret >> 8); Push(ret & 0xFF); } PC = a; break;
            case 0x60: PC = ((Pop() | (Pop() << 8)) + 1) & 0xFFFF; break;
            case 0x40: P = Pop(); PC = Pop() | (Pop() << 8); break;
            case 0x00:
                {
                    int ret = (PC + 1) & 0xFFFF;
                    Push(ret >> 8); Push(ret & 0xFF); Push(P | 0x10);
                    I = true; if (huc) D = false;
                    PC = Read16(huc ? 0xFFF6 : 0xFFFE);
                }
                break;
            case 0x10: Branch(!N); break;
            case 0x30: Branch(N); break;
            case 0x50: Branch(!V); break;
            case 0x70: Branch(V); break;
            case 0x90: Branch(!C); break;
            case 0xB0: Branch(C); break;
            case 0xD0: Branch(!Z); break;
            case 0xF0: Branch(Z); break;

            // ---- 깃발
            case 0x18: C = false; break;
            case 0x38: C = true; break;
            case 0x58: I = false; break;
            case 0x78: I = true; break;
            case 0xB8: V = false; break;
            case 0xD8: D = false; break;
            case 0xF8: D = true; break;
            case 0xEA: break;

            default:
                if (huc) { Exec65C02(op); break; }
                ExecIllegal(op);
                break;
        }
    }

    // ------------------------------------------------------------ NMOS 비공식 명령 (2A03)

    private void ExecIllegal(int op)
    {
        int a;
        switch (op)
        {
            // NOP 변형 (주소 읽기만)
            case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: break;
            case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: Imm(); break;
            case 0x04: case 0x44: case 0x64: AZp(); break;
            case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: AZpX(); break;
            case 0x0C: AAbs(); break;
            case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: AAbsX(); break;
            // LAX
            case 0xA7: A = X = Read(AZp()); NZ(A); break;
            case 0xB7: A = X = Read(AZpY()); NZ(A); break;
            case 0xAF: A = X = Read(AAbs()); NZ(A); break;
            case 0xBF: A = X = Read(AAbsY()); NZ(A); break;
            case 0xA3: A = X = Read(AIndX()); NZ(A); break;
            case 0xB3: A = X = Read(AIndY()); NZ(A); break;
            // SAX
            case 0x87: Write(AZp(), A & X); break;
            case 0x97: Write(AZpY(), A & X); break;
            case 0x8F: Write(AAbs(), A & X); break;
            case 0x83: Write(AIndX(), A & X); break;
            // DCP / ISB / SLO / RLA / SRE / RRA
            case 0xC7: case 0xD7: case 0xCF: case 0xDF: case 0xDB: case 0xC3: case 0xD3:
                a = RmwAddr(op); { int v = (Read(a) - 1) & 0xFF; Write(a, v); Cmp(A, v); } break;
            case 0xE7: case 0xF7: case 0xEF: case 0xFF: case 0xFB: case 0xE3: case 0xF3:
                a = RmwAddr(op); { int v = (Read(a) + 1) & 0xFF; Write(a, v); Sbc(v); } break;
            case 0x07: case 0x17: case 0x0F: case 0x1F: case 0x1B: case 0x03: case 0x13:
                a = RmwAddr(op); { int v = Asl(Read(a)); Write(a, v); A |= v; NZ(A); } break;
            case 0x27: case 0x37: case 0x2F: case 0x3F: case 0x3B: case 0x23: case 0x33:
                a = RmwAddr(op); { int v = Rol(Read(a)); Write(a, v); A &= v; NZ(A); } break;
            case 0x47: case 0x57: case 0x4F: case 0x5F: case 0x5B: case 0x43: case 0x53:
                a = RmwAddr(op); { int v = Lsr(Read(a)); Write(a, v); A ^= v; NZ(A); } break;
            case 0x67: case 0x77: case 0x6F: case 0x7F: case 0x7B: case 0x63: case 0x73:
                a = RmwAddr(op); { int v = Ror(Read(a)); Write(a, v); Adc(v); } break;
            case 0x0B: case 0x2B: Logic(Read(Imm()), 0); C = N; break;                    // ANC
            case 0x4B: Logic(Read(Imm()), 0); A = Lsr(A); break;                          // ALR
            case 0x6B: Logic(Read(Imm()), 0); A = Ror(A); V = (((A >> 6) ^ (A >> 5)) & 1) != 0; C = (A & 0x40) != 0; break;   // ARR (대략)
            case 0xCB: { int m = Read(Imm()); int t = (A & X) - m; C = t >= 0; X = t & 0xFF; NZ(X); } break;   // AXS
            case 0x02: case 0x12: case 0x22: case 0x32: case 0x42: case 0x52: case 0x62: case 0x72: case 0x92: case 0xB2: case 0xD2: case 0xF2:
                Jammed = true; break;
            default: UnknownOps++; break;
        }
    }

    /// <summary>비공식 읽고-고치고-쓰기 명령의 주소 (열 = 주소 방식).</summary>
    private int RmwAddr(int op)
    {
        switch (op & 0x1F)
        {
            case 0x03: return AIndX();
            case 0x07: return AZp();
            case 0x0F: return AAbs();
            case 0x13: return AIndY(false);
            case 0x17: return AZpX();
            case 0x1B: return AAbsY(false);
            default: return AAbsX(false);   // 0x1F
        }
    }

    // ------------------------------------------------------------ 65C02 (HuC6280 바탕)

    private void Exec65C02(int op)
    {
        int a;
        switch (op)
        {
            case 0x80: Branch(true); Cycles++; break;                       // BRA
            case 0xDA: Push(X); Cycles++; break;                             // PHX
            case 0x5A: Push(Y); Cycles++; break;                             // PHY
            case 0xFA: X = Pop(); NZ(X); Cycles += 2; break;                 // PLX
            case 0x7A: Y = Pop(); NZ(Y); Cycles += 2; break;                 // PLY
            case 0x64: Write(AZp(), 0); break;                               // STZ
            case 0x74: Write(AZpX(), 0); break;
            case 0x9C: Write(AAbs(), 0); break;
            case 0x9E: Write(AAbsX(false), 0); break;
            case 0x1A: A = (A + 1) & 0xFF; NZ(A); break;                     // INC A
            case 0x3A: A = (A - 1) & 0xFF; NZ(A); break;                     // DEC A
            case 0x89: Z = (A & Read(Imm())) == 0; break;                    // BIT #
            case 0x34: Bit(Read(AZpX())); break;
            case 0x3C: Bit(Read(AAbsX())); break;
            case 0x7C: a = (Fetch16() + X) & 0xFFFF; PC = Read16(a); break;  // JMP (abs,X)
            case 0x04: a = AZp(); { int m = Read(a); Z = (A & m) == 0; Write(a, m | A); } break;      // TSB
            case 0x0C: a = AAbs(); { int m = Read(a); Z = (A & m) == 0; Write(a, m | A); } break;
            case 0x14: a = AZp(); { int m = Read(a); Z = (A & m) == 0; Write(a, m & ~A); } break;     // TRB
            case 0x1C: a = AAbs(); { int m = Read(a); Z = (A & m) == 0; Write(a, m & ~A); } break;
            case 0x12: Logic(Read(AInd()), 1); break;                        // (zp)
            case 0x32: Logic(Read(AInd()), 0); break;
            case 0x52: Logic(Read(AInd()), 2); break;
            case 0x72: AdcT(Read(AInd())); break;
            case 0x92: Write(AInd(), A); break;
            case 0xB2: A = Read(AInd()); NZ(A); break;
            case 0xD2: Cmp(A, Read(AInd())); break;
            case 0xF2: Sbc(Read(AInd())); break;
            default:
                if ((op & 0x0F) == 0x07)
                {
                    // RMB n / SMB n
                    a = AZp(); int bit = 1 << ((op >> 4) & 7);
                    Write(a, op < 0x80 ? Read(a) & ~bit : Read(a) | bit);
                    Cycles += 2;
                    break;
                }
                if ((op & 0x0F) == 0x0F)
                {
                    // BBR n / BBS n
                    int m = Read(AZp()); int bit = 1 << ((op >> 4) & 7);
                    bool set = (m & bit) != 0;
                    Branch(op < 0x80 ? !set : set);
                    Cycles += 2;
                    break;
                }
                UnknownOps++;   // 65C02의 나머지 = NOP
                break;
        }
    }

    // ------------------------------------------------------------ HuC6280 전용

    /// <summary>VDC 쓰기 (ST0 · ST1 · ST2): 실제 주소 = 페이지 0xFF의 0 · 2 · 3.</summary>
    private void Vdc(int reg, int v) { bus.Write((0xFF << 13) | reg, (byte)v); }

    private bool ExecHuc(int op, bool t)
    {
        int tmp;
        switch (op)
        {
            case 0x02: tmp = X; X = Y; Y = tmp; Cycles += 1; return true;                            // SXY
            case 0x22: tmp = A; A = X; X = tmp; Cycles += 1; return true;                            // SAX
            case 0x42: tmp = A; A = Y; Y = tmp; Cycles += 1; return true;                            // SAY
            case 0x62: A = 0; return true;                                                           // CLA
            case 0x82: X = 0; return true;                                                           // CLX
            case 0xC2: Y = 0; return true;                                                           // CLY
            case 0x03: Vdc(0, Fetch()); Cycles += 2; return true;                                    // ST0
            case 0x13: Vdc(2, Fetch()); Cycles += 2; return true;                                    // ST1
            case 0x23: Vdc(3, Fetch()); Cycles += 2; return true;                                    // ST2
            case 0x43:                                                                               // TMA
                {
                    int m = Fetch();
                    for (int i = 0; i < 8; i++) if ((m & (1 << i)) != 0) { A = Mpr[i]; break; }
                    Cycles += 2;
                    return true;
                }
            case 0x53:                                                                               // TAM
                {
                    int m = Fetch();
                    for (int i = 0; i < 8; i++) if ((m & (1 << i)) != 0) Mpr[i] = (byte)A;
                    Cycles += 3;
                    return true;
                }
            case 0x54: case 0xD4: Cycles += 1; return true;                                          // CSL · CSH (속도: 무시)
            case 0xF4: T = true; return true;                                                        // SET (다음 명령에 T)
            case 0x44:                                                                               // BSR
                {
                    int off = (sbyte)Fetch();
                    int ret = (PC - 1) & 0xFFFF;
                    Push(ret >> 8); Push(ret & 0xFF);
                    PC = (PC + off) & 0xFFFF;
                    Cycles += 6;
                    return true;
                }
            case 0x83: case 0xA3: case 0x93: case 0xB3:                                              // TST
                {
                    int imm = Fetch();
                    int addr = op == 0x83 ? AZp() : op == 0xA3 ? AZpX() : op == 0x93 ? AAbs() : (Fetch16() + X) & 0xFFFF;
                    int m = Read(addr);
                    Z = (m & imm) == 0; N = (m & 0x80) != 0; V = (m & 0x40) != 0;
                    Cycles += 5;
                    return true;
                }
            case 0x73: case 0xC3: case 0xD3: case 0xE3: case 0xF3:                                   // TII · TDD · TIN · TIA · TAI
                {
                    int src = Fetch16(), dst = Fetch16(), len = Fetch16();
                    if (len == 0) len = 0x10000;
                    int alt = 0;
                    for (int i = 0; i < len; i++)
                    {
                        Write(dst, Read(src));
                        switch (op)
                        {
                            case 0x73: src++; dst++; break;                                          // TII
                            case 0xC3: src--; dst--; break;                                          // TDD
                            case 0xD3: src++; break;                                                 // TIN (dst 고정)
                            case 0xE3: src++; dst += alt == 0 ? 1 : -1; alt ^= 1; break;             // TIA (dst 번갈아)
                            case 0xF3: src += alt == 0 ? 1 : -1; alt ^= 1; dst++; break;             // TAI (src 번갈아)
                        }
                        src &= 0xFFFF; dst &= 0xFFFF;
                    }
                    Cycles += 17 + 6L * len - BaseCycles[op];
                    return true;
                }
        }
        return false;
    }
}
