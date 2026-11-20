using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio.Cpu;

namespace AdlibComposer.SelfTest;

/// <summary>x86 (80286 리얼 모드): 손으로 짠 짧은 프로그램으로 깃발 · 곱셈 · 나눗셈 · 시프트 · 문자열 · 호출 · 인터럽트 · I/O.</summary>
public static class X86Tests
{
    private sealed class Bus : IX86Bus
    {
        public readonly byte[] M = new byte[0x100000];
        public readonly List<(int port, int val)> Outs = new List<(int, int)>();
        public byte Read(int a) { return M[a & 0xFFFFF]; }
        public void Write(int a, byte v) { M[a & 0xFFFFF] = v; }
        public byte In(int p) { return p == 0x188 ? (byte)0x5A : (byte)0xFF; }
        public void Out(int p, byte v) { Outs.Add((p, v)); }
        public void Put(int seg, int off, params int[] b) { for (int i = 0; i < b.Length; i++) M[X86.Linear(seg, off + i)] = (byte)b[i]; }
    }

    /// <summary>코드를 1000:0000에 두고 HLT까지 돌린다. DS = 3000, ES = 4000, SS:SP = 2000:FFFE.</summary>
    private static (X86 cpu, Bus bus) Run(int[] code, Action<X86, Bus> setup = null)
    {
        var bus = new Bus();
        bus.Put(0x1000, 0, code);
        var c = new X86(bus) { CS = 0x1000, IP = 0, DS = 0x3000, ES = 0x4000, SS = 0x2000, SP = 0xFFFE };
        setup?.Invoke(c, bus);
        for (int n = 0; n < 100000 && !c.Halted; n++) c.Step();
        return (c, bus);
    }

    private static int[] C(params int[] b) { return b; }

    public static void Run(Action<string, bool, string> check)
    {
        var (c, _) = Run(C(0xB0, 0x7F, 0x04, 0x01, 0xF4));
        check("x86 ADD AL: 7F + 1 = 80, OF SF AF, not CF ZF PF", c.AL == 0x80 && c.GetFlag(X86.OF) && c.GetFlag(X86.SF) && c.GetFlag(X86.AF) && !c.GetFlag(X86.CF) && !c.GetFlag(X86.ZF) && !c.GetFlag(X86.PF),
            c.AL.ToString("X2") + " F=" + c.Flags.ToString("X4"));

        (c, _) = Run(C(0xB8, 0x00, 0x00, 0x2D, 0x01, 0x00, 0xF4));
        check("x86 SUB AX: 0 − 1 = FFFF, CF SF, not OF", c.AX == 0xFFFF && c.GetFlag(X86.CF) && c.GetFlag(X86.SF) && !c.GetFlag(X86.OF), c.AX.ToString("X4"));

        (c, _) = Run(C(0xB8, 0x34, 0x12, 0xBB, 0x00, 0x01, 0xF7, 0xE3, 0x89, 0xC6, 0x89, 0xD7, 0xF7, 0xF3, 0xF4));
        check("x86 MUL BX → DX:AX 0012:3400 (CF OF), DIV BX back to 1234", c.SI == 0x3400 && c.DI == 0x0012 && c.AX == 0x1234 && c.DX == 0, "SI=" + c.SI.ToString("X4") + " DI=" + c.DI.ToString("X4") + " AX=" + c.AX.ToString("X4"));

        (c, _) = Run(C(0xB8, 0x07, 0x01, 0xB3, 0x0A, 0xF6, 0xF3, 0xF4));
        check("x86 DIV BL: 263 / 10 = 26 r 3", c.AL == 26 && c.AH == 3, c.AX.ToString("X4"));

        (c, _) = Run(C(0xB8, 0xF9, 0xFF, 0x99, 0xB9, 0x02, 0x00, 0xF7, 0xF9, 0xF4));
        check("x86 CWD + IDIV CX: −7 / 2 = −3 r −1", c.AX == 0xFFFD && c.DX == 0xFFFF, c.AX.ToString("X4") + " " + c.DX.ToString("X4"));

        (c, _) = Run(C(0xB3, 0x00, 0xF6, 0xF3, 0xF4), (x, b) => { b.Put(0, 0, 0x00, 0x00, 0x00, 0x05); b.Put(0x0500, 0, 0xBA, 0xEF, 0xBE, 0xF4); });
        check("x86 divide by zero → INT 0 through vector table", c.DX == 0xBEEF && c.CS == 0x0500, "DX=" + c.DX.ToString("X4"));

        (c, _) = Run(C(0xB0, 0x81, 0xD0, 0xE0, 0xF4));
        check("x86 SHL AL,1: 81 → 02, CF OF", c.AL == 0x02 && c.GetFlag(X86.CF) && c.GetFlag(X86.OF), c.AL.ToString("X2"));
        (c, _) = Run(C(0xB1, 0x04, 0xBB, 0x01, 0x80, 0xD3, 0xFB, 0xBA, 0x01, 0x00, 0xD1, 0xCA, 0xB8, 0x01, 0x00, 0xC1, 0xE0, 0x03, 0xF4));
        check("x86 SAR BX,CL · ROR DX,1 · SHL AX,3 (186, CF from last shift = 0)", c.BX == 0xF800 && c.DX == 0x8000 && !c.GetFlag(X86.CF) && c.AX == 8, c.BX.ToString("X4") + " " + c.DX.ToString("X4") + " " + c.AX.ToString("X4"));

        // 문자열: DS:0 "HELLO" → ES:10, 비교, ES: 접두어로 읽기, REPNE SCASB
        (c, var bus) = Run(C(
            0xBE, 0x00, 0x00, 0xBF, 0x10, 0x00, 0xB9, 0x05, 0x00, 0xFC, 0xF3, 0xA4,   // REP MOVSB
            0xBE, 0x00, 0x00, 0xBF, 0x10, 0x00, 0xB9, 0x05, 0x00, 0xF3, 0xA6,         // REPE CMPSB
            0x9C, 0x5A,                                                               // PUSHF; POP DX
            0x26, 0xA0, 0x10, 0x00, 0x88, 0xC3,                                       // MOV AL,ES:[10]; MOV BL,AL
            0xBF, 0x10, 0x00, 0xB9, 0x05, 0x00, 0xB0, 0x4C, 0xF2, 0xAE, 0xF4),        // REPNE SCASB 'L'
            (x, b) => b.Put(0x3000, 0, 'H', 'E', 'L', 'L', 'O'));
        string copied = new string(Enumerable.Range(0, 5).Select(i => (char)bus.M[X86.Linear(0x4000, 0x10 + i)]).ToArray());
        check("x86 REP MOVSB · REPE CMPSB (equal) · ES: override · REPNE SCASB", copied == "HELLO" && (c.DX & X86.ZF) != 0 && c.BL == 'H' && c.DI == 0x13 && c.CX == 2,
            copied + " ZF=" + ((c.DX & X86.ZF) != 0) + " BL=" + (char)c.BL + " DI=" + c.DI.ToString("X") + " CX=" + c.CX);

        // 호출: PUSHA/POPA, CALL near → ENTER/LEAVE, CALL far → RETF
        (c, _) = Run(C(
            0xB8, 0x11, 0x11, 0xBB, 0x22, 0x22,       // 0: MOV AX,1111; MOV BX,2222
            0x60, 0x31, 0xC0, 0x31, 0xDB, 0x61,       // 6: PUSHA; XOR AX,AX; XOR BX,BX; POPA
            0xE8, 0x06, 0x00,                         // 12: CALL 21
            0x9A, 0x00, 0x00, 0x00, 0x06,             // 15: CALL FAR 0600:0000
            0xF4,                                     // 20: HLT
            0xC8, 0x04, 0x00, 0x00, 0x89, 0x46, 0xFE, 0x8B, 0x4E, 0xFE, 0xC9, 0xC3),   // 21: ENTER 4,0; MOV [BP-2],AX; MOV CX,[BP-2]; LEAVE; RET
            (x, b) => b.Put(0x0600, 0, 0xB2, 0x77, 0xCB));
        check("x86 PUSHA/POPA, CALL/RET, ENTER/LEAVE, CALL FAR/RETF", c.AX == 0x1111 && c.BX == 0x2222 && c.CX == 0x1111 && c.DL == 0x77 && c.SP == 0xFFFE && c.IP == 21 && c.CS == 0x1000,
            "AX=" + c.AX.ToString("X4") + " BX=" + c.BX.ToString("X4") + " CX=" + c.CX.ToString("X4") + " DL=" + c.DL.ToString("X2") + " SP=" + c.SP.ToString("X4") + " IP=" + c.IP);

        // 인터럽트: INT 21h는 바깥이, INT 60h는 벡터 표 처리기 + IRET
        var hooked = new List<int>();
        (c, _) = Run(C(0xB4, 0x30, 0xCD, 0x21, 0xFB, 0xCD, 0x60, 0xF4), (x, b) =>
        {
            x.HostInterrupt = n => { hooked.Add(n); if (n != 0x21) return false; x.AL = 5; return true; };
            b.Put(0, 0x60 * 4, 0x00, 0x00, 0x00, 0x07);
            b.Put(0x0700, 0, 0xB1, 0x42, 0xCF);
        });
        check("x86 INT 21h handled by host, INT 60h via vector + IRET (IF restored)", c.AL == 5 && c.CL == 0x42 && c.GetFlag(X86.IF) && hooked.SequenceEqual(new[] { 0x21, 0x60 }) && c.SP == 0xFFFE,
            "AL=" + c.AL + " CL=" + c.CL.ToString("X2") + " hooks " + string.Join(",", hooked.Select(h => h.ToString("X2"))));

        // 하드웨어 인터럽트: IF 꺼짐 → 받지 않음
        var bus2 = new Bus();
        var h = new X86(bus2) { CS = 0x1000, IP = 0, SS = 0x2000, SP = 0xFFFE };
        bool refused = !h.HardwareInterrupt(8);
        h.SetFlag(X86.IF, true);
        bool taken = h.HardwareInterrupt(8);
        check("x86 hardware interrupt respects IF", refused && taken && !h.GetFlag(X86.IF) && h.SP == 0xFFF8, refused + " " + taken);

        // I/O
        (c, bus) = Run(C(0xBA, 0x88, 0x01, 0xB0, 0x28, 0xEE, 0xEC, 0xE6, 0x37, 0xF4));
        check("x86 OUT DX,AL · IN AL,DX · OUT imm8", bus.Outs.SequenceEqual(new[] { (0x188, 0x28), (0x37, 0x5A) }), string.Join(" ", bus.Outs.Select(o => o.port.ToString("X") + "=" + o.val.ToString("X2"))));

        (c, _) = Run(C(0xB0, 0x19, 0x04, 0x28, 0x27, 0xF4));
        check("x86 DAA: BCD 19 + 28 = 47", c.AL == 0x47, c.AL.ToString("X2"));

        (c, _) = Run(C(0xB9, 0x0A, 0x00, 0x31, 0xC0, 0x01, 0xC8, 0xE2, 0xFC, 0xF4));
        check("x86 LOOP: 10 + 9 + … + 1 = 55", c.AX == 55 && c.CX == 0, c.AX.ToString());

        (c, _) = Run(C(0xBB, 0x20, 0x00, 0xB0, 0x03, 0xD7, 0xC4, 0x3E, 0x40, 0x00, 0xF4), (x, b) => { b.Put(0x3000, 0x20, 0x00, 0x11, 0x22, 0x33); b.Put(0x3000, 0x40, 0x34, 0x12, 0x78, 0x56); });
        check("x86 XLAT · LES", c.AL == 0x33 && c.DI == 0x1234 && c.ES == 0x5678, c.AL.ToString("X2") + " " + c.ES.ToString("X4") + ":" + c.DI.ToString("X4"));

        (c, _) = Run(C(0xB9, 0xFD, 0xFF, 0x6B, 0xC1, 0x07, 0xF4));
        check("x86 IMUL AX,CX,7 (186): −3 × 7 = −21", c.AX == 0xFFEB, c.AX.ToString("X4"));

        (c, _) = Run(C(0x0F, 0x01, 0xE0, 0x9C, 0x5B, 0xF4));
        check("x86 286: SMSW = real mode, PUSHF bits 12~15 = 0", c.AX == 0xFFF0 && (c.BX & 0xF000) == 0, c.AX.ToString("X4") + " " + c.BX.ToString("X4"));

        (c, _) = Run(C(0x90, 0x66, 0x40, 0xF4));
        check("x86 unknown opcode (386 0x66) halts at the instruction", c.Halted && c.UnknownOps == 1 && c.IP == 1 && c.LastUnknown == 0x66, "IP=" + c.IP + " op=" + c.LastUnknown.ToString("X"));

        // 체 (에라토스테네스): DS:0~999 표, 2~999 소수 개수 = 168
        (c, _) = Run(C(
            0xB9, 0xE8, 0x03,             // MOV CX,1000
            0x31, 0xFF,                   // XOR DI,DI
            0x8C, 0xD8, 0x8E, 0xC0,       // MOV AX,DS; MOV ES,AX
            0xB0, 0x01, 0xF3, 0xAA,       // MOV AL,1; REP STOSB      (모두 소수로)
            0xBE, 0x02, 0x00,             // 13: MOV SI,2
            0x80, 0x3C, 0x00,             // 16: CMP BYTE [SI],0
            0x74, 0x0F,                   // 19: JZ next (36)
            0x89, 0xF3,                   // 21: MOV BX,SI
            0x01, 0xF3,                   // 23: ADD BX,SI
            0x81, 0xFB, 0xE8, 0x03,       // 25: CMP BX,1000
            0x73, 0x05,                   // 29: JAE next (36)
            0xC6, 0x07, 0x00,             // 31: MOV BYTE [BX],0
            0xEB, 0xF3,                   // 34: JMP 23
            0x46,                         // 36: INC SI
            0x83, 0xFE, 0x20,             // CMP SI,32
            0x72, 0xE6,                   // JB 16
            0x31, 0xD2,                   // XOR DX,DX
            0xBE, 0x02, 0x00,             // MOV SI,2
            0x02, 0x14,                   // ADD DL,[SI]   (8비트 합 + 아래 ADC)
            0x80, 0xD6, 0x00,             // ADC DH,0
            0x46,                         // INC SI
            0x81, 0xFE, 0xE8, 0x03,       // CMP SI,1000
            0x72, 0xF4,                   // JB (ADD DL)
            0xF4));
        check("x86 integration: sieve of Eratosthenes below 1000 → 168 primes", c.DX == 168, "DX=" + c.DX + " unknown " + c.UnknownOps + " cycles " + c.Cycles);
        Run386(check);
    }

    /// <summary>386 리얼 모드 (Is386): 판별 · 66h · 67h · 0F 확장.</summary>
    private static void Run386(Action<string, bool, string> check)
    {
        Action<X86, Bus> on = (x, _) => x.Is386 = true;
        // CPU 판별: POPF F000h → 286은 bit12~15가 0, 386은 bit12~14가 남음
        int[] detect = C(0xB8, 0x00, 0xF0, 0x50, 0x9D, 0x9C, 0x58, 0x25, 0x00, 0xF0, 0xF4);
        var (c, _) = Run(detect);
        var (c3, _) = Run(detect, on);
        check("x86 386: FLAGS bit12-14 settable only with Is386 (CPU detection)", c.AX == 0 && c3.AX == 0x7000, c.AX.ToString("X4") + " / " + c3.AX.ToString("X4"));
        (c, _) = Run(C(0x66, 0xB8, 0x78, 0x56, 0x34, 0x12, 0xF4));
        check("x86 286: 66h prefix stays an unknown op", c.UnknownOps == 1, "");

        // MOV EAX,12345678h; ADD EAX,0F0000001h; SHL EAX,4; MOV AX,0BEEFh (위 16비트 그대로)
        (c, _) = Run(C(0x66, 0xB8, 0x78, 0x56, 0x34, 0x12, 0x66, 0x05, 0x01, 0x00, 0x00, 0xF0, 0x66, 0xC1, 0xE0, 0x04, 0xB8, 0xEF, 0xBE, 0xF4), on);
        check("x86 386: MOV/ADD/SHL EAX, 16-bit write keeps the upper half", c.EAX == 0x2345BEEFu && c.UnknownOps == 0, c.EAX.ToString("X8"));
        // EAX = 10000h, EBX = 30000h: MUL EBX → EDX:EAX = 3:00000000, DIV EBX → 10000h
        (c, _) = Run(C(0x66, 0xB8, 0x00, 0x00, 0x01, 0x00, 0x66, 0xBB, 0x00, 0x00, 0x03, 0x00, 0x66, 0xF7, 0xE3, 0x66, 0x89, 0xD6, 0x66, 0xF7, 0xF3, 0xF4), on);
        check("x86 386: MUL/DIV EBX through EDX:EAX", c.GetReg32(6) == 3 && c.EAX == 0x10000 && c.EDX == 0, "ESI=" + c.GetReg32(6).ToString("X8") + " EAX=" + c.EAX.ToString("X8"));
        // MOV BL,0F0h; MOVZX CX,BL; MOVSX EDX,BL; CMP CX,0F0h; SETE AL; JE near +1 (건너뜀: INC AH)
        (c, _) = Run(C(0xB3, 0xF0, 0x0F, 0xB6, 0xCB, 0x66, 0x0F, 0xBE, 0xD3, 0x81, 0xF9, 0xF0, 0x00, 0x0F, 0x94, 0xC0, 0x0F, 0x84, 0x02, 0x00, 0xFE, 0xC4, 0xF4), on);
        check("x86 386: MOVZX/MOVSX, SETE, near JE", c.CX == 0xF0 && c.EDX == 0xFFFFFFF0u && c.AL == 1 && c.AH == 0, "CX=" + c.CX.ToString("X4") + " EDX=" + c.EDX.ToString("X8") + " AX=" + c.AX.ToString("X4"));
        // FS = 3000h (PUSH DS; POP FS), MOV AL,FS:[10h]; PUSHAD/POPAD round trip; BT AX,3 → CF
        (c, _) = Run(C(0x1E, 0x0F, 0xA1, 0x64, 0xA0, 0x10, 0x00, 0x66, 0x60, 0x66, 0x31, 0xC9, 0x66, 0x61, 0x0F, 0xBA, 0xE0, 0x03, 0xF4),
            (x, b) => { x.Is386 = true; b.Put(0x3000, 0x10, 0x0C); x.SetReg32(1, 0xCAFEBABE); });
        check("x86 386: FS override, PUSHAD/POPAD, BT imm", c.FS == 0x3000 && c.AL == 0x0C && c.ECX == 0xCAFEBABE && c.GetFlag(X86.CF), "FS=" + c.FS.ToString("X4") + " AL=" + c.AL.ToString("X2") + " ECX=" + c.ECX.ToString("X8"));
        // 67h: MOV AX,[EBX+ECX*4+2] (DS:0+3*4+2 = 0Eh)
        (c, _) = Run(C(0x66, 0x31, 0xDB, 0x66, 0xB9, 0x03, 0x00, 0x00, 0x00, 0x67, 0x8B, 0x44, 0x8B, 0x02, 0xF4), (x, b) => { x.Is386 = true; b.Put(0x3000, 0x0E, 0x34, 0x12); });
        check("x86 386: 67h SIB addressing [EBX+ECX*4+2]", c.AX == 0x1234 && c.UnknownOps == 0, c.AX.ToString("X4"));
        // SHLD AX,BX,4: AX=1234, BX=ABCD → 234A
        (c, _) = Run(C(0xB8, 0x34, 0x12, 0xBB, 0xCD, 0xAB, 0x0F, 0xA4, 0xD8, 0x04, 0xF4), on);
        check("x86 386: SHLD AX,BX,4", c.AX == 0x234A, c.AX.ToString("X4"));
    }
}
