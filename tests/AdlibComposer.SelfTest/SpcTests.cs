using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>SPC (슈퍼 패미컴): SPC700 CPU · S-DSP · 타이머 · ID666 · 실제 파일.</summary>
public static class SpcTests
{
    private const int Rate = 44100;

    private sealed class Flat : ISpcBus
    {
        public readonly byte[] M = new byte[0x10000];
        public int Read(int a) { return M[a]; }
        public void Write(int a, int v) { M[a] = (byte)v; }
    }

    public static void Run(Action<string, bool, string> check)
    {
        Cpu(check);
        Synthetic(check);
        Real(check);
    }

    // ------------------------------------------------------------ CPU

    private static Spc700 RunProgram(Flat bus, byte[] code, int maxSteps = 2000)
    {
        // 부르는 곳 ($0600): MOV A,#$42  MOV $33,A  RET
        bus.M[0x0600] = 0xE8; bus.M[0x0601] = 0x42; bus.M[0x0602] = 0xC4; bus.M[0x0603] = 0x33; bus.M[0x0604] = 0x6F;
        code.CopyTo(bus.M, 0x0400);
        var c = new Spc700(bus) { PC = 0x0400, SP = 0xEF };
        for (int i = 0; i < maxSteps && !c.Stopped; i++) c.Step();
        return c;
    }

    private static void Cpu(Action<string, bool, string> check)
    {
        var b = new Flat();
        var c = RunProgram(b, new byte[]
        {
            0xE8, 0x7F, 0x88, 0x01, 0xC4, 0x10,       // MOV A,#$7F  ADC A,#1 → $80 (V, N)  MOV $10,A
            0x0D, 0xAE, 0xC4, 0x11,                   // PUSH PSW  POP A  MOV $11,A
            0x8D, 0x12, 0xE8, 0x34, 0xCF,             // MOV Y,#$12 MOV A,#$34 MUL YA → $03A8
            0xDA, 0x12,                               // MOVW $12,YA
            0x8D, 0x01, 0xE8, 0x00, 0xCD, 0x10, 0x9E, // YA=$0100, X=$10, DIV → A=$10, Y=0
            0xC4, 0x14, 0xCB, 0x15,                   // MOV $14,A  MOV $15,Y
            0xE8, 0x5A, 0x9F, 0xC4, 0x16,             // XCN $5A → $A5
            0x60, 0xE8, 0x19, 0x88, 0x28, 0xDF, 0xC4, 0x17,   // CLRC  19+28 BCD → DAA → $47
            0x8F, 0x34, 0x20, 0x8F, 0x12, 0x21,       // MOV $20,#$34  MOV $21,#$12 (word $1234)
            0xE8, 0x11, 0x8D, 0x11, 0x7A, 0x20, 0xDA, 0x22,   // YA=$1111 ADDW YA,$20 → $2345  MOVW $22,YA
            0x9A, 0x20, 0xDA, 0x24,                   // SUBW YA,$20 → $1111  MOVW $24,YA
            0xCD, 0x05, 0x8F, 0x00, 0x26, 0xAB, 0x26, 0x1D, 0xD0, 0xFB,   // X=5: INC $26 · DEC X · BNE → $26 = 5
            0x8F, 0x03, 0x27, 0x6E, 0x27, 0xFD,       // MOV $27,#3  DBNZ $27,-3 → 0
            0x3F, 0x00, 0x06,                         // CALL $0600
            0x02, 0x30, 0x03, 0x30, 0x03, 0x8F, 0xEE, 0x31,   // SET1 $30.0  BBS $30.0,+3 (건너뜀: MOV $31,#$EE)
            0xCD, 0x40, 0xE8, 0x99, 0xAF, 0xAF,       // X=$40 A=$99 MOV (X)+,A ×2 → $40 $41, X=$42
            0x8F, 0x40, 0x50, 0x8F, 0x00, 0x51, 0x8D, 0x01, 0xF7, 0x50, 0xC4, 0x32,   // [$50]+Y = $41 → A=$99 → $32
            0x40, 0xE8, 0x77, 0xC4, 0x05, 0x20,       // SETP  MOV $05,A ($0105)  CLRP
            0xFF,                                     // STOP
        });
        var m = b.M;
        check("SPC700: ADC overflow flags, PUSH PSW", m[0x10] == 0x80 && (m[0x11] & 0xC0) == 0xC0, m[0x10].ToString("X2") + " psw " + m[0x11].ToString("X2"));
        check("SPC700: MUL · DIV · XCN · DAA", m[0x12] == 0xA8 && m[0x13] == 0x03 && m[0x14] == 0x10 && m[0x15] == 0 && m[0x16] == 0xA5 && m[0x17] == 0x47,
            string.Join(" ", new[] { 0x12, 0x13, 0x14, 0x15, 0x16, 0x17 }.Select(i => m[i].ToString("X2"))));
        check("SPC700: ADDW · SUBW · MOVW", m[0x22] == 0x45 && m[0x23] == 0x23 && m[0x24] == 0x11 && m[0x25] == 0x11, m[0x23].ToString("X2") + m[0x22].ToString("X2") + " " + m[0x25].ToString("X2") + m[0x24].ToString("X2"));
        check("SPC700: loops (BNE · DBNZ), CALL / RET", m[0x26] == 5 && m[0x27] == 0 && m[0x33] == 0x42, m[0x26] + " " + m[0x27] + " " + m[0x33].ToString("X2"));
        check("SPC700: SET1 · BBS · (X)+ · [d]+Y · direct page P", m[0x30] == 1 && m[0x31] == 0 && m[0x40] == 0x99 && m[0x41] == 0x99 && c.X == 0x42 && m[0x32] == 0x99 && m[0x105] == 0x77 && !c.P,
            "X=" + c.X.ToString("X2") + " $105=" + m[0x105].ToString("X2") + " $30=" + m[0x30] + " $31=" + m[0x31] + " $40=" + m[0x40].ToString("X2") + " $41=" + m[0x41].ToString("X2") + " $32=" + m[0x32].ToString("X2") + " P=" + c.P);
        check("SPC700: stops cleanly, no unknown opcodes", c.Stopped && c.UnknownOps == 0, c.UnknownOps.ToString());
    }

    // ------------------------------------------------------------ 만든 SPC

    /// <summary>머리말 · 태그 · RAM(디렉터리 $0200, BRR $0300, 프로그램 $0400)으로 SPC 파일을 만든다.</summary>
    public static byte[] MakeSpc(byte[] program, string title, int seconds, int fadeMs)
    {
        var d = new byte[0x10200];
        Encoding.ASCII.GetBytes("SNES-SPC700 Sound File Data v0.30").CopyTo(d, 0);
        d[0x21] = 0x1A; d[0x22] = 0x1A; d[0x23] = 0x1A; d[0x24] = 30;
        d[0x25] = 0x00; d[0x26] = 0x04;      // PC = $0400
        d[0x2B] = 0xEF;
        Encoding.ASCII.GetBytes(title).CopyTo(d, 0x2E);
        Encoding.ASCII.GetBytes("Test Game").CopyTo(d, 0x4E);
        Encoding.ASCII.GetBytes(seconds.ToString()).CopyTo(d, 0xA9);
        Encoding.ASCII.GetBytes(fadeMs.ToString()).CopyTo(d, 0xAC);
        Encoding.ASCII.GetBytes("Tester").CopyTo(d, 0xB1);
        int ram = 0x100;
        // 디렉터리 0: 시작 $0300, 반복 $0300
        d[ram + 0x200] = 0x00; d[ram + 0x201] = 0x03; d[ram + 0x202] = 0x00; d[ram + 0x203] = 0x03;
        // BRR 한 블록: 범위 11, 필터 0, 반복 + 끝, 앞 8칸 +7 · 뒤 8칸 −8 = 16샘플 네모
        d[ram + 0x300] = 0xB3;
        for (int i = 0; i < 4; i++) { d[ram + 0x301 + i] = 0x77; d[ram + 0x305 + i] = 0x88; }
        program.CopyTo(d, ram + 0x400);
        return d;
    }

    private static byte[] DspSet(params (int reg, int val)[] w)
    {
        var l = new List<byte>();
        foreach (var (reg, val) in w) { l.AddRange(new byte[] { 0x8F, (byte)reg, 0xF2, 0x8F, (byte)val, 0xF3 }); }
        return l.ToArray();
    }

    private static byte[] ToneProgram()
    {
        var p = new List<byte>(DspSet((0x5D, 0x02), (0x00, 0x7F), (0x01, 0x7F), (0x02, 0x00), (0x03, 0x10), (0x04, 0x00), (0x05, 0x00), (0x07, 0x7F),
            (0x0C, 0x7F), (0x1C, 0x7F), (0x2C, 0x00), (0x3C, 0x00), (0x6C, 0x20), (0x4D, 0x00), (0x5C, 0x00), (0x4C, 0x01)));
        p.AddRange(new byte[] { 0x2F, 0xFE });   // BRA 제자리
        return p.ToArray();
    }

    private static double Freq(double[] a, int from, int to)
    {
        double mean = 0; for (int i = from; i < to; i++) mean += a[i]; mean /= Math.Max(1, to - from);
        int cross = 0;
        for (int i = from + 1; i < to; i++) if ((a[i - 1] - mean < 0) != (a[i] - mean < 0)) cross++;
        return cross / 2.0 / ((to - from) / (double)Rate);
    }

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static double[] Render(VgmSong v, double sec, double startMs = 0)
    {
        var p = new VgmPlayer(); p.Start(v, startMs, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
        return a;
    }

    private static void Synthetic(Action<string, bool, string> check)
    {
        var spc = MakeSpc(ToneProgram(), "Square Test", 5, 1000);
        var t = Spc.ReadTags(spc);
        check("SPC ID666 (text): title · game · artist · length · fade", t.Title == "Square Test" && t.Game == "Test Game" && t.Artist == "Tester" && t.Seconds == 5 && t.Fade == 1,
            t.Title + " / " + t.Seconds + " s / fade " + t.Fade);
        var v = Spc.Read(spc, "x");
        check("SPC → song: 5 s + 1 s fade, SPC chip, playable", v.SpcImage != null && v.IsPlayable && Math.Abs(v.LengthSeconds - 6) < 0.01 && v.FadeSeconds == 1 && v.ChipSummary.Contains("SPC700"),
            v.ChipSummary + " " + v.LengthSeconds.ToString("0.00"));
        var a = Render(v, 1);
        double f = Freq(a, 4410, Rate);
        // 16샘플 네모, 피치 $1000 (1배) → 32000 / 16 = 2000Hz
        check("SPC DSP: BRR square at pitch $1000 = 2000 Hz", Math.Abs(f - 2000) < 25 && Rms(a, 4410, Rate) > 1000, f.ToString("0.0") + " Hz, rms " + Rms(a, 4410, Rate).ToString("0"));
        var ch = VgmPlayer.ChannelsOf(v);
        var used = VgmPlayer.ScanUsed(v);
        check("SPC: 8 voice channels, V1 used, others not", ch.Count == 8 && ch[0].Label == "V1" && used[0] && !used[1], string.Join(",", ch.Select(x => x.Label)));
        // 이동: 3초 뒤부터 (SPC 칩이 그만큼 앞으로)
        var a2 = Render(v, 0.5, 3000);
        check("SPC: seek (chip runs ahead)", Rms(a2, 2205, a2.Length) > 1000 && Math.Abs(Freq(a2, 2205, a2.Length) - 2000) < 30, Rms(a2, 2205, a2.Length).ToString("0"));
        // 피치 2배 ($2000) → 4000Hz, 볼륨 0이면 무음
        var hi = ToneProgram(); hi[4 * 6 + 4] = 0x20;   // 다섯째 쓰기 (PITCH H = $03)의 값
        double f2 = Freq(Render(Spc.Read(MakeSpc(hi, "x", 2, 0), "x"), 0.5), 2205, Rate / 2);
        check("SPC DSP: pitch $2000 = 4000 Hz", Math.Abs(f2 - 4000) < 50, f2.ToString("0.0"));

        // 타이머 0: 목표 80 (= 10ms) — 틱마다 $0010을 늘리는 프로그램을 1초 돌리면 약 100
        var timer = new List<byte>
        {
            0x8F, 0x50, 0xFA,          // MOV $FA,#80
            0x8F, 0x01, 0xF1,          // MOV $F1,#1 (타이머 0 켜기)
            0xE4, 0xFD,                // loop: MOV A,$FD
            0xF0, 0xFC,                //       BEQ loop
            0xAB, 0x10,                //       INC $10
            0x2F, 0xF8,                //       BRA loop
        };
        var chip = new SnesSpc(MakeSpc(timer.ToArray(), "t", 2, 0));
        chip.Skip(1.0);
        int ticks = chip.Read(0x10);
        check("SPC timers: 8 kHz timer 0, target 80 → ~100 ticks/s", Math.Abs(ticks - 100) <= 2 && chip.UnknownOps == 0, ticks.ToString());
    }

    // ------------------------------------------------------------ 실제 파일 (Hootarchive/snes의 zip)

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string snes = dir == null ? null : Path.Combine(dir, "Hootarchive", "snes");
        if (snes == null || !Directory.Exists(snes)) { Console.WriteLine("SKIP  real SPC (_incoming/Hootarchive/snes not found)"); return; }
        var zips = Directory.GetFiles(snes, "*.zip", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        if (zips.Count == 0) { Console.WriteLine("SKIP  real SPC (no zip)"); return; }
        var pick = new[] { zips[0], zips[zips.Count / 3], zips[2 * zips.Count / 3] };
        foreach (var z in pick)
        {
            byte[] data; string name;
            using (var zip = ZipFile.OpenRead(z))
            {
                var e = zip.Entries.Where(x => x.FullName.EndsWith(".spc", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.FullName).Skip(1).FirstOrDefault()
                    ?? zip.Entries.First(x => x.FullName.EndsWith(".spc", StringComparison.OrdinalIgnoreCase));
                using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                name = Path.GetFileName(z) + "/" + e.Name;
            }
            var v = Spc.Read(data, name);
            var sw = Stopwatch.StartNew();
            var a = Render(v, 8);
            double speed = 8 / sw.Elapsed.TotalSeconds;
            var used = VgmPlayer.ScanUsed(v);
            var probe = new SnesSpc(data);
            probe.Skip(3);
            double r = Rms(a, Rate, a.Length);
            check("Real SPC " + name + ": sounds, several voices, no unknown opcodes", r > 200 && used.Count(u => u) >= 2 && probe.UnknownOps == 0,
                "\"" + v.Title + "\" " + v.LengthSeconds.ToString("0") + " s, rms " + r.ToString("0") + ", voices " + used.Count(u => u) + ", " + speed.ToString("0") + "x realtime");
        }
    }
}
