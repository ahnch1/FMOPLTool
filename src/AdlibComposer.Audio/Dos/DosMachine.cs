using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// MS-DOS 3.3 흉내 (hoot pc98dos · pcatdos처럼 사운드 드라이버를 돌리기 위한 것): 1MB 메모리 · <see cref="X86"/> · 메모리 안 파일(<see cref="DosFileSystem"/>).
/// <para>인터럽트 벡터는 처음에 모두 F000:(n×4)의 작은 처리기 "F1 n 00 CF"를 가리킨다: F1 = 호스트 함정이라 INT 20h · 21h · 27h · 29h · 2Fh는
/// C#이 처리하고 IRET으로 돌아간다. 상주 프로그램이 벡터를 가로채 옛 처리기로 넘겨도 여기까지 온다. 그 밖의 벡터는 그냥 IRET
/// (기종 흉내가 <see cref="Interrupt"/>을 덮어 BIOS를 더한다).</para>
/// <para>메모리: MCB 사슬 (0x0800 ~ <see cref="MemoryTop"/>, 첫 맞는 블록), PSP · 환경, COM (PSP:100h) · EXE (MZ, 재배치) 읽기, 4Bh 실행 · 4Ch 끝 · 31h · 27h 상주.
/// 파일 핸들은 프로세스마다가 아니라 하나의 표 (자식이 물려받음과 같다), 핸들 0~4 = CON · CON · CON · AUX · PRN, 0번 입력은 처음엔 비어 있다 (conin).
/// hoot처럼 파일 · 글자를 핸들 번호에 미리 묶을 수 있다 (<see cref="BindFile"/> · <see cref="BindText"/> · <see cref="Duplicate"/>).</para>
/// </summary>
public class DosMachine : IX86Bus
{
    public const int StubSeg = 0xF000, DosSeg = 0x0070, FirstMcb = 0x0800, MaxHandles = 256;   // hoot rom file offset = 핸들 번호, 40h 넘는 것도 (xak_98 40h~44h)
    /// <summary>호스트 함정 번호: 0~255 = 그 벡터의 기본 처리기, 이 값 = 바깥(C#)이 부른 far call에서 돌아옴.</summary>
    private const int TrapReturn = 0x0100;
    private const int ReturnStubOff = 0x0400, IdleOff = 0x0410;
    /// <summary>셸 블록 (PSP + 바깥이 부르는 far call의 스택) 크기.</summary>
    private const int ShellParas = 0x80;

    public readonly byte[] Mem = new byte[0x100000];
    public readonly X86 Cpu;
    public readonly DosFileSystem Fs;
    /// <summary>DOS가 쓰는 메모리 위 끝 (세그먼트, 기본 0xA000 = 640KB).</summary>
    public readonly int MemoryTop;
    /// <summary>표준 출력 · 오류에 쓴 바이트 (Shift_JIS).</summary>
    public readonly List<byte> ConsoleBytes = new List<byte>();
    /// <summary>처리하지 못한 INT 21h 기능 (AH, 횟수) — 진단.</summary>
    public readonly Dictionary<int, int> UnknownFunctions = new Dictionary<int, int>();
    /// <summary>열지 못한 파일 이름 (진단, 64개까지).</summary>
    public readonly List<string> MissingFiles = new List<string>();
    public int ReturnCode { get; private set; }
    /// <summary>마지막으로 읽은 프로그램의 PSP (진단 · 시험).</summary>
    public int LastLoadedPsp { get; private set; }
    /// <summary>INT 21h를 부른 횟수 (진단).</summary>
    public long DosCalls { get; private set; }

    public string ConsoleText { get { return KoreanText.ShiftJis.GetString(ConsoleBytes.ToArray()); } }

    // ------------------------------------------------------------ 파일 핸들

    private sealed class OpenFile
    {
        public string Name;
        public byte[] Data;
        public int Length;
        public int Pos;
        public bool Console, Nul, Dirty;
        public string Key;   // 쓴 내용을 돌려놓을 파일 이름 (없으면 null)
    }

    private readonly OpenFile[] handles = new OpenFile[MaxHandles];

    // ------------------------------------------------------------ 프로세스

    private sealed class Frame
    {
        public int ParentPsp, Ss, Sp;
        public bool ToHost;
    }

    private readonly Stack<Frame> frames = new Stack<Frame>();
    private int currentPsp;
    private int dtaSeg, dtaOff;
    private bool hostReturn;
    private int lastError;
    private readonly int shellPsp;
    private readonly Dictionary<int, (List<(string name, int size)> list, int next)> searches = new Dictionary<int, (List<(string, int)>, int)>();
    private bool ctrlBreak;

    public DosMachine(DosFileSystem fs, int memoryTop = 0xA000)
    {
        Fs = fs ?? new DosFileSystem();
        MemoryTop = Math.Max(0x2000, Math.Min(0xA000, memoryTop));
        Cpu = new X86(this) { HostTrap = Trap };
        // 벡터 → 기본 처리기 (F1 n 00 CF), 돌아옴 함정
        for (int v = 0; v < 256; v++)
        {
            Poke16(0, v * 4, v * 4); Poke16(0, v * 4 + 2, StubSeg);
            Poke(StubSeg, v * 4, 0xF1); Poke(StubSeg, v * 4 + 1, v); Poke(StubSeg, v * 4 + 2, 0); Poke(StubSeg, v * 4 + 3, 0xCF);
        }
        // 사용자 벡터 40h~7Fh를 DOS 세그먼트 안 함정으로 (NEC MS-DOS: 안 쓴 벡터 = 0060:xxxx — 상주 확인에 이 값을 보는 드라이버가 있다)
        if (UserVectorSeg != StubSeg)
            for (int v = 0x40; v < 0x80; v++)
            {
                int off = (v - 0x40) * 4;
                Poke16(0, v * 4, off); Poke16(0, v * 4 + 2, UserVectorSeg);
                Poke(UserVectorSeg, off, 0xF1); Poke(UserVectorSeg, off + 1, v); Poke(UserVectorSeg, off + 2, 0); Poke(UserVectorSeg, off + 3, 0xCF);
            }
        Poke(StubSeg, ReturnStubOff, 0xF1); Poke16(StubSeg, ReturnStubOff + 1, TrapReturn); Poke(StubSeg, ReturnStubOff + 3, 0xF4);
        // 쉬는 곳 (셸 프롬프트 대신): STI; HLT; JMP 앞으로
        Poke(StubSeg, IdleOff, 0xFB); Poke(StubSeg, IdleOff + 1, 0xF4); Poke(StubSeg, IdleOff + 2, 0xEB); Poke(StubSeg, IdleOff + 3, 0xFC);
        // DOS 데이터: 목록의 목록 (BX−2 = 첫 MCB), InDOS
        Poke16(DosSeg, 0x24, FirstMcb);
        // MCB 하나 (전체 빈 블록)
        WriteMcb(FirstMcb, 'Z', 0, MemoryTop - FirstMcb - 1);
        // 셸 (COMMAND.COM 자리): 작은 블록 하나
        int envShell = Alloc(4, 8);
        WriteEnvironment(envShell, "A:\\COMMAND.COM");
        shellPsp = Alloc(ShellParas, 8);
        BuildPsp(shellPsp, shellPsp + ShellParas, shellPsp, envShell, "");
        SetOwner(envShell, shellPsp); SetOwner(shellPsp, shellPsp);
        currentPsp = shellPsp;
        dtaSeg = shellPsp; dtaOff = 0x80;
        handles[0] = new OpenFile { Name = "CON", Console = true, Data = new byte[0] };
        handles[1] = handles[0];
        handles[2] = handles[0];
        handles[3] = new OpenFile { Name = "AUX", Nul = true, Data = new byte[0] };
        handles[4] = new OpenFile { Name = "PRN", Nul = true, Data = new byte[0] };
        Cpu.SS = shellPsp; Cpu.SP = ShellParas * 16 - 2; Cpu.CS = StubSeg; Cpu.IP = ReturnStubOff + 3; Cpu.Halted = true;
    }

    // ------------------------------------------------------------ 버스

    public virtual byte Read(int a) { return Mem[a & 0xFFFFF]; }
    public virtual void Write(int a, byte v)
    {
        a &= 0xFFFFF;
        if (a >= 0xF0000) return;   // ROM (함정 처리기)
        Mem[a] = v;
    }
    public virtual byte In(int port) { return 0xFF; }
    public virtual void Out(int port, byte value) { }

    protected void Poke(int seg, int off, int v) { Mem[X86.Linear(seg, off)] = (byte)v; }
    protected void Poke16(int seg, int off, int v) { Poke(seg, off, v); Poke(seg, off + 1, v >> 8); }
    protected int Peek(int seg, int off) { return Mem[X86.Linear(seg, off)]; }
    public int PeekByte(int seg, int off) { return Peek(seg, off); }
    public int PeekWord(int seg, int off) { return Peek16(seg, off); }
    protected int Peek16(int seg, int off) { return Peek(seg, off) | (Peek(seg, off + 1) << 8); }

    private string ReadAsciiz(int seg, int off, int max = 128)
    {
        var b = new List<byte>();
        for (int i = 0; i < max; i++) { int c = Peek(seg, off + i); if (c == 0) break; b.Add((byte)c); }
        return KoreanText.ShiftJis.GetString(b.ToArray());
    }

    /// <summary>seg:off부터 n바이트 (선형 주소로 이어 씀: 64KB를 넘는 EXE 그림도 오프셋이 감기지 않게).</summary>
    private void WriteBytes(int seg, int off, byte[] d, int start, int n)
    {
        int at = (seg << 4) + (off & 0xFFFF);
        for (int i = 0; i < n; i++) Mem[(at + i) & 0xFFFFF] = d[start + i];
    }

    // ------------------------------------------------------------ 메모리 (MCB)

    private void WriteMcb(int m, char kind, int owner, int size) { Poke(m, 0, kind); Poke16(m, 1, owner); Poke16(m, 3, size); }
    private bool McbOk(int m) { int k = Peek(m, 0); return k == 'M' || k == 'Z'; }

    /// <summary>빈 블록끼리 이어 붙인다.</summary>
    private void Coalesce()
    {
        for (int m = FirstMcb; McbOk(m) && m < MemoryTop;)
        {
            int size = Peek16(m, 3);
            if (Peek(m, 0) == 'Z') return;
            int n = m + size + 1;
            if (!McbOk(n)) return;
            if (Peek16(m, 1) == 0 && Peek16(n, 1) == 0)
            {
                WriteMcb(m, (char)Peek(n, 0), 0, size + Peek16(n, 3) + 1);
                continue;
            }
            m = n;
        }
    }

    /// <summary>paras 문단을 owner 것으로 (첫 맞는 블록). 실패하면 −1 (largest = 가장 큰 빈 블록).</summary>
    public int Alloc(int paras, int owner) { return Alloc(paras, owner, out _); }

    public int Alloc(int paras, int owner, out int largest)
    {
        Coalesce();
        largest = 0;
        for (int m = FirstMcb; McbOk(m);)
        {
            int size = Peek16(m, 3);
            char kind = (char)Peek(m, 0);
            if (Peek16(m, 1) == 0)
            {
                if (size >= paras)
                {
                    if (size > paras)
                    {
                        WriteMcb(m + paras + 1, kind, 0, size - paras - 1);
                        WriteMcb(m, 'M', owner, paras);
                    }
                    else WriteMcb(m, kind, owner, paras);
                    return m + 1;
                }
                largest = Math.Max(largest, size);
            }
            if (kind == 'Z') break;
            m += size + 1;
        }
        return -1;
    }

    public bool Free(int seg)
    {
        int m = seg - 1;
        if (!McbOk(m)) return false;
        Poke16(m, 1, 0);
        return true;
    }

    /// <summary>블록 크기 바꾸기. 실패하면 false, max = 가능한 가장 큰 크기.</summary>
    public bool Resize(int seg, int paras, out int max)
    {
        Coalesce();
        int m = seg - 1;
        max = 0;
        if (!McbOk(m)) return false;
        int size = Peek16(m, 3), owner = Peek16(m, 1);
        char kind = (char)Peek(m, 0);
        int avail = size;
        char endKind = kind;
        if (kind == 'M')
        {
            int n = m + size + 1;
            if (McbOk(n) && Peek16(n, 1) == 0) { avail += Peek16(n, 3) + 1; endKind = (char)Peek(n, 0); }
        }
        max = avail;
        if (paras > avail) return false;
        if (avail - paras >= 1)
        {
            WriteMcb(m, 'M', owner, paras);
            WriteMcb(m + paras + 1, endKind, 0, avail - paras - 1);
        }
        else WriteMcb(m, endKind, owner, avail);
        return true;
    }

    private void SetOwner(int seg, int owner) { Poke16(seg - 1, 1, owner); }

    /// <summary>owner의 블록을 모두 놓는다 (프로세스 끝).</summary>
    private void FreeOwnedBy(int owner)
    {
        for (int m = FirstMcb; McbOk(m);)
        {
            if (Peek16(m, 1) == owner) Poke16(m, 1, 0);
            if (Peek(m, 0) == 'Z') break;
            m += Peek16(m, 3) + 1;
        }
    }

    // ------------------------------------------------------------ 프로그램 읽기

    /// <summary>셸 환경 변수 ("이름=값", 셸의 SET으로 바꾸고 자식이 물려받는다).</summary>
    public readonly List<string> Environment = new List<string> { "COMSPEC=A:\\COMMAND.COM", "PATH=A:\\" };

    private void WriteEnvironment(int seg, string progPath)
    {
        var b = new List<byte>();
        foreach (var v in Environment) { b.AddRange(KoreanText.ShiftJis.GetBytes(v)); b.Add(0); }
        b.Add(0);
        b.Add(1); b.Add(0);
        b.AddRange(KoreanText.ShiftJis.GetBytes(progPath)); b.Add(0);
        for (int i = 0; i < b.Count && i < 0x7FF0; i++) Poke(seg, i, b[i]);
    }

    private void BuildPsp(int psp, int top, int parent, int env, string tail)
    {
        for (int i = 0; i < 0x100; i++) Poke(psp, i, 0);
        Poke(psp, 0, 0xCD); Poke(psp, 1, 0x20);
        Poke16(psp, 2, top);
        Poke16(psp, 0x0A, ReturnStubOff); Poke16(psp, 0x0C, StubSeg);
        Poke16(psp, 0x16, parent);
        for (int i = 0; i < 20; i++) Poke(psp, 0x18 + i, i < 5 ? i : 0xFF);
        Poke16(psp, 0x2C, env);
        Poke16(psp, 0x32, 20); Poke16(psp, 0x34, 0x18); Poke16(psp, 0x36, psp);
        Poke(psp, 0x50, 0xCD); Poke(psp, 0x51, 0x21); Poke(psp, 0x52, 0xCB);
        for (int f = 0; f < 2; f++) for (int i = 1; i < 12; i++) Poke(psp, 0x5C + f * 0x10 + i, ' ');
        var t = KoreanText.ShiftJis.GetBytes(tail ?? "");
        int n = Math.Min(126, t.Length);
        Poke(psp, 0x80, n);
        WriteBytes(psp, 0x81, t, 0, n);
        Poke(psp, 0x81 + n, 0x0D);
    }

    /// <summary>프로그램 파일 찾기 (그대로 · .COM · .EXE).</summary>
    public string ResolveProgram(string name)
    {
        foreach (var c in new[] { name, name + ".COM", name + ".EXE" })
            if (Fs.Exists(c)) return Fs.ResolveExisting(c);
        return null;
    }

    /// <summary>
    /// 프로그램을 읽어 시작할 준비 (레지스터를 그 시작점으로). 성공하면 새 PSP, 실패하면 −DOS 오류 (2 없음, 8 메모리 모자람, 11 형식).
    /// </summary>
    private int Load(string key, string tail, int envFrom)
    {
        if (!Fs.TryGet(key, out var img)) return -2;
        int envParas = Math.Max(0x10, (Environment.Sum(v => v.Length + 1) + 1 + 2 + key.Length + 4 + 15) / 16 + 1);
        int env = Alloc(envParas, 8);
        if (env < 0) return -8;
        WriteEnvironment(env, "A:\\" + key);
        bool exe = img.Length >= 0x1C && img[0] == 'M' && img[1] == 'Z' || img.Length >= 0x1C && img[0] == 'Z' && img[1] == 'M';
        Alloc(0xFFFF, 0, out int largest);
        int need, want, imageParas = 0, hdr = 0, imageLen = 0;
        if (exe)
        {
            int cblp = U16(img, 2), cp = U16(img, 4);
            hdr = U16(img, 8) * 16;
            imageLen = cp * 512 - (cblp != 0 ? 512 - cblp : 0) - hdr;
            imageLen = Math.Max(0, Math.Min(imageLen, img.Length - hdr));
            imageParas = (imageLen + 15) / 16;
            int min = U16(img, 0x0A), max = U16(img, 0x0C);
            need = 0x10 + imageParas + min;
            want = max == 0 ? largest : Math.Min(0xFFFF, 0x10 + imageParas + max);
        }
        else
        {
            need = (0x100 + img.Length + 0x100 + 15) / 16;
            want = largest;
        }
        if (largest < need) { Free(env); return -8; }
        int size = Math.Max(need, Math.Min(want, largest));
        int psp = Alloc(size, 8);
        if (psp < 0) { Free(env); return -8; }
        SetOwner(psp, psp); SetOwner(env, psp);
        BuildPsp(psp, psp + size, currentPsp, env, tail);
        if (exe)
        {
            int load = psp + 0x10;
            WriteBytes(load, 0, img, hdr, imageLen);
            int nrel = U16(img, 6), relAt = U16(img, 0x18);
            for (int i = 0; i < nrel && relAt + i * 4 + 4 <= img.Length; i++)
            {
                int off = U16(img, relAt + i * 4), seg = U16(img, relAt + i * 4 + 2);
                Poke16(load + seg, off, Peek16(load + seg, off) + load);
            }
            Cpu.SS = (U16(img, 0x0E) + load) & 0xFFFF; Cpu.SP = U16(img, 0x10);
            Cpu.CS = (U16(img, 0x16) + load) & 0xFFFF; Cpu.IP = U16(img, 0x14);
        }
        else
        {
            WriteBytes(psp, 0x100, img, 0, Math.Min(img.Length, size * 16 - 0x100));
            Cpu.SS = psp; Cpu.SP = size >= 0x1000 ? 0xFFFE : size * 16 - 2;
            Cpu.CS = psp; Cpu.IP = 0x100;
            Cpu.Push(0);
        }
        Cpu.DS = Cpu.ES = psp;
        Cpu.AX = 0; Cpu.BX = 0; Cpu.CX = 0xFF; Cpu.DX = psp; Cpu.SI = Cpu.IP; Cpu.DI = 0; Cpu.BP = 0x91C;   // DI = 0: hoot의 DOS (실제 DOS는 SP) — syakyulm SS_98은 곡 이름 주소를 XOR DI,DX로 만든다
        Cpu.Flags = 0x0202;
        currentPsp = psp;
        LastLoadedPsp = psp;
        dtaSeg = psp; dtaOff = 0x80;
        Cpu.Halted = false;
        return psp;
    }

    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }

    // ------------------------------------------------------------ 바깥에서 쓰기

    public enum ShellResult { Exited, Resident, Running, NotFound, NoMemory, Halted }

    /// <summary>셸 한 줄 ("PMD # /m8 /v0" · "pmd_98"): 프로그램을 돌려 끝나거나 상주하면 돌아온다. maxCycles 안에 끝나지 않으면 Running (계속 돌리려면 <see cref="Run"/>).</summary>
    public ShellResult Shell(string commandLine, long maxCycles = 200_000_000)
    {
        string line = (commandLine ?? "").Trim();
        int sp = line.IndexOfAny(new[] { ' ', '\t', '/' });
        string prog = sp < 0 ? line : line.Substring(0, sp);
        string tail = sp < 0 ? "" : (line[sp] == '/' ? line.Substring(sp) : line.Substring(sp));
        if (tail.Length > 0 && tail[0] != ' ') tail = " " + tail;
        if (BuiltIn(prog.ToUpperInvariant(), tail.Trim())) { ReturnCode = 0; return ShellResult.Exited; }
        string key = ResolveProgram(prog);
        if (key == null) return ShellResult.NotFound;
        frames.Push(new Frame { ParentPsp = currentPsp, ToHost = true, Ss = Cpu.SS, Sp = Cpu.SP });
        hostReturn = false;
        int psp = Load(key, tail, currentPsp);
        if (psp < 0) { frames.Pop(); return psp == -2 ? ShellResult.NotFound : ShellResult.NoMemory; }
        Run(maxCycles);
        if (hostReturn) return (ReturnCode >> 8) == 3 ? ShellResult.Resident : ShellResult.Exited;
        return Cpu.Halted && !HaltIsIdle ? ShellResult.Halted : ShellResult.Running;
    }

    /// <summary>COMMAND.COM 안 명령: SET · PATH · CD · ECHO · REM · CLS · PROMPT · BREAK · VERIFY (그 밖은 false = 프로그램으로 찾기).</summary>
    private bool BuiltIn(string cmd, string arg)
    {
        switch (cmd)
        {
            case "SET":
                {
                    int eq = arg.IndexOf('=');
                    if (eq <= 0) return true;
                    string name = arg.Substring(0, eq).Trim().ToUpperInvariant(), val = arg.Substring(eq + 1);
                    Environment.RemoveAll(v => v.StartsWith(name + "=", StringComparison.Ordinal));
                    if (val.Length > 0) Environment.Add(name + "=" + val);
                    return true;
                }
            case "PATH": return BuiltIn("SET", "PATH=" + arg.TrimStart('='));
            case "CD": case "CHDIR":
                if (arg.Length > 0 && Fs.DirectoryExists(arg)) Fs.CurrentDir = Fs.Key(arg);
                return true;
            case "ECHO": case "REM": case "CLS": case "PROMPT": case "BREAK": case "VERIFY": case "@ECHO": return true;
        }
        return false;
    }

    /// <summary>HLT가 인터럽트를 기다리는 것인가 (기종 흉내는 true: 아직 도는 중).</summary>
    protected virtual bool HaltIsIdle { get { return false; } }

    /// <summary>벡터 40h~7Fh의 기본 처리기 세그먼트 (PC-98 = 0060h: NEC MS-DOS처럼, 그 밖 = <see cref="StubSeg"/>). 함정은 seg:(v−40h)×4.</summary>
    protected virtual int UserVectorSeg { get { return StubSeg; } }

    /// <summary>벡터 v가 아직 기본 처리기 (아무도 가로채지 않음)인가.</summary>
    public bool VectorIsStub(int v)
    {
        int seg = Peek16(0, (v & 0xFF) * 4 + 2);
        return seg == StubSeg || (seg == UserVectorSeg && v >= 0x40 && v < 0x80);
    }

    /// <summary>CPU를 cycles만큼 (또는 바깥으로 돌아오거나 HLT까지) 돌린다.</summary>
    public virtual void Run(long cycles)
    {
        long end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end && !hostReturn && !Cpu.Halted) Cpu.Step();
    }

    /// <summary>far call seg:off을 바깥에서 부르고 RETF로 돌아올 때까지 (장치 드라이버 초기화 등). 돌아오면 true.</summary>
    public bool CallFar(int seg, int off, long maxCycles = 50_000_000)
    {
        int ss = Cpu.SS, sp = Cpu.SP, cs = Cpu.CS, ip = Cpu.IP; bool halted = Cpu.Halted;
        Cpu.SS = shellPsp; Cpu.SP = ShellParas * 16 - 2;   // 셸 블록 끝의 스택
        Cpu.Push(StubSeg); Cpu.Push(ReturnStubOff);
        Cpu.CS = seg; Cpu.IP = off; Cpu.Halted = false;
        hostReturn = false;
        frames.Push(new Frame { ToHost = true, ParentPsp = currentPsp, Ss = ss, Sp = sp });
        Run(maxCycles);
        bool ok = hostReturn;
        if (!ok) frames.Pop();
        hostReturn = false;
        Cpu.SS = ss; Cpu.SP = sp; Cpu.CS = cs; Cpu.IP = ip; Cpu.Halted = halted;
        return ok;
    }

    /// <summary>config.sys의 device= 줄: 드라이버를 읽어 초기화 (전략 → 인터럽트, 명령 0) 하고 끝 주소까지 남긴다. 성공하면 드라이버 세그먼트, 실패하면 −1.</summary>
    /// <summary>진단: 마지막 <see cref="LoadDevice"/>의 호출 결과 · 끝 주소 · 상태.</summary>
    public string LastDeviceResult = "";

    public int LoadDevice(string commandLine)
    {
        string line = (commandLine ?? "").Trim();
        int sp = line.IndexOf(' ');
        string key = ResolveProgram(sp < 0 ? line : line.Substring(0, sp));
        if (key == null || !Fs.TryGet(key, out var img)) return -1;
        int seg;
        Alloc(0xFFFF, 0, out int largest);
        int blk = Alloc(largest, 8);
        if (blk < 0) return -1;
        if (img.Length >= 2 && img[0] == 'M' && img[1] == 'Z')
        {
            int hdr = U16(img, 8) * 16;
            seg = blk;
            WriteBytes(seg, 0, img, hdr, Math.Min(img.Length - hdr, largest * 16));
            int nrel = U16(img, 6), relAt = U16(img, 0x18);
            for (int i = 0; i < nrel; i++) { int o = U16(img, relAt + i * 4), s = U16(img, relAt + i * 4 + 2); Poke16(seg + s, o, Peek16(seg + s, o) + seg); }
        }
        else { seg = blk; WriteBytes(seg, 0, img, 0, Math.Min(img.Length, largest * 16)); }
        // 요청 머리 (DOS 데이터 0x100): 길이 · 장치 · 명령 0 · 상태, +0Eh 끝 주소, +12h 명령 줄
        int rq = 0x100, cmd = 0x180;
        for (int i = 0; i < 0x30; i++) Poke(DosSeg, rq + i, 0);
        Poke(DosSeg, rq, 0x17);
        // MS-DOS SYSINIT처럼 파일 이름 뒤 첫 빈칸을 NUL로 (이름은 ASCIIZ, 인자는 그 뒤): NUL까지 건너뛰어 인자를 찾는 드라이버가 있다 (MMD2.SYS)
        var cl = KoreanText.ShiftJis.GetBytes(line + "\r\n");
        if (sp > 0 && sp < cl.Length) cl[sp] = 0;
        WriteBytes(DosSeg, cmd, cl, 0, Math.Min(cl.Length, 0x7F));
        Poke16(DosSeg, rq + 0x12, cmd); Poke16(DosSeg, rq + 0x14, DosSeg);
        Poke(DosSeg, rq + 0x16, 2);
        int strategy = Peek16(seg, 6), intr = Peek16(seg, 8);
        int es = Cpu.ES, bx = Cpu.BX, ds = Cpu.DS;
        // DS = 드라이버 세그먼트 (MS-DOS SYSINIT처럼): CS: 없이 [12h]에 요청 주소를 적는 드라이버가 있다 (NMUSE.DRV)
        Cpu.ES = DosSeg; Cpu.BX = rq; Cpu.DS = seg;
        bool ok = CallFar(seg, strategy);
        Cpu.ES = DosSeg; Cpu.BX = rq; Cpu.DS = seg;
        ok &= CallFar(seg, intr);
        Cpu.ES = es; Cpu.BX = bx; Cpu.DS = ds;
        int endOff = Peek16(DosSeg, rq + 0x0E), endSeg = Peek16(DosSeg, rq + 0x10);
        int keep = ((endSeg * 16 + endOff) - seg * 16 + 15) / 16;
        LastDeviceResult = "call " + ok + ", end " + endSeg.ToString("X4") + ":" + endOff.ToString("X4") + ", status " + Peek16(DosSeg, rq + 3).ToString("X4");
        if (!ok || keep <= 0 || (Peek16(DosSeg, rq + 3) & 0x8000) != 0) { Free(blk); return -1; }
        Resize(blk, Math.Min(keep, largest), out _);
        return seg;
    }

    /// <summary>파일을 핸들 번호에 미리 묶는다 (hoot rom type="file" offset="n").</summary>
    public bool BindFile(int handle, string name)
    {
        if (handle < 0 || handle >= MaxHandles) return false;
        if (!Fs.TryGet(name, out var d))
        {
            // 목록의 폴더 (DISC_1/AMUS.DAT)가 zip에 없으면 이름만 같은 파일 (zip 뿌리의 AMUS.DAT)
            string leaf = name.Replace('/', '\\');
            leaf = leaf.Substring(leaf.LastIndexOf('\\') + 1);
            string hit = Fs.Names.FirstOrDefault(n => string.Equals(n.Substring(n.LastIndexOf('\\') + 1), leaf, StringComparison.OrdinalIgnoreCase));
            if (hit == null || !Fs.TryGet(hit, out d)) return false;
        }
        handles[handle] = new OpenFile { Name = name, Data = d, Length = d.Length };
        boundFiles[handle] = name;
        return true;
    }

    private readonly string[] boundFiles = new string[MaxHandles];

    /// <summary>
    /// hoot 곡 요청: 곡 코드 번호의 핸들을 0번으로 복사 (처음으로 되감음). 드라이버 · 재생기가 부팅 중 핸들을 닫았으면
    /// (<see cref="BindFile"/>로 묶어 둔 파일을) 다시 연다 — 상주하기 전에 핸들 0~31을 모두 닫는 드라이버가 있다 (FUGA OPNDRV).
    /// </summary>
    public bool SelectSongHandle(int handle)
    {
        if (handle < 0 || handle >= MaxHandles) return false;
        if (handles[handle] == null && boundFiles[handle] != null) BindFile(handle, boundFiles[handle]);
        return handles[handle] != null && Duplicate(handle, 0);
    }

    /// <summary>글자를 핸들 번호에 묶는다 (hoot rom type="conin": 되감을 수 있는 가짜 입력).</summary>
    public void BindText(int handle, byte[] text)
    {
        if (handle < 0 || handle >= MaxHandles) return;
        handles[handle] = new OpenFile { Name = "CONIN", Data = text ?? new byte[0], Length = text?.Length ?? 0 };
    }

    /// <summary>핸들 from을 to로 이중화 (DOS 46h; hoot는 곡 번호의 핸들을 0번 입력에). 위치는 처음으로.</summary>
    public bool Duplicate(int from, int to, bool rewind = true)
    {
        if (from < 0 || from >= MaxHandles || to < 0 || to >= MaxHandles || handles[from] == null) return false;
        handles[to] = handles[from];
        if (rewind) handles[to].Pos = 0;
        return true;
    }

    public bool HandleOpen(int h) { return h >= 0 && h < MaxHandles && handles[h] != null; }
    public bool HostReturned { get { return hostReturn; } }

    /// <summary>셸로 돌아온 뒤 (상주 · 끝남): CPU를 셸 스택 위의 쉬는 고리(STI · HLT)에 두어 인터럽트(타이머 · hoot)를 계속 받게 한다.</summary>
    public void Idle()
    {
        hostReturn = false;
        Cpu.SS = shellPsp; Cpu.SP = ShellParas * 16 - 2; Cpu.CS = StubSeg; Cpu.IP = IdleOff; Cpu.Halted = false;
    }

    // ------------------------------------------------------------ 함정 · 인터럽트

    private void Trap(int id)
    {
        if (id > TrapReturn) { MachineTrap(id); return; }
        if (id == TrapReturn)
        {
            // 바깥이 부른 far call이 돌아옴
            var f = frames.Pop();
            hostReturn = true;
            Cpu.Halted = true;
            return;
        }
        Interrupt(id & 0xFF);
    }

    /// <summary>기종 흉내가 F000 영역에 둔 함정 (F1 id, id &gt; 0x100) — 기본은 아무것도 안 함.</summary>
    protected virtual void MachineTrap(int id) { }

    /// <summary>기본 벡터 처리기 (F000:n×4)에서. 이 뒤에 IRET이 이어진다. 기종 흉내가 덮어 BIOS를 더한다.</summary>
    protected virtual void Interrupt(int n)
    {
        switch (n)
        {
            case 0x20: Terminate(0, -1); break;
            case 0x21: Int21(); break;
            case 0x27: Terminate(0, (Cpu.DX + 15) / 16, true); break;
            case 0x29: ConsoleOut(Cpu.AL); break;
            case 0x2F: if (Cpu.AH != 0x16 && Cpu.AH != 0x43) Cpu.AL = 0; break;   // 다중 처리: 설치 안 됨
        }
    }

    /// <summary>쌓인 FLAGS (IRET이 꺼낼 것)의 깃발.</summary>
    protected void SetStackFlag(int flag, bool on)
    {
        int f = Peek16(Cpu.SS, Cpu.SP + 4);
        Poke16(Cpu.SS, Cpu.SP + 4, on ? f | flag : f & ~flag);
    }

    private void Ok() { SetStackFlag(X86.CF, false); }
    private void Fail(int err) { lastError = err; Cpu.AX = err; SetStackFlag(X86.CF, true); }

    private void ConsoleOut(int c) { ConsoleBytes.Add((byte)c); if (ConsoleBytes.Count > 1 << 20) ConsoleBytes.RemoveRange(0, 1 << 19); }

    /// <summary>프로세스 끝: keep ≥ 0이면 상주 (PSP 블록을 keep 문단으로).</summary>
    private void Terminate(int code, int keep, bool resident = false)
    {
        int psp = currentPsp;
        if (keep >= 0)
        {
            Resize(psp, Math.Max(keep, 6), out _);
            ReturnCode = (code & 0xFF) | 0x300;
        }
        else
        {
            FreeOwnedBy(psp);
            ReturnCode = code & 0xFF;
        }
        if (frames.Count == 0) { hostReturn = true; Cpu.Halted = true; return; }
        var f = frames.Pop();
        currentPsp = f.ParentPsp;
        dtaSeg = currentPsp; dtaOff = 0x80;
        if (f.ToHost)
        {
            hostReturn = true; Cpu.Halted = true;
            Cpu.SS = f.Ss; Cpu.SP = f.Sp;
            return;
        }
        // 부모의 INT 21h 4Bh로 돌아감: 그때의 스택 (IRET 틀이 위에)
        Cpu.SS = f.Ss; Cpu.SP = f.Sp;
        Cpu.CS = StubSeg; Cpu.IP = 0x21 * 4 + 3;
        SetStackFlag(X86.CF, false);
    }

    // ------------------------------------------------------------ INT 21h

    private void Int21()
    {
        int ah = Cpu.AH;
        DosCalls++;
        switch (ah)
        {
            case 0x00: Terminate(0, -1); return;
            case 0x01: case 0x07: case 0x08: { int c = ReadStdin(); Cpu.AL = c < 0 ? 0x0D : c; if (ah == 0x01) ConsoleOut(Cpu.AL); return; }
            case 0x02: ConsoleOut(Cpu.DL); return;
            case 0x06:
                if (Cpu.DL != 0xFF) { ConsoleOut(Cpu.DL); return; }
                { int c = ReadStdin(); Cpu.AL = c < 0 ? 0 : c; SetStackFlag(X86.ZF, c < 0); }
                return;
            case 0x09:
                for (int i = 0; i < 0x10000; i++) { int c = Peek(Cpu.DS, Cpu.DX + i); if (c == '$') break; ConsoleOut(c); }
                Cpu.AL = '$';
                return;
            case 0x0A: Poke(Cpu.DS, Cpu.DX + 1, 0); Poke(Cpu.DS, Cpu.DX + 2, 0x0D); return;
            case 0x0B: Cpu.AL = 0; return;
            case 0x0C: { int al = Cpu.AL; if (al == 1 || al == 6 || al == 7 || al == 8 || al == 0x0A) { Cpu.AH = al; Int21(); Cpu.AH = 0x0C; } else Cpu.AL = 0; return; }
            case 0x0D: return;
            case 0x0E: Cpu.AL = 5; return;
            case 0x19: Cpu.AL = 0; return;
            case 0x1A: dtaSeg = Cpu.DS; dtaOff = Cpu.DX; return;
            case 0x25: Poke16(0, Cpu.AL * 4, Cpu.DX); Poke16(0, Cpu.AL * 4 + 2, Cpu.DS); return;
            case 0x29: ParseFcbName(); return;
            case 0x2A: Cpu.CX = 1995; Cpu.DH = 1; Cpu.DL = 1; Cpu.AL = 0; return;
            case 0x2B: case 0x2D: Cpu.AL = 0; return;
            case 0x2C:
                {
                    long cs = Cpu.Cycles / 80000;   // 대략 1/100초 (8MHz 기준)
                    Cpu.CH = (int)(cs / 360000 % 24); Cpu.CL = (int)(cs / 6000 % 60); Cpu.DH = (int)(cs / 100 % 60); Cpu.DL = (int)(cs % 100);
                    return;
                }
            case 0x2E: return;
            case 0x2F: Cpu.ES = dtaSeg; Cpu.BX = dtaOff; return;
            case 0x30: Cpu.AX = 0x1E03; Cpu.BX = 0; Cpu.CX = 0; return;
            case 0x31: Terminate(Cpu.AL, Cpu.DX, true); return;
            case 0x33:
                if (Cpu.AL == 0) Cpu.DL = ctrlBreak ? 1 : 0;
                else if (Cpu.AL == 1) ctrlBreak = Cpu.DL != 0;
                else if (Cpu.AL == 5) Cpu.DL = 1;
                return;
            case 0x34: Cpu.ES = DosSeg; Cpu.BX = 0x20; return;
            case 0x35: Cpu.BX = Peek16(0, Cpu.AL * 4); Cpu.ES = Peek16(0, Cpu.AL * 4 + 2); return;
            case 0x36: Cpu.AX = 4; Cpu.BX = 0x4000; Cpu.CX = 512; Cpu.DX = 0xFFFF; return;
            case 0x37: if (Cpu.AL == 0) Cpu.DL = '/'; Cpu.AL = 0; return;
            case 0x38: for (int i = 0; i < 34; i++) Poke(Cpu.DS, Cpu.DX + i, 0); Cpu.BX = 81; Ok(); return;
            case 0x39: case 0x3A: Ok(); return;
            case 0x3B:
                {
                    string p = ReadAsciiz(Cpu.DS, Cpu.DX);
                    if (!Fs.DirectoryExists(p)) { Fail(3); return; }
                    Fs.CurrentDir = Fs.Key(p);
                    Ok(); return;
                }
            case 0x3C: case 0x5B: CreateFile(ah == 0x5B); return;
            case 0x3D: OpenFileFn(); return;
            case 0x3E: CloseFile(Cpu.BX); return;
            case 0x3F: ReadFile(); return;
            case 0x40: WriteFile(); return;
            case 0x41: if (Fs.Delete(ReadAsciiz(Cpu.DS, Cpu.DX))) Ok(); else Fail(2); return;
            case 0x42: Seek(); return;
            case 0x43:
                if (!Fs.Exists(ReadAsciiz(Cpu.DS, Cpu.DX)) && !Fs.DirectoryExists(ReadAsciiz(Cpu.DS, Cpu.DX))) { Fail(2); return; }
                if (Cpu.AL == 0) Cpu.CX = Fs.Exists(ReadAsciiz(Cpu.DS, Cpu.DX)) ? 0x20 : 0x10;
                Ok(); return;
            case 0x44: Ioctl(); return;
            case 0x45:
                {
                    int h = Cpu.BX;
                    if (!HandleOpen(h)) { Fail(6); return; }
                    int n = FreeHandle();
                    if (n < 0) { Fail(4); return; }
                    handles[n] = handles[h]; Cpu.AX = n; Ok(); return;
                }
            case 0x46:
                {
                    if (!HandleOpen(Cpu.BX) || Cpu.CX >= MaxHandles) { Fail(6); return; }
                    handles[Cpu.CX] = handles[Cpu.BX]; Ok(); return;
                }
            case 0x47:
                {
                    var b = KoreanText.ShiftJis.GetBytes(Fs.CurrentDir);
                    WriteBytes(Cpu.DS, Cpu.SI, b, 0, b.Length); Poke(Cpu.DS, Cpu.SI + b.Length, 0);
                    Cpu.AX = 0x0100; Ok(); return;
                }
            case 0x48:
                {
                    int seg = Alloc(Cpu.BX, currentPsp, out int largest);
                    if (seg < 0) { Cpu.BX = largest; Fail(8); return; }
                    Cpu.AX = seg; Ok(); return;
                }
            case 0x49: if (Free(Cpu.ES)) Ok(); else Fail(9); return;
            case 0x4A:
                {
                    if (Resize(Cpu.ES, Cpu.BX, out int max)) { Ok(); return; }
                    Cpu.BX = max; Fail(McbOk(Cpu.ES - 1) ? 8 : 9); return;
                }
            case 0x4B: Exec(); return;
            case 0x4C: Terminate(Cpu.AL, -1); return;
            case 0x4D: Cpu.AX = ReturnCode; ReturnCode = 0; return;
            case 0x4E: FindFirst(); return;
            case 0x4F: FindNext(); return;
            case 0x50: currentPsp = Cpu.BX; return;
            case 0x51: case 0x62: Cpu.BX = currentPsp; return;
            case 0x52: Cpu.ES = DosSeg; Cpu.BX = 0x26; return;
            case 0x54: Cpu.AL = 0; return;
            case 0x56: if (Fs.Rename(ReadAsciiz(Cpu.DS, Cpu.DX), ReadAsciiz(Cpu.ES, Cpu.DI))) Ok(); else Fail(5); return;
            case 0x57: if (Cpu.AL == 0) { Cpu.CX = 0; Cpu.DX = (15 << 9) | (1 << 5) | 1; } Ok(); return;
            case 0x58: if (Cpu.AL == 0) Cpu.AX = 0; Ok(); return;
            case 0x59: Cpu.AX = lastError; Cpu.BX = 0x0104; Cpu.CH = 1; return;
            case 0x5A:
                {
                    int i = 0; string name;
                    string dir = ReadAsciiz(Cpu.DS, Cpu.DX);
                    do name = dir + "TMP" + (i++).ToString("X5"); while (Fs.Exists(name));
                    var b = KoreanText.ShiftJis.GetBytes(name.Substring(dir.Length));
                    WriteBytes(Cpu.DS, Cpu.DX + dir.Length, b, 0, b.Length); Poke(Cpu.DS, Cpu.DX + dir.Length + b.Length, 0);
                    CreateFile(false); return;
                }
            case 0x67: case 0x68: Ok(); return;
        }
        UnknownFunctions[ah] = UnknownFunctions.TryGetValue(ah, out int n0) ? n0 + 1 : 1;
        Fail(1);
    }

    private int ReadStdin()
    {
        var f = handles[0];
        if (f == null || f.Console || f.Pos >= f.Length) return -1;
        return f.Data[f.Pos++];
    }

    private int FreeHandle()
    {
        for (int i = 5; i < MaxHandles; i++) if (handles[i] == null) return i;
        return -1;
    }

    private static bool IsDeviceName(string n, out string dev)
    {
        string b = n.ToUpperInvariant().Replace('/', '\\');
        int c = b.LastIndexOf('\\'); if (c >= 0) b = b.Substring(c + 1);
        int dot = b.IndexOf('.'); if (dot >= 0) b = b.Substring(0, dot);
        if (b.Length >= 2 && b[1] == ':') b = b.Substring(2);
        dev = b;
        return b == "CON" || b == "NUL" || b == "AUX" || b == "PRN";
    }

    private void OpenFileFn()
    {
        string name = ReadAsciiz(Cpu.DS, Cpu.DX);
        int h = FreeHandle();
        if (h < 0) { Fail(4); return; }
        if (IsDeviceName(name, out string dev))
        {
            handles[h] = dev == "CON" ? handles[1] ?? new OpenFile { Name = "CON", Console = true, Data = new byte[0] } : new OpenFile { Name = dev, Nul = true, Data = new byte[0] };
            Cpu.AX = h; Ok(); return;
        }
        string key = Fs.ResolveExisting(name);
        if (key == null || !Fs.TryGet(key, out var d)) { if (MissingFiles.Count < 64) MissingFiles.Add(name); Fail(Fs.DirectoryExists(name) ? 5 : 2); return; }
        handles[h] = new OpenFile { Name = name, Key = key, Data = d, Length = d.Length };
        Cpu.AX = h; Ok();
    }

    private void CreateFile(bool mustBeNew)
    {
        string name = ReadAsciiz(Cpu.DS, Cpu.DX);
        if (mustBeNew && Fs.Exists(name)) { Fail(0x50); return; }
        int h = FreeHandle();
        if (h < 0) { Fail(4); return; }
        if (IsDeviceName(name, out string dev)) { handles[h] = new OpenFile { Name = dev, Nul = dev != "CON", Console = dev == "CON", Data = new byte[0] }; Cpu.AX = h; Ok(); return; }
        Fs.Put(name, new byte[0]);
        handles[h] = new OpenFile { Name = name, Key = Fs.ResolveExisting(name), Data = new byte[0], Dirty = true };
        Cpu.AX = h; Ok();
    }

    private void CloseFile(int h)
    {
        if (!HandleOpen(h)) { Fail(6); return; }
        var f = handles[h];
        handles[h] = null;
        if (f.Dirty && f.Key != null && !handles.Contains(f))
        {
            var d = new byte[f.Length]; Array.Copy(f.Data, d, f.Length);
            Fs.Put(f.Key, d);
        }
        Ok();
    }

    private void ReadFile()
    {
        int h = Cpu.BX;
        if (!HandleOpen(h)) { Fail(6); return; }
        var f = handles[h];
        if (f.Console || f.Nul) { Cpu.AX = 0; Ok(); return; }
        int n = Math.Max(0, Math.Min(Cpu.CX, f.Length - f.Pos));
        WriteBytes(Cpu.DS, Cpu.DX, f.Data, f.Pos, n);
        f.Pos += n;
        Cpu.AX = n; Ok();
    }

    private void WriteFile()
    {
        int h = Cpu.BX, n = Cpu.CX;
        if (!HandleOpen(h)) { Fail(6); return; }
        var f = handles[h];
        if (f.Console) { for (int i = 0; i < n; i++) ConsoleOut(Peek(Cpu.DS, Cpu.DX + i)); Cpu.AX = n; Ok(); return; }
        if (f.Nul) { Cpu.AX = n; Ok(); return; }
        if (n == 0) { f.Length = f.Pos; f.Dirty = true; Cpu.AX = 0; Ok(); return; }   // 0바이트 쓰기 = 여기서 자르기
        if (f.Pos + n > f.Data.Length || !f.Dirty)
        {
            var d = new byte[Math.Max(f.Pos + n, f.Length) + 256];
            Array.Copy(f.Data, d, f.Length);
            f.Data = d;
        }
        for (int i = 0; i < n; i++) f.Data[f.Pos + i] = (byte)Peek(Cpu.DS, Cpu.DX + i);
        f.Pos += n; f.Length = Math.Max(f.Length, f.Pos); f.Dirty = true;
        if (f.Key == null) f.Key = Fs.ResolveExisting(f.Name) ?? Fs.Key(f.Name);
        Cpu.AX = n; Ok();
    }

    private void Seek()
    {
        int h = Cpu.BX;
        if (!HandleOpen(h)) { Fail(6); return; }
        var f = handles[h];
        long off = (int)((Cpu.CX << 16) | Cpu.DX);
        long p = Cpu.AL == 0 ? off : Cpu.AL == 1 ? f.Pos + off : f.Length + off;
        if (p < 0) { Fail(0x19); return; }
        f.Pos = (int)Math.Min(p, int.MaxValue / 2);
        Cpu.AX = f.Pos & 0xFFFF; Cpu.DX = (f.Pos >> 16) & 0xFFFF; Ok();
    }

    private void Ioctl()
    {
        int h = Cpu.BX;
        switch (Cpu.AL)
        {
            case 0x00:
                if (!HandleOpen(h)) { Fail(6); return; }
                {
                    var f = handles[h];
                    Cpu.DX = f.Console ? 0x80D3 : f.Nul ? 0x8084 : (f.Pos >= f.Length ? 0x0040 : 0x0000);
                    Cpu.AX = Cpu.DX; Ok(); return;
                }
            case 0x01: Ok(); return;
            case 0x06:
                if (!HandleOpen(h)) { Fail(6); return; }
                { var f = handles[h]; Cpu.AL = f.Console || f.Pos < f.Length ? 0xFF : 0; Ok(); return; }
            case 0x07: Cpu.AL = 0xFF; Ok(); return;
            case 0x08: Cpu.AX = 1; Ok(); return;
            case 0x09: case 0x0A: Cpu.DX = 0; Ok(); return;
            case 0x0E: Cpu.AL = 0; Ok(); return;
        }
        Fail(1);
    }

    private void FindFirst()
    {
        string pat = ReadAsciiz(Cpu.DS, Cpu.DX);
        var list = Fs.Find(pat);
        int key = X86.Linear(dtaSeg, dtaOff);
        searches[key] = (list, 0);
        FindNext();
    }

    private void FindNext()
    {
        int key = X86.Linear(dtaSeg, dtaOff);
        if (!searches.TryGetValue(key, out var s) || s.next >= s.list.Count) { Fail(0x12); return; }
        var (name, size) = s.list[s.next];
        searches[key] = (s.list, s.next + 1);
        Poke(dtaSeg, dtaOff + 0x15, 0x20);
        Poke16(dtaSeg, dtaOff + 0x16, 0);
        Poke16(dtaSeg, dtaOff + 0x18, (15 << 9) | (1 << 5) | 1);
        Poke16(dtaSeg, dtaOff + 0x1A, size & 0xFFFF); Poke16(dtaSeg, dtaOff + 0x1C, size >> 16);
        var b = KoreanText.ShiftJis.GetBytes(name);
        for (int i = 0; i < 13; i++) Poke(dtaSeg, dtaOff + 0x1E + i, i < b.Length ? b[i] : 0);
        Ok();
    }

    /// <summary>29h: DS:SI의 파일 이름 → ES:DI FCB (드라이브 · 8 · 3, 무늬 '*' → '?').</summary>
    private void ParseFcbName()
    {
        int si = Cpu.SI;
        if ((Cpu.AL & 1) != 0) while (Peek(Cpu.DS, si) == ' ' || Peek(Cpu.DS, si) == '\t') si++;
        int drive = 0;
        if (Peek(Cpu.DS, si + 1) == ':') { drive = (Peek(Cpu.DS, si) | 0x20) - 'a' + 1; si += 2; }
        var name = new char[11]; for (int i = 0; i < 11; i++) name[i] = ' ';
        bool wild = false;
        int p = 0;
        bool ext = false;
        while (true)
        {
            int c = Peek(Cpu.DS, si);
            if (c == '.' && !ext) { p = 8; ext = true; si++; continue; }
            if (c <= ' ' || "/\\:;,=+<>|\"[]".IndexOf((char)c) >= 0) break;
            int end = ext ? 11 : 8;
            if (c == '*') { while (p < end) name[p++] = '?'; wild = true; si++; continue; }
            if (c == '?') wild = true;
            if (p < end) name[p++] = char.ToUpperInvariant((char)c);
            si++;
        }
        Poke(Cpu.ES, Cpu.DI, drive);
        for (int i = 0; i < 11; i++) Poke(Cpu.ES, Cpu.DI + 1 + i, name[i]);
        Cpu.SI = si;
        Cpu.AL = wild ? 1 : 0;
    }

    /// <summary>4Bh: AL 0 = 읽고 실행, 3 = 덮어 읽기 (오버레이).</summary>
    private void Exec()
    {
        string name = ReadAsciiz(Cpu.DS, Cpu.DX);
        string key = Fs.ResolveExisting(name) ?? ResolveProgram(name);
        if (key == null) { Fail(2); return; }
        int pb = Cpu.BX, pbSeg = Cpu.ES;
        if (Cpu.AL == 3)
        {
            Fs.TryGet(key, out var img);
            int load = Peek16(pbSeg, pb), reloc = Peek16(pbSeg, pb + 2);
            if (img.Length >= 2 && img[0] == 'M' && img[1] == 'Z')
            {
                int hdr = U16(img, 8) * 16, cp = U16(img, 4), cblp = U16(img, 2);
                int len = Math.Max(0, Math.Min(cp * 512 - (cblp != 0 ? 512 - cblp : 0) - hdr, img.Length - hdr));   // 머리말의 그림 크기 (뒤에 붙은 자료는 빼고)
                WriteBytes(load, 0, img, hdr, len);
                int nrel = U16(img, 6), relAt = U16(img, 0x18);
                for (int i = 0; i < nrel; i++) { int o = U16(img, relAt + i * 4), s = U16(img, relAt + i * 4 + 2); Poke16(load + s, o, Peek16(load + s, o) + reloc); }
            }
            else WriteBytes(load, 0, img, 0, img.Length);
            Ok(); return;
        }
        if (Cpu.AL != 0) { Fail(1); return; }
        int tailOff = Peek16(pbSeg, pb + 2), tailSeg = Peek16(pbSeg, pb + 4);
        int tlen = Math.Min(126, Peek(tailSeg, tailOff));
        var tb = new byte[tlen]; for (int i = 0; i < tlen; i++) tb[i] = (byte)Peek(tailSeg, tailOff + 1 + i);
        string tail = KoreanText.ShiftJis.GetString(tb);
        frames.Push(new Frame { ParentPsp = currentPsp, Ss = Cpu.SS, Sp = Cpu.SP });
        int ss = Cpu.SS, sp = Cpu.SP;
        int psp = Load(key, tail, currentPsp);
        if (psp < 0) { frames.Pop(); Cpu.SS = ss; Cpu.SP = sp; Cpu.CS = StubSeg; Cpu.IP = 0x21 * 4 + 3; Fail(-psp); return; }
    }
}
