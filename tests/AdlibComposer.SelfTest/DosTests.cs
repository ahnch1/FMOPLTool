using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Audio.Dos;

namespace AdlibComposer.SelfTest;

/// <summary>MS-DOS 흉내: 손으로 짠 COM · EXE · SYS로 출력 · 파일 · 메모리 · 상주 · 벡터 가로채기 · 실행 · 찾기, 실제 PMD 상주.</summary>
public static class DosTests
{
    private static byte[] B(params int[] b) { return b.Select(x => (byte)x).ToArray(); }

    /// <summary>COM 코드 (org 100h): 바이트 배열 + 오프셋(100h 기준)에 넣을 덩어리.</summary>
    private static byte[] Com(int[] code, params (int at, byte[] data)[] extra)
    {
        int len = code.Length;
        foreach (var (at, d) in extra) len = Math.Max(len, at - 0x100 + d.Length);
        var b = new byte[len];
        for (int i = 0; i < code.Length; i++) b[i] = (byte)code[i];
        foreach (var (at, d) in extra) Array.Copy(d, 0, b, at - 0x100, d.Length);
        return b;
    }

    private static byte[] Z(string s) { return Encoding.ASCII.GetBytes(s + "\0"); }

    public static void Run(Action<string, bool, string> check)
    {
        FileSystem(check);
        Programs(check);
        Memory(check);
        Resident(check);
        ExeAndExec(check);
        Device(check);
        Real(check);
    }

    private static void FileSystem(Action<string, bool, string> check)
    {
        var fs = new DosFileSystem();
        fs.Add("mdat/Song1.mmm", new byte[3]); fs.Add("MDAT\\SONG2.MMM", new byte[5]); fs.Add("PMD.COM", new byte[1]); fs.Add("README", new byte[1]);
        bool ok = fs.Exists("a:\\mdat\\song1.MMM") && fs.Exists("MDAT/./SONG2.MMM") && fs.Exists("mdat\\..\\pmd.com") && !fs.Exists("SONG1.MMM");
        var found = fs.Find("MDAT\\*.MMM").Select(f => f.name).ToList();
        bool wild = DosFileSystem.Match83("*.*", "README") && DosFileSystem.Match83("S?NG*.M*", "SONG2.MMM") && !DosFileSystem.Match83("*.COM", "PMD.EXE");
        fs.CurrentDir = "MDAT";
        bool rel = fs.Exists("SONG1.MMM") && fs.Exists("PMD.COM");   // 현재 폴더 → 뿌리
        check("DOS file system: case/slash/drive/.. normalize, 8.3 wildcards, current dir", ok && found.SequenceEqual(new[] { "SONG1.MMM", "SONG2.MMM" }) && wild && rel, string.Join(",", found));
    }

    private static void Programs(Action<string, bool, string> check)
    {
        var fs = new DosFileSystem();
        fs.Add("HELLO.COM", Com(new[] { 0xB4, 0x09, 0xBA, 0x0C, 0x01, 0xCD, 0x21, 0xB8, 0x03, 0x4C, 0xCD, 0x21 }, (0x10C, Encoding.ASCII.GetBytes("Hi$"))));
        // 핸들 h에서 100바이트 읽어 표준 출력으로
        byte[] Echo(int h) => Com(new[] { 0xB4, 0x3F, 0xBB, h, 0x00, 0xB9, 0x64, 0x00, 0xBA, 0x20, 0x01, 0xCD, 0x21, 0x89, 0xC1, 0xB4, 0x40, 0xBB, 0x01, 0x00, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21 });
        fs.Add("ECHO5.COM", Echo(5)); fs.Add("ECHO0.COM", Echo(0));
        fs.Add("DATA.TXT", Encoding.ASCII.GetBytes("abc"));
        fs.Add("SONG.M", Encoding.ASCII.GetBytes("song!"));
        // 이름으로 열고 끝으로 옮겨 크기 = 끝 코드 (없으면 FF)
        fs.Add("SIZE.COM", Com(new[] { 0xB8, 0x00, 0x3D, 0xBA, 0x20, 0x01, 0xCD, 0x21, 0x72, 0x10, 0x89, 0xC3, 0xB8, 0x02, 0x42, 0x31, 0xC9, 0x31, 0xD2, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21, 0x90, 0xB8, 0xFF, 0x4C, 0xCD, 0x21 }, (0x120, Z("mdat/x.bin"))));
        fs.Add("MDAT\\X.BIN", new byte[0x2A]);
        // 찾기: *.MMM 개수
        fs.Add("COUNT.COM", Com(new[] { 0xB4, 0x4E, 0xB9, 0x00, 0x00, 0xBA, 0x30, 0x01, 0x31, 0xF6, 0xCD, 0x21, 0x72, 0x07, 0x46, 0xB4, 0x4F, 0xCD, 0x21, 0x73, 0xF9, 0x89, 0xF0, 0xB4, 0x4C, 0xCD, 0x21 }, (0x130, Z("*.MMM"))));
        foreach (var n in new[] { "A.MMM", "B.MMM", "C.MMM" }) fs.Add(n, new byte[1]);

        var dos = new DosMachine(fs);
        var r = dos.Shell("hello");
        check("DOS COM: AH=09h prints, AH=4Ch exit code (name without .COM)", r == DosMachine.ShellResult.Exited && dos.ConsoleText == "Hi" && dos.ReturnCode == 3, r + " '" + dos.ConsoleText + "' " + dos.ReturnCode);

        dos = new DosMachine(fs);
        dos.BindFile(5, "DATA.TXT");
        dos.Shell("ECHO5");
        check("DOS pre-bound file handle 5 (hoot rom type=file offset=5) → read → stdout", dos.ConsoleText == "abc", dos.ConsoleText);

        dos = new DosMachine(fs);
        dos.BindText(6, Encoding.ASCII.GetBytes("conin"));
        dos.BindFile(0x10, "SONG.M");
        var e0 = dos.Shell("ECHO0");
        string before = dos.ConsoleText;
        dos.Duplicate(0x10, 0);                       // hoot: 곡 번호의 핸들 → 표준 입력
        dos.Shell("ECHO0");
        string afterSong = dos.ConsoleText;
        dos.Duplicate(6, 0);
        dos.Shell("ECHO0");
        check("DOS stdin: empty at start, then handle 0x10 / conin text duplicated onto it", before == "" && afterSong == "song!" && dos.ConsoleText == "song!conin", "'" + before + "' '" + afterSong + "' '" + dos.ConsoleText + "'");

        dos = new DosMachine(fs);
        dos.Shell("SIZE");
        int sz = dos.ReturnCode;
        fs.Delete("MDAT\\X.BIN");
        dos = new DosMachine(fs);
        dos.Shell("SIZE");
        check("DOS open by name (mixed case · slash) + LSEEK end = size, missing → CF", sz == 0x2A && dos.ReturnCode == 0xFF, sz.ToString("X") + " / " + dos.ReturnCode.ToString("X"));

        dos = new DosMachine(fs);
        dos.Shell("COUNT");
        check("DOS find first / next (*.MMM → 3)", dos.ReturnCode == 3, dos.ReturnCode.ToString());

        dos = new DosMachine(fs);
        var rs = dos.Shell("SET BLASTER=A220 I5");
        dos.Shell("hello");
        int envSeg = dos.PeekWord(dos.LastLoadedPsp, 0x2C);
        var env = new StringBuilder();
        for (int i = 0; i < 512 && !(dos.PeekByte(envSeg, i) == 0 && dos.PeekByte(envSeg, i + 1) == 0); i++) env.Append(dos.PeekByte(envSeg, i) == 0 ? '|' : (char)dos.PeekByte(envSeg, i));
        check("DOS shell built-in SET → child environment block (PSP:2Ch)", rs == DosMachine.ShellResult.Exited && env.ToString().Contains("BLASTER=A220 I5") && env.ToString().Contains("COMSPEC="), env.ToString());

        dos = new DosMachine(fs);
        check("DOS missing program → NotFound", dos.Shell("NOPE /x") == DosMachine.ShellResult.NotFound, "");
    }

    private static void Memory(Action<string, bool, string> check)
    {
        var fs = new DosFileSystem();
        fs.Add("MEM.COM", Com(new[]
        {
            0xB4, 0x4A, 0xBB, 0x00, 0x10, 0xCD, 0x21,   // 4Ah: PSP 블록 → 64KB
            0xB4, 0x48, 0xBB, 0x00, 0x20, 0xCD, 0x21,   // 48h: 2000h 문단
            0x89, 0xC6,                                 // SI = 세그먼트
            0xB4, 0x48, 0xBB, 0xFF, 0xFF, 0xCD, 0x21,   // 48h FFFF → 실패, BX = 가장 큰 것
            0x89, 0xDF,                                 // DI = 가장 큰 것
            0x8E, 0xC6, 0xB4, 0x49, 0xCD, 0x21,         // 49h: SI 놓기
            0xB4, 0x48, 0xBB, 0xFF, 0xFF, 0xCD, 0x21,   // 다시 가장 큰 것
            0x29, 0xFB,                                 // BX −= DI
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        }));
        var dos = new DosMachine(fs);
        dos.Shell("MEM");
        int psp = dos.LastLoadedPsp;
        check("DOS memory: 4Ah shrink, 48h alloc above PSP, 48h FFFF fails with largest, 49h free merges back (+2001h)",
            dos.Cpu.SI == psp + 0x1001 && dos.Cpu.BX == 0x2001 && dos.Cpu.DI > 0x5000,
            "PSP " + psp.ToString("X4") + " SI " + dos.Cpu.SI.ToString("X4") + " largest " + dos.Cpu.DI.ToString("X4") + " diff " + dos.Cpu.BX.ToString("X4"));
        // 끝나면 메모리를 모두 돌려받는다: 같은 프로그램을 다시 돌려도 같은 자리
        dos.Shell("MEM");
        check("DOS memory freed at exit (same PSP on rerun)", dos.LastLoadedPsp == psp, dos.LastLoadedPsp.ToString("X4"));
    }

    private static void Resident(Action<string, bool, string> check)
    {
        var fs = new DosFileSystem();
        // INT 60h 처리기 (AL = 42h)를 걸고 31h로 상주
        fs.Add("TSR.COM", Com(new[] { 0xB8, 0x60, 0x25, 0xBA, 0x10, 0x01, 0xCD, 0x21, 0xBA, 0x20, 0x00, 0xB8, 0x00, 0x31, 0xCD, 0x21, 0xB0, 0x42, 0xCF }));
        fs.Add("USE.COM", Com(new[] { 0xCD, 0x60, 0xB4, 0x4C, 0xCD, 0x21 }));
        // INT 21h를 가로채 세고 옛 처리기로 (JMP FAR)
        fs.Add("HOOK.COM", Com(new[]
        {
            0xB8, 0x21, 0x35, 0xCD, 0x21, 0x89, 0x1E, 0x30, 0x01, 0x8C, 0x06, 0x32, 0x01,
            0xB8, 0x21, 0x25, 0xBA, 0x20, 0x01, 0xCD, 0x21,
            0xBA, 0x20, 0x00, 0xB8, 0x00, 0x31, 0xCD, 0x21, 0x90, 0x90, 0x90,
            0x2E, 0xFF, 0x06, 0x34, 0x01,     // 120: INC WORD CS:[134]
            0x2E, 0xFF, 0x2E, 0x30, 0x01,     // JMP FAR CS:[130]
        }));
        fs.Add("VER.COM", Com(new[] { 0xB4, 0x30, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21 }));
        var dos = new DosMachine(fs);
        var r1 = dos.Shell("TSR");
        int tsr = dos.LastLoadedPsp;
        dos.Shell("USE");
        check("DOS TSR (31h): handler stays resident, INT 60h from a later program", r1 == DosMachine.ShellResult.Resident && dos.ReturnCode == 0x42, r1 + " " + dos.ReturnCode.ToString("X"));
        dos.Shell("USE");
        check("DOS later programs load above the resident block", dos.LastLoadedPsp > tsr + 0x10, tsr.ToString("X4") + " < " + dos.LastLoadedPsp.ToString("X4"));

        dos = new DosMachine(fs);
        dos.Shell("HOOK");
        int hook = dos.LastLoadedPsp;
        dos.Shell("VER");
        int count = dos.PeekWord(hook, 0x134);
        check("DOS INT 21h hooked by a TSR still reaches DOS through the default handler (version 3.30; 31h + 30h + 4Ch counted)", dos.ReturnCode == 3 && count == 3, "code " + dos.ReturnCode + " count " + count);
    }

    private static void ExeAndExec(Action<string, bool, string> check)
    {
        var fs = new DosFileSystem();
        // 코드 = 문단 0, 글자 = 문단 2 (재배치로 DS), 스택 = 문단 3
        var exe = new byte[0x50];
        var hdr = B('M', 'Z', 0x50, 0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x10, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
        Array.Copy(hdr, exe, hdr.Length);
        var code = B(0xB8, 0x02, 0x00, 0x8E, 0xD8, 0xB4, 0x09, 0xBA, 0x00, 0x00, 0xCD, 0x21, 0xB8, 0x05, 0x4C, 0xCD, 0x21);
        Array.Copy(code, 0, exe, 0x20, code.Length);
        Array.Copy(Encoding.ASCII.GetBytes("EXE$"), 0, exe, 0x40, 4);
        fs.Add("PROG.EXE", exe);
        var dos = new DosMachine(fs);
        var r = dos.Shell("PROG");
        check("DOS EXE (MZ): relocation, SS:SP, CS:IP", r == DosMachine.ShellResult.Exited && dos.ConsoleText == "EXE" && dos.ReturnCode == 5, r + " '" + dos.ConsoleText + "' " + dos.ReturnCode);

        fs.Add("PARENT.COM", Com(new[]
        {
            0xB4, 0x4A, 0xBB, 0x00, 0x10, 0xCD, 0x21,
            0x8C, 0x0E, 0x54, 0x01,             // [154] = CS (꼬리 세그먼트)
            0xB8, 0x00, 0x4B, 0xBA, 0x40, 0x01, 0xBB, 0x50, 0x01, 0xCD, 0x21,
            0x72, 0x0A,
            0xB4, 0x4D, 0xCD, 0x21, 0xB4, 0x4C, 0xCD, 0x21, 0x90, 0x90,
            0xB8, 0xEE, 0x4C, 0xCD, 0x21,
        }, (0x140, Z("CHILD.COM")), (0x150, B(0, 0, 0x60, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), (0x160, B(2, ' ', 'X', 0x0D))));
        fs.Add("CHILD.COM", Com(new[] { 0xA0, 0x82, 0x00, 0x88, 0xC2, 0xB4, 0x02, 0xCD, 0x21, 0xB8, 0x07, 0x4C, 0xCD, 0x21 }));
        dos = new DosMachine(fs);
        r = dos.Shell("PARENT");
        check("DOS 4Bh exec child with command tail, 4Dh return code back in parent", r == DosMachine.ShellResult.Exited && dos.ConsoleText == "X" && dos.ReturnCode == 7, r + " '" + dos.ConsoleText + "' " + dos.ReturnCode.ToString("X"));
    }

    private static void Device(Action<string, bool, string> check)
    {
        var sys = new byte[0x40];
        var b = B(0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x80, 0x12, 0x00, 0x1D, 0x00, 'T', 'E', 'S', 'T', 'D', 'E', 'V', ' ',
            0x2E, 0x89, 0x1E, 0x38, 0x00, 0x2E, 0x8C, 0x06, 0x3A, 0x00, 0xCB,                // 12: 전략
            0x2E, 0xC4, 0x1E, 0x38, 0x00,                                                    // 1D: 인터럽트: LES BX,CS:[38]
            0x26, 0xC7, 0x47, 0x0E, 0x40, 0x00,                                              //     끝 오프셋 40h
            0x26, 0x8C, 0x4F, 0x10,                                                          //     끝 세그먼트 = CS
            0x26, 0xC7, 0x47, 0x03, 0x00, 0x01,                                              //     상태: 끝남
            0xCB);
        Array.Copy(b, sys, b.Length);
        var fs = new DosFileSystem();
        fs.Add("TEST.SYS", sys);
        var dos = new DosMachine(fs);
        int seg = dos.LoadDevice("TEST.SYS /x");
        fs.Add("HELLO.COM", Com(new[] { 0xB8, 0x00, 0x4C, 0xCD, 0x21 }));
        dos.Shell("HELLO");
        int psp = dos.LastLoadedPsp;
        bool kept = seg > 0 && dos.PeekWord(seg - 1, 3) == 4 && dos.PeekWord(seg - 1, 1) == 8 && dos.PeekByte(seg, 0x0A) == 'T';
        check("DOS device= driver: strategy + interrupt (init), keeps 4 paragraphs up to its break address, programs load above", kept && psp > seg + 4,
            "seg " + seg.ToString("X4") + " MCB size " + (seg > 0 ? dos.PeekWord(seg - 1, 3).ToString("X") : "-") + " next PSP " + psp.ToString("X4"));

        // MS-DOS처럼: 전략 루틴이 DS = 드라이버 세그먼트로 불림 (CS: 없이 [3Ch]에 적음), 명령 줄의 파일 이름 뒤는 NUL (아니면 끝 주소를 안 줌)
        sys = new byte[0x40];
        b = B(0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x80, 0x12, 0x00, 0x1B, 0x00, 'T', 'E', 'S', 'T', 'D', 'E', 'V', '2',
            0x89, 0x1E, 0x3C, 0x00, 0x8C, 0x06, 0x3E, 0x00, 0xCB,                            // 12: 전략 (DS 기준)
            0x2E, 0xC4, 0x1E, 0x3C, 0x00,                                                    // 1B: LES BX,CS:[3C]
            0x26, 0xC5, 0x77, 0x12,                                                          //     LDS SI,ES:[BX+12] (명령 줄)
            0x80, 0x7C, 0x09, 0x00, 0x75, 0x06,                                              //     CMP [SI+9],0 / JNE (끝 주소 없음)
            0x26, 0xC7, 0x47, 0x0E, 0x40, 0x00,                                              //     끝 오프셋 40h
            0x26, 0x8C, 0x4F, 0x10,                                                          //     끝 세그먼트 = CS
            0x26, 0xC7, 0x47, 0x03, 0x00, 0x01,                                              //     상태: 끝남
            0xCB);
        Array.Copy(b, sys, b.Length);
        fs.Add("TEST2.SYS", sys);
        dos = new DosMachine(fs);
        seg = dos.LoadDevice("TEST2.SYS 8192");
        check("DOS device= driver: strategy called with DS = driver, file name NUL-terminated (MS-DOS SYSINIT)", seg > 0 && dos.PeekWord(seg - 1, 3) == 4, dos.LastDeviceResult);

        // hoot 곡 요청: 묶어 둔 파일 핸들을 프로그램이 닫았어도 다시 열어 0번으로 (FUGA OPNDRV는 상주 전에 핸들 0~31을 닫는다)
        fs.Add("SONG.DAT", B(1, 2, 3));
        fs.Add("CLOSE.COM", Com(new[] { 0xB4, 0x3E, 0xBB, 0x10, 0x00, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21 }));
        dos = new DosMachine(fs);
        dos.BindFile(0x10, "SONG.DAT");
        dos.Shell("CLOSE");
        bool closed = !dos.HandleOpen(0x10);
        check("DOS hoot song handle: reopened after the program closed it, duplicated to handle 0", closed && dos.SelectSongHandle(0x10) && dos.HandleOpen(0), "closed " + closed);

        // 64KB를 넘는 EXE 그림: 오프셋이 감기지 않고 이어서 올라감 (그림 10010h의 'Z'를 읽어 찍음)
        var big = new byte[0x10040];
        Array.Copy(B('M', 'Z', 0x40, 0x00, 0x81, 0x00, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00, 0xFF, 0xFF, 0x00, 0x11, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00), big, 28);
        var prog = B(0x8C, 0xC8, 0x05, 0x01, 0x10, 0x8E, 0xD8, 0x8A, 0x16, 0x00, 0x00, 0xB4, 0x02, 0xCD, 0x21, 0xB8, 0x00, 0x4C, 0xCD, 0x21);
        Array.Copy(prog, 0, big, 0x20, prog.Length);
        big[0x20 + 0x10010] = (byte)'Z';
        fs.Add("BIG.EXE", big);
        dos = new DosMachine(fs);
        var rb = dos.Shell("BIG");
        check("DOS EXE image over 64 KB loads linearly (no offset wrap)", rb == DosMachine.ShellResult.Exited && dos.ConsoleText == "Z", rb + " '" + dos.ConsoleText + "'");
    }

    // ------------------------------------------------------------ 실제: PMD 상주 + hoot 드라이버

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string zip = dir == null ? null : Path.Combine(dir, "Hootarchive", "pc98", "yumenosi_98.zip");
        if (zip == null || !File.Exists(zip)) { Console.WriteLine("SKIP  real PMD (_incoming/Hootarchive/pc98/yumenosi_98.zip not found)"); return; }
        var fs = new DosFileSystem();
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries)
                using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); fs.Add(e.FullName, ms.ToArray()); }
        var dos = new DosMachine(fs);
        var r = dos.Shell("PMD # /m8 /v0 /e2 /k /p");
        string con = dos.ConsoleText;
        int vec60 = dos.PeekWord(0, 0x60 * 4 + 2);
        var r2 = dos.Shell("pmd_98", 5_000_000);
        check("Real PMD 4.8e under DOS: banner, stays resident on INT 60h; hoot shim PMD_98 then runs its idle loop",
            r == DosMachine.ShellResult.Resident && con.Contains("Version 4.8") && con.Contains("常駐") && vec60 != DosMachine.StubSeg && r2 == DosMachine.ShellResult.Running && dos.Cpu.UnknownOps == 0 && dos.UnknownFunctions.Count == 0,
            r + " / " + r2 + ", INT60 seg " + vec60.ToString("X4") + ", calls " + dos.DosCalls + ", unknown fn " + string.Join(",", dos.UnknownFunctions.Keys.Select(k => k.ToString("X2"))));
    }
}
