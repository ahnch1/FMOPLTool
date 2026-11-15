using System;

namespace AdlibComposer.Audio.Cpu;

/// <summary>게임보이 CPU가 보는 메모리 (I/O도 메모리 주소 $FF00~).</summary>
public interface IGbBus
{
    byte Read(int addr);
    void Write(int addr, byte value);
}

/// <summary>
/// Sharp LR35902 (게임보이 · 게임보이 컬러 CPU): Z80을 줄이고 바꾼 8비트 CPU. 깃발 Z N H C (F의 bit7~4), IX · IY · 뒷 레지스터 · ED 접두어 없음,
/// LDI · LDD · LDH · ADD SP · LD HL,SP+e · SWAP · STOP · RETI가 더해졌다. <see cref="Cycles"/> = T-state (4.194304MHz, 명령표 값).
/// <para>사운드 드라이버(GBS)를 돌리기 위한 것이라 인터럽트는 드라이버를 직접 CALL해서 대신한다. HALT · 없는 명령이면 <see cref="Halted"/> · <see cref="Jammed"/>.</para>
/// </summary>
public sealed class Lr35902
{
    public int A, F, B, C, D, E, H, L, SP, PC;
    public bool Ime, Halted, Jammed;
    public long Cycles;
    public int UnknownOps;

    private const int FZ = 0x80, FN = 0x40, FH = 0x20, FC = 0x10;
    private readonly IGbBus bus;

    public Lr35902(IGbBus bus) { this.bus = bus; Reset(); }

    public void Reset()
    {
        A = 0x01; F = 0xB0; B = 0x00; C = 0x13; D = 0x00; E = 0xD8; H = 0x01; L = 0x4D;
        SP = 0xFFFE; PC = 0x100; Ime = false; Halted = Jammed = false; Cycles = 0; UnknownOps = 0;
    }

    public int BC { get { return (B << 8) | C; } set { B = (value >> 8) & 0xFF; C = value & 0xFF; } }
    public int DE { get { return (D << 8) | E; } set { D = (value >> 8) & 0xFF; E = value & 0xFF; } }
    public int HL { get { return (H << 8) | L; } set { H = (value >> 8) & 0xFF; L = value & 0xFF; } }
    public int AF { get { return (A << 8) | F; } set { A = (value >> 8) & 0xFF; F = value & 0xF0; } }

    private int Rd(int a) { return bus.Read(a & 0xFFFF); }
    private void Wr(int a, int v) { bus.Write(a & 0xFFFF, (byte)v); }
    private int Fetch() { int v = Rd(PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int lo = Fetch(); return lo | (Fetch() << 8); }
    public void Push(int v) { SP = (SP - 1) & 0xFFFF; Wr(SP, v >> 8); SP = (SP - 1) & 0xFFFF; Wr(SP, v); }
    private int Pop() { int lo = Rd(SP); SP = (SP + 1) & 0xFFFF; int hi = Rd(SP); SP = (SP + 1) & 0xFFFF; return lo | (hi << 8); }

    /// <summary>레지스터 번호 0~7 = B C D E H L (HL) A.</summary>
    private int Reg(int r)
    {
        switch (r) { case 0: return B; case 1: return C; case 2: return D; case 3: return E; case 4: return H; case 5: return L; case 6: return Rd(HL); default: return A; }
    }

    private void SetReg(int r, int v)
    {
        v &= 0xFF;
        switch (r) { case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break; case 4: H = v; break; case 5: L = v; break; case 6: Wr(HL, v); break; default: A = v; break; }
    }

    private int Rp(int p) { switch (p) { case 0: return BC; case 1: return DE; case 2: return HL; default: return SP; } }
    private void SetRp(int p, int v) { v &= 0xFFFF; switch (p) { case 0: BC = v; break; case 1: DE = v; break; case 2: HL = v; break; default: SP = v; break; } }

    private bool Cond(int c)
    {
        switch (c & 3) { case 0: return (F & FZ) == 0; case 1: return (F & FZ) != 0; case 2: return (F & FC) == 0; default: return (F & FC) != 0; }
    }

    private void Alu(int op, int v)
    {
        int c = (F & FC) != 0 ? 1 : 0, r;
        switch (op)
        {
            case 0: r = A + v; F = ((r & 0xFF) == 0 ? FZ : 0) | (((A & 15) + (v & 15)) > 15 ? FH : 0) | (r > 0xFF ? FC : 0); A = r & 0xFF; break;
            case 1: r = A + v + c; F = ((r & 0xFF) == 0 ? FZ : 0) | (((A & 15) + (v & 15) + c) > 15 ? FH : 0) | (r > 0xFF ? FC : 0); A = r & 0xFF; break;
            case 2: case 7:
                r = A - v;
                F = ((r & 0xFF) == 0 ? FZ : 0) | FN | ((A & 15) < (v & 15) ? FH : 0) | (r < 0 ? FC : 0);
                if (op == 2) A = r & 0xFF;
                break;
            case 3: r = A - v - c; F = ((r & 0xFF) == 0 ? FZ : 0) | FN | (((A & 15) - (v & 15) - c) < 0 ? FH : 0) | (r < 0 ? FC : 0); A = r & 0xFF; break;
            case 4: A &= v; F = (A == 0 ? FZ : 0) | FH; break;
            case 5: A ^= v; A &= 0xFF; F = A == 0 ? FZ : 0; break;
            default: A |= v; A &= 0xFF; F = A == 0 ? FZ : 0; break;
        }
    }

    private int Inc(int v) { int r = (v + 1) & 0xFF; F = (F & FC) | (r == 0 ? FZ : 0) | ((v & 15) == 15 ? FH : 0); return r; }
    private int Dec(int v) { int r = (v - 1) & 0xFF; F = (F & FC) | (r == 0 ? FZ : 0) | FN | ((v & 15) == 0 ? FH : 0); return r; }

    /// <summary>SP + 부호 있는 e: H · C는 아래 바이트 덧셈에서 (ADD SP,e · LD HL,SP+e).</summary>
    private int SpPlus(int e)
    {
        int s = (sbyte)(byte)e;
        F = (((SP & 0x0F) + (e & 0x0F)) > 0x0F ? FH : 0) | (((SP & 0xFF) + (e & 0xFF)) > 0xFF ? FC : 0);
        return (SP + s) & 0xFFFF;
    }

    /// <summary>명령 하나를 실행한다.</summary>
    public void Step()
    {
        if (Halted || Jammed) { Cycles += 4; return; }
        int op = Fetch();
        int t = 4;
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        if (x == 1)
        {
            if (op == 0x76) { Halted = true; Cycles += 4; return; }
            SetReg(y, Reg(z));
            Cycles += (y == 6 || z == 6) ? 8 : 4;
            return;
        }
        if (x == 2) { Alu(y, Reg(z)); Cycles += z == 6 ? 8 : 4; return; }
        switch (op)
        {
            case 0x00: break;
            case 0x01: case 0x11: case 0x21: case 0x31: SetRp(op >> 4, Fetch16()); t = 12; break;
            case 0x02: Wr(BC, A); t = 8; break;
            case 0x12: Wr(DE, A); t = 8; break;
            case 0x22: Wr(HL, A); HL = HL + 1; t = 8; break;
            case 0x32: Wr(HL, A); HL = HL - 1; t = 8; break;
            case 0x0A: A = Rd(BC); t = 8; break;
            case 0x1A: A = Rd(DE); t = 8; break;
            case 0x2A: A = Rd(HL); HL = HL + 1; t = 8; break;
            case 0x3A: A = Rd(HL); HL = HL - 1; t = 8; break;
            case 0x03: case 0x13: case 0x23: case 0x33: SetRp(op >> 4, Rp(op >> 4) + 1); t = 8; break;
            case 0x0B: case 0x1B: case 0x2B: case 0x3B: SetRp(op >> 4, Rp(op >> 4) - 1); t = 8; break;
            case 0x04: case 0x0C: case 0x14: case 0x1C: case 0x24: case 0x2C: case 0x34: case 0x3C:
                SetReg(y, Inc(Reg(y))); t = y == 6 ? 12 : 4; break;
            case 0x05: case 0x0D: case 0x15: case 0x1D: case 0x25: case 0x2D: case 0x35: case 0x3D:
                SetReg(y, Dec(Reg(y))); t = y == 6 ? 12 : 4; break;
            case 0x06: case 0x0E: case 0x16: case 0x1E: case 0x26: case 0x2E: case 0x36: case 0x3E:
                SetReg(y, Fetch()); t = y == 6 ? 12 : 8; break;
            case 0x07: { int c = A >> 7; A = ((A << 1) | c) & 0xFF; F = c != 0 ? FC : 0; break; }
            case 0x0F: { int c = A & 1; A = (A >> 1) | (c << 7); F = c != 0 ? FC : 0; break; }
            case 0x17: { int c = A >> 7; A = ((A << 1) | ((F & FC) != 0 ? 1 : 0)) & 0xFF; F = c != 0 ? FC : 0; break; }
            case 0x1F: { int c = A & 1; A = (A >> 1) | ((F & FC) != 0 ? 0x80 : 0); F = c != 0 ? FC : 0; break; }
            case 0x08: { int a = Fetch16(); Wr(a, SP); Wr(a + 1, SP >> 8); t = 20; break; }
            case 0x09: case 0x19: case 0x29: case 0x39:
                {
                    int v = Rp(op >> 4), r = HL + v;
                    F = (F & FZ) | (((HL & 0xFFF) + (v & 0xFFF)) > 0xFFF ? FH : 0) | (r > 0xFFFF ? FC : 0);
                    HL = r & 0xFFFF; t = 8; break;
                }
            case 0x10: Fetch(); break;   // STOP (드라이버에서는 무시)
            case 0x18: { int e = (sbyte)(byte)Fetch(); PC = (PC + e) & 0xFFFF; t = 12; break; }
            case 0x20: case 0x28: case 0x30: case 0x38:
                {
                    int e = (sbyte)(byte)Fetch();
                    if (Cond(y - 4)) { PC = (PC + e) & 0xFFFF; t = 12; } else t = 8;
                    break;
                }
            case 0x27:
                {
                    int a = A;
                    if ((F & FN) == 0)
                    {
                        if ((F & FC) != 0 || a > 0x99) { a += 0x60; F |= FC; }
                        if ((F & FH) != 0 || (a & 0x0F) > 0x09) a += 0x06;
                    }
                    else
                    {
                        if ((F & FC) != 0) a -= 0x60;
                        if ((F & FH) != 0) a -= 0x06;
                    }
                    A = a & 0xFF;
                    F = (F & (FN | FC)) | (A == 0 ? FZ : 0);
                    break;
                }
            case 0x2F: A ^= 0xFF; F |= FN | FH; break;
            case 0x37: F = (F & FZ) | FC; break;
            case 0x3F: F = (F & FZ) | ((F & FC) ^ FC); break;
            case 0xC0: case 0xC8: case 0xD0: case 0xD8: if (Cond(y)) { PC = Pop(); t = 20; } else t = 8; break;
            case 0xC9: PC = Pop(); t = 16; break;
            case 0xD9: PC = Pop(); Ime = true; t = 16; break;
            case 0xC1: case 0xD1: case 0xE1: { int v = Pop(); SetRp((op >> 4) & 3, v); t = 12; break; }
            case 0xF1: AF = Pop(); t = 12; break;
            case 0xC5: case 0xD5: case 0xE5: Push(Rp((op >> 4) & 3)); t = 16; break;
            case 0xF5: Push(AF); t = 16; break;
            case 0xC2: case 0xCA: case 0xD2: case 0xDA: { int a = Fetch16(); if (Cond(y)) { PC = a; t = 16; } else t = 12; break; }
            case 0xC3: PC = Fetch16(); t = 16; break;
            case 0xE9: PC = HL; break;
            case 0xC4: case 0xCC: case 0xD4: case 0xDC: { int a = Fetch16(); if (Cond(y)) { Push(PC); PC = a; t = 24; } else t = 12; break; }
            case 0xCD: { int a = Fetch16(); Push(PC); PC = a; t = 24; break; }
            case 0xC6: case 0xCE: case 0xD6: case 0xDE: case 0xE6: case 0xEE: case 0xF6: case 0xFE: Alu(y, Fetch()); t = 8; break;
            case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF: Push(PC); PC = y * 8; t = 16; break;
            case 0xCB: t = Cb(); break;
            case 0xE0: Wr(0xFF00 + Fetch(), A); t = 12; break;
            case 0xF0: A = Rd(0xFF00 + Fetch()); t = 12; break;
            case 0xE2: Wr(0xFF00 + C, A); t = 8; break;
            case 0xF2: A = Rd(0xFF00 + C); t = 8; break;
            case 0xE8: SP = SpPlus(Fetch()); t = 16; break;
            case 0xF8: HL = SpPlus(Fetch()); t = 12; break;
            case 0xF9: SP = HL; t = 8; break;
            case 0xEA: Wr(Fetch16(), A); t = 16; break;
            case 0xFA: A = Rd(Fetch16()); t = 16; break;
            case 0xF3: Ime = false; break;
            case 0xFB: Ime = true; break;
            default:
                // D3 DB DD E3 E4 EB EC ED F4 FC FD: 실제 칩은 멈춘다
                UnknownOps++; Jammed = true; break;
        }
        Cycles += t;
    }

    private int Cb()
    {
        int op = Fetch();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        int v = Reg(z);
        if (x == 1)
        {
            F = (F & FC) | FH | ((v & (1 << y)) == 0 ? FZ : 0);
            return z == 6 ? 12 : 8;
        }
        if (x == 2) { SetReg(z, v & ~(1 << y)); return z == 6 ? 16 : 8; }
        if (x == 3) { SetReg(z, v | (1 << y)); return z == 6 ? 16 : 8; }
        int r, c;
        switch (y)
        {
            case 0: c = v >> 7; r = (v << 1) | c; break;                                   // RLC
            case 1: c = v & 1; r = (v >> 1) | (c << 7); break;                              // RRC
            case 2: c = v >> 7; r = (v << 1) | ((F & FC) != 0 ? 1 : 0); break;            // RL
            case 3: c = v & 1; r = (v >> 1) | ((F & FC) != 0 ? 0x80 : 0); break;          // RR
            case 4: c = v >> 7; r = v << 1; break;                                          // SLA
            case 5: c = v & 1; r = (v >> 1) | (v & 0x80); break;                            // SRA
            case 6: c = 0; r = ((v & 15) << 4) | (v >> 4); break;                           // SWAP
            default: c = v & 1; r = v >> 1; break;                                          // SRL
        }
        r &= 0xFF;
        F = (r == 0 ? FZ : 0) | (c != 0 ? FC : 0);
        SetReg(z, r);
        return z == 6 ? 16 : 8;
    }
}
