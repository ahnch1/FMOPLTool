using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>x86 CPU가 보는 메모리 (실제 주소 20비트) · I/O 포트 (바이트 단위, 워드 = 포트 · 포트+1).</summary>
public interface IX86Bus
{
    byte Read(int linear);
    void Write(int linear, byte value);
    byte In(int port);
    void Out(int port, byte value);
}

/// <summary>
/// Intel 80286 리얼 모드 CPU (8086 명령 전부 + 80186 명령 PUSHA · POPA · ENTER · LEAVE · BOUND · INS · OUTS · IMUL 즉치 · 시프트 즉치 · PUSH 즉치,
/// 286의 SMSW · LMSW). hoot의 PC-98 DOS 흉내(pc98dos = 80286 + MS-DOS)처럼 사운드 드라이버를 돌리기 위한 것이라 보호 모드는 없다.
/// <para>주소 = 세그먼트 × 16 + 오프셋 (20비트, A20 꺼짐). PUSHF의 bit12~15 = 0 (286 리얼 모드). 0으로 나누기 = INT 0.
/// <see cref="HostInterrupt"/>가 true를 돌려주면 INT n을 바깥(C#의 DOS · BIOS 흉내)이 처리한 것으로 본다.</para>
/// <para><see cref="Cycles"/>는 명령마다 대략 값 (286 표에 가깝게 2~20). <see cref="Is386"/>가 꺼져 있으면 386 명령(0x66 · 0x67 접두어 · 0F 확장)은 모르는 명령으로 세고 그 자리에서 멈춘다 (<see cref="Halted"/>).
/// 켜면 386 리얼 모드 (X86.386.cs): 32비트 레지스터 · FS · GS · 66h · 67h 접두어 · 0F 확장 명령.</para>
/// </summary>
public sealed partial class X86
{
    // 레지스터 (16비트 값으로 보관)
    public int AX, BX, CX, DX, SP, BP, SI, DI;
    public int ES, CS, SS, DS, FS, GS;
    public int IP;
    public int Flags = 0x0002;
    public bool Halted;
    public long Cycles;
    public int UnknownOps;
    /// <summary>마지막 모르는 명령 (진단).</summary>
    public int LastUnknown = -1, LastUnknownAt = -1;
    /// <summary>INT n: true면 바깥이 처리함 (레지스터를 직접 바꿔도 된다).</summary>
    public Func<int, bool> HostInterrupt;
    /// <summary>호스트 함정: 명령 F1 xx xx (286에 없는 명령) = 바깥 처리기 호출 (번호 16비트). DOS · BIOS 흉내는 인터럽트 벡터가 이 함정을 가리키게 둔다
    /// (상주 프로그램이 벡터를 가로채 원래 처리기로 넘겨도 바깥까지 온다). 없으면 모르는 명령.</summary>
    public Action<int> HostTrap;

    public const int CF = 0x0001, PF = 0x0004, AF = 0x0010, ZF = 0x0040, SF = 0x0080, TF = 0x0100, IF = 0x0200, DF = 0x0400, OF = 0x0800;

    private readonly IX86Bus bus;
    private int segOverride = -1;   // 0 ES 1 CS 2 SS 3 DS 4 FS 5 GS
    private int rep;                // 0 없음, 1 REPNE, 2 REP/REPE
    private int opStartIp, opStartCs;
    private bool interruptShadow;   // MOV SS · POP SS 다음 한 명령은 인터럽트를 받지 않는다

    private static readonly bool[] Parity = BuildParity();
    private static bool[] BuildParity()
    {
        var p = new bool[256];
        for (int i = 0; i < 256; i++) { int b = 0; for (int k = 0; k < 8; k++) b += (i >> k) & 1; p[i] = (b & 1) == 0; }
        return p;
    }

    public X86(IX86Bus bus) { this.bus = bus; Reset(); }

    public void Reset()
    {
        AX = BX = CX = DX = SP = BP = SI = DI = 0;
        ES = SS = DS = FS = GS = 0; CS = 0xFFFF; IP = 0;
        Array.Clear(hi, 0, hi.Length);
        Flags = 0x0002; Halted = false; Cycles = 0; UnknownOps = 0;
    }

    // ------------------------------------------------------------ 레지스터 · 메모리

    public int AL { get { return AX & 0xFF; } set { AX = (AX & 0xFF00) | (value & 0xFF); } }
    public int AH { get { return AX >> 8; } set { AX = (AX & 0xFF) | ((value & 0xFF) << 8); } }
    public int BL { get { return BX & 0xFF; } set { BX = (BX & 0xFF00) | (value & 0xFF); } }
    public int BH { get { return BX >> 8; } set { BX = (BX & 0xFF) | ((value & 0xFF) << 8); } }
    public int CL { get { return CX & 0xFF; } set { CX = (CX & 0xFF00) | (value & 0xFF); } }
    public int CH { get { return CX >> 8; } set { CX = (CX & 0xFF) | ((value & 0xFF) << 8); } }
    public int DL { get { return DX & 0xFF; } set { DX = (DX & 0xFF00) | (value & 0xFF); } }
    public int DH { get { return DX >> 8; } set { DX = (DX & 0xFF) | ((value & 0xFF) << 8); } }

    public bool GetFlag(int f) { return (Flags & f) != 0; }
    public void SetFlag(int f, bool on) { if (on) Flags |= f; else Flags &= ~f; }

    public static int Linear(int seg, int off) { return ((seg << 4) + (off & 0xFFFF)) & 0xFFFFF; }
    public int Read8(int seg, int off) { return bus.Read(Linear(seg, off)); }
    public int Read16(int seg, int off) { return bus.Read(Linear(seg, off)) | (bus.Read(Linear(seg, off + 1)) << 8); }
    public void Write8(int seg, int off, int v) { bus.Write(Linear(seg, off), (byte)v); }
    public void Write16(int seg, int off, int v) { bus.Write(Linear(seg, off), (byte)v); bus.Write(Linear(seg, off + 1), (byte)(v >> 8)); }

    private int Fetch8() { int v = Read8(CS, IP); IP = (IP + 1) & 0xFFFF; return v; }
    private int Fetch16() { int v = Read16(CS, IP); IP = (IP + 2) & 0xFFFF; return v; }

    public void Push(int v) { SP = (SP - 2) & 0xFFFF; Write16(SS, SP, v); }
    public int Pop() { int v = Read16(SS, SP); SP = (SP + 2) & 0xFFFF; return v; }

    private int GetReg16(int r)
    {
        switch (r) { case 0: return AX; case 1: return CX; case 2: return DX; case 3: return BX; case 4: return SP; case 5: return BP; case 6: return SI; default: return DI; }
    }

    private void SetReg16(int r, int v)
    {
        v &= 0xFFFF;
        switch (r) { case 0: AX = v; break; case 1: CX = v; break; case 2: DX = v; break; case 3: BX = v; break; case 4: SP = v; break; case 5: BP = v; break; case 6: SI = v; break; default: DI = v; break; }
    }

    private int GetReg8(int r)
    {
        switch (r) { case 0: return AL; case 1: return CL; case 2: return DL; case 3: return BL; case 4: return AH; case 5: return CH; case 6: return DH; default: return BH; }
    }

    private void SetReg8(int r, int v)
    {
        switch (r) { case 0: AL = v; break; case 1: CL = v; break; case 2: DL = v; break; case 3: BL = v; break; case 4: AH = v; break; case 5: CH = v; break; case 6: DH = v; break; default: BH = v; break; }
    }

    private int GetSeg(int s) { switch (s) { case 0: return ES; case 1: return CS; case 2: return SS; case 4: return FS; case 5: return GS; default: return DS; } }
    private void SetSeg(int s, int v)
    {
        v &= 0xFFFF;
        switch (s) { case 0: ES = v; break; case 1: CS = v; break; case 2: SS = v; interruptShadow = true; break; case 4: FS = v; break; case 5: GS = v; break; default: DS = v; break; }
    }

    // ------------------------------------------------------------ ModR/M

    private int mMod, mReg, mRm, mSeg, mOff;

    private void ModRm()
    {
        int b = Fetch8();
        mMod = b >> 6; mReg = (b >> 3) & 7; mRm = b & 7;
        if (mMod == 3) return;
        if (addr32) { ModRm32(); return; }
        int disp = 0, ea;
        bool bp = false;
        switch (mRm)
        {
            case 0: ea = BX + SI; break;
            case 1: ea = BX + DI; break;
            case 2: ea = BP + SI; bp = true; break;
            case 3: ea = BP + DI; bp = true; break;
            case 4: ea = SI; break;
            case 5: ea = DI; break;
            case 6: if (mMod == 0) { ea = Fetch16(); } else { ea = BP; bp = true; } break;
            default: ea = BX; break;
        }
        if (mMod == 1) disp = (sbyte)(byte)Fetch8();
        else if (mMod == 2) disp = (short)Fetch16();
        mOff = (ea + disp) & 0xFFFF;
        mSeg = segOverride >= 0 ? GetSeg(segOverride) : bp ? SS : DS;
        Cycles += 2;
    }

    private int Rm8() { return mMod == 3 ? GetReg8(mRm) : Read8(mSeg, mOff); }
    private int Rm16() { return mMod == 3 ? GetReg16(mRm) : Read16(mSeg, mOff); }
    private void SetRm8(int v) { if (mMod == 3) SetReg8(mRm, v); else Write8(mSeg, mOff, v); }
    private void SetRm16(int v) { if (mMod == 3) SetReg16(mRm, v); else Write16(mSeg, mOff, v); }

    private int DataSeg { get { return segOverride >= 0 ? GetSeg(segOverride) : DS; } }

    // ------------------------------------------------------------ 깃발 계산

    private void SzP(int r, bool word)
    {
        int m = word ? 0xFFFF : 0xFF;
        r &= m;
        SetFlag(ZF, r == 0);
        SetFlag(SF, (r & (word ? 0x8000 : 0x80)) != 0);
        SetFlag(PF, Parity[r & 0xFF]);
    }

    private int Add(int a, int b, int c, bool word)
    {
        int m = word ? 0xFFFF : 0xFF, sb = word ? 0x8000 : 0x80;
        int r = a + b + c;
        SetFlag(CF, r > m);
        SetFlag(AF, ((a ^ b ^ r) & 0x10) != 0);
        SetFlag(OF, ((a ^ r) & (b ^ r) & sb) != 0);
        SzP(r, word);
        return r & m;
    }

    private int Sub(int a, int b, int c, bool word)
    {
        int m = word ? 0xFFFF : 0xFF, sb = word ? 0x8000 : 0x80;
        int r = a - b - c;
        SetFlag(CF, r < 0);
        SetFlag(AF, ((a ^ b ^ r) & 0x10) != 0);
        SetFlag(OF, ((a ^ b) & (a ^ r) & sb) != 0);
        SzP(r, word);
        return r & m;
    }

    private int Logic(int r, bool word)
    {
        Flags &= ~(CF | OF | AF);
        SzP(r, word);
        return r & (word ? 0xFFFF : 0xFF);
    }

    /// <summary>ALU 연산 0~7 = ADD OR ADC SBB AND SUB XOR CMP. CMP는 a를 그대로 돌려준다.</summary>
    private int Alu(int op, int a, int b, bool word)
    {
        int c = GetFlag(CF) ? 1 : 0;
        switch (op)
        {
            case 0: return Add(a, b, 0, word);
            case 1: return Logic(a | b, word);
            case 2: return Add(a, b, c, word);
            case 3: return Sub(a, b, c, word);
            case 4: return Logic(a & b, word);
            case 5: return Sub(a, b, 0, word);
            case 6: return Logic(a ^ b, word);
            default: Sub(a, b, 0, word); return a;
        }
    }

    private int Inc(int v, bool word) { bool c = GetFlag(CF); int r = Add(v, 1, 0, word); SetFlag(CF, c); return r; }
    private int Dec(int v, bool word) { bool c = GetFlag(CF); int r = Sub(v, 1, 0, word); SetFlag(CF, c); return r; }

    /// <summary>시프트 · 회전 0~7 = ROL ROR RCL RCR SHL SHR SAL(=SHL) SAR. 286: 횟수는 아래 5비트.</summary>
    private int Shift(int op, int v, int count, bool word)
    {
        count &= 0x1F;
        if (count == 0) return v;
        int bits = word ? 16 : 8, m = word ? 0xFFFF : 0xFF, sb = word ? 0x8000 : 0x80;
        Cycles += count;
        switch (op)
        {
            case 0:
                for (int i = 0; i < count; i++) { int c = (v & sb) != 0 ? 1 : 0; v = ((v << 1) | c) & m; SetFlag(CF, c != 0); }
                SetFlag(OF, ((v & sb) != 0) ^ GetFlag(CF));
                return v;
            case 1:
                for (int i = 0; i < count; i++) { int c = v & 1; v = (v >> 1) | (c != 0 ? sb : 0); SetFlag(CF, c != 0); }
                SetFlag(OF, ((v ^ (v << 1)) & sb) != 0);
                return v;
            case 2:
                for (int i = 0; i < count; i++) { int c = (v & sb) != 0 ? 1 : 0; v = ((v << 1) | (GetFlag(CF) ? 1 : 0)) & m; SetFlag(CF, c != 0); }
                SetFlag(OF, ((v & sb) != 0) ^ GetFlag(CF));
                return v;
            case 3:
                for (int i = 0; i < count; i++) { int c = v & 1; v = (v >> 1) | (GetFlag(CF) ? sb : 0); SetFlag(CF, c != 0); }
                SetFlag(OF, ((v ^ (v << 1)) & sb) != 0);
                return v;
            case 4: case 6:
                {
                    int r = count > bits ? 0 : (v << count);
                    SetFlag(CF, count <= bits && (r & (m + 1)) != 0);
                    r &= m;
                    SetFlag(OF, ((r & sb) != 0) ^ GetFlag(CF));
                    SzP(r, word); SetFlag(AF, false);
                    return r;
                }
            case 5:
                {
                    SetFlag(CF, count <= bits && ((v >> (count - 1)) & 1) != 0);
                    SetFlag(OF, count == 1 && (v & sb) != 0);
                    int r = count >= bits ? 0 : v >> count;
                    SzP(r, word); SetFlag(AF, false);
                    return r;
                }
            default:
                {
                    int s = word ? (short)v : (sbyte)(byte)v;
                    int sh = Math.Min(count, bits);
                    SetFlag(CF, ((s >> (sh - 1)) & 1) != 0);
                    SetFlag(OF, false);
                    int r = (s >> sh) & m;
                    SzP(r, word); SetFlag(AF, false);
                    return r;
                }
        }
    }

    // ------------------------------------------------------------ 인터럽트

    /// <summary>INT n (소프트웨어 · 예외): 바깥 처리 → 아니면 벡터 표로.</summary>
    public void Interrupt(int n)
    {
        n &= 0xFF;
        if (HostInterrupt != null && HostInterrupt(n)) return;
        Vector(n);
    }

    /// <summary>벡터 표로만 (바깥 처리 없이): FLAGS · CS · IP를 쌓고 IF · TF를 끈다.</summary>
    public void Vector(int n)
    {
        Push((Flags & (Is386 ? 0x7FFF : 0x0FFF)) | 0x0002);
        Push(CS); Push(IP);
        Flags &= ~(IF | TF);
        IP = Read16(0, n * 4);
        CS = Read16(0, n * 4 + 2);
        Halted = false;
        Cycles += 23;
    }

    /// <summary>하드웨어 인터럽트: IF가 켜져 있고 바로 앞이 MOV SS가 아니면 받아들인다.</summary>
    public bool HardwareInterrupt(int vector)
    {
        if (!GetFlag(IF) || interruptShadow) return false;
        Vector(vector);
        return true;
    }

    /// <summary>IRET (바깥 처리기에서 돌아갈 때 등).</summary>
    public void Iret()
    {
        IP = Pop(); CS = Pop(); Flags = (Pop() & FlagMask) | 0x0002;
    }

    private void Unknown(int op)
    {
        UnknownOps++;
        LastUnknown = op; LastUnknownAt = Linear(opStartCs, opStartIp);
        // 드라이버가 모르는 명령(386 등)에 닿으면 더 가도 소용없다: 그 자리에서 멈춘다
        IP = opStartIp; CS = opStartCs;
        Halted = true;
    }

    // ------------------------------------------------------------ 실행

    /// <summary>명령 하나 (접두어 포함).</summary>
    public void Step()
    {
        if (Halted) { Cycles += 2; return; }
        interruptShadow = false;
        segOverride = -1; rep = 0; op32 = addr32 = false;
        opStartIp = IP; opStartCs = CS;
        int op = Fetch8();
        for (int n = 0; n < 14; n++)   // 접두어 (세그먼트 · REP · LOCK, 386: FS · GS · 크기)
        {
            if (op == 0x26 || op == 0x2E || op == 0x36 || op == 0x3E) segOverride = (op >> 3) & 3;
            else if (Is386 && (op == 0x64 || op == 0x65)) segOverride = op - 0x60;
            else if (Is386 && op == 0x66) op32 = true;
            else if (Is386 && op == 0x67) addr32 = true;
            else if (op == 0xF2) rep = 1;
            else if (op == 0xF3) rep = 2;
            else if (op != 0xF0) break;
            op = Fetch8();
        }
        Cycles += 2;
        Execute(op);
    }

    private void Execute(int op)
    {
        if ((op32 || addr32) && Execute386(op)) return;
        bool word = (op & 1) != 0;
        if (op < 0x40 && (op & 7) < 6)
        {
            int alu = op >> 3;
            switch (op & 7)
            {
                case 0: { ModRm(); int r = Alu(alu, Rm8(), GetReg8(mReg), false); if (alu != 7) SetRm8(r); return; }
                case 1: { ModRm(); int r = Alu(alu, Rm16(), GetReg16(mReg), true); if (alu != 7) SetRm16(r); return; }
                case 2: { ModRm(); int r = Alu(alu, GetReg8(mReg), Rm8(), false); if (alu != 7) SetReg8(mReg, r); return; }
                case 3: { ModRm(); int r = Alu(alu, GetReg16(mReg), Rm16(), true); if (alu != 7) SetReg16(mReg, r); return; }
                case 4: { int r = Alu(alu, AL, Fetch8(), false); if (alu != 7) AL = r; return; }
                default: { int r = Alu(alu, AX, Fetch16(), true); if (alu != 7) AX = r; return; }
            }
        }
        switch (op)
        {
            case 0x06: Push(ES); return;
            case 0x07: ES = Pop(); return;
            case 0x0E: Push(CS); return;
            case 0x0F: Op0F(); return;
            case 0x16: Push(SS); return;
            case 0x17: SS = Pop(); interruptShadow = true; return;
            case 0x1E: Push(DS); return;
            case 0x1F: DS = Pop(); return;
            case 0x27:
                {
                    int a = AL; bool c = GetFlag(CF);
                    if ((a & 15) > 9 || GetFlag(AF)) { AL = a + 6; SetFlag(CF, c || a + 6 > 0xFF); SetFlag(AF, true); } else SetFlag(AF, false);
                    if (a > 0x99 || c) { AL += 0x60; SetFlag(CF, true); } else SetFlag(CF, false);
                    SzP(AL, false); return;
                }
            case 0x2F:
                {
                    int a = AL; bool c = GetFlag(CF);
                    if ((a & 15) > 9 || GetFlag(AF)) { AL = a - 6; SetFlag(CF, c || a < 6); SetFlag(AF, true); } else SetFlag(AF, false);
                    if (a > 0x99 || c) { AL -= 0x60; SetFlag(CF, true); } else SetFlag(CF, false);
                    SzP(AL, false); return;
                }
            case 0x37:
                if ((AL & 15) > 9 || GetFlag(AF)) { AX = (AX + 0x106) & 0xFFFF; SetFlag(AF, true); SetFlag(CF, true); } else { SetFlag(AF, false); SetFlag(CF, false); }
                AL &= 15; return;
            case 0x3F:
                if ((AL & 15) > 9 || GetFlag(AF)) { AX = (AX - 6) & 0xFFFF; AH = AH - 1; SetFlag(AF, true); SetFlag(CF, true); } else { SetFlag(AF, false); SetFlag(CF, false); }
                AL &= 15; return;
        }
        if (op >= 0x40 && op <= 0x47) { SetReg16(op & 7, Inc(GetReg16(op & 7), true)); return; }
        if (op >= 0x48 && op <= 0x4F) { SetReg16(op & 7, Dec(GetReg16(op & 7), true)); return; }
        if (op >= 0x50 && op <= 0x57) { int v = op == 0x54 ? SP : GetReg16(op & 7); Push(v); return; }   // 286: PUSH SP = 이전 값
        if (op >= 0x58 && op <= 0x5F) { SetReg16(op & 7, Pop()); return; }
        if (op >= 0x70 && op <= 0x7F) { int e = (sbyte)(byte)Fetch8(); if (Cond(op & 15)) { IP = (IP + e) & 0xFFFF; Cycles += 6; } return; }
        if (op >= 0x91 && op <= 0x97) { int t = AX; AX = GetReg16(op & 7); SetReg16(op & 7, t); return; }
        if (op >= 0xB0 && op <= 0xB7) { SetReg8(op & 7, Fetch8()); return; }
        if (op >= 0xB8 && op <= 0xBF) { SetReg16(op & 7, Fetch16()); return; }
        if (op >= 0xD8 && op <= 0xDF) { ModRm(); return; }   // FPU: 무시
        switch (op)
        {
            case 0x60:
                {
                    int sp = SP;
                    Push(AX); Push(CX); Push(DX); Push(BX); Push(sp); Push(BP); Push(SI); Push(DI);
                    Cycles += 15; return;
                }
            case 0x61:
                DI = Pop(); SI = Pop(); BP = Pop(); Pop(); BX = Pop(); DX = Pop(); CX = Pop(); AX = Pop();
                Cycles += 15; return;
            case 0x62:
                {
                    ModRm();
                    int idx = (short)GetReg16(mReg), lo = (short)Read16(mSeg, mOff), hi = (short)Read16(mSeg, mOff + 2);
                    if (idx < lo || idx > hi) { IP = opStartIp; CS = opStartCs; Vector(5); }
                    return;
                }
            case 0x68: Push(Fetch16()); return;
            case 0x6A: Push((sbyte)(byte)Fetch8() & 0xFFFF); return;
            case 0x69: case 0x6B:
                {
                    ModRm();
                    int a = (short)Rm16();
                    int b = op == 0x69 ? (short)Fetch16() : (sbyte)(byte)Fetch8();
                    int r = a * b;
                    SetReg16(mReg, r);
                    bool of = r != (short)r;
                    SetFlag(CF, of); SetFlag(OF, of);
                    Cycles += 20; return;
                }
            case 0x6C: case 0x6D: case 0x6E: case 0x6F: StringOp(op); return;
            case 0x80: case 0x81: case 0x82: case 0x83:
                {
                    ModRm();
                    bool w = op == 0x81 || op == 0x83;
                    int a = w ? Rm16() : Rm8();
                    int b = op == 0x81 ? Fetch16() : op == 0x83 ? ((sbyte)(byte)Fetch8()) & 0xFFFF : Fetch8();
                    int r = Alu(mReg, a, b, w);
                    if (mReg != 7) { if (w) SetRm16(r); else SetRm8(r); }
                    return;
                }
            case 0x84: ModRm(); Logic(Rm8() & GetReg8(mReg), false); return;
            case 0x85: ModRm(); Logic(Rm16() & GetReg16(mReg), true); return;
            case 0x86: { ModRm(); int t = Rm8(); SetRm8(GetReg8(mReg)); SetReg8(mReg, t); return; }
            case 0x87: { ModRm(); int t = Rm16(); SetRm16(GetReg16(mReg)); SetReg16(mReg, t); return; }
            case 0x88: ModRm(); SetRm8(GetReg8(mReg)); return;
            case 0x89: ModRm(); SetRm16(GetReg16(mReg)); return;
            case 0x8A: ModRm(); SetReg8(mReg, Rm8()); return;
            case 0x8B: ModRm(); SetReg16(mReg, Rm16()); return;
            case 0x8C: ModRm(); if (mReg > 5 || (mReg > 3 && !Is386)) { Unknown(op); return; } SetRm16(GetSeg(mReg)); return;
            case 0x8D: ModRm(); if (mMod != 3) SetReg16(mReg, mOff); return;
            case 0x8E: ModRm(); if (mReg > 5 || mReg == 1 || (mReg > 3 && !Is386)) { Unknown(op); return; } SetSeg(mReg, Rm16()); return;
            case 0x8F: { int v = Pop(); ModRm(); SetRm16(v); return; }
            case 0x90: return;
            case 0x98: AX = (sbyte)(byte)AL & 0xFFFF; return;
            case 0x99: DX = (AX & 0x8000) != 0 ? 0xFFFF : 0; return;
            case 0x9A: { int ip = Fetch16(), cs = Fetch16(); Push(CS); Push(IP); CS = cs; IP = ip; Cycles += 13; return; }
            case 0x9B: return;
            case 0x9C: Push(Flags & (Is386 ? 0x7FFF : 0x0FFF) | 0x0002); return;
            case 0x9D: Flags = (Pop() & FlagMask) | 0x0002; return;
            case 0x9E: Flags = (Flags & 0xFF00) | (AH & 0xD5) | 0x02; return;
            case 0x9F: AH = (Flags & 0xD5) | 0x02; return;
            case 0xA0: AL = Read8(DataSeg, Fetch16()); return;
            case 0xA1: AX = Read16(DataSeg, Fetch16()); return;
            case 0xA2: Write8(DataSeg, Fetch16(), AL); return;
            case 0xA3: Write16(DataSeg, Fetch16(), AX); return;
            case 0xA4: case 0xA5: case 0xA6: case 0xA7: case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF: StringOp(op); return;
            case 0xA8: Logic(AL & Fetch8(), false); return;
            case 0xA9: Logic(AX & Fetch16(), true); return;
            case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                {
                    ModRm();
                    bool w = word;
                    int v = w ? Rm16() : Rm8();
                    int cnt = op <= 0xC1 ? Fetch8() : op <= 0xD1 ? 1 : CL;
                    int r = Shift(mReg, v, cnt, w);
                    if (w) SetRm16(r); else SetRm8(r);
                    return;
                }
            case 0xC2: { int n = Fetch16(); IP = Pop(); SP = (SP + n) & 0xFFFF; Cycles += 9; return; }
            case 0xC3: IP = Pop(); Cycles += 9; return;
            case 0xC4: case 0xC5:
                {
                    ModRm();
                    int off = Read16(mSeg, mOff), seg = Read16(mSeg, mOff + 2);
                    SetReg16(mReg, off);
                    if (op == 0xC4) ES = seg; else DS = seg;
                    return;
                }
            case 0xC6: ModRm(); SetRm8(Fetch8()); return;
            case 0xC7: ModRm(); SetRm16(Fetch16()); return;
            case 0xC8:
                {
                    int size = Fetch16(), level = Fetch8() & 31;
                    Push(BP);
                    int frame = SP;
                    for (int i = 1; i < level; i++) { BP = (BP - 2) & 0xFFFF; Push(Read16(SS, BP)); }
                    if (level > 0) Push(frame);
                    BP = frame;
                    SP = (SP - size) & 0xFFFF;
                    Cycles += 11; return;
                }
            case 0xC9: SP = BP; BP = Pop(); return;
            case 0xCA: { int n = Fetch16(); IP = Pop(); CS = Pop(); SP = (SP + n) & 0xFFFF; Cycles += 13; return; }
            case 0xCB: IP = Pop(); CS = Pop(); Cycles += 13; return;
            case 0xCC: Interrupt(3); return;
            case 0xCD: Interrupt(Fetch8()); return;
            case 0xCE: if (GetFlag(OF)) Interrupt(4); return;
            case 0xCF: Iret(); Cycles += 15; return;
            case 0xD4:
                {
                    int b = Fetch8();
                    if (b == 0) { Interrupt(0); return; }
                    AH = AL / b; AL = AL % b; SzP(AL, false); return;
                }
            case 0xD5: { int b = Fetch8(); AL = AH * b + AL; AH = 0; SzP(AL, false); return; }
            case 0xD6: AL = GetFlag(CF) ? 0xFF : 0; return;
            case 0xD7: AL = Read8(DataSeg, (BX + AL) & 0xFFFF); return;
            case 0xE0: case 0xE1: case 0xE2: case 0xE3:
                {
                    int e = (sbyte)(byte)Fetch8();
                    bool jump;
                    if (op == 0xE3) jump = CX == 0;
                    else
                    {
                        CX = (CX - 1) & 0xFFFF;
                        jump = CX != 0 && (op == 0xE2 || (op == 0xE1 ? GetFlag(ZF) : !GetFlag(ZF)));
                    }
                    if (jump) { IP = (IP + e) & 0xFFFF; Cycles += 6; }
                    return;
                }
            case 0xE4: AL = bus.In(Fetch8()); return;
            case 0xE5: { int p = Fetch8(); AX = bus.In(p) | (bus.In(p + 1) << 8); return; }
            case 0xE6: bus.Out(Fetch8(), (byte)AL); return;
            case 0xE7: { int p = Fetch8(); bus.Out(p, (byte)AL); bus.Out(p + 1, (byte)AH); return; }
            case 0xE8: { int e = Fetch16(); Push(IP); IP = (IP + e) & 0xFFFF; Cycles += 5; return; }
            case 0xE9: { int e = Fetch16(); IP = (IP + e) & 0xFFFF; Cycles += 5; return; }
            case 0xEA: { int ip = Fetch16(), cs = Fetch16(); IP = ip; CS = cs; Cycles += 9; return; }
            case 0xEB: { int e = (sbyte)(byte)Fetch8(); IP = (IP + e) & 0xFFFF; Cycles += 5; return; }
            case 0xEC: AL = bus.In(DX); return;
            case 0xED: AX = bus.In(DX) | (bus.In((DX + 1) & 0xFFFF) << 8); return;
            case 0xEE: bus.Out(DX, (byte)AL); return;
            case 0xEF: bus.Out(DX, (byte)AL); bus.Out((DX + 1) & 0xFFFF, (byte)AH); return;
            case 0xF4: Halted = true; return;
            case 0xF1:
                if (HostTrap == null) break;
                { int id = Fetch16(); HostTrap(id); return; }
            case 0xF5: Flags ^= CF; return;
            case 0xF6: case 0xF7: Group3(word); return;
            case 0xF8: SetFlag(CF, false); return;
            case 0xF9: SetFlag(CF, true); return;
            case 0xFA: SetFlag(IF, false); return;
            case 0xFB: SetFlag(IF, true); interruptShadow = true; return;
            case 0xFC: SetFlag(DF, false); return;
            case 0xFD: SetFlag(DF, true); return;
            case 0xFE:
                ModRm();
                if (mReg == 0) SetRm8(Inc(Rm8(), false));
                else if (mReg == 1) SetRm8(Dec(Rm8(), false));
                else Unknown(op);
                return;
            case 0xFF: Group5(); return;
        }
        Unknown(op);
    }

    private bool Cond(int c)
    {
        bool r;
        switch (c >> 1)
        {
            case 0: r = GetFlag(OF); break;
            case 1: r = GetFlag(CF); break;
            case 2: r = GetFlag(ZF); break;
            case 3: r = GetFlag(CF) || GetFlag(ZF); break;
            case 4: r = GetFlag(SF); break;
            case 5: r = GetFlag(PF); break;
            case 6: r = GetFlag(SF) != GetFlag(OF); break;
            default: r = GetFlag(ZF) || GetFlag(SF) != GetFlag(OF); break;
        }
        return (c & 1) != 0 ? !r : r;
    }

    private void Group3(bool word)
    {
        ModRm();
        int v = word ? Rm16() : Rm8();
        switch (mReg)
        {
            case 0: case 1: Logic(v & (word ? Fetch16() : Fetch8()), word); return;
            case 2: if (word) SetRm16(~v); else SetRm8(~v); return;
            case 3:
                {
                    int r = Sub(0, v, 0, word);
                    SetFlag(CF, v != 0);
                    if (word) SetRm16(r); else SetRm8(r);
                    return;
                }
            case 4:
                if (word) { uint r = (uint)AX * (uint)v; AX = (int)(r & 0xFFFF); DX = (int)(r >> 16); bool f = DX != 0; SetFlag(CF, f); SetFlag(OF, f); }
                else { int r = AL * v; AX = r & 0xFFFF; bool f = AH != 0; SetFlag(CF, f); SetFlag(OF, f); }
                Cycles += 15; return;
            case 5:
                if (word) { int r = (short)AX * (short)v; AX = r & 0xFFFF; DX = (r >> 16) & 0xFFFF; bool f = r != (short)r; SetFlag(CF, f); SetFlag(OF, f); }
                else { int r = (sbyte)(byte)AL * (sbyte)(byte)v; AX = r & 0xFFFF; bool f = r != (sbyte)r; SetFlag(CF, f); SetFlag(OF, f); }
                Cycles += 15; return;
            case 6:
                if (v == 0) { DivideError(); return; }
                if (word)
                {
                    uint n = ((uint)DX << 16) | (uint)AX;
                    uint q = n / (uint)v;
                    if (q > 0xFFFF) { DivideError(); return; }
                    AX = (int)q; DX = (int)(n % (uint)v);
                }
                else
                {
                    int n = AX, q = n / v;
                    if (q > 0xFF) { DivideError(); return; }
                    AX = q | ((n % v) << 8);
                }
                Cycles += 20; return;
            default:
                if (v == 0) { DivideError(); return; }
                if (word)
                {
                    int n = (DX << 16) | AX;
                    int d = (short)v;
                    long q = (long)n / d;
                    if (q > 32767 || q < -32768) { DivideError(); return; }
                    AX = (int)q & 0xFFFF; DX = (int)((long)n % d) & 0xFFFF;
                }
                else
                {
                    int n = (short)AX, d = (sbyte)(byte)v;
                    int q = n / d;
                    if (q > 127 || q < -128) { DivideError(); return; }
                    int rem = n % d;
                    AX = (q & 0xFF) | ((rem & 0xFF) << 8);
                }
                Cycles += 22; return;
        }
    }

    /// <summary>나누기 오류: 286은 나누기 명령 자리를 쌓고 INT 0.</summary>
    private void DivideError() { IP = opStartIp; CS = opStartCs; Interrupt(0); }

    private void Group5()
    {
        ModRm();
        switch (mReg)
        {
            case 0: SetRm16(Inc(Rm16(), true)); return;
            case 1: SetRm16(Dec(Rm16(), true)); return;
            case 2: { int t = Rm16(); Push(IP); IP = t; Cycles += 7; return; }
            case 3: { if (mMod == 3) { Unknown(0xFF); return; } int ip = Read16(mSeg, mOff), cs = Read16(mSeg, mOff + 2); Push(CS); Push(IP); CS = cs; IP = ip; Cycles += 16; return; }
            case 4: IP = Rm16(); Cycles += 7; return;
            case 5: { if (mMod == 3) { Unknown(0xFF); return; } IP = Read16(mSeg, mOff); CS = Read16(mSeg, mOff + 2); Cycles += 11; return; }
            case 6: Push(Rm16()); return;
            default: Unknown(0xFF); return;
        }
    }

    /// <summary>0F: 286 리얼 모드에서 쓸 만한 것만 (SMSW · LMSW, 그 밖은 INT 6).</summary>
    private void Op0F()
    {
        int op = Fetch8();
        if (Is386 && Op0F386(op)) return;
        if (op == 0x01)
        {
            ModRm();
            if (mReg == 4) { SetRm16(0xFFF0); return; }   // SMSW: 리얼 모드 (PE = 0)
            if (mReg == 6) { Rm16(); return; }             // LMSW: 무시 (보호 모드 없음)
        }
        Unknown(0x0F00 | op);
    }

    // ------------------------------------------------------------ 문자열 명령

    private void StringOp(int op)
    {
        bool word = (op & 1) != 0;
        int d = GetFlag(DF) ? -1 : 1, step = word ? 2 * d : d;
        int src = DataSeg;
        if (rep != 0 && CX == 0) return;
        do
        {
            switch (op)
            {
                case 0xA4: Write8(ES, DI, Read8(src, SI)); SI = (SI + step) & 0xFFFF; DI = (DI + step) & 0xFFFF; break;
                case 0xA5: Write16(ES, DI, Read16(src, SI)); SI = (SI + step) & 0xFFFF; DI = (DI + step) & 0xFFFF; break;
                case 0xA6: Sub(Read8(src, SI), Read8(ES, DI), 0, false); SI = (SI + step) & 0xFFFF; DI = (DI + step) & 0xFFFF; break;
                case 0xA7: Sub(Read16(src, SI), Read16(ES, DI), 0, true); SI = (SI + step) & 0xFFFF; DI = (DI + step) & 0xFFFF; break;
                case 0xAA: Write8(ES, DI, AL); DI = (DI + step) & 0xFFFF; break;
                case 0xAB: Write16(ES, DI, AX); DI = (DI + step) & 0xFFFF; break;
                case 0xAC: AL = Read8(src, SI); SI = (SI + step) & 0xFFFF; break;
                case 0xAD: AX = Read16(src, SI); SI = (SI + step) & 0xFFFF; break;
                case 0xAE: Sub(AL, Read8(ES, DI), 0, false); DI = (DI + step) & 0xFFFF; break;
                case 0xAF: Sub(AX, Read16(ES, DI), 0, true); DI = (DI + step) & 0xFFFF; break;
                case 0x6C: Write8(ES, DI, bus.In(DX)); DI = (DI + step) & 0xFFFF; break;
                case 0x6D: Write16(ES, DI, bus.In(DX) | (bus.In((DX + 1) & 0xFFFF) << 8)); DI = (DI + step) & 0xFFFF; break;
                case 0x6E: bus.Out(DX, (byte)Read8(src, SI)); SI = (SI + step) & 0xFFFF; break;
                case 0x6F: { int v = Read16(src, SI); bus.Out(DX, (byte)v); bus.Out((DX + 1) & 0xFFFF, (byte)(v >> 8)); SI = (SI + step) & 0xFFFF; break; }
            }
            Cycles += 4;
            if (rep == 0) return;
            CX = (CX - 1) & 0xFFFF;
            if (op == 0xA6 || op == 0xA7 || op == 0xAE || op == 0xAF)
            {
                if (rep == 2 && !GetFlag(ZF)) return;
                if (rep == 1 && GetFlag(ZF)) return;
            }
        } while (CX != 0);
    }
}
