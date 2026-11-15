using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Dos;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>PC-98 기계: PIT · PIC · OPN 타이머 · VSYNC 인터럽트, hoot 인터럽트 (7E0h · 7E2h · 표준 입력 이중화), 실제 PMD · PMDB2 연주.</summary>
public static class Pc98Tests
{
    private static byte[] Com(int[] code, params (int at, byte[] data)[] extra)
    {
        int len = code.Length;
        foreach (var (at, d) in extra) len = Math.Max(len, at - 0x100 + d.Length);
        var b = new byte[len];
        for (int i = 0; i < code.Length; i++) b[i] = (byte)code[i];
        foreach (var (at, d) in extra) Array.Copy(d, 0, b, at - 0x100, d.Length);
        return b;
    }

    private static byte[] Code(params int[] b) { return b.Select(x => (byte)x).ToArray(); }

    /// <summary>프로그램을 돌려 1초 뒤 PSP:160h의 셈 값.</summary>
    private static (int count, Pc98Machine m) CountFor(byte[] com, Pc98Machine.Board board = Pc98Machine.Board.Opn, double sec = 1)
    {
        var fs = new DosFileSystem();
        fs.Add("T.COM", com);
        var m = new Pc98Machine(fs, board, 8);
        var r = m.Shell("T", 1000);
        long start = m.Cpu.Cycles;
        m.Run((long)(m.CpuHz * sec));
        return (m.PeekWord(m.LastLoadedPsp, 0x160), m);
    }

    // 처리기 (140h): INC WORD CS:[160]; PUSH AX; MOV AL,20h; OUT 00h,AL (EOI); POP AX; IRET
    private static readonly byte[] CountEoi = Code(0x2E, 0xFF, 0x06, 0x60, 0x01, 0x50, 0xB0, 0x20, 0xE6, 0x00, 0x58, 0xCF);

    public static void Run(Action<string, bool, string> check)
    {
        // PIT 0번 방식 3, 24576 → 2.4576MHz / 24576 = 100Hz → IRQ0 (INT 08h)
        var (n, m) = CountFor(Com(new[]
        {
            0xFA, 0xB8, 0x08, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21,
            0xB0, 0x36, 0xE6, 0x77, 0xB8, 0x00, 0x60, 0xE6, 0x71, 0x88, 0xE0, 0xE6, 0x71,
            0xE4, 0x02, 0x24, 0xFE, 0xE6, 0x02,
            0xFB, 0xF4, 0xEB, 0xFD,
        }, (0x140, CountEoi)));
        check("PC-98 PIT counter 0 (2.4576 MHz / 24576) → IRQ0 → INT 08h, 100 per second (HLT waits)", Math.Abs(n - 100) <= 1, n + " / s, CPU " + (m.CpuHz / 1e6).ToString("0.00") + " MHz");

        // OPN 타이머 B = 200 → 1152 × 56 / 3.9936MHz = 16.15ms → INT5 (IRQ12 = 종 IR4, INT 14h), 처리기에서 깃발 지움 · 종 · 주 EOI
        (n, m) = CountFor(Com(new[]
        {
            0xFA, 0xB8, 0x14, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21,
            0xBA, 0x88, 0x01, 0xB0, 0x26, 0xEE, 0xBA, 0x8A, 0x01, 0xB0, 0xC8, 0xEE,
            0xBA, 0x88, 0x01, 0xB0, 0x27, 0xEE, 0xBA, 0x8A, 0x01, 0xB0, 0x2A, 0xEE,
            0xE4, 0x0A, 0x24, 0xEF, 0xE6, 0x0A,
            0xFB, 0xF4, 0xEB, 0xFD,
        }, (0x140, Code(0x2E, 0xFF, 0x06, 0x60, 0x01, 0x50, 0x52, 0xBA, 0x88, 0x01, 0xB0, 0x27, 0xEE, 0xBA, 0x8A, 0x01, 0xB0, 0x2A, 0xEE, 0xB0, 0x20, 0xE6, 0x08, 0xE6, 0x00, 0x5A, 0x58, 0xCF))));
        check("PC-98 OPN timer B (NB=200) → status flag → INT5 (IRQ12, slave) ≈ 61.9 per second", Math.Abs(n - 61.9) <= 1.5, n + " / s");
        check("PC-98 board INT jumper read from SSG port A (reg 0Eh bits 6-7 = 11 → INT5)", (ReadReg(m, 0x0E) & 0xC0) == 0xC0, ReadReg(m, 0x0E).ToString("X2"));
        // 26K 보드에는 OPNA가 없다: 18Ch 읽기 = FF, YM2608 ID 없음
        check("PC-98 26K board: no OPNA extension port, OPN ID register = 0", m.In(0x18C) == 0xFF && ReadReg(m, 0xFF) == 0, m.In(0x18C).ToString("X2"));
        var (n2, m2) = CountFor(Com(new[] { 0xFA, 0xF4 }), Pc98Machine.Board.Opna, 0.01);
        check("PC-98 86 board: OPNA ID register 0xFF = 1, A460h ID = 0x4x", ReadReg(m2, 0xFF) == 1 && (m2.In(0xA460) & 0xF0) == 0x40, m2.In(0xA460).ToString("X2"));

        // VSYNC: INT 0Ah, 64h에 써서 다시 받음 → 56.4Hz
        (n, m) = CountFor(Com(new[]
        {
            0xFA, 0xB8, 0x0A, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21,
            0xE4, 0x02, 0x24, 0xFB, 0xE6, 0x02, 0xE6, 0x64,
            0xFB, 0xF4, 0xEB, 0xFD,
        }, (0x140, Code(0x2E, 0xFF, 0x06, 0x60, 0x01, 0x50, 0xE6, 0x64, 0xB0, 0x20, 0xE6, 0x00, 0x58, 0xCF))));
        check("PC-98 VSYNC → IRQ2 (INT 0Ah) re-armed by OUT 64h, ≈ 56.4 per second", Math.Abs(n - 56.4) <= 1.5, n + " / s");
        int on = 0, total = 2000;
        for (int i = 0; i < total; i++) { m.Cpu.Cycles = (long)(i * m.CpuHz / 56.4229 / 100); if ((m.In(0xA0) & 0x20) != 0) on++; }
        check("PC-98 GDC status (A0h) bit5 = vertical retrace part of the time", on > 0 && on < total / 4, on + "/" + total);

        // hoot 인터럽트: 7F 벡터, 7E8h ← 81h. 처리기가 요인 · 코드 · 표준 입력을 저장
        var fs = new DosFileSystem();
        fs.Add("H.COM", Com(new[]
        {
            0xB8, 0x7F, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21,
            0xBA, 0xE8, 0x07, 0xB0, 0x81, 0xEE,
            0xFB, 0xF4, 0xEB, 0xFD,
        }, (0x140, Code(0x60, 0x1E, 0x0E, 0x1F,
            0xBA, 0xE0, 0x07, 0xEC, 0xA2, 0x80, 0x01,
            0xBA, 0xE2, 0x07, 0xED, 0xA3, 0x82, 0x01,
            0xB4, 0x3F, 0x31, 0xDB, 0xB9, 0x10, 0x00, 0xBA, 0x90, 0x01, 0xCD, 0x21,
            0x1F, 0x61, 0xCF))));
        fs.Add("SONG.M", Encoding.ASCII.GetBytes("hello"));
        var h = new Pc98Machine(fs, Pc98Machine.Board.Opn, 8);
        h.BindFile(0x10, "SONG.M");
        var hr = h.Shell("H", 100000);
        int psp = h.LastLoadedPsp;
        h.Play(0x10);
        h.Run(100000);
        string got = new string(Enumerable.Range(0, 5).Select(i => (char)h.PeekByte(psp, 0x190 + i)).ToArray());
        bool playOk = h.PeekByte(psp, 0x180) == 0 && h.PeekWord(psp, 0x182) == 0x10 && got == "hello";
        h.Stop();
        h.Run(100000);
        check("PC-98 hoot interrupt: funcvect 7Fh, 7E0h cause 0 / 2, 7E2h code, song handle duplicated onto stdin",
            hr == DosMachine.ShellResult.Running && h.HootInterruptEnabled && playOk && h.PeekByte(psp, 0x180) == 2, hr + " '" + got + "' cause " + h.PeekByte(psp, 0x180));

        // 마우스 인터페이스 타이머: BFDBh = 0 (120Hz), 7FDFh ← 08h (포트 C bit4 = 0: 인터럽트 켬) → IRQ13 (종 IR5, INT 15h)
        (n, m) = CountFor(Com(new[]
        {
            0xFA, 0xB8, 0x15, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21,
            0xBA, 0xDB, 0xBF, 0xB0, 0x00, 0xEE,
            0xBA, 0xDF, 0x7F, 0xB0, 0x08, 0xEE,
            0xE4, 0x0A, 0x24, 0xDF, 0xE6, 0x0A,
            0xFB, 0xF4, 0xEB, 0xFD,
        }, (0x140, Code(0x2E, 0xFF, 0x06, 0x60, 0x01, 0x50, 0xB0, 0x20, 0xE6, 0x08, 0xE6, 0x00, 0x58, 0xCF))));
        check("PC-98 mouse interface timer (BFDBh 120 Hz, 7FDFh bit4 enable) → IRQ13 (INT 15h) ≈ 120 per second", Math.Abs(n - 120) <= 2, n + " / s");
        check("PC-98 port 42h bit5 = 0 (5 / 10 MHz series: PIT 2.4576 MHz)", (m.In(0x42) & 0x20) == 0, m.In(0x42).ToString("X2"));
        var (n3, m3) = CountFor(Com(new[] { 0xFB, 0xF4 }), Pc98Machine.Board.Opna, 0.01);
        check("PC-98 OPNA extended status (18Ch): ADPCM BRDY always ready", (m3.In(0x18C) & 0x08) != 0, m3.In(0x18C).ToString("X2"));

        // 셸 프로그램이 끝나도 (재생 중) 멈추지 않는다: AutoIdle = 쉬는 고리에서 계속
        var fsx = new DosFileSystem();
        fsx.Add("X.COM", Com(new[] { 0xB8, 0x00, 0x4C, 0xCD, 0x21 }));
        var mx = new Pc98Machine(fsx, Pc98Machine.Board.Opn, 8);
        var rx = mx.Shell("X", 1000000);
        mx.AutoIdle = true;
        long c0 = mx.Cpu.Cycles;
        mx.Run(2_000_000);
        check("PC-98 AutoIdle: a program exiting during playback drops to the idle loop (machine keeps running)",
            rx == DosMachine.ShellResult.Exited && !mx.HostReturned && mx.Cpu.Cycles - c0 >= 2_000_000 && mx.Cpu.GetFlag(AdlibComposer.Audio.Cpu.X86.IF), rx + ", +" + (mx.Cpu.Cycles - c0) + " cycles");

        // zip 안 Shift_JIS 이름 (UTF-8 깃발 없음) → DOS 이름 그대로
        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_sjis_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
        try
        {
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create, KoreanText.ShiftJis))
            using (var s = z.CreateEntry("曲データ/殺し.MUS").Open()) s.Write(new byte[] { 1, 2, 3 }, 0, 3);
            var fz = DosFileSystem.FromZip(tmp);
            check("DOS file system from zip: Shift_JIS entry names (no UTF-8 flag) keep their Japanese names", fz.TryGet("曲データ\\殺し.MUS", out var d3) && d3.Length == 3, string.Join(", ", fz.Names));
        }
        finally { try { File.Delete(tmp); } catch (Exception) { } }

        Real(check);
    }

    private static int ReadReg(Pc98Machine m, int reg) { m.Out(0x188, (byte)reg); return m.In(0x18A); }

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string zip = dir == null ? null : Path.Combine(dir, "Hootarchive", "pc98", "yumenosi_98.zip");
        if (zip == null || !File.Exists(zip)) { Console.WriteLine("SKIP  real PC-98 PMD (yumenosi_98.zip not found)"); return; }
        foreach (bool opna in new[] { false, true })
        {
            var fs = new DosFileSystem();
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries) using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); fs.Add(e.FullName, ms.ToArray()); }
            var m = new Pc98Machine(fs, opna ? Pc98Machine.Board.Opna : Pc98Machine.Board.Opn, 8) { FuncVector = 0x7E };
            m.BindFile(0x14, "ON_1.MMM");
            var r1 = m.Shell(opna ? "PMDB2 # /m8 /v0 /e2 /k /p" : "PMD # /m8 /v0 /e2 /k /p");
            var r2 = m.Shell("pmd_98", 20_000_000);
            long t0 = m.Cpu.Cycles; int log0 = m.Log.Count;
            m.Play(0x14);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            m.Run((long)(m.CpuHz * 8));
            double speed = 8 / sw.Elapsed.TotalSeconds;
            int keyons = m.Log.Skip(log0).Count(e => e.kind == 0 && e.reg == 0x28 && (e.val & 0xF0) != 0);
            var v = VgmFormat.Read(m.ToVgm(t0, 8, "Daydream"));
            var p = new VgmPlayer(); p.Start(v, 0, null);
            double sum = 0; int n = 8 * 44100;
            for (int i = 0; i < n; i++) { p.Step(); p.Generate(out double l, out double rr); sum += l * l; }
            double rms = Math.Sqrt(sum / n);
            string con = m.ConsoleText;
            check("Real PC-98: " + (opna ? "PMDB2 on 86 (YM2608)" : "PMD on 26K (YM2203)") + " + hoot PMD_98 plays song 0x14 (key-ons, audible VGM)",
                r1 == DosMachine.ShellResult.Resident && r2 == DosMachine.ShellResult.Running && con.Contains("Int 5") && keyons > 20 && rms > 300 && v.ChipSummary == (opna ? "YM2608" : "YM2203") && m.Cpu.UnknownOps == 0,
                keyons + " key-ons / 8 s, rms " + rms.ToString("0") + ", " + speed.ToString("0") + "x realtime");
        }
    }
}
