using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>v1.9 추가 묶음: Z80 · KSS · S98 (칩 기록 곡을 VGM으로 바꿔 내기).</summary>
public static class ExtraTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Z80Tests(check);
        KssSynthetic(check);
        KssReal(check);
        S98(check);
        FormatTests.Run(check);
        Systems(check);
        DiskTests.Run(check);
    }

    // ------------------------------------------------------------ 시스템 정의

    private static void Systems(Action<string, bool, string> check)
    {
        SystemDefinitions.OverridePath = null;
        SystemDefinitions.Reload();
        var msx = SystemDefinitions.Get("msx");
        check("Systems: built-in MSX (PSG ports A0/A1, SCC at 0x9800, 60 Hz)", msx != null && msx.Chip("AY-3-8910").Port("addr", 0) == 0xA0 && msx.Chip("SCC").Memory == 0x9800 && msx.FrameRate == 60, msx?.ToString());
        check("Systems: built-in ids", new[] { "msx", "sms", "pc88", "pc98-86", "x68000", "adlib", "wolf3d", "sbpro2" }.All(id => SystemDefinitions.Get(id) != null), SystemDefinitions.All.Count.ToString());

        string res = SopTests.FindDir(Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        if (res != null && File.Exists(Path.Combine(res, "systems.json")))
        {
            var file = SystemDefinitions.Parse(File.ReadAllText(Path.Combine(res, "systems.json")));
            var builtin = SystemDefinitions.Parse(SystemDefinitions.BuiltinJson);
            check("Systems: resource/systems.json = built-in", file.Count == builtin.Count && file.Select(x => x.ToString()).SequenceEqual(builtin.Select(x => x.ToString())), file.Count + "/" + builtin.Count);
        }

        // 덮어쓰기: MSX PSG 포트를 0x10/0x11로 → 0xA0/0xA1에 쓰는 KSS는 PSG를 쓰지 않은 것이 된다. 새 id도 더해진다.
        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_systems_test.json");
        File.WriteAllText(tmp, "// test\n{ \"systems\": [ { \"id\": \"msx\", \"name\": \"MSX test\", \"cpuClock\": 3579545, \"frameRate\": 50, \"chips\": [ { \"chip\": \"AY-3-8910\", \"clock\": 1789772.5, \"ports\": { \"addr\": \"0x10\", \"data\": \"0x11\" } } ] },"
            + " { \"id\": \"mine\", \"name\": \"Mine\", \"chips\": [] } ] }");
        try
        {
            SystemDefinitions.OverridePath = tmp;
            SystemDefinitions.Reload();
            check("Systems: override replaces msx, adds new id, keeps others", SystemDefinitions.Get("msx").Name == "MSX test" && SystemDefinitions.Get("mine") != null && SystemDefinitions.Get("sms") != null, SystemDefinitions.Get("msx").Name);
            var v = Kss.Render(MakeKss(false), 0, 1, 0);
            check("Systems: KSS machine follows remapped PSG ports", !v.HasPsg, v.ChipSummary);
        }
        finally
        {
            SystemDefinitions.OverridePath = null;
            SystemDefinitions.Reload();
            try { File.Delete(tmp); } catch (Exception) { }
        }
        var back = Kss.Render(MakeKss(false), 0, 1, 0);
        check("Systems: back to built-in → PSG again", back.HasPsg, back.ChipSummary);

        // hoot kss.cpp · ssAY8910: PSG 읽기 = 레지스터 값 (R7 = B8h를 읽어 R9에), BIOS 영역 = RET (CALL 00D8h가 돌아온다)
        var rb = MakeKss(false, new byte[]
        {
            0x3E, 0x07, 0xD3, 0xA0, 0x3E, 0xB8, 0xD3, 0xA1,   // R7 = B8h
            0xCD, 0xD8, 0x00,                                 // CALL 00D8h (GTTRIG, 없는 BIOS)
            0x3E, 0x07, 0xD3, 0xA0, 0xDB, 0xA2, 0x47,         // B = R7 읽기
            0x3E, 0x09, 0xD3, 0xA0, 0x78, 0xD3, 0xA1,         // R9 = B
        });
        var vr = Kss.Render(rb, 0, 1, 0);
        bool found = false;
        for (int i = vr.DataOffset; i + 2 < vr.Data.Length && !found; i++) found = vr.Data[i] == 0xA0 && vr.Data[i + 1] == 0x09 && vr.Data[i + 2] == 0xB8;
        check("KSS: PSG read-back returns the register (hoot ssAY8910), unknown BIOS entries return (RET fill)", found, vr.ChipSummary);
    }

    // ------------------------------------------------------------ Z80

    private sealed class Ram : IZ80Bus
    {
        public readonly byte[] M = new byte[0x10000];
        public readonly List<(int port, int v)> Outs = new List<(int, int)>();
        public byte Read(int a) { return M[a]; }
        public void Write(int a, byte v) { M[a] = v; }
        public byte In(int p) { return 0x5A; }
        public void Out(int p, byte v) { Outs.Add((p & 0xFF, v)); }
    }

    private static Z80 RunCode(Ram r, params byte[] code)
    {
        Array.Copy(code, 0, r.M, 0x100, code.Length);
        r.M[0x100 + code.Length] = 0x76;   // HALT
        var c = new Z80(r) { PC = 0x100, SP = 0xF000 };
        for (int i = 0; i < 100000 && !c.Halted; i++) c.Step();
        return c;
    }

    private static void Z80Tests(Action<string, bool, string> check)
    {
        var r = new Ram();
        // LD A,0x3C · ADD A,0x0C → 0x48, H 깃발
        var c = RunCode(r, 0x3E, 0x3C, 0xC6, 0x0C);
        check("Z80 ADD A,n → 0x48 with half-carry", c.A == 0x48 && (c.F & 0x10) != 0 && (c.F & 0x01) == 0, c.A.ToString("X2") + " F=" + c.F.ToString("X2"));
        // SUB: 0x10 - 0x20 → 0xF0, C · N · S
        c = RunCode(new Ram(), 0x3E, 0x10, 0xD6, 0x20);
        check("Z80 SUB borrow → 0xF0, C N S", c.A == 0xF0 && (c.F & 0x83) == 0x83, c.F.ToString("X2"));
        // DAA: 0x15 + 0x27 = 0x3C → 0x42
        c = RunCode(new Ram(), 0x3E, 0x15, 0xC6, 0x27, 0x27);
        check("Z80 DAA 15 + 27 = 42", c.A == 0x42, c.A.ToString("X2"));
        // DJNZ: B = 10 → A = 10번 더함
        c = RunCode(new Ram(), 0x06, 0x0A, 0xAF, 0x3C, 0x10, 0xFD);
        check("Z80 DJNZ loop 10", c.A == 10 && c.B == 0, c.A.ToString());
        // LDIR: 0x200 → 0x300, 5바이트
        r = new Ram();
        for (int i = 0; i < 5; i++) r.M[0x200 + i] = (byte)(i + 1);
        c = RunCode(r, 0x21, 0x00, 0x02, 0x11, 0x00, 0x03, 0x01, 0x05, 0x00, 0xED, 0xB0);
        check("Z80 LDIR copies 5 bytes", r.M[0x300] == 1 && r.M[0x304] == 5 && c.BC == 0 && c.HL == 0x205, c.BC.ToString());
        // CALL · RET · PUSH · POP
        c = RunCode(new Ram(), 0x01, 0x34, 0x12, 0xC5, 0xCD, 0x0A, 0x01, 0xD1, 0x76, 0x00, 0x3E, 0x77, 0xC9);
        check("Z80 CALL/RET + PUSH/POP", c.A == 0x77 && c.DE == 0x1234, c.DE.ToString("X4"));
        // IX: LD IX,0x400 · LD (IX+5),0x99 · LD A,(IX+5) · INC (IX+5)
        r = new Ram();
        c = RunCode(r, 0xDD, 0x21, 0x00, 0x04, 0xDD, 0x36, 0x05, 0x99, 0xDD, 0x7E, 0x05, 0xDD, 0x34, 0x05);
        check("Z80 IX indexed load/inc", c.A == 0x99 && r.M[0x405] == 0x9A, r.M[0x405].ToString("X2"));
        // CB: SET 3,B · BIT 3,B · RRC B
        c = RunCode(new Ram(), 0x06, 0x01, 0xCB, 0xD8, 0xCB, 0x58, 0xCB, 0x08);
        check("Z80 CB SET/BIT/RRC", c.B == 0x84 && (c.F & 0x01) == 1, c.B.ToString("X2"));
        // ADC HL / SBC HL · NEG
        c = RunCode(new Ram(), 0x21, 0xFF, 0xFF, 0x01, 0x01, 0x00, 0x37, 0xED, 0x4A);
        check("Z80 ADC HL,BC with carry → 0x0001, C", c.HL == 0x0001 && (c.F & 1) == 1, c.HL.ToString("X4"));
        c = RunCode(new Ram(), 0x21, 0x00, 0x10, 0x11, 0x01, 0x00, 0xB7, 0xED, 0x52, 0x3E, 0x01, 0xED, 0x44);
        check("Z80 SBC HL,DE · NEG", c.HL == 0x0FFF && c.A == 0xFF, c.HL.ToString("X4") + " A=" + c.A.ToString("X2"));
        // OUT (n),A · OUT (C),r · IN A,(n)
        r = new Ram();
        c = RunCode(r, 0x3E, 0x07, 0xD3, 0xA0, 0x0E, 0xA1, 0x06, 0x00, 0x16, 0x3E, 0xED, 0x51, 0xDB, 0xA2);
        check("Z80 OUT (n),A · OUT (C),D · IN A,(n)", r.Outs.Count == 2 && r.Outs[0] == (0xA0, 7) && r.Outs[1] == (0xA1, 0x3E) && c.A == 0x5A, r.Outs.Count.ToString());
        // EX AF · EXX
        c = RunCode(new Ram(), 0x3E, 0x11, 0x08, 0x3E, 0x22, 0x08, 0x01, 0x33, 0x00, 0xD9, 0x01, 0x44, 0x00, 0xD9);
        check("Z80 EX AF,AF' · EXX", c.A == 0x11 && c.BC == 0x33, c.A.ToString("X2") + " " + c.BC.ToString("X4"));
    }

    // ------------------------------------------------------------ KSS

    /// <summary>KSCC 하나: 0x4000에 init (PSG A = 440Hz, SCC 채널 1 켬) + play (RET).</summary>
    private static byte[] MakeKss(bool scc, byte[] extra = null)
    {
        var code = extra != null ? new List<byte>(extra) : new List<byte>
        {
            0x3E, 0x00, 0xD3, 0xA0, 0x3E, 0xFE, 0xD3, 0xA1,   // R0 = 0xFE
            0x3E, 0x01, 0xD3, 0xA0, 0x3E, 0x00, 0xD3, 0xA1,   // R1 = 0
            0x3E, 0x07, 0xD3, 0xA0, 0x3E, 0x3E, 0xD3, 0xA1,   // 믹서: 톤 A
            0x3E, 0x08, 0xD3, 0xA0, 0x3E, 0x0F, 0xD3, 0xA1,   // 볼륨 A = 15
        };
        if (scc)
        {
            // 파형 1 = 사각 (0x9800~0x981F), 주파수 = 0xFE (0x9880), 볼륨 15 (0x988A), 켜기 1 (0x988F)
            code.AddRange(new byte[] { 0x21, 0x00, 0x98, 0x06, 0x20, 0x3E, 0x60, 0x77, 0x23, 0x10, 0xFC });
            code.AddRange(new byte[] { 0x21, 0x10, 0x98, 0x06, 0x10, 0x3E, 0xA0, 0x77, 0x23, 0x10, 0xFC });
            code.AddRange(new byte[] { 0x3E, 0xFE, 0x32, 0x80, 0x98, 0xAF, 0x32, 0x81, 0x98, 0x3E, 0x0F, 0x32, 0x8A, 0x98, 0x3E, 0x01, 0x32, 0x8F, 0x98 });
        }
        code.Add(0xC9);
        int play = 0x4000 + code.Count;
        code.Add(0xC9);
        var d = new byte[0x10 + code.Count];
        Encoding.ASCII.GetBytes("KSCC").CopyTo(d, 0);
        d[4] = 0x00; d[5] = 0x40;
        d[6] = (byte)code.Count; d[7] = (byte)(code.Count >> 8);
        d[8] = 0x00; d[9] = 0x40;
        d[10] = (byte)play; d[11] = (byte)(play >> 8);
        code.CopyTo(d, 0x10);
        return d;
    }

    private static double[] Render(VgmSong s, double sec)
    {
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
        return a;
    }

    private static double Rms(double[] a, int from, int to)
    {
        to = Math.Min(to, a.Length);
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static void KssSynthetic(Action<string, bool, string> check)
    {
        var v = Kss.Render(MakeKss(false), 0, 3, 1);
        check("KSS (synthetic): PSG writes → AY VGM, 3 s", v.HasPsg && Math.Abs(v.LengthSeconds - 3) < 0.05 && v.FadeSeconds == 1, v.ChipSummary + " " + v.LengthSeconds.ToString("0.00"));
        var a = Render(v, 3);
        double on = Rms(a, 4410, Rate), end = Rms(a, (int)(2.97 * Rate), 3 * Rate);
        check("KSS (synthetic): tone sounds, fades at end", on > 1000 && end < on * 0.1, on.ToString("0") + " → " + end.ToString("0"));
        var used = VgmPlayer.ScanUsed(v);
        check("KSS (synthetic): channel A used", used[0] && !used[1], "");

        var s = Kss.Render(MakeKss(true), 0, 1, 0);
        check("KSS (synthetic): SCC memory writes → SCC chip", s.HasScc && !s.SccPlus && s.HasPsg, s.ChipSummary);
        var ch = VgmPlayer.ChannelsOf(s);
        var su = VgmPlayer.ScanUsed(s);
        int s1 = ch.FindIndex(x => x.Label == "S1");
        check("KSS (synthetic): SCC channel 1 used", s1 >= 0 && su[s1], s1.ToString());
    }

    private static void KssReal(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        var files = dir != null ? Directory.GetFiles(dir, "*.kss", SearchOption.AllDirectories) : new string[0];
        if (files.Length == 0) { Console.WriteLine("SKIP  real KSS (_incoming/*.kss not found)"); return; }
        foreach (var f in files.OrderBy(x => x))
        {
            var tracks = ChipSongs.Tracks(f);
            var v = ChipSongs.Load(tracks[0].path);
            double r = Rms(Render(v, 6), Rate, 6 * Rate);
            var used = VgmPlayer.ScanUsed(v);
            int n = used.Count(u => u);
            check("Real KSS " + Path.GetFileName(f) + ": " + tracks.Count + " tracks, first sounds", tracks.Count > 1 && r > 200 && n >= 2 && v.Title == tracks[0].title,
                v.ChipSummary + ", rms " + r.ToString("0") + ", " + n + " ch, " + v.LengthSeconds.ToString("0") + " s");
        }
    }

    // ------------------------------------------------------------ S98

    private static void S98(Action<string, bool, string> check)
    {
        // 판 3: YM2203 하나, 틱 = 1/100초, 태그
        var d = new List<byte>();
        d.AddRange(Encoding.ASCII.GetBytes("S983"));
        d.AddRange(BitConverter.GetBytes(10)); d.AddRange(BitConverter.GetBytes(1000));
        d.AddRange(BitConverter.GetBytes(0));
        int tagPos = d.Count; d.AddRange(BitConverter.GetBytes(0));
        int dataPos = d.Count; d.AddRange(BitConverter.GetBytes(0));
        int loopPos = d.Count; d.AddRange(BitConverter.GetBytes(0));
        d.AddRange(BitConverter.GetBytes(1));
        d.AddRange(BitConverter.GetBytes(2)); d.AddRange(BitConverter.GetBytes(3993600)); d.AddRange(BitConverter.GetBytes(0)); d.AddRange(BitConverter.GetBytes(0));
        int dataAt = d.Count;
        // SSG A: 440Hz 근처, 볼륨 15
        int tp = (int)Math.Round(3993600 / 2 / (16 * 440.0));
        foreach (var (r, v) in new[] { (0, tp & 0xFF), (1, tp >> 8), (7, 0x3E), (8, 0x0F) }) { d.Add(0x00); d.Add((byte)r); d.Add((byte)v); }
        d.Add(0xFF);                          // 1틱
        int loopAt = d.Count;
        d.Add(0xFE); d.Add(0x80 | 0x16); d.Add(0x01);   // 0x96 + 2 = 152틱
        d.Add(0xFD);
        int tagAt = d.Count;
        d.AddRange(Encoding.ASCII.GetBytes("[S98]"));
        d.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
        d.AddRange(Encoding.UTF8.GetBytes("title=Opening\ngame=Test Game\nartist=Someone\n"));
        d.Add(0);
        var b = d.ToArray();
        BitConverter.GetBytes(tagAt).CopyTo(b, tagPos);
        BitConverter.GetBytes(dataAt).CopyTo(b, dataPos);
        BitConverter.GetBytes(loopAt).CopyTo(b, loopPos);
        var s = S98Format.Read(b);
        check("S98 v3: YM2203 device + tags", s.Ym2203Clock == 3993600 && s.Title == "Opening" && s.Game == "Test Game" && s.Author == "Someone" && s.SourceFormat == "S98 v3", s.ChipSummary + " " + s.Title);
        check("S98 v3: ticks (1 + 0xFE varlen 152) = 1.53 s, loop", Math.Abs(s.LengthSeconds - 1.53) < 0.01 && Math.Abs(s.LoopSamples / (double)Rate - 1.52) < 0.01, s.LengthSeconds.ToString("0.000"));
        var ch = VgmPlayer.ChannelsOf(s);
        var used = VgmPlayer.ScanUsed(s);
        int a = ch.FindIndex(x => x.Label == "A");
        check("S98 v3: SSG A used", a >= 0 && used[a], a.ToString());
        double rms = Rms(Render(s, 1), 4410, Rate);
        check("S98 v3: SSG sounds", rms > 500, rms.ToString("0"));

        // 판 1: 장치 칸 없음 → YM2608, 제목 = Shift-JIS 문자열
        var v1 = new List<byte>();
        v1.AddRange(Encoding.ASCII.GetBytes("S981"));
        v1.AddRange(new byte[0x1C]);
        int t1 = v1.Count + 4; // 아래에서 고침
        var title = KoreanText.ShiftJis.GetBytes("空中戦");
        var body = new List<byte> { 0x00, 0x28, 0x00, 0xFF, 0xFD };
        var all = new byte[0x40 + body.Count + title.Length + 1];
        v1.ToArray().CopyTo(all, 0);
        BitConverter.GetBytes(0x40 + body.Count).CopyTo(all, 0x10);
        BitConverter.GetBytes(0x40).CopyTo(all, 0x14);
        body.ToArray().CopyTo(all, 0x40);
        title.CopyTo(all, 0x40 + body.Count);
        var s1 = S98Format.Read(all, "fallback");
        check("S98 v1: default YM2608, Shift-JIS title, 10 ms tick", s1.Ym2608Clock == 7987200 && s1.Title == "空中戦" && s1.TotalSamples == 441, s1.ChipSummary + " " + s1.Title + " " + s1.TotalSamples);
    }
}
