using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>68000이 보는 버스 (24비트 주소, 빅 엔디언). 워드 읽기 · 쓰기는 짝수 주소로만 온다.</summary>
public interface IM68kBus
{
    byte Read8(uint addr);
    ushort Read16(uint addr);
    void Write8(uint addr, byte value);
    void Write16(uint addr, ushort value);
    /// <summary>인터럽트 받음 (level 1~7): 벡터 번호, 자동 벡터면 −1.</summary>
    int Acknowledge(int level);
}

/// <summary>
/// Motorola MC68000 CPU (명령 전부: 정수 · BCD · MOVEM · MOVEP · 비트 · 시프트 · 예외 · 슈퍼바이저 / 사용자 모드 · 주소 오류).
/// 사운드 드라이버를 돌리기 위한 것이라 클럭은 대략 값 (<see cref="Cycles"/>: 버스 워드 하나 = 4클럭 + 명령마다 안쪽 클럭).
/// <para>인터럽트: 바깥이 <see cref="IrqLevel"/>을 올리면 명령 사이에서 SR 마스크보다 높을 때 (7은 늘) 받는다 (<see cref="IM68kBus.Acknowledge"/>로 벡터).
/// 명령을 모르면 불법 명령 예외 (벡터 4), A줄 · F줄 = 벡터 10 · 11. 홀수 주소 워드 접근은 <see cref="AddressErrors"/>에 따라 주소 오류 또는 바이트 둘.</para>
/// </summary>
public sealed class M68000
{
    public readonly uint[] D = new uint[8], A = new uint[8];
    public uint PC;
    /// <summary>다른 쪽 스택 포인터 (S = 1이면 USP, 0이면 SSP를 여기 둠).</summary>
    public uint OtherSp;
    public bool S, T;
    public int IntMask;
    public bool X, N, Z, V, C;
    public bool Stopped;
    /// <summary>바깥 인터럽트 줄 (0 = 없음).</summary>
    public int IrqLevel;
    public long Cycles;
    /// <summary>마지막으로 읽은 명령 워드 (진단).</summary>
    public ushort Ir;
    /// <summary>마지막 예외 벡터 (진단).</summary>
    public int LastException = -1;
    public long Exceptions;
    /// <summary>홀수 주소 워드 · 롱 접근 = 주소 오류 (벡터 3). 끄면 바이트로 나눠 그대로 접근한다 (hoot의 68000처럼).</summary>
    public bool AddressErrors = true;
    /// <summary>진단: 예외 (인터럽트 빼고)가 날 때 (벡터, 그 명령의 PC, 주소 오류면 주소).</summary>
    public Action<int, uint, uint> OnException;
    private uint instrPc;

    private readonly IM68kBus bus;

    private sealed class AddressError : System.Exception
    {
        public readonly uint Addr; public readonly bool Write;
        public AddressError(uint a, bool w) { Addr = a; Write = w; }
    }

    public M68000(IM68kBus bus) { this.bus = bus; }

    public ushort SR
    {
        get
        {
            return (ushort)((T ? 0x8000 : 0) | (S ? 0x2000 : 0) | ((IntMask & 7) << 8) |
                (X ? 0x10 : 0) | (N ? 8 : 0) | (Z ? 4 : 0) | (V ? 2 : 0) | (C ? 1 : 0));
        }
        set
        {
            bool newS = (value & 0x2000) != 0;
            if (newS != S) { uint t = A[7]; A[7] = OtherSp; OtherSp = t; S = newS; }
            T = (value & 0x8000) != 0;
            IntMask = (value >> 8) & 7;
            Ccr = value;
        }
    }

    public int Ccr
    {
        get { return SR & 0x1F; }
        set { X = (value & 0x10) != 0; N = (value & 8) != 0; Z = (value & 4) != 0; V = (value & 2) != 0; C = (value & 1) != 0; }
    }

    public uint Usp { get { return S ? OtherSp : A[7]; } set { if (S) OtherSp = value; else A[7] = value; } }
    public uint Ssp { get { return S ? A[7] : OtherSp; } set { if (S) A[7] = value; else OtherSp = value; } }

    /// <summary>리셋: 0번지에서 SSP, 4번지에서 PC.</summary>
    public void Reset()
    {
        S = true; T = false; IntMask = 7; Stopped = false;
        A[7] = Read32(0); PC = Read32(4);
        Cycles += 40;
    }

    // ------------------------------------------------------------ 버스

    private uint Read8(uint a) { Cycles += 4; return bus.Read8(a & 0xFFFFFF); }
    private uint Read16(uint a)
    {
        if ((a & 1) != 0)
        {
            if (AddressErrors) throw new AddressError(a, false);
            Cycles += 4; return (uint)bus.Read8(a & 0xFFFFFF) << 8 | bus.Read8((a + 1) & 0xFFFFFF);
        }
        Cycles += 4; return bus.Read16(a & 0xFFFFFF);
    }
    private uint Read32(uint a)
    {
        if ((a & 1) != 0)
        {
            if (AddressErrors) throw new AddressError(a, false);
            return Read16(a) << 16 | Read16(a + 2);
        }
        Cycles += 8;
        return ((uint)bus.Read16(a & 0xFFFFFF) << 16) | bus.Read16((a + 2) & 0xFFFFFF);
    }
    private void Write8(uint a, uint v) { Cycles += 4; bus.Write8(a & 0xFFFFFF, (byte)v); }
    private void Write16(uint a, uint v)
    {
        if ((a & 1) != 0)
        {
            if (AddressErrors) throw new AddressError(a, true);
            Cycles += 4; bus.Write8(a & 0xFFFFFF, (byte)(v >> 8)); bus.Write8((a + 1) & 0xFFFFFF, (byte)v);
            return;
        }
        Cycles += 4; bus.Write16(a & 0xFFFFFF, (ushort)v);
    }
    private void Write32(uint a, uint v)
    {
        if ((a & 1) != 0)
        {
            if (AddressErrors) throw new AddressError(a, true);
            Write16(a, v >> 16); Write16(a + 2, v);
            return;
        }
        Cycles += 8;
        bus.Write16(a & 0xFFFFFF, (ushort)(v >> 16)); bus.Write16((a + 2) & 0xFFFFFF, (ushort)v);
    }

    private uint Read(uint a, int sz) { return sz == 1 ? Read8(a) : sz == 2 ? Read16(a) : Read32(a); }
    private void Write(uint a, int sz, uint v) { if (sz == 1) Write8(a, v); else if (sz == 2) Write16(a, v); else Write32(a, v); }

    private uint Fetch16() { uint v = Read16(PC); PC += 2; return v; }
    private uint Fetch32() { uint v = Read32(PC); PC += 4; return v; }

    private void Push16(uint v) { A[7] -= 2; Write16(A[7], v); }
    private void Push32(uint v) { A[7] -= 4; Write32(A[7], v); }
    private uint Pop16() { uint v = Read16(A[7]); A[7] += 2; return v; }
    private uint Pop32() { uint v = Read32(A[7]); A[7] += 4; return v; }

    // ------------------------------------------------------------ 크기 도움

    private static uint Mask(int sz) { return sz == 1 ? 0xFFu : sz == 2 ? 0xFFFFu : 0xFFFFFFFFu; }
    private static uint Msb(int sz) { return sz == 1 ? 0x80u : sz == 2 ? 0x8000u : 0x80000000u; }
    private static int SizeOf(int code) { return code == 0 ? 1 : code == 1 ? 2 : code == 2 ? 4 : 0; }
    private static uint SignExt(uint v, int sz) { return sz == 1 ? (uint)(sbyte)v : sz == 2 ? (uint)(short)v : v; }

    private void SetD(int r, int sz, uint v)
    {
        uint m = Mask(sz);
        D[r] = (D[r] & ~m) | (v & m);
    }

    private void SetNz(uint v, int sz)
    {
        v &= Mask(sz);
        N = (v & Msb(sz)) != 0; Z = v == 0;
    }

    private void Logic(uint v, int sz) { SetNz(v, sz); V = C = false; }

    // ------------------------------------------------------------ 유효 주소

    // 풀어 둔 유효 주소: 0 = Dn, 1 = An, 2 = 메모리, 3 = 즉치
    private int eaKind, eaReg;
    private uint eaAddr;

    private uint IndexExt(uint baseAddr)
    {
        uint ext = Fetch16();
        int r = (int)(ext >> 12) & 7;
        uint xn = (ext & 0x8000) != 0 ? A[r] : D[r];
        if ((ext & 0x800) == 0) xn = (uint)(short)xn;
        Cycles += 2;
        return baseAddr + xn + (uint)(sbyte)ext;
    }

    /// <summary>모드 · 레지스터를 풀어 둔다 (sz = 바이트 수, (An)+ · −(An)의 걸음).</summary>
    private void Resolve(int mode, int reg, int sz)
    {
        switch (mode)
        {
            case 0: eaKind = 0; eaReg = reg; return;
            case 1: eaKind = 1; eaReg = reg; return;
            case 2: eaKind = 2; eaAddr = A[reg]; return;
            case 3:
                eaKind = 2; eaAddr = A[reg];
                A[reg] += (uint)(reg == 7 && sz == 1 ? 2 : sz);
                return;
            case 4:
                eaKind = 2;
                Cycles += 2;
                A[reg] -= (uint)(reg == 7 && sz == 1 ? 2 : sz);
                eaAddr = A[reg];
                return;
            case 5: eaKind = 2; eaAddr = A[reg] + (uint)(short)Fetch16(); return;
            case 6: eaKind = 2; eaAddr = IndexExt(A[reg]); return;
            case 7:
                switch (reg)
                {
                    case 0: eaKind = 2; eaAddr = (uint)(short)Fetch16(); return;
                    case 1: eaKind = 2; eaAddr = Fetch32(); return;
                    case 2: { eaKind = 2; uint pc = PC; eaAddr = pc + (uint)(short)Fetch16(); return; }
                    case 3: { eaKind = 2; uint pc = PC; eaAddr = IndexExt(pc); return; }
                    case 4:
                        eaKind = 3;
                        eaAddr = sz == 4 ? Fetch32() : sz == 2 ? Fetch16() : Fetch16() & 0xFF;
                        return;
                }
                break;
        }
        throw new IllegalInstruction();
    }

    private sealed class IllegalInstruction : System.Exception { }

    private uint GetEa(int sz)
    {
        switch (eaKind)
        {
            case 0: return D[eaReg] & Mask(sz);
            case 1: return A[eaReg] & Mask(sz);
            case 2: return Read(eaAddr, sz);
            default: return eaAddr & Mask(sz);
        }
    }

    private void SetEa(int sz, uint v)
    {
        switch (eaKind)
        {
            case 0: SetD(eaReg, sz, v); return;
            case 1: A[eaReg] = sz == 2 ? (uint)(short)v : v; return;
            case 2: Write(eaAddr, sz, v); return;
            default: throw new IllegalInstruction();
        }
    }

    private uint ReadEa(int mode, int reg, int sz) { Resolve(mode, reg, sz); return GetEa(sz); }

    /// <summary>제어 주소 (LEA · PEA · JMP · JSR · MOVEM)만 되는 모드의 주소.</summary>
    private uint ControlAddr(int mode, int reg)
    {
        if (mode < 2 || mode == 3 || mode == 4 || (mode == 7 && reg > 3)) throw new IllegalInstruction();
        Resolve(mode, reg, 4);
        return eaAddr;
    }

    // ------------------------------------------------------------ 조건

    private bool Cond(int cc)
    {
        switch (cc & 15)
        {
            case 0: return true;
            case 1: return false;
            case 2: return !C && !Z;
            case 3: return C || Z;
            case 4: return !C;
            case 5: return C;
            case 6: return !Z;
            case 7: return Z;
            case 8: return !V;
            case 9: return V;
            case 10: return !N;
            case 11: return N;
            case 12: return N == V;
            case 13: return N != V;
            case 14: return !Z && N == V;
            default: return Z || N != V;
        }
    }

    // ------------------------------------------------------------ 산술

    private uint Add(uint s, uint d, int sz, bool withX)
    {
        uint m = Mask(sz), msb = Msb(sz);
        s &= m; d &= m;
        ulong r64 = (ulong)s + d + (withX && X ? 1u : 0u);
        uint r = (uint)r64 & m;
        C = X = sz == 4 ? (r64 >> 32) != 0 : (r64 & ~(ulong)m) != 0;
        V = ((~(s ^ d) & (s ^ r)) & msb) != 0;
        N = (r & msb) != 0;
        if (withX) { if (r != 0) Z = false; } else Z = r == 0;
        return r;
    }

    private uint Sub(uint s, uint d, int sz, bool withX, bool setX = true)
    {
        uint m = Mask(sz), msb = Msb(sz);
        s &= m; d &= m;
        uint bx = withX && X ? 1u : 0u;
        uint r = (d - s - bx) & m;
        bool c = (ulong)s + bx > d;
        C = c; if (setX) X = c;
        V = (((s ^ d) & (d ^ r)) & msb) != 0;
        N = (r & msb) != 0;
        if (withX) { if (r != 0) Z = false; } else Z = r == 0;
        return r;
    }

    private void Cmp(uint s, uint d, int sz) { Sub(s, d, sz, false, false); }

    // ------------------------------------------------------------ 예외

    /// <summary>예외 처리 (그룹 1 · 2): SR · PC를 슈퍼바이저 스택에.</summary>
    public void TakeException(int vector, uint returnPc)
    {
        OnException?.Invoke(vector, instrPc, 0);
        LastException = vector;
        Exceptions++;
        ushort old = SR;
        SR = (ushort)((old | 0x2000) & ~0x8000);
        Push32(returnPc);
        Push16(old);
        PC = Read32((uint)vector * 4);
        Stopped = false;
        Cycles += 26;
    }

    private void AddressException(AddressError e, ushort ir)
    {
        OnException?.Invoke(3, instrPc, e.Addr);
        LastException = 3;
        Exceptions++;
        ushort old = SR;
        SR = (ushort)((old | 0x2000) & ~0x8000);
        try
        {
            Push32(PC);
            Push16(old);
            Push16(ir);
            Push32(e.Addr);
            Push16((uint)((e.Write ? 0 : 0x10) | (S ? 5 : 1)));
            PC = Read32(3 * 4);
        }
        catch (AddressError) { Stopped = true; }   // 이중 오류: 멈춤
        Cycles += 50;
    }

    private bool TryInterrupt()
    {
        int lv = IrqLevel & 7;
        if (lv == 0 || (lv <= IntMask && lv != 7)) return false;
        int vec = bus.Acknowledge(lv);
        if (vec < 0) vec = 24 + lv;
        ushort old = SR;
        SR = (ushort)((old | 0x2000) & ~0x8000);
        IntMask = lv;
        Push32(PC);
        Push16(old);
        PC = Read32((uint)vec * 4);
        LastException = vec;
        Stopped = false;
        Cycles += 44;
        return true;
    }

    // ------------------------------------------------------------ 실행

    /// <summary>명령 하나 (또는 인터럽트 받기). 멈춰 있으면 (STOP) 4클럭만 보낸다.</summary>
    public void Step()
    {
        try
        {
            if (TryInterrupt()) return;
        }
        catch (AddressError e) { AddressException(e, 0); return; }
        if (Stopped) { Cycles += 4; return; }
        uint startPc = PC;
        instrPc = startPc;
        bool trace = T;
        ushort ir = 0;
        try
        {
            try
            {
                ir = (ushort)Fetch16();
                Ir = ir;
                Execute(ir, startPc);
            }
            catch (IllegalInstruction) { PC = startPc; TakeException(4, startPc); return; }
            if (trace && T) TakeException(9, PC);
        }
        catch (AddressError e) { AddressException(e, ir); }
    }

    private void Privileged(uint startPc) { PC = startPc; TakeException(8, startPc); }

    private void Execute(ushort op, uint startPc)
    {
        switch (op >> 12)
        {
            case 0x0: Line0(op, startPc); return;
            case 0x1: Move(op, 1); return;
            case 0x2: Move(op, 4); return;
            case 0x3: Move(op, 2); return;
            case 0x4: Line4(op, startPc); return;
            case 0x5: Line5(op); return;
            case 0x6: Branch(op); return;
            case 0x7:
                if ((op & 0x100) != 0) throw new IllegalInstruction();
                {
                    uint v = (uint)(sbyte)op;
                    D[(op >> 9) & 7] = v;
                    Logic(v, 4);
                }
                return;
            case 0x8: Line8(op); return;
            case 0x9: AddSub(op, false); return;
            case 0xA: PC = startPc; TakeException(10, startPc); return;
            case 0xB: LineB(op); return;
            case 0xC: LineC(op); return;
            case 0xD: AddSub(op, true); return;
            case 0xE: Shift(op); return;
            default: PC = startPc; TakeException(11, startPc); return;
        }
    }

    // ---- 0줄: 즉치 · 비트 · MOVEP

    private void Line0(ushort op, uint startPc)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        if ((op & 0x100) != 0)
        {
            if (mode == 1) { Movep(op); return; }
            int bit = (int)D[(op >> 9) & 7];
            BitOp(op, bit, mode, reg);
            return;
        }
        int kind = (op >> 9) & 7;
        if (kind == 4)
        {
            int bit = (int)Fetch16() & 0xFF;
            BitOp(op, bit, mode, reg);
            return;
        }
        int szc = (op >> 6) & 3;
        if (szc == 3) throw new IllegalInstruction();
        int sz = SizeOf(szc);
        // CCR · SR 대상
        if (mode == 7 && reg == 4 && (kind == 0 || kind == 1 || kind == 5))
        {
            uint imm = Fetch16();
            if (sz == 1)
            {
                int ccr = Ccr;
                ccr = kind == 0 ? ccr | (int)imm : kind == 1 ? ccr & (int)imm : ccr ^ (int)imm;
                Ccr = ccr & 0x1F;
                Cycles += 12;
                return;
            }
            if (sz == 2)
            {
                if (!S) { Privileged(startPc); return; }
                int sr = SR;
                sr = kind == 0 ? sr | (int)imm : kind == 1 ? sr & (int)imm : sr ^ (int)imm;
                SR = (ushort)sr;
                Cycles += 12;
                return;
            }
            throw new IllegalInstruction();
        }
        uint src = sz == 4 ? Fetch32() : sz == 2 ? Fetch16() : Fetch16() & 0xFF;
        if (mode == 1) throw new IllegalInstruction();
        Resolve(mode, reg, sz);
        uint d = GetEa(sz);
        switch (kind)
        {
            case 0: d |= src; Logic(d, sz); SetEa(sz, d); break;
            case 1: d &= src; Logic(d, sz); SetEa(sz, d); break;
            case 2: SetEa(sz, Sub(src, d, sz, false)); break;
            case 3: SetEa(sz, Add(src, d, sz, false)); break;
            case 5: d ^= src; Logic(d, sz); SetEa(sz, d); break;
            case 6: Cmp(src, d, sz); break;
            default: throw new IllegalInstruction();
        }
        if (mode == 0 && sz == 4) Cycles += 4;
    }

    private void BitOp(ushort op, int bit, int mode, int reg)
    {
        int type = (op >> 6) & 3;
        if (mode == 1) throw new IllegalInstruction();
        if (mode == 0)
        {
            int b = bit & 31;
            uint m = 1u << b;
            uint v = D[reg];
            Z = (v & m) == 0;
            switch (type)
            {
                case 1: D[reg] = v ^ m; break;
                case 2: D[reg] = v & ~m; break;
                case 3: D[reg] = v | m; break;
            }
            Cycles += type == 0 ? 2 : 4;
            return;
        }
        Resolve(mode, reg, 1);
        uint mm = 1u << (bit & 7);
        uint x = GetEa(1);
        Z = (x & mm) == 0;
        if (type == 0) return;
        if (eaKind == 3) throw new IllegalInstruction();
        switch (type)
        {
            case 1: x ^= mm; break;
            case 2: x &= ~mm; break;
            case 3: x |= mm; break;
        }
        SetEa(1, x);
    }

    private void Movep(ushort op)
    {
        int dr = (op >> 9) & 7, ar = op & 7;
        uint addr = A[ar] + (uint)(short)Fetch16();
        bool toMem = (op & 0x80) != 0, lng = (op & 0x40) != 0;
        int n = lng ? 4 : 2;
        if (toMem)
        {
            uint v = D[dr];
            for (int i = n - 1; i >= 0; i--) { Write8(addr, v >> (i * 8)); addr += 2; }
        }
        else
        {
            uint v = 0;
            for (int i = 0; i < n; i++) { v = (v << 8) | Read8(addr); addr += 2; }
            if (lng) D[dr] = v; else SetD(dr, 2, v);
        }
    }

    // ---- MOVE

    private void Move(ushort op, int sz)
    {
        int smode = (op >> 3) & 7, sreg = op & 7;
        int dmode = (op >> 6) & 7, dreg = (op >> 9) & 7;
        if (sz == 1 && smode == 1) throw new IllegalInstruction();
        uint v = ReadEa(smode, sreg, sz);
        if (dmode == 1)
        {
            if (sz == 1) throw new IllegalInstruction();
            A[dreg] = sz == 2 ? (uint)(short)v : v;   // MOVEA: 깃발 그대로
            return;
        }
        if (dmode == 7 && dreg > 1) throw new IllegalInstruction();
        Resolve(dmode, dreg, sz);
        Logic(v, sz);
        SetEa(sz, v);
    }

    // ---- 4줄

    private void Line4(ushort op, uint startPc)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        if ((op & 0x1C0) == 0x1C0)
        {
            // LEA
            A[(op >> 9) & 7] = ControlAddr(mode, reg);
            return;
        }
        if ((op & 0x1C0) == 0x180)
        {
            // CHK.W
            uint bound = ReadEa(mode, reg, 2);
            short dv = (short)D[(op >> 9) & 7];
            Cycles += 6;
            Z = dv == 0; V = C = false;   // 문서에 없는 깃발 (실제 68000과 같게)
            if (dv < 0) { N = true; TakeException(6, PC); return; }
            if (dv > (short)bound) { N = false; TakeException(6, PC); return; }
            return;
        }
        if ((op & 0x100) != 0) throw new IllegalInstruction();
        int szc = (op >> 6) & 3;
        switch ((op >> 8) & 0xF)
        {
            case 0x0:
                if (szc == 3)
                {
                    // MOVE from SR (68000: 권한 없음)
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, 2);
                    if (eaKind == 2) Read16(eaAddr);
                    SetEa(2, SR);
                    return;
                }
                {
                    int sz = SizeOf(szc);
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, sz);
                    uint d = GetEa(sz);
                    SetEa(sz, Sub(d, 0, sz, true));   // NEGX
                }
                return;
            case 0x2:
                if (szc == 3) throw new IllegalInstruction();
                {
                    int sz = SizeOf(szc);
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, sz);
                    if (eaKind == 2) Read(eaAddr, sz);   // 68000 CLR은 먼저 읽는다
                    SetEa(sz, 0);
                    N = V = C = false; Z = true;
                }
                return;
            case 0x4:
                if (szc == 3)
                {
                    // MOVE to CCR
                    Ccr = (int)ReadEa(mode, reg, 2) & 0x1F;
                    Cycles += 8;
                    return;
                }
                {
                    int sz = SizeOf(szc);
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, sz);
                    uint d = GetEa(sz);
                    SetEa(sz, Sub(d, 0, sz, false));   // NEG
                }
                return;
            case 0x6:
                if (szc == 3)
                {
                    // MOVE to SR
                    if (!S) { Privileged(startPc); return; }
                    SR = (ushort)ReadEa(mode, reg, 2);
                    Cycles += 8;
                    return;
                }
                {
                    int sz = SizeOf(szc);
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, sz);
                    uint d = ~GetEa(sz);
                    Logic(d, sz);
                    SetEa(sz, d);   // NOT
                }
                return;
            case 0x8:
                switch (szc)
                {
                    case 0:
                        {
                            // NBCD
                            if (mode == 1) throw new IllegalInstruction();
                            Resolve(mode, reg, 1);
                            uint d = GetEa(1);
                            SetEa(1, Sbcd(d, 0));
                            return;
                        }
                    case 1:
                        if (mode == 0)
                        {
                            uint v = D[reg];
                            v = (v >> 16) | (v << 16);
                            D[reg] = v;
                            Logic(v, 4);
                            return;
                        }
                        Push32(ControlAddr(mode, reg));   // PEA
                        return;
                    case 2:
                    case 3:
                        if (mode == 0)
                        {
                            if (szc == 2) { SetD(reg, 2, (uint)(sbyte)D[reg]); Logic(D[reg], 2); }
                            else { D[reg] = (uint)(short)D[reg]; Logic(D[reg], 4); }
                            return;
                        }
                        MovemToMem(op, mode, reg, szc == 3 ? 4 : 2);
                        return;
                }
                break;
            case 0xA:
                if (szc == 3)
                {
                    if (op == 0x4AFC) throw new IllegalInstruction();   // ILLEGAL
                    // TAS
                    if (mode == 1) throw new IllegalInstruction();
                    Resolve(mode, reg, 1);
                    uint d = GetEa(1);
                    Logic(d, 1);
                    SetEa(1, d | 0x80);
                    return;
                }
                {
                    int sz = SizeOf(szc);
                    if (mode == 1 && sz == 1) throw new IllegalInstruction();
                    Logic(ReadEa(mode, reg, sz), sz);   // TST
                }
                return;
            case 0xC:
                if (szc >= 2) { MovemToReg(op, mode, reg, szc == 3 ? 4 : 2); return; }
                throw new IllegalInstruction();
            case 0xE:
                Line4E(op, startPc, mode, reg);
                return;
        }
        throw new IllegalInstruction();
    }

    private void Line4E(ushort op, uint startPc, int mode, int reg)
    {
        int szc = (op >> 6) & 3;
        if (szc == 2) { uint t = ControlAddr(mode, reg); Push32(PC); PC = t; return; }   // JSR
        if (szc == 3) { PC = ControlAddr(mode, reg); return; }   // JMP
        if (szc != 1) throw new IllegalInstruction();
        switch ((op >> 3) & 7)
        {
            case 0:
            case 1:
                TakeException(32 + (op & 15), PC);   // TRAP #n
                return;
            case 2:
                {
                    // LINK
                    int disp = (short)Fetch16();
                    Push32(A[reg]);
                    A[reg] = A[7];
                    A[7] = (uint)(A[7] + disp);
                    return;
                }
            case 3:
                // UNLK
                A[7] = A[reg];
                A[reg] = Pop32();
                return;
            case 4:
                if (!S) { Privileged(startPc); return; }
                OtherSp = A[reg];   // MOVE An,USP
                return;
            case 5:
                if (!S) { Privileged(startPc); return; }
                A[reg] = OtherSp;   // MOVE USP,An
                return;
            case 6:
                switch (op & 7)
                {
                    case 0:
                        if (!S) { Privileged(startPc); return; }
                        Cycles += 124;   // RESET (바깥 장치 리셋: 흉내 안 함)
                        return;
                    case 1: return;   // NOP
                    case 2:
                        {
                            if (!S) { Privileged(startPc); return; }
                            uint v = Fetch16();
                            SR = (ushort)v;
                            Stopped = true;   // STOP
                            return;
                        }
                    case 3:
                        {
                            if (!S) { Privileged(startPc); return; }
                            uint sr = Pop16();
                            uint pc = Pop32();
                            SR = (ushort)sr;
                            PC = pc;   // RTE
                            return;
                        }
                    case 5: PC = Pop32(); return;   // RTS
                    case 6: if (V) TakeException(7, PC); return;   // TRAPV
                    case 7:
                        {
                            Ccr = (int)Pop16() & 0x1F;
                            PC = Pop32();   // RTR
                            return;
                        }
                }
                break;
        }
        throw new IllegalInstruction();
    }

    private void MovemToMem(ushort op, int mode, int reg, int sz)
    {
        uint list = Fetch16();
        if (mode == 4)
        {
            // −(An): 목록 비트 0 = A7 … 15 = D0, 높은 레지스터부터 내려 쓴다
            uint addr = A[reg];
            for (int i = 0; i < 16; i++)
            {
                if ((list & (1u << i)) == 0) continue;
                int r = 15 - i;
                uint v = r < 8 ? D[r] : A[r - 8];
                addr -= (uint)sz;
                Write(addr, sz, v);
            }
            A[reg] = addr;
            return;
        }
        if (mode < 2 || mode == 3 || (mode == 7 && reg > 1)) throw new IllegalInstruction();
        uint a = ControlAddr(mode, reg);
        for (int i = 0; i < 16; i++)
        {
            if ((list & (1u << i)) == 0) continue;
            uint v = i < 8 ? D[i] : A[i - 8];
            Write(a, sz, v);
            a += (uint)sz;
        }
    }

    private void MovemToReg(ushort op, int mode, int reg, int sz)
    {
        uint list = Fetch16();
        uint a;
        if (mode == 3) a = A[reg];
        else
        {
            if (mode < 2 || mode == 4 || (mode == 7 && reg > 3)) throw new IllegalInstruction();
            a = ControlAddr(mode, reg);
        }
        for (int i = 0; i < 16; i++)
        {
            if ((list & (1u << i)) == 0) continue;
            uint v = Read(a, sz);
            if (sz == 2) v = (uint)(short)v;
            if (i < 8) D[i] = v; else A[i - 8] = v;
            a += (uint)sz;
        }
        if (sz == 2) Read16(a);   // 68000: 하나 더 읽는다
        if (mode == 3) A[reg] = a;
    }

    // ---- 5줄: ADDQ · SUBQ · Scc · DBcc

    private void Line5(ushort op)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        int szc = (op >> 6) & 3;
        if (szc == 3)
        {
            int cc = (op >> 8) & 15;
            if (mode == 1)
            {
                // DBcc
                uint pc = PC;
                int disp = (short)Fetch16();
                if (Cond(cc)) { Cycles += 4; return; }
                uint w = (D[reg] - 1) & 0xFFFF;
                SetD(reg, 2, w);
                if (w != 0xFFFF) { PC = (uint)(pc + disp); Cycles += 2; }
                else Cycles += 6;
                return;
            }
            Resolve(mode, reg, 1);
            if (eaKind == 1 || eaKind == 3) throw new IllegalInstruction();
            if (eaKind == 2) Read8(eaAddr);
            bool t = Cond(cc);
            SetEa(1, t ? 0xFFu : 0u);
            if (t && eaKind == 0) Cycles += 2;
            return;
        }
        uint q = (uint)((op >> 9) & 7);
        if (q == 0) q = 8;
        int sz = SizeOf(szc);
        if (mode == 1)
        {
            if (sz == 1) throw new IllegalInstruction();
            A[reg] = (op & 0x100) != 0 ? A[reg] - q : A[reg] + q;   // 주소 레지스터: 깃발 그대로, 늘 32비트
            Cycles += 4;
            return;
        }
        Resolve(mode, reg, sz);
        if (eaKind == 3) throw new IllegalInstruction();
        uint d = GetEa(sz);
        SetEa(sz, (op & 0x100) != 0 ? Sub(q, d, sz, false) : Add(q, d, sz, false));
        if (mode == 0 && sz == 4) Cycles += 4;
    }

    // ---- 6줄: 가지

    private void Branch(ushort op)
    {
        int cc = (op >> 8) & 15;
        uint pc = PC;
        int disp = (sbyte)op;
        if (disp == 0) disp = (short)Fetch16();
        if (cc == 1)
        {
            // BSR
            Push32(PC);
            PC = (uint)(pc + disp);
            return;
        }
        if (Cond(cc)) { PC = (uint)(pc + disp); Cycles += 2; }
        else Cycles += 4;
    }

    // ---- 8줄: OR · DIVU · DIVS · SBCD

    private void Line8(ushort op)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dr = (op >> 9) & 7;
        int opm = (op >> 6) & 7;
        if (opm == 3 || opm == 7)
        {
            uint src = ReadEa(mode, reg, 2);
            if (mode == 1) throw new IllegalInstruction();
            if ((src & 0xFFFF) == 0) { TakeException(5, PC); return; }
            if (opm == 3)
            {
                uint q = D[dr] / (src & 0xFFFF), r = D[dr] % (src & 0xFFFF);
                Cycles += 136;
                if (q > 0xFFFF) { V = true; C = false; N = true; return; }
                D[dr] = (r << 16) | q;
                N = (q & 0x8000) != 0; Z = q == 0; V = C = false;
            }
            else
            {
                long dv = (int)D[dr], s = (short)src;
                long q = dv / s, r = dv % s;
                Cycles += 154;
                if (q > 32767 || q < -32768) { V = true; C = false; N = true; return; }
                D[dr] = ((uint)(r & 0xFFFF) << 16) | (uint)(q & 0xFFFF);
                N = (q & 0x8000) != 0; Z = (q & 0xFFFF) == 0; V = C = false;
            }
            return;
        }
        if ((op & 0x1F0) == 0x100)
        {
            // SBCD
            if ((op & 8) == 0) SetD(dr, 1, Sbcd(D[reg] & 0xFF, D[dr] & 0xFF));
            else
            {
                A[reg] -= (uint)(reg == 7 ? 2 : 1); uint s = Read8(A[reg]);
                A[dr] -= (uint)(dr == 7 ? 2 : 1); uint d = Read8(A[dr]);
                Write8(A[dr], Sbcd(s, d));
            }
            return;
        }
        int sz = SizeOf(opm & 3);
        if ((opm & 4) == 0)
        {
            if (mode == 1) throw new IllegalInstruction();
            uint v = ReadEa(mode, reg, sz) | D[dr];
            Logic(v, sz);
            SetD(dr, sz, v);
        }
        else
        {
            Resolve(mode, reg, sz);
            if (eaKind != 2) throw new IllegalInstruction();
            uint v = GetEa(sz) | D[dr];
            Logic(v, sz);
            SetEa(sz, v);
        }
    }

    private uint Abcd(uint s, uint d)
    {
        int x = X ? 1 : 0;
        int lo = (int)(s & 15) + (int)(d & 15) + x;
        int hi = (int)(s & 0xF0) + (int)(d & 0xF0);
        int r = hi + lo;
        if (lo > 9) r += 6;
        bool c = r > 0x99;
        if (c) r += 0x60;
        C = X = c;
        if ((r & 0xFF) != 0) Z = false;
        N = (r & 0x80) != 0;
        V = false;
        Cycles += 2;
        return (uint)r & 0xFF;
    }

    private uint Sbcd(uint s, uint d)
    {
        int x = X ? 1 : 0;
        int lo = (int)(d & 15) - (int)(s & 15) - x;
        int r = (int)(d & 0xF0) - (int)(s & 0xF0) + lo;
        if (lo < 0) r -= 6;
        bool c = r < 0;
        if (c) r -= 0x60;
        C = X = c;
        if ((r & 0xFF) != 0) Z = false;
        N = (r & 0x80) != 0;
        V = false;
        Cycles += 2;
        return (uint)r & 0xFF;
    }

    // ---- 9 · D줄: SUB · ADD

    private void AddSub(ushort op, bool add)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dr = (op >> 9) & 7;
        int opm = (op >> 6) & 7;
        if (opm == 3 || opm == 7)
        {
            // ADDA · SUBA: 깃발 그대로
            int sz = opm == 3 ? 2 : 4;
            uint s = ReadEa(mode, reg, sz);
            if (sz == 2) s = (uint)(short)s;
            A[dr] = add ? A[dr] + s : A[dr] - s;
            Cycles += 4;
            return;
        }
        int size = SizeOf(opm & 3);
        if ((opm & 4) != 0 && (mode == 0 || mode == 1))
        {
            // ADDX · SUBX
            if (mode == 0)
            {
                uint r = add ? Add(D[reg], D[dr], size, true) : Sub(D[reg], D[dr], size, true);
                SetD(dr, size, r);
                if (size == 4) Cycles += 4;
            }
            else
            {
                A[reg] -= (uint)(reg == 7 && size == 1 ? 2 : size); uint s = Read(A[reg], size);
                A[dr] -= (uint)(dr == 7 && size == 1 ? 2 : size); uint d = Read(A[dr], size);
                Write(A[dr], size, add ? Add(s, d, size, true) : Sub(s, d, size, true));
            }
            return;
        }
        if ((opm & 4) == 0)
        {
            if (mode == 1 && size == 1) throw new IllegalInstruction();
            uint s = ReadEa(mode, reg, size);
            SetD(dr, size, add ? Add(s, D[dr], size, false) : Sub(s, D[dr], size, false));
            if (size == 4) Cycles += 2;
        }
        else
        {
            Resolve(mode, reg, size);
            if (eaKind != 2) throw new IllegalInstruction();
            uint d = GetEa(size);
            SetEa(size, add ? Add(D[dr], d, size, false) : Sub(D[dr], d, size, false));
        }
    }

    // ---- B줄: CMP · CMPA · CMPM · EOR

    private void LineB(ushort op)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dr = (op >> 9) & 7;
        int opm = (op >> 6) & 7;
        if (opm == 3 || opm == 7)
        {
            int sz = opm == 3 ? 2 : 4;
            uint s = ReadEa(mode, reg, sz);
            if (sz == 2) s = (uint)(short)s;
            Cmp(s, A[dr], 4);
            Cycles += 2;
            return;
        }
        int size = SizeOf(opm & 3);
        if ((opm & 4) == 0)
        {
            if (mode == 1 && size == 1) throw new IllegalInstruction();
            Cmp(ReadEa(mode, reg, size), D[dr], size);
            return;
        }
        if (mode == 1)
        {
            // CMPM (Ay)+,(Ax)+
            uint s = Read(A[reg], size); A[reg] += (uint)(reg == 7 && size == 1 ? 2 : size);
            uint d = Read(A[dr], size); A[dr] += (uint)(dr == 7 && size == 1 ? 2 : size);
            Cmp(s, d, size);
            return;
        }
        Resolve(mode, reg, size);
        if (eaKind == 1 || eaKind == 3) throw new IllegalInstruction();
        uint v = GetEa(size) ^ D[dr];
        Logic(v, size);
        SetEa(size, v);
    }

    // ---- C줄: AND · MULU · MULS · ABCD · EXG

    private void LineC(ushort op)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dr = (op >> 9) & 7;
        int opm = (op >> 6) & 7;
        if (opm == 3 || opm == 7)
        {
            if (mode == 1) throw new IllegalInstruction();
            uint s = ReadEa(mode, reg, 2);
            uint r = opm == 3 ? (s & 0xFFFF) * (D[dr] & 0xFFFF) : (uint)((short)s * (short)D[dr]);
            D[dr] = r;
            Logic(r, 4);
            Cycles += 54;
            return;
        }
        if ((op & 0x1F0) == 0x100)
        {
            // ABCD
            if ((op & 8) == 0) SetD(dr, 1, Abcd(D[reg] & 0xFF, D[dr] & 0xFF));
            else
            {
                A[reg] -= (uint)(reg == 7 ? 2 : 1); uint s = Read8(A[reg]);
                A[dr] -= (uint)(dr == 7 ? 2 : 1); uint d = Read8(A[dr]);
                Write8(A[dr], Abcd(s, d));
            }
            return;
        }
        if ((op & 0x1F8) == 0x140) { uint t = D[dr]; D[dr] = D[reg]; D[reg] = t; Cycles += 2; return; }
        if ((op & 0x1F8) == 0x148) { uint t = A[dr]; A[dr] = A[reg]; A[reg] = t; Cycles += 2; return; }
        if ((op & 0x1F8) == 0x188) { uint t = D[dr]; D[dr] = A[reg]; A[reg] = t; Cycles += 2; return; }
        int size = SizeOf(opm & 3);
        if ((opm & 4) == 0)
        {
            if (mode == 1) throw new IllegalInstruction();
            uint v = ReadEa(mode, reg, size) & D[dr];
            Logic(v, size);
            SetD(dr, size, v);
        }
        else
        {
            Resolve(mode, reg, size);
            if (eaKind != 2) throw new IllegalInstruction();
            uint v = GetEa(size) & D[dr];
            Logic(v, size);
            SetEa(size, v);
        }
    }

    // ---- E줄: 시프트 · 회전

    private void Shift(ushort op)
    {
        int szc = (op >> 6) & 3;
        if (szc == 3)
        {
            // 메모리: 한 칸, 워드
            int type = (op >> 9) & 3;
            if ((op & 0x800) != 0) throw new IllegalInstruction();
            Resolve((op >> 3) & 7, op & 7, 2);
            if (eaKind != 2) throw new IllegalInstruction();
            uint v = GetEa(2);
            SetEa(2, ShiftOp(type, (op & 0x100) != 0, v, 1, 2));
            return;
        }
        int sz = SizeOf(szc);
        int reg = op & 7;
        int cnt = (op >> 9) & 7;
        int count = (op & 0x20) != 0 ? (int)(D[cnt] & 63) : (cnt == 0 ? 8 : cnt);
        Cycles += 2 + 2 * count + (sz == 4 ? 2 : 0);
        SetD(reg, sz, ShiftOp((op >> 3) & 3, (op & 0x100) != 0, D[reg] & Mask(sz), count, sz));
    }

    private uint ShiftOp(int type, bool left, uint v, int count, int sz)
    {
        uint m = Mask(sz), msb = Msb(sz);
        int bits = sz * 8;
        v &= m;
        V = false;
        switch (type)
        {
            case 0:
                // ASL · ASR
                if (count == 0) { C = false; break; }
                if (left)
                {
                    bool ov = false;
                    for (int i = 0; i < count; i++)
                    {
                        bool c = (v & msb) != 0;
                        uint nv = (v << 1) & m;
                        if (((nv ^ v) & msb) != 0) ov = true;
                        v = nv; C = X = c;
                    }
                    V = ov;
                }
                else
                {
                    bool sign = (v & msb) != 0;
                    for (int i = 0; i < count; i++)
                    {
                        C = X = (v & 1) != 0;
                        v = (v >> 1) | (sign ? msb : 0);
                    }
                }
                break;
            case 1:
                // LSL · LSR
                if (count == 0) { C = false; break; }
                for (int i = 0; i < count; i++)
                {
                    if (left) { C = X = (v & msb) != 0; v = (v << 1) & m; }
                    else { C = X = (v & 1) != 0; v >>= 1; }
                }
                break;
            case 2:
                // ROXL · ROXR
                if (count == 0) { C = X; break; }
                for (int i = 0; i < count; i++)
                {
                    bool x = X;
                    if (left) { X = (v & msb) != 0; v = ((v << 1) | (x ? 1u : 0u)) & m; }
                    else { X = (v & 1) != 0; v = (v >> 1) | (x ? msb : 0); }
                }
                C = X;
                break;
            default:
                // ROL · ROR (X 그대로)
                if (count == 0) { C = false; break; }
                {
                    int n = count % bits;
                    if (left)
                    {
                        if (n != 0) v = ((v << n) | (v >> (bits - n))) & m;
                        C = (v & 1) != 0;
                    }
                    else
                    {
                        if (n != 0) v = ((v >> n) | (v << (bits - n))) & m;
                        C = (v & msb) != 0;
                    }
                }
                break;
        }
        N = (v & msb) != 0; Z = v == 0;
        return v;
    }
}
