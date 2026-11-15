using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>게임보이: LR35902 명령 · DMG APU (VGM 0xB3) · GBS / GBR (만든 파일 + Hootarchive/gb의 실제 파일).</summary>
public static class GbsTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Cpu(check);
        Apu(check);
        GbsFile(check);
        GbrFile(check);
        Real(check);
    }

    // ------------------------------------------------------------ CPU

    private sealed class Ram : IGbBus
    {
        public readonly byte[] M = new byte[0x10000];
        public byte Read(int a) { return M[a]; }
        public void Write(int a, byte v) { M[a] = v; }
    }

    /// <summary>0x100부터 코드를 넣고 HALT(0x76)까지 돌린다.</summary>
    private static Lr35902 Exec(params int[] code)
    {
        var r = new Ram();
        for (int i = 0; i < code.Length; i++) r.M[0x100 + i] = (byte)code[i];
        r.M[0x100 + code.Length] = 0x76;
        var c = new Lr35902(r);
        for (int n = 0; n < 10000 && !c.Halted && !c.Jammed; n++) c.Step();
        return c;
    }

    private static void Cpu(Action<string, bool, string> check)
    {
        // ADD: 0x3A + 0xC6 = 0x00 → Z H C
        var c = Exec(0x3E, 0x3A, 0x06, 0xC6, 0x80);
        check("LR35902 ADD A,B: 3A + C6 = 00, flags Z H C", c.A == 0 && c.F == 0xB0, c.A.ToString("X2") + " F=" + c.F.ToString("X2"));
        // SUB + DAA: 0x45 − 0x17 = 0x2E → DAA → 0x28 (BCD 45 − 17 = 28)
        c = Exec(0x3E, 0x45, 0xD6, 0x17, 0x27);
        check("LR35902 SUB + DAA: BCD 45 − 17 = 28", c.A == 0x28 && (c.F & 0x40) != 0, c.A.ToString("X2"));
        // LDI / LDD / LDH, SWAP, BIT
        c = Exec(0x21, 0x00, 0xC0, 0x3E, 0x12, 0x22, 0x3E, 0x34, 0x32, 0x2A, 0x47, 0xE0, 0x80, 0xF0, 0x80, 0xCB, 0x37, 0xCB, 0x7F);
        check("LR35902 LD (HL+) · (HL−) · LDH · SWAP · BIT 7", c.HL == 0xC001 && c.B == 0x12 && c.A == 0x21 && (c.F & 0x80) != 0 && (c.F & 0x20) != 0,
            "HL=" + c.HL.ToString("X4") + " B=" + c.B.ToString("X2") + " A=" + c.A.ToString("X2") + " F=" + c.F.ToString("X2"));
        // PUSH AF / POP AF: F 아래 4비트는 늘 0
        c = Exec(0x31, 0xFE, 0xFF, 0x01, 0xFF, 0x12, 0xC5, 0xF1);
        check("LR35902 POP AF clears low nibble of F", c.A == 0x12 && c.F == 0xF0, c.AF.ToString("X4"));
        // ADD SP,−1 (0xFFFE − 1): H · C는 아래 바이트 더하기 (FE + FF)
        c = Exec(0x31, 0xFE, 0xFF, 0xE8, 0xFF, 0xF8, 0x02);
        check("LR35902 ADD SP,e / LD HL,SP+e", c.SP == 0xFFFD && c.HL == 0xFFFF && (c.F & 0x30) == 0x00,
            "SP=" + c.SP.ToString("X4") + " HL=" + c.HL.ToString("X4") + " F=" + c.F.ToString("X2"));
        // CALL / RET / JR 되풀이: B = 5 → 0, 클럭
        // 0x105 CALL 0x109 → DEC B / JR NZ −3 다섯 번 → RET → 0x108 HALT. 클럭 12 + 8 + 24 + 5×4 + 4×12 + 8 + 16 + 4 = 140
        c = Exec(0x31, 0xFE, 0xFF, 0x06, 0x05, 0xCD, 0x09, 0x01, 0x76, 0x05, 0x20, 0xFD, 0xC9);
        check("LR35902 CALL / JR NZ loop / RET, cycle count", c.B == 0 && c.PC == 0x109 && c.SP == 0xFFFE && c.Cycles == 140, "B=" + c.B + " PC=" + c.PC.ToString("X4") + " cycles " + c.Cycles);
        // 없는 명령 (0xD3) → 멈춤
        c = Exec(0xD3);
        check("LR35902 illegal opcode jams", c.Jammed && c.UnknownOps == 1, "");
    }

    // ------------------------------------------------------------ APU (VGM 0xB3)

    private static VgmWriter Writer()
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockGbDmg, GbApu.DefaultClock);
        w.Write((byte)0xB3, 0x16, 0x80);   // NR52 전원
        w.Write((byte)0xB3, 0x14, 0x77);   // NR50
        return w;
    }

    private static double[] Render(VgmSong s, double sec, int side = 0)
    {
        var p = new VgmPlayer(); p.Start(s, 0, null);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = side < 0 ? l : side > 0 ? r : (l + r) / 2; }
        return a;
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
        double m = 0; for (int i = from; i < to; i++) m += a[i]; m /= Math.Max(1, to - from);
        double s = 0; for (int i = from; i < to; i++) s += (a[i] - m) * (a[i] - m);
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    /// <summary>사각파 1: 50%, 음량 15, 주파수 x (440Hz = 1750).</summary>
    private static void Square1(VgmWriter w, int x, int nr10 = 0, int nr11 = 0x80, int nr12 = 0xF0, int lenOn = 0)
    {
        w.Write((byte)0xB3, 0x00, nr10);
        w.Write((byte)0xB3, 0x01, nr11);
        w.Write((byte)0xB3, 0x02, nr12);
        w.Write((byte)0xB3, 0x03, x & 0xFF);
        w.Write((byte)0xB3, 0x04, 0x80 | lenOn | (x >> 8));
    }

    private static void Apu(Action<string, bool, string> check)
    {
        var w = Writer();
        w.Write((byte)0xB3, 0x15, 0x11);
        Square1(w, 1750);
        w.Wait(Rate);
        var s = VgmFormat.Read(w.ToArray());
        var labels = string.Join(",", VgmPlayer.ChannelsOf(s).Select(c => c.Label));
        var a = Render(s, 1);
        double f = Freq(a, 4410, Rate);
        check("GB DMG: VGM clock 0x80 → chip, P1 P2 W N", s.GbDmgClock == 4194304 && s.UnsupportedChips.Count == 0 && s.ChipSummary == "GB DMG" && labels == "P1,P2,W,N", labels);
        check("GB DMG square 1: freq 1750 → 439.8 Hz", Math.Abs(f - 439.8) < 3 && Rms(a, 4410, Rate) > 1000 && VgmPlayer.ScanUsed(s)[0], f.ToString("0.0") + " Hz, rms " + Rms(a, 4410, Rate).ToString("0"));

        // NR51 = 0x10 → 왼쪽만
        var wp = Writer(); wp.Write((byte)0xB3, 0x15, 0x10); Square1(wp, 1750); wp.Wait(Rate / 4);
        var sp = VgmFormat.Read(wp.ToArray());
        double pl = Rms(Render(sp, 0.25, -1), 2205, Rate / 4), pr = Rms(Render(sp, 0.25, 1), 2205, Rate / 4);
        check("GB DMG NR51 panning: left only", pl > 1000 && pr < 1, pl.ToString("0") + " / " + pr.ToString("0"));

        // 파형: 사인 32칸, 주파수 1750 → 65536 / 298 = 219.9Hz
        var ww = Writer(); ww.Write((byte)0xB3, 0x15, 0x44);
        for (int i = 0; i < 16; i++)
        {
            int hi = (int)Math.Round(7.5 + 7.5 * Math.Sin(2 * Math.PI * (2 * i) / 32)), lo = (int)Math.Round(7.5 + 7.5 * Math.Sin(2 * Math.PI * (2 * i + 1) / 32));
            ww.Write((byte)0xB3, 0x20 + i, (hi << 4) | lo);
        }
        ww.Write((byte)0xB3, 0x0A, 0x80); ww.Write((byte)0xB3, 0x0C, 0x20); ww.Write((byte)0xB3, 0x0D, 1750 & 0xFF); ww.Write((byte)0xB3, 0x0E, 0x80 | (1750 >> 8));
        ww.Wait(Rate);
        var aw = Render(VgmFormat.Read(ww.ToArray()), 1);
        double fw = Freq(aw, 4410, Rate);
        check("GB DMG wave channel: sine RAM, 219.9 Hz", Math.Abs(fw - 219.9) < 2 && Rms(aw, 4410, Rate) > 1000, fw.ToString("0.0") + " Hz");

        // 길이: NR11 길이 63 → 1/256초 뒤 꺼짐
        var wl = Writer(); wl.Write((byte)0xB3, 0x15, 0x11); Square1(wl, 1750, 0, 0x80 | 63, 0xF0, 0x40); wl.Wait(Rate / 2);
        var al = Render(VgmFormat.Read(wl.ToArray()), 0.5);
        check("GB DMG length counter stops square 1 (~4 ms)", Rms(al, Rate / 20, Rate / 2) < 30, Rms(al, Rate / 20, Rate / 2).ToString("0.0"));

        // 엔벨로프: 15 → 0, 주기 1 (1/64초 칸) → 0.25초 뒤 조용
        var we = Writer(); we.Write((byte)0xB3, 0x15, 0x11); Square1(we, 1750, 0, 0x80, 0xF1); we.Wait(Rate / 2);
        var ae = Render(VgmFormat.Read(we.ToArray()), 0.5);
        double e0 = Rms(ae, 0, Rate / 20), e1 = Rms(ae, Rate * 3 / 10, Rate / 2);
        check("GB DMG envelope decay (15 → 0 in 0.23 s)", e0 > 1500 && e1 < 30, e0.ToString("0") + " → " + e1.ToString("0"));

        // 쓸기: 주기 7 (55ms), 올림, 자리 2 → 1000 (125Hz) → 1250 (164.5Hz) → 1562 → 1952, 다음 계산이 넘쳐 꺼짐 (≈ 165ms)
        var wsw = Writer(); wsw.Write((byte)0xB3, 0x15, 0x11); Square1(wsw, 1000, 0x72); wsw.Wait(Rate / 2);
        var asw = Render(VgmFormat.Read(wsw.ToArray()), 0.5);
        double fs0 = Freq(asw, Rate / 100, Rate * 45 / 1000), fs1 = Freq(asw, Rate * 65 / 1000, Rate * 105 / 1000);
        check("GB DMG sweep: 125 → 164.5 Hz, then overflow turns channel off", Math.Abs(fs0 - 125) < 15 && Math.Abs(fs1 - 164.5) < 15 && Rms(asw, Rate / 4, Rate / 2) < 30,
            fs0.ToString("0") + " → " + fs1.ToString("0") + " Hz, tail " + Rms(asw, Rate / 4, Rate / 2).ToString("0.0"));
        // 넘치는 값으로 시작하면 트리거 때 바로 꺼진다 (1500 + 750 > 2047)
        var wso = Writer(); wso.Write((byte)0xB3, 0x15, 0x11); Square1(wso, 1500, 0x11); wso.Wait(Rate / 10);
        check("GB DMG sweep overflow at trigger silences at once", Rms(Render(VgmFormat.Read(wso.ToArray()), 0.1), 0, Rate / 10) < 30, "");

        // 노이즈
        var wn = Writer(); wn.Write((byte)0xB3, 0x15, 0x88); wn.Write((byte)0xB3, 0x11, 0xF0); wn.Write((byte)0xB3, 0x12, 0x22); wn.Write((byte)0xB3, 0x13, 0x80); wn.Wait(Rate / 2);
        var sn = VgmFormat.Read(wn.ToArray());
        var an = Render(sn, 0.5);
        check("GB DMG noise channel sounds (no pitch shown)", Rms(an, 2205, Rate / 2) > 1000 && VgmPlayer.ScanUsed(sn)[3], Rms(an, 2205, Rate / 2).ToString("0"));

        // 전원 끔 → 조용
        var wo = Writer(); wo.Write((byte)0xB3, 0x15, 0x11); Square1(wo, 1750); wo.Wait(Rate / 10); wo.Write((byte)0xB3, 0x16, 0x00); wo.Wait(Rate / 2);
        var ao = Render(VgmFormat.Read(wo.ToArray()), 0.6);
        check("GB DMG NR52 power off silences all channels", Rms(ao, 0, Rate / 10) > 1000 && Rms(ao, Rate * 4 / 10, Rate * 6 / 10) < 30, Rms(ao, Rate * 4 / 10, Rate * 6 / 10).ToString("0.0"));

        // 읽기: NR52 = 켜짐 + 채널 1 켜짐, 읽히지 않는 비트 = 1
        var apu = new GbApu();
        apu.Write(0, 0x16, 0x80); apu.Write(0, 0x02, 0xF0); apu.Write(0, 0x04, 0x80); apu.Write(0, 0x01, 0x80);
        check("GB DMG register reads: NR52 status, NR11 = duty | 0x3F", apu.Read(0x16) == 0xF1 && apu.Read(0x01) == 0xBF, apu.Read(0x16).ToString("X2") + " " + apu.Read(0x01).ToString("X2"));
    }

    // ------------------------------------------------------------ GBS (만든 파일)

    /// <summary>
    /// load $0400. init: 뱅크 = 곡 번호 + 1 → 전원 · 음량 · 왼오른쪽 → $4000의 바이트를 NR13으로 (뱅크 1 = $D6 = 440Hz, 뱅크 2 = $00 = 256Hz) → 소리.
    /// play: NR50 = $77 (부른 횟수 세기).
    /// </summary>
    private static byte[] MakeGbs(int tma, int tac, int songs = 2)
    {
        var data = new byte[0x8000 - 0x400 + 1];
        int[] init =
        {
            0x3C, 0xEA, 0x00, 0x20,       // INC A; LD ($2000),A  (뱅크 = 곡 + 1)
            0x3E, 0x80, 0xE0, 0x26,       // NR52
            0x3E, 0x77, 0xE0, 0x24,       // NR50
            0x3E, 0x11, 0xE0, 0x25,       // NR51
            0x3E, 0x80, 0xE0, 0x11,       // NR11
            0x3E, 0xF0, 0xE0, 0x12,       // NR12
            0xFA, 0x00, 0x40, 0xE0, 0x13, // NR13 = ($4000)
            0x3E, 0x86, 0xE0, 0x14,       // NR14 (트리거, 위 3비트 6)
            0xC9,
        };
        int[] play = { 0x3E, 0x77, 0xE0, 0x24, 0xC9 };
        for (int i = 0; i < init.Length; i++) data[i] = (byte)init[i];
        for (int i = 0; i < play.Length; i++) data[0x40 + i] = (byte)play[i];
        data[0x4000 - 0x400] = 0xD6;     // 뱅크 1
        data[0x8000 - 0x400] = 0x00;     // 뱅크 2
        var f = new byte[0x70 + data.Length];
        f[0] = (byte)'G'; f[1] = (byte)'B'; f[2] = (byte)'S'; f[3] = 1; f[4] = (byte)songs; f[5] = 1;
        f[6] = 0x00; f[7] = 0x04; f[8] = 0x00; f[9] = 0x04; f[10] = 0x40; f[11] = 0x04; f[12] = 0xFE; f[13] = 0xFF;
        f[14] = (byte)tma; f[15] = (byte)tac;
        var title = System.Text.Encoding.ASCII.GetBytes("Test GBS");
        Array.Copy(title, 0, f, 0x10, title.Length);
        Array.Copy(data, 0, f, 0x70, data.Length);
        return f;
    }

    private static int CountWrites(VgmSong v, int reg)
    {
        int n = 0, p = v.DataOffset;
        var d = v.Data;
        while (p < v.DataEnd && d[p] != 0x66)
        {
            if (d[p] == 0xB3 && d[p + 1] == reg) n++;
            int len = VgmFormat.CommandLength(d, p);
            if (len <= 0) break;
            p += len;
        }
        return n;
    }

    private static void GbsFile(Action<string, bool, string> check)
    {
        var f = MakeGbs(0, 0);
        var h = Gbs.ReadHeader(f);
        check("GBS header: songs · load · init · play · stack · title", h.Songs == 2 && h.LoadAddr == 0x400 && h.InitAddr == 0x400 && h.PlayAddr == 0x440 && h.Stack == 0xFFFE && h.Title == "Test GBS" && !h.UsesTimer && h.Rom[0x38] == 0xC3,
            h.Songs + " " + h.LoadAddr.ToString("X4"));
        // 곡 1 = 뱅크 1 (0 → 1) → 440Hz, 곡 2 = 뱅크 2 → 256Hz
        var v0 = Gbs.Render(f, 0, 2);
        var v1 = Gbs.Render(f, 1, 2);
        double f0 = Freq(Render(v0, 1), 4410, Rate), f1 = Freq(Render(v1, 1), 4410, Rate);
        check("GBS: init A = song, bank switch $2000 (song 1 → 440 Hz, song 2 → 256 Hz)", Math.Abs(f0 - 439.8) < 3 && Math.Abs(f1 - 256) < 3 && v0.ChipSummary == "GB DMG" && v0.UnsupportedChips.Count == 0,
            f0.ToString("0.0") + " / " + f1.ToString("0.0") + " Hz");
        // VBlank: 2초에 ≈ 119번 (NR50 쓰기에서 처음 상태 · init의 2번을 뺀다)
        int vb = CountWrites(v0, 0x14) - 2;
        // 타이머: TAC = 4 (4096Hz), TMA = $C0 → 64Hz → 2초에 128번 / TAC bit7 (두 배 속도) → 256번
        int tm = CountWrites(Gbs.Render(MakeGbs(0xC0, 0x04), 0, 2), 0x14) - 2;
        int tm2 = CountWrites(Gbs.Render(MakeGbs(0xC0, 0x84), 0, 2), 0x14) - 2;
        check("GBS play rate: VBlank 59.7 Hz, timer (256−TMA)·1024 clocks = 64 Hz, ×2 with TAC bit7", Math.Abs(vb - 119.5) <= 1.5 && Math.Abs(tm - 128) <= 1 && Math.Abs(tm2 - 256) <= 1, vb + " / " + tm + " / " + tm2);
    }

    // ------------------------------------------------------------ GBR (만든 파일)

    /// <summary>
    /// 'GBRF' + 뱅크 3개 (0번 = 코드, 1 · 2번 첫 바이트 = 원래 번호 $70 · $71). init: 곡 &lt; 3이면 MBC5 방식 ($2100 = $70 + 곡 % 2, $3100 = 0)으로
    /// 뱅크를 골라 $4001의 바이트 + 곡 번호를 NR13으로 → 소리. 곡 3 이상은 조용.
    /// </summary>
    private static byte[] MakeGbr()
    {
        var rom = new byte[3 * 0x4000];
        int[] init =
        {
            0x47,                         // LD B,A
            0xFE, 0x03, 0xD0,             // CP 3; RET NC
            0xE6, 0x01, 0xC6, 0x70,       // AND 1; ADD $70
            0xEA, 0x00, 0x21,             // LD ($2100),A
            0xAF, 0xEA, 0x00, 0x31,       // XOR A; LD ($3100),A
            0x3E, 0x80, 0xE0, 0x26, 0x3E, 0x77, 0xE0, 0x24, 0x3E, 0x11, 0xE0, 0x25, 0x3E, 0x80, 0xE0, 0x11, 0x3E, 0xF0, 0xE0, 0x12,
            0xFA, 0x01, 0x40, 0x80, 0xE0, 0x13, // NR13 = ($4001) + B
            0x3E, 0x86, 0xE0, 0x14,
            0xC9,
        };
        for (int i = 0; i < init.Length; i++) rom[0x1000 + i] = (byte)init[i];
        rom[0x2000] = 0xC9;              // VBlank: RET
        rom[0x4000] = 0x70; rom[0x4001] = 0xD0;
        rom[0x8000] = 0x71; rom[0x8001] = 0x10;
        var f = new byte[0x20 + rom.Length];
        f[0] = (byte)'G'; f[1] = (byte)'B'; f[2] = (byte)'R'; f[3] = (byte)'F'; f[4] = 3; f[6] = 1; f[7] = 1;
        f[8] = 0x00; f[9] = 0x10; f[10] = 0x00; f[11] = 0x20;
        Array.Copy(rom, 0, f, 0x20, rom.Length);
        return f;
    }

    private static void GbrFile(Action<string, bool, string> check)
    {
        var f = MakeGbr();
        var h = Gbs.ReadHeader(f);
        check("GBR header: banks · init · VBlank · bank ids", h.Gbr && h.BankCount == 3 && h.InitAddr == 0x1000 && h.PlayAddr == 0x2000 && h.BankIds[0x70] == 1 && h.BankIds[0x71] == 2 && h.Stack == Gbs.GbrStack,
            h.BankCount + " " + string.Join(",", h.BankIds.Select(kv => kv.Key.ToString("X2") + ">" + kv.Value)));
        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_gbr_test_" + Guid.NewGuid().ToString("N") + ".gbr");
        try
        {
            File.WriteAllBytes(tmp, f);
            var tracks = Gbs.Tracks(tmp);
            check("GBR: songs found by probing (0, 1, 2 sound; 3~255 silent)", tracks.Select(t => t.Song).SequenceEqual(new[] { 0, 1, 2 }) && Gbs.TracksKnown(tmp),
                string.Join(",", tracks.Select(t => t.Song)));
            // 곡 1 → 뱅크 $71 (파일 뱅크 2): NR13 = $10 + 1 = $11 → 1553 → 264.8Hz, 곡 0 → 뱅크 $70: $D0 → 1744 → 431.2Hz
            double fa = Freq(Render(ChipSongs.Load(tmp + "::0"), 1), 4410, Rate);
            double fb = Freq(Render(ChipSongs.Load(tmp + "::1"), 1), 4410, Rate);
            check("GBR: MBC5 bank writes map to file banks by id ($70 → 431 Hz, $71 → 265 Hz)", Math.Abs(fa - 431.2) < 3 && Math.Abs(fb - 264.8) < 3, fa.ToString("0.0") + " / " + fb.ToString("0.0") + " Hz");
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // ------------------------------------------------------------ 실제 파일 (Hootarchive/gb의 zip)

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string gb = dir == null ? null : Path.Combine(dir, "Hootarchive", "gb");
        if (gb == null || !Directory.Exists(gb)) { Console.WriteLine("SKIP  real GBS (_incoming/Hootarchive/gb not found)"); return; }
        int files = 0, sounding = 0, unknown = 0;
        var silent = new List<string>();
        var sw = Stopwatch.StartNew();
        double renderSec = 0;
        foreach (var z in Directory.GetFiles(gb, "*.zip").OrderBy(x => x))
        {
            using (var zip = ZipFile.OpenRead(z))
            {
                foreach (var e in zip.Entries.Where(x => x.FullName.EndsWith(".gbs", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".gbr", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] data;
                    using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                    files++;
                    var h = Gbs.ReadHeader(data);
                    int song = 0;
                    if (h.Gbr)
                    {
                        var songs = Gbs.SoundingSongs(Path.Combine(z, e.Name), data);
                        if (songs.Count == 0) { silent.Add(e.Name); continue; }
                        song = songs[0];
                    }
                    var v = Gbs.Render(data, song, 6);
                    renderSec += 6;
                    if (v.UnsupportedChips.Any(u => u.Contains("LR35902"))) unknown++;
                    double r = Rms(Render(v, 6), Rate, 6 * Rate);
                    if (r > 200) sounding++; else silent.Add(e.Name);
                }
            }
        }
        check("Real GBS / GBR (" + files + " files): all but one sound, no unknown opcodes", files > 0 && sounding >= files - 1 && unknown == 0,
            sounding + "/" + files + " sound, silent: " + string.Join("; ", silent) + ", " + sw.ElapsedMilliseconds + " ms");
    }
}
