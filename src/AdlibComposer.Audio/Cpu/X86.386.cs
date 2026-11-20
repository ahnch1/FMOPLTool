using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>
/// 80386 리얼 모드 (<see cref="Is386"/>): PC-9821용 드라이버처럼 386을 확인하고 32비트 명령을 쓰는 프로그램을 위해.
/// <para>32비트 레지스터 = 16비트 레지스터 + 위 16비트 (<c>hi</c>, 16비트 쓰기는 위를 그대로 둠), FS · GS, 66h (피연산자 32비트) · 67h (주소 32비트, SIB) 접두어,
/// 0F 확장 (MOVZX · MOVSX · 가까운 Jcc · SETcc · BT 계열 · SHLD · SHRD · BSF · BSR · IMUL r,rm · PUSH/POP FS · GS · LSS · LFS · LGS).
/// FLAGS bit12~14 (IOPL · NT)는 쓸 수 있고 (386 판별), EFLAGS 위 (AC · ID)는 늘 0 (486이 아님, CPUID 없음). 보호 모드 · 페이징은 없다.</para>
/// <para>32비트 판이 없는 명령에 66h가 붙으면 모르는 명령으로 멈춘다 (조용히 틀리지 않게).</para>
/// </summary>
public sealed partial class X86
{
    /// <summary>386 리얼 모드로 (기본 꺼짐 = 286).</summary>
    public bool Is386;

    /// <summary>32비트 레지스터의 위 16비트 (0 EAX 1 ECX 2 EDX 3 EBX 4 ESP 5 EBP 6 ESI 7 EDI).</summary>
    private readonly int[] hi = new int[8];
    private bool op32, addr32;

    private int FlagMask { get { return Is386 ? 0x7FD5 : 0x0FD5; } }

    public uint GetReg32(int r) { return ((uint)hi[r & 7] << 16) | (uint)GetReg16(r & 7); }
    public void SetReg32(int r, uint v) { SetReg16(r & 7, (int)(v & 0xFFFF)); hi[r & 7] = (int)(v >> 16); }
    public uint EAX { get { return GetReg32(0); } set { SetReg32(0, value); } }
    public uint ECX { get { return GetReg32(1); } set { SetReg32(1, value); } }
    public uint EDX { get { return GetReg32(2); } set { SetReg32(2, value); } }
    public uint EBX { get { return GetReg32(3); } set { SetReg32(3, value); } }

    public uint Read32(int seg, int off) { return (uint)Read16(seg, off) | ((uint)Read16(seg, off + 2) << 16); }
    public void Write32(int seg, int off, uint v) { Write16(seg, off, (int)(v & 0xFFFF)); Write16(seg, off + 2, (int)(v >> 16)); }
    private uint Fetch32() { uint v = (uint)Fetch16(); return v | ((uint)Fetch16() << 16); }
    private void Push32(uint v) { SP = (SP - 4) & 0xFFFF; Write32(SS, SP, v); }
    private uint Pop32() { uint v = Read32(SS, SP); SP = (SP + 4) & 0xFFFF; return v; }

    private uint Rm32() { return mMod == 3 ? GetReg32(mRm) : Read32(mSeg, mOff); }
    private void SetRm32(uint v) { if (mMod == 3) SetReg32(mRm, v); else Write32(mSeg, mOff, v); }

    /// <summary>67h: 32비트 주소 ModR/M (+ SIB). 리얼 모드라 오프셋은 아래 16비트만 (세그먼트 한도 64KB).</summary>
    private void ModRm32()
    {
        uint ea; bool bp = false;
        int rm = mRm;
        if (rm == 4)
        {
            int sib = Fetch8();
            int scale = sib >> 6, index = (sib >> 3) & 7, bas = sib & 7;
            uint b;
            if (bas == 5 && mMod == 0) b = Fetch32();
            else { b = GetReg32(bas); bp = bas == 4 || bas == 5; }
            ea = b + (index == 4 ? 0u : GetReg32(index) << scale);
        }
        else if (rm == 5 && mMod == 0) ea = Fetch32();
        else { ea = GetReg32(rm); bp = rm == 5; }
        if (mMod == 1) ea += (uint)(sbyte)(byte)Fetch8();
        else if (mMod == 2) ea += Fetch32();
        mOff = (int)(ea & 0xFFFF);
        mSeg = segOverride >= 0 ? GetSeg(segOverride) : bp ? SS : DS;
        Cycles += 2;
    }

    // ------------------------------------------------------------ 32비트 깃발

    private void SzP32(uint r)
    {
        SetFlag(ZF, r == 0);
        SetFlag(SF, (r & 0x80000000u) != 0);
        SetFlag(PF, Parity[r & 0xFF]);
    }

    private uint Add32(uint a, uint b, uint c)
    {
        ulong r = (ulong)a + b + c;
        uint v = (uint)r;
        SetFlag(CF, r > 0xFFFFFFFFul);
        SetFlag(AF, ((a ^ b ^ v) & 0x10) != 0);
        SetFlag(OF, ((a ^ v) & (b ^ v) & 0x80000000u) != 0);
        SzP32(v);
        return v;
    }

    private uint Sub32(uint a, uint b, uint c)
    {
        long r = (long)a - b - c;
        uint v = (uint)r;
        SetFlag(CF, r < 0);
        SetFlag(AF, ((a ^ b ^ v) & 0x10) != 0);
        SetFlag(OF, ((a ^ b) & (a ^ v) & 0x80000000u) != 0);
        SzP32(v);
        return v;
    }

    private uint Logic32(uint r) { Flags &= ~(CF | OF | AF); SzP32(r); return r; }

    private uint Alu32(int op, uint a, uint b)
    {
        uint c = GetFlag(CF) ? 1u : 0u;
        switch (op)
        {
            case 0: return Add32(a, b, 0);
            case 1: return Logic32(a | b);
            case 2: return Add32(a, b, c);
            case 3: return Sub32(a, b, c);
            case 4: return Logic32(a & b);
            case 5: return Sub32(a, b, 0);
            case 6: return Logic32(a ^ b);
            default: Sub32(a, b, 0); return a;
        }
    }

    private uint Shift32(int op, uint v, int count)
    {
        count &= 0x1F;
        if (count == 0) return v;
        Cycles += count;
        switch (op)
        {
            case 0: v = (v << count) | (v >> (32 - count)); SetFlag(CF, (v & 1) != 0); SetFlag(OF, ((v >> 31) != 0) ^ GetFlag(CF)); return v;
            case 1: v = (v >> count) | (v << (32 - count)); SetFlag(CF, (v >> 31) != 0); SetFlag(OF, (((v ^ (v << 1)) >> 31) & 1) != 0); return v;
            case 2:
                for (int i = 0; i < count; i++) { bool c = (v >> 31) != 0; v = (v << 1) | (GetFlag(CF) ? 1u : 0u); SetFlag(CF, c); }
                SetFlag(OF, ((v >> 31) != 0) ^ GetFlag(CF)); return v;
            case 3:
                for (int i = 0; i < count; i++) { bool c = (v & 1) != 0; v = (v >> 1) | (GetFlag(CF) ? 0x80000000u : 0); SetFlag(CF, c); }
                SetFlag(OF, (((v ^ (v << 1)) >> 31) & 1) != 0); return v;
            case 4: case 6:
                {
                    SetFlag(CF, ((v >> (32 - count)) & 1) != 0);
                    uint r = v << count;
                    SetFlag(OF, ((r >> 31) != 0) ^ GetFlag(CF));
                    SzP32(r); SetFlag(AF, false); return r;
                }
            case 5:
                {
                    SetFlag(CF, ((v >> (count - 1)) & 1) != 0);
                    SetFlag(OF, count == 1 && (v >> 31) != 0);
                    uint r = v >> count;
                    SzP32(r); SetFlag(AF, false); return r;
                }
            default:
                {
                    int s = (int)v;
                    SetFlag(CF, ((s >> (count - 1)) & 1) != 0);
                    SetFlag(OF, false);
                    uint r = (uint)(s >> count);
                    SzP32(r); SetFlag(AF, false); return r;
                }
        }
    }

    // ------------------------------------------------------------ 66h · 67h가 붙은 명령

    /// <summary>66h (피연산자 32비트) · 67h (주소 32비트)가 붙은 명령. 크기와 상관없는 명령은 false (보통 길로), 32비트 판이 없으면 모르는 명령.</summary>
    private bool Execute386(int op)
    {
        if (!op32)
        {
            // 67h만: 문자열 · LOOP · JCXZ (ECX · ESI · EDI)는 아직 없음, 그 밖은 ModR/M이 32비트 주소로 풀려 보통 길로
            if ((op >= 0xA4 && op <= 0xAF) || (op >= 0x6C && op <= 0x6F) || (op >= 0xE0 && op <= 0xE3) || op == 0xD7) { Unknown(op); return true; }
            return false;
        }
        if (op < 0x40 && (op & 7) < 6 && (op & 1) != 0)
        {
            int alu = op >> 3;
            switch (op & 7)
            {
                case 1: { ModRm(); uint r = Alu32(alu, Rm32(), GetReg32(mReg)); if (alu != 7) SetRm32(r); return true; }
                case 3: { ModRm(); uint r = Alu32(alu, GetReg32(mReg), Rm32()); if (alu != 7) SetReg32(mReg, r); return true; }
                default: { uint r = Alu32(alu, EAX, Fetch32()); if (alu != 7) EAX = r; return true; }
            }
        }
        if (op >= 0x40 && op <= 0x47) { bool c = GetFlag(CF); SetReg32(op & 7, Add32(GetReg32(op & 7), 1, 0)); SetFlag(CF, c); return true; }
        if (op >= 0x48 && op <= 0x4F) { bool c = GetFlag(CF); SetReg32(op & 7, Sub32(GetReg32(op & 7), 1, 0)); SetFlag(CF, c); return true; }
        if (op >= 0x50 && op <= 0x57) { Push32(GetReg32(op & 7)); return true; }
        if (op >= 0x58 && op <= 0x5F) { uint v = Pop32(); SetReg32(op & 7, v); return true; }
        if (op >= 0x91 && op <= 0x97) { uint t = EAX; EAX = GetReg32(op & 7); SetReg32(op & 7, t); return true; }
        if (op >= 0xB8 && op <= 0xBF) { SetReg32(op & 7, Fetch32()); return true; }
        switch (op)
        {
            case 0x0F: Op0F(); return true;
            case 0x60:
                {
                    uint sp = GetReg32(4);
                    Push32(EAX); Push32(ECX); Push32(EDX); Push32(EBX); Push32(sp); Push32(GetReg32(5)); Push32(GetReg32(6)); Push32(GetReg32(7));
                    Cycles += 15; return true;
                }
            case 0x61:
                SetReg32(7, Pop32()); SetReg32(6, Pop32()); SetReg32(5, Pop32()); Pop32(); EBX = Pop32(); EDX = Pop32(); ECX = Pop32(); EAX = Pop32();
                Cycles += 15; return true;
            case 0x68: Push32(Fetch32()); return true;
            case 0x6A: Push32((uint)(sbyte)(byte)Fetch8()); return true;
            case 0x69: case 0x6B:
                {
                    ModRm();
                    long a = (int)Rm32();
                    long b = op == 0x69 ? (int)Fetch32() : (sbyte)(byte)Fetch8();
                    long r = a * b;
                    SetReg32(mReg, (uint)r);
                    bool of = r != (int)r;
                    SetFlag(CF, of); SetFlag(OF, of);
                    Cycles += 20; return true;
                }
            case 0x81: case 0x83:
                {
                    ModRm();
                    uint a = Rm32();
                    uint b = op == 0x81 ? Fetch32() : (uint)(sbyte)(byte)Fetch8();
                    uint r = Alu32(mReg, a, b);
                    if (mReg != 7) SetRm32(r);
                    return true;
                }
            case 0x85: ModRm(); Logic32(Rm32() & GetReg32(mReg)); return true;
            case 0x87: { ModRm(); uint t = Rm32(); SetRm32(GetReg32(mReg)); SetReg32(mReg, t); return true; }
            case 0x89: ModRm(); SetRm32(GetReg32(mReg)); return true;
            case 0x8B: ModRm(); SetReg32(mReg, Rm32()); return true;
            case 0x8C: ModRm(); if (mReg > 5) { Unknown(op); return true; } if (mMod == 3) SetReg32(mRm, (uint)GetSeg(mReg)); else Write16(mSeg, mOff, GetSeg(mReg)); return true;
            case 0x8D: ModRm(); if (mMod != 3) SetReg32(mReg, (uint)mOff); return true;
            case 0x8F: { uint v = Pop32(); ModRm(); SetRm32(v); return true; }
            case 0x98: EAX = (uint)(short)AX; return true;                          // CWDE
            case 0x99: EDX = (EAX & 0x80000000u) != 0 ? 0xFFFFFFFFu : 0; return true;   // CDQ
            case 0x9C: Push32((uint)(Flags & 0x7FFF | 0x0002)); return true;        // PUSHFD (AC · ID = 0)
            case 0x9D: Flags = ((int)Pop32() & FlagMask) | 0x0002; return true;    // POPFD
            case 0xA1: EAX = Read32(DataSeg, Fetch16()); return true;
            case 0xA3: Write32(DataSeg, Fetch16(), EAX); return true;
            case 0xA5: case 0xA7: case 0xAB: case 0xAD: case 0xAF: StringOp32(op); return true;
            case 0xA9: Logic32(EAX & Fetch32()); return true;
            case 0xC1: case 0xD1: case 0xD3:
                {
                    ModRm();
                    uint v = Rm32();
                    int cnt = op == 0xC1 ? Fetch8() : op == 0xD1 ? 1 : CL;
                    SetRm32(Shift32(mReg, v, cnt));
                    return true;
                }
            case 0xC7: ModRm(); SetRm32(Fetch32()); return true;
            case 0xE5: { int p = Fetch8(); EAX = (uint)(bus.In(p) | (bus.In(p + 1) << 8) | (bus.In(p + 2) << 16)) | ((uint)bus.In(p + 3) << 24); return true; }
            case 0xE7: { int p = Fetch8(); uint v = EAX; for (int i = 0; i < 4; i++) bus.Out(p + i, (byte)(v >> (8 * i))); return true; }
            case 0xED: { uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)bus.In((DX + i) & 0xFFFF) << (8 * i); EAX = v; return true; }
            case 0xEF: { uint v = EAX; for (int i = 0; i < 4; i++) bus.Out((DX + i) & 0xFFFF, (byte)(v >> (8 * i))); return true; }
            case 0xF7: Group3_32(); return true;
            case 0xFF:
                {
                    ModRm();
                    switch (mReg)
                    {
                        case 0: { bool c = GetFlag(CF); SetRm32(Add32(Rm32(), 1, 0)); SetFlag(CF, c); return true; }
                        case 1: { bool c = GetFlag(CF); SetRm32(Sub32(Rm32(), 1, 0)); SetFlag(CF, c); return true; }
                        case 6: Push32(Rm32()); return true;
                        default: Unknown(op); return true;
                    }
                }
        }
        // 크기에 따라 달라지는데 32비트 판이 없는 것
        if (op == 0x6D || op == 0x6F || op == 0x9A || op == 0xC2 || op == 0xC3 || op == 0xC4 || op == 0xC5 || op == 0xC8 || op == 0xC9 || op == 0xCA || op == 0xCB || op == 0xCF
            || op == 0xE8 || op == 0xE9 || op == 0x62 || op == 0x8E) { Unknown(op); return true; }
        return false;   // 8비트 · 분기 · 세그먼트 등은 크기와 상관없음
    }

    private void Group3_32()
    {
        ModRm();
        uint v = Rm32();
        switch (mReg)
        {
            case 0: case 1: Logic32(v & Fetch32()); return;
            case 2: SetRm32(~v); return;
            case 3: { uint r = Sub32(0, v, 0); SetFlag(CF, v != 0); SetRm32(r); return; }
            case 4: { ulong r = (ulong)EAX * v; EAX = (uint)r; EDX = (uint)(r >> 32); bool f = EDX != 0; SetFlag(CF, f); SetFlag(OF, f); Cycles += 20; return; }
            case 5: { long r = (long)(int)EAX * (int)v; EAX = (uint)r; EDX = (uint)((ulong)r >> 32); bool f = r != (int)r; SetFlag(CF, f); SetFlag(OF, f); Cycles += 20; return; }
            case 6:
                {
                    if (v == 0) { DivideError(); return; }
                    ulong n = ((ulong)EDX << 32) | EAX;
                    ulong q = n / v;
                    if (q > 0xFFFFFFFFul) { DivideError(); return; }
                    EAX = (uint)q; EDX = (uint)(n % v); Cycles += 38; return;
                }
            default:
                {
                    if (v == 0) { DivideError(); return; }
                    long n = (long)(((ulong)EDX << 32) | EAX);
                    long d = (int)v;
                    if (n == long.MinValue && d == -1) { DivideError(); return; }
                    long q = n / d;
                    if (q > int.MaxValue || q < int.MinValue) { DivideError(); return; }
                    EAX = (uint)q; EDX = (uint)(n % d); Cycles += 43; return;
                }
        }
    }

    private void StringOp32(int op)
    {
        int d = GetFlag(DF) ? -4 : 4;
        int src = DataSeg;
        if (rep != 0 && CX == 0) return;
        do
        {
            switch (op)
            {
                case 0xA5: Write32(ES, DI, Read32(src, SI)); SI = (SI + d) & 0xFFFF; DI = (DI + d) & 0xFFFF; break;
                case 0xA7: Sub32(Read32(src, SI), Read32(ES, DI), 0); SI = (SI + d) & 0xFFFF; DI = (DI + d) & 0xFFFF; break;
                case 0xAB: Write32(ES, DI, EAX); DI = (DI + d) & 0xFFFF; break;
                case 0xAD: EAX = Read32(src, SI); SI = (SI + d) & 0xFFFF; break;
                case 0xAF: Sub32(EAX, Read32(ES, DI), 0); DI = (DI + d) & 0xFFFF; break;
            }
            Cycles += 4;
            if (rep == 0) return;
            CX = (CX - 1) & 0xFFFF;
            if (op == 0xA7 || op == 0xAF)
            {
                if (rep == 2 && !GetFlag(ZF)) return;
                if (rep == 1 && GetFlag(ZF)) return;
            }
        } while (CX != 0);
    }

    // ------------------------------------------------------------ 0F 확장 (386)

    private uint RmSized() { return op32 ? Rm32() : (uint)Rm16(); }
    private void SetRmSized(uint v) { if (op32) SetRm32(v); else SetRm16((int)(v & 0xFFFF)); }
    private uint RegSized(int r) { return op32 ? GetReg32(r) : (uint)GetReg16(r); }
    private void SetRegSized(int r, uint v) { if (op32) SetReg32(r, v); else SetReg16(r, (int)(v & 0xFFFF)); }

    /// <summary>386 0F 명령. 처리했으면 true.</summary>
    private bool Op0F386(int op)
    {
        if (op >= 0x80 && op <= 0x8F)   // Jcc 가까이 (16비트 변위, 66h면 32비트)
        {
            int e = op32 ? (int)Fetch32() : (short)Fetch16();
            if (Cond(op & 15)) { IP = (IP + e) & 0xFFFF; Cycles += 6; }
            return true;
        }
        if (op >= 0x90 && op <= 0x9F) { ModRm(); SetRm8(Cond(op & 15) ? 1 : 0); return true; }   // SETcc
        int bits = op32 ? 32 : 16;
        switch (op)
        {
            case 0xA0: if (op32) Push32((uint)FS); else Push(FS); return true;
            case 0xA1: FS = op32 ? (int)(Pop32() & 0xFFFF) : Pop(); return true;
            case 0xA8: if (op32) Push32((uint)GS); else Push(GS); return true;
            case 0xA9: GS = op32 ? (int)(Pop32() & 0xFFFF) : Pop(); return true;
            case 0xB6: ModRm(); SetRegSized(mReg, (uint)Rm8()); return true;                       // MOVZX r, rm8
            case 0xB7: ModRm(); SetRegSized(mReg, (uint)Rm16()); return true;                      // MOVZX r, rm16
            case 0xBE: ModRm(); SetRegSized(mReg, (uint)(sbyte)(byte)Rm8()); return true;          // MOVSX r, rm8
            case 0xBF: ModRm(); SetRegSized(mReg, (uint)(short)Rm16()); return true;               // MOVSX r, rm16
            case 0xAF:                                                                              // IMUL r, rm
                {
                    ModRm();
                    long a = op32 ? (int)GetReg32(mReg) : (short)GetReg16(mReg), b = op32 ? (int)Rm32() : (short)Rm16();
                    long r = a * b;
                    SetRegSized(mReg, (uint)r);
                    bool of = op32 ? r != (int)r : r != (short)r;
                    SetFlag(CF, of); SetFlag(OF, of);
                    Cycles += 20; return true;
                }
            case 0xA3: case 0xAB: case 0xB3: case 0xBB:                                             // BT · BTS · BTR · BTC r
                {
                    ModRm();
                    int bit = (int)RegSized(mReg);
                    if (mMod != 3)
                    {
                        // 메모리: 비트 번호가 범위를 넘으면 주소가 옮겨감 (부호 있음)
                        int n = op32 ? bit : (short)bit;
                        mOff = (mOff + (n >> (op32 ? 5 : 4)) * (bits / 8)) & 0xFFFF;
                        bit = n & (bits - 1);
                    }
                    else bit &= bits - 1;
                    BitOp(op == 0xA3 ? 0 : op == 0xAB ? 1 : op == 0xB3 ? 2 : 3, bit);
                    return true;
                }
            case 0xBA:                                                                              // BT · BTS · BTR · BTC 즉치
                {
                    ModRm();
                    if (mReg < 4) { Unknown(0x0FBA); return true; }
                    BitOp(mReg - 4, Fetch8() & (bits - 1));
                    return true;
                }
            case 0xBC: case 0xBD:                                                                   // BSF · BSR
                {
                    ModRm();
                    uint v = RmSized();
                    if (v == 0) { SetFlag(ZF, true); return true; }
                    SetFlag(ZF, false);
                    int i;
                    if (op == 0xBC) { i = 0; while (((v >> i) & 1) == 0) i++; }
                    else { i = bits - 1; while (((v >> i) & 1) == 0) i--; }
                    SetRegSized(mReg, (uint)i);
                    return true;
                }
            case 0xA4: case 0xA5: case 0xAC: case 0xAD:                                             // SHLD · SHRD
                {
                    ModRm();
                    int cnt = (op == 0xA4 || op == 0xAC ? Fetch8() : CL) & 31;
                    if (cnt == 0) return true;
                    ulong dst = RmSized(), src = RegSized(mReg);
                    ulong r; bool cf;
                    if (op <= 0xA5)
                    {
                        ulong both = (dst << bits) | src;
                        cf = ((both >> (2 * bits - cnt)) & 1) != 0;
                        r = (both << cnt) >> bits;
                    }
                    else
                    {
                        ulong both = (src << bits) | dst;
                        cf = ((both >> (cnt - 1)) & 1) != 0;
                        r = both >> cnt;
                    }
                    uint m = op32 ? 0xFFFFFFFFu : 0xFFFFu;
                    uint res = (uint)r & m;
                    SetFlag(CF, cf);
                    SetFlag(OF, ((res ^ (uint)dst) & (op32 ? 0x80000000u : 0x8000u)) != 0);
                    SetFlag(ZF, res == 0); SetFlag(SF, (res & (op32 ? 0x80000000u : 0x8000u)) != 0); SetFlag(PF, Parity[res & 0xFF]);
                    SetRmSized(res);
                    Cycles += 3; return true;
                }
            case 0xB2: case 0xB4: case 0xB5:                                                        // LSS · LFS · LGS
                {
                    ModRm();
                    if (mMod == 3) { Unknown(0x0F00 | op); return true; }
                    uint off = op32 ? Read32(mSeg, mOff) : (uint)Read16(mSeg, mOff);
                    int seg = Read16(mSeg, mOff + (op32 ? 4 : 2));
                    SetRegSized(mReg, off);
                    if (op == 0xB2) { SS = seg; interruptShadow = true; } else if (op == 0xB4) FS = seg; else GS = seg;
                    return true;
                }
        }
        return false;
    }

    /// <summary>BT 계열: kind 0 BT · 1 BTS · 2 BTR · 3 BTC, CF = 원래 비트.</summary>
    private void BitOp(int kind, int bit)
    {
        uint v = RmSized();
        uint mask = 1u << bit;
        SetFlag(CF, (v & mask) != 0);
        if (kind == 0) return;
        v = kind == 1 ? v | mask : kind == 2 ? v & ~mask : v ^ mask;
        SetRmSized(v);
    }
}
