using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>NES (6502 · 2A03 APU · NSF) · PC엔진 (HuC6280 CPU · PSG · HES).</summary>
public static class NesPceTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Cpu2A03(check);
        CpuHuc(check);
        Apu(check);
        PcePsg(check);
        VgmChips(check);
        NsfSynthetic(check);
        HesSynthetic(check);
        NsfExpansions(check);
        Real(check);
    }

    private sealed class FlatBus : ICpu6502Bus
    {
        public readonly byte[] M;
        public FlatBus(int size) { M = new byte[size]; }
        public byte Read(int a) { return M[a % M.Length]; }
        public void Write(int a, byte v) { M[a % M.Length] = v; }
    }

    private static int RunTo(Cpu6502 cpu, int stopPc, int max = 10000)
    {
        int n = 0;
        while (cpu.PC != stopPc && n++ < max) cpu.Step();
        return n;
    }

    // ------------------------------------------------------------ CPU

    private static void Cpu2A03(Action<string, bool, string> check)
    {
        var bus = new FlatBus(0x10000);
        byte[] code =
        {
            0xA9, 0x50, 0x18, 0x69, 0x50, 0x85, 0x00,       // LDA #$50 CLC ADC #$50 STA $00   → A0, V N
            0x08, 0x68, 0x85, 0x01,                          // PHP PLA STA $01 (깃발)
            0x38, 0xA9, 0x50, 0xE9, 0xF0, 0x85, 0x02,        // SEC LDA #$50 SBC #$F0 STA $02  → 60, C=0
            0xA2, 0x05, 0xCA, 0xD0, 0xFD, 0x86, 0x03,        // LDX #5 : DEX BNE : STX $03
            0x20, 0x40, 0x80,                                // JSR $8040
            0xA7, 0x00, 0x86, 0x05,                          // LAX $00 STX $05 (비공식)
            0x6C, 0xFF, 0x10,                                // JMP ($10FF) → NMOS 버그: 상위 바이트를 $1000에서
        };
        code.CopyTo(bus.M, 0x8000);
        bus.M[0x8040] = 0xA0; bus.M[0x8041] = 0x42; bus.M[0x8042] = 0x84; bus.M[0x8043] = 0x04; bus.M[0x8044] = 0x60;   // LDY #$42 STY $04 RTS
        bus.M[0x10FF] = 0x00; bus.M[0x1000] = 0x90; bus.M[0x1100] = 0x70;
        var cpu = new Cpu6502(bus, Cpu6502.Variant.Nes2A03) { PC = 0x8000 };
        RunTo(cpu, 0x9000);
        var m = bus.M;
        check("6502: ADC overflow / SBC borrow / loop / JSR / LAX", m[0] == 0xA0 && (m[1] & 0xC1) == 0xC0 && m[2] == 0x60 && m[3] == 0 && m[4] == 0x42 && m[5] == 0xA0,
            string.Join(" ", m.Take(6).Select(b => b.ToString("X2"))));
        check("6502: JMP (xxFF) page bug", cpu.PC == 0x9000 && cpu.UnknownOps == 0, cpu.PC.ToString("X4"));
    }

    private static void CpuHuc(Action<string, bool, string> check)
    {
        var bus = new FlatBus(0x200000);
        var cpu = new Cpu6502(bus, Cpu6502.Variant.HuC6280);
        cpu.Mpr[0] = 0xFF; cpu.Mpr[1] = 0xF8; cpu.Mpr[7] = 0x00;
        var c = new List<byte>
        {
            0xA9, 0x05, 0x53, 0x04,                 // LDA #5  TAM #$04  (MPR2 = 5)
            0x62, 0x43, 0x04,                       // CLA  TMA #$04  → A = 5
            0x85, 0x30,                             // STA $30 (zp = $2030)
            0xA2, 0x07, 0x22, 0x85, 0x31,           // LDX #7  SAX  STA $31 → 7
            0x73, 0x00, 0xE1, 0x10, 0x20, 0x04, 0x00,   // TII $E100 → $2010, 4
            0xB7, 0x20,                             // SMB3 $20
            0xBF, 0x20, 0x02,                       // BBS3 $20, +2
            0x85, 0x32,                             // (건너뜀) STA $32
            0x64, 0x00, 0xA2, 0x00,                 // STZ $00  LDX #0
            0xF4, 0x69, 0x09,                       // SET  ADC #9 → (zp,X) = $2000 += 9 (A 그대로)
            0x85, 0x33,                             // STA $33 (A = 7 그대로)
            0x4C, 0x00, 0xF0,                       // JMP $F000 (끝)
        };
        c.ToArray().CopyTo(bus.M, 0);
        bus.M[0x100] = 1; bus.M[0x101] = 2; bus.M[0x102] = 3; bus.M[0x103] = 4;
        cpu.PC = 0xE000;
        RunTo(cpu, 0xF000);
        int ram = 0xF8 << 13;
        var m = bus.M;
        check("HuC6280: TAM/TMA, SAX, zero page at $2000", cpu.Mpr[2] == 5 && m[ram + 0x30] == 5 && m[ram + 0x31] == 7, m[ram + 0x30] + " " + m[ram + 0x31]);
        check("HuC6280: TII block copy", m[ram + 0x10] == 1 && m[ram + 0x13] == 4, "");
        check("HuC6280: SMB / BBS branch", m[ram + 0x20] == 0x08 && m[ram + 0x32] == 0, m[ram + 0x32].ToString());
        check("HuC6280: T flag ADC writes memory", m[ram] == 9 && m[ram + 0x33] == 7 && cpu.UnknownOps == 0, m[ram] + " / A=" + m[ram + 0x33]);
    }

    // ------------------------------------------------------------ 칩

    private static double[] Gen(ISoundChip chip, double sec, bool right = false)
    {
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { chip.Generate(out int l, out int r); a[i] = right ? r : l; }
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
        double mean = 0; for (int i = from; i < to; i++) mean += a[i]; mean /= Math.Max(1, to - from);
        double s = 0; for (int i = from; i < to; i++) s += (a[i] - mean) * (a[i] - mean);
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    private static void Apu(Action<string, bool, string> check)
    {
        var apu = new NesApu();
        apu.Write(0, 0x15, 0x0F);
        apu.Write(0, 0x00, 0xBF);                       // 50%, 멈춤 없음(길이 정지), 고정 볼륨 15
        apu.Write(0, 0x02, 253); apu.Write(0, 0x03, 0x08);   // 주기 253 → 440Hz
        var a = Gen(apu, 1);
        double f = Freq(a, 4410, Rate);
        check("2A03 pulse 1: A4 = 440 Hz", Math.Abs(f - 440) < 4 && Math.Abs(apu.ChannelFrequency(0) - 440) < 2 && Rms(a, 4410, Rate) > 1000, f.ToString("0.0") + " Hz, rms " + Rms(a, 4410, Rate).ToString("0"));
        apu.SetPitch(0, Math.Pow(2, 1 / 12.0));
        double fs = Freq(Gen(apu, 0.5), 2205, Rate / 2);
        check("2A03 pitch: +1 semitone", Math.Abs(fs - 466.16) < 5, fs.ToString("0.0"));
        apu.SetPitch(0, 1); apu.SetMute(0, true);
        check("2A03 mute pulse 1", Rms(Gen(apu, 0.3), 2205, (int)(0.3 * Rate)) < 50, "");

        var t = new NesApu();
        t.Write(0, 0x15, 0x04);
        t.Write(0, 0x08, 0xFF); t.Write(0, 0x0A, 253); t.Write(0, 0x0B, 0x08);   // 삼각파 220Hz
        var ta = Gen(t, 1);
        double tf = Freq(ta, 4410, Rate);
        check("2A03 triangle: A3 = 220 Hz", Math.Abs(tf - 220) < 3 && Math.Abs(t.ChannelFrequency(2) - 220) < 1, tf.ToString("0.0"));

        var l = new NesApu();
        l.Write(0, 0x15, 0x01);
        l.Write(0, 0x00, 0x9F);                         // 길이 카운터 돎
        l.Write(0, 0x02, 253); l.Write(0, 0x03, 0x18);  // 길이 칸 3 → 2 (반 프레임 2번 ≈ 17ms)
        var la = Gen(l, 0.3);
        check("2A03 length counter stops note", Rms(la, 0, 400) > 1000 && Rms(la, 4410, 13230) < 30, Rms(la, 0, 400).ToString("0") + " → " + Rms(la, 4410, 13230).ToString("0"));

        var n = new NesApu();
        n.Write(0, 0x15, 0x08); n.Write(0, 0x0C, 0x3F); n.Write(0, 0x0E, 0x04); n.Write(0, 0x0F, 0x08);
        var na = Gen(n, 0.5);
        check("2A03 noise sounds, active", Rms(na, 2205, Rate / 2) > 500 && n.ChannelActive(3) && n.ChannelFrequency(3) == 0, Rms(na, 2205, Rate / 2).ToString("0"));

        var d = new NesApu();
        var samples = Enumerable.Range(0, 64).Select(i => (byte)(i % 16 < 8 ? 0xFF : 0x00)).ToArray();   // 64비트 오르고 64비트 내림 (네모 소리)
        d.LoadRam(0xC000, samples, 0, samples.Length);
        d.Write(0, 0x10, 0x4F); d.Write(0, 0x11, 64); d.Write(0, 0x12, 0); d.Write(0, 0x13, 1); d.Write(0, 0x15, 0x10);
        var da = Gen(d, 0.3);
        check("2A03 DPCM plays memory (loop)", Rms(da, 2205, 13230) > 200 && d.ChannelActive(4), Rms(da, 2205, 13230).ToString("0"));
    }

    private static void SetupPceSquare(Huc6280Psg p, int ch, int period, int balance)
    {
        p.Write(0, 0, ch); p.Write(0, 1, 0xFF);
        p.Write(0, 4, 0x00);
        for (int i = 0; i < 32; i++) p.Write(0, 6, i < 16 ? 31 : 0);
        p.Write(0, 2, period & 0xFF); p.Write(0, 3, period >> 8);
        p.Write(0, 5, balance);
        p.Write(0, 4, 0x80 | 31);
    }

    private static void PcePsg(Action<string, bool, string> check)
    {
        var p = new Huc6280Psg();
        SetupPceSquare(p, 0, 254, 0xFF);            // 3579545 / (32 × 254) = 440.4 Hz
        var a = Gen(p, 1);
        double f = Freq(a, 4410, Rate);
        check("HuC6280 wave channel: 440 Hz", Math.Abs(f - 440.4) < 4 && Math.Abs(p.ChannelFrequency(0) - 440.4) < 1 && Rms(a, 4410, Rate) > 1000, f.ToString("0.0") + " rms " + Rms(a, 4410, Rate).ToString("0"));
        var q = new Huc6280Psg();
        SetupPceSquare(q, 2, 254, 0xF0);            // 왼쪽만
        check("HuC6280 balance: left only", Rms(Gen(q, 0.3), 2205, 13230) > 1000 && Rms(Gen(q, 0.3, true), 2205, 13230) < 1, "");
        var v = new Huc6280Psg();
        SetupPceSquare(v, 0, 254, 0xFF);
        double loud = Rms(Gen(v, 0.2), 2205, 8820);
        v.Write(0, 4, 0x80 | 21);                   // 10칸 = 15dB 작게
        double soft = Rms(Gen(v, 0.2), 2205, 8820);
        check("HuC6280 volume is logarithmic (−15 dB)", Math.Abs(20 * Math.Log10(soft / loud) + 15) < 1, (20 * Math.Log10(soft / loud)).ToString("0.0") + " dB");
        var dd = new Huc6280Psg();
        dd.Write(0, 0, 1); dd.Write(0, 1, 0xFF); dd.Write(0, 5, 0xFF); dd.Write(0, 4, 0xC0 | 31);
        var da = new double[Rate / 10];
        for (int i = 0; i < da.Length; i++) { if (i % 50 == 0) dd.Write(0, 6, (i / 50) % 2 == 0 ? 31 : 0); dd.Generate(out int l, out _); da[i] = l; }
        check("HuC6280 DDA: direct output", Rms(da, 0, da.Length) > 1000 && dd.ChannelActive(1), Rms(da, 0, da.Length).ToString("0"));
    }

    private static void VgmChips(Action<string, bool, string> check)
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockNesApu, 1789773);
        w.SetClock(VgmWriter.ClockHuc6280, 3579545);
        w.Write(0xB4, 0x15, 0x01); w.Write(0xB4, 0x00, 0xBF); w.Write(0xB4, 0x02, 253); w.Write(0xB4, 0x03, 0x08);
        w.Write(0xB9, 0, 0); w.Write(0xB9, 1, 0xFF); w.Write(0xB9, 4, 0);
        for (int i = 0; i < 32; i++) w.Write(0xB9, 6, i < 16 ? 31 : 0);
        w.Write(0xB9, 2, 127); w.Write(0xB9, 3, 0); w.Write(0xB9, 5, 0xFF); w.Write(0xB9, 4, 0x9F);
        w.Wait(Rate);
        var s = VgmFormat.Read(w.ToArray());
        var ch = VgmPlayer.ChannelsOf(s);
        var used = VgmPlayer.ScanUsed(s);
        int p1 = ch.FindIndex(c => c.Label == "P1"), w1 = ch.FindIndex(c => c.Label == "W1");
        check("VGM: 2A03 + HuC6280 chips play (no unsupported)", s.ChipSummary.Contains("2A03") && s.ChipSummary.Contains("HuC6280") && s.UnsupportedChips.Count == 0 && s.IsPlayable,
            s.ChipSummary + " / " + string.Join(",", s.UnsupportedChips));
        // 멈춘 뒤 소리가 남지 않아야 한다 (NES 사각파는 길이 정지 · 고정 볼륨이면 끝없이 운다)
        var vp = new VgmPlayer(); vp.Start(s, 0, null);
        var before = new double[Rate / 10];
        for (int i = 0; i < before.Length; i++) { vp.Step(); vp.Generate(out double l, out _); before[i] = l; }
        vp.Stop();
        var after = new double[Rate / 10];
        for (int i = 0; i < after.Length; i++) { vp.Step(); vp.Generate(out double l, out _); after[i] = l; }
        check("VGM stop: held NES / PCE notes go silent (5 ms ramp)", Rms(before, 441, before.Length) > 1000 && Rms(after, 441, after.Length) == 0 && Math.Abs(after[0]) < 40000,
            Rms(before, 441, before.Length).ToString("0") + " → " + Rms(after, 441, after.Length).ToString("0.00"));
        using (var eng = new AudioEngine())
        {
            eng.PlayVgm(s);
            var buf = new short[4410 * 2];
            eng.RenderStereo(buf, 4410);
            double on = 0; for (int i = 0; i < buf.Length; i++) on += (double)buf[i] * buf[i];
            eng.Stop();
            eng.RenderStereo(buf, 441);   // 10ms: 줄어드는 동안
            eng.RenderStereo(buf, 4410);
            double off = 0; for (int i = 0; i < buf.Length; i++) off += (double)buf[i] * buf[i];
            check("Engine: Jukebox stop on chip song leaves no sound", on > 0 && off == 0, Math.Sqrt(on / buf.Length).ToString("0") + " → " + Math.Sqrt(off / buf.Length).ToString("0"));
        }
        check("VGM: channels P1 · W1 used", p1 >= 0 && w1 >= 0 && used[p1] && used[w1] && !used[p1 + 1], string.Join(",", ch.Select(c => c.Label)));
    }

    // ------------------------------------------------------------ NSF · HES (만든 파일)

    private static byte[] MakeNsf()
    {
        var d = new byte[0x80 + 0x5000];
        Encoding.ASCII.GetBytes("NESM\x1A").CopyTo(d, 0);
        d[5] = 1; d[6] = 2; d[7] = 1;
        d[8] = 0x00; d[9] = 0x80; d[10] = 0x00; d[11] = 0x80; d[12] = 0x40; d[13] = 0x80;
        Encoding.ASCII.GetBytes("Test Song").CopyTo(d, 0x0E);
        Encoding.ASCII.GetBytes("Tester").CopyTo(d, 0x2E);
        d[0x6E] = 0x1A; d[0x6F] = 0x41;   // 16666 µs = 60Hz
        byte[] init =
        {
            0xC9, 0x01, 0xF0, 0x15,                         // CMP #1  BEQ dmc
            0xA9, 0x01, 0x8D, 0x15, 0x40,                   // LDA #1 STA $4015
            0xA9, 0xBF, 0x8D, 0x00, 0x40,                   // LDA #$BF STA $4000
            0xA9, 0xFD, 0x8D, 0x02, 0x40,                   // LDA #$FD STA $4002
            0xA9, 0x08, 0x8D, 0x03, 0x40,                   // LDA #$08 STA $4003
            0x60,                                           // RTS
            // dmc:
            0xA9, 0x4F, 0x8D, 0x10, 0x40,                   // LDA #$4F STA $4010 (반복, 빠르게)
            0xA9, 0x00, 0x8D, 0x12, 0x40,                   // $4012 = 0 → $C000
            0xA9, 0x01, 0x8D, 0x13, 0x40,                   // $4013 = 1 → 17바이트
            0xA9, 0x10, 0x8D, 0x15, 0x40,                   // $4015 = $10
            0x60,
        };
        init.CopyTo(d, 0x80);
        byte[] play = { 0xE6, 0x00, 0xA5, 0x00, 0x8D, 0x02, 0x40, 0x60 };   // INC $00 LDA $00 STA $4002 RTS
        play.CopyTo(d, 0x80 + 0x40);
        for (int i = 0; i < 17; i++) d[0x80 + 0x4000 + i] = 0x55;            // $C000: DPCM
        return d;
    }

    private static void NsfSynthetic(Action<string, bool, string> check)
    {
        var nsf = MakeNsf();
        var h = Nsf.ReadHeader(nsf);
        check("NSF header: songs, addresses, tags", h.Songs == 2 && h.InitAddr == 0x8000 && h.PlayAddr == 0x8040 && h.Title == "Test Song" && h.Artist == "Tester" && !h.Banked, h.Title);
        var v = Nsf.Render(nsf, 0, 1, 0);
        int writes = 0;
        for (int p = v.DataOffset; p < v.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(v.Data, p))) if (v.Data[p] == 0xB4 && v.Data[p + 1] == 0x02) writes++;
        check("NSF (synthetic): init + play every frame → 2A03 VGM", v.NesApuClock > 0 && Math.Abs(v.LengthSeconds - 1) < 0.02 && writes >= 60 && writes <= 63 && v.UnsupportedChips.Count == 0,
            v.ChipSummary + ", $4002 writes " + writes + ", " + string.Join(",", v.UnsupportedChips));
        var p1 = new VgmPlayer(); p1.Start(v, 0, null);
        var a = new double[Rate / 2];
        for (int i = 0; i < a.Length; i++) { p1.Step(); p1.Generate(out double l, out double r); a[i] = l; }
        check("NSF (synthetic): sounds", Rms(a, 2205, a.Length) > 500, Rms(a, 2205, a.Length).ToString("0"));

        var dv = Nsf.Render(nsf, 1, 0.5, 0);
        bool block = false;
        for (int p = dv.DataOffset; p < dv.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(dv.Data, p)))
            if (dv.Data[p] == 0x67 && dv.Data[p + 2] == 0xC2 && dv.Data[p + 7] == 0x00 && dv.Data[p + 8] == 0xC0 && dv.Data[p + 9] == 0x55) block = true;
        var used = VgmPlayer.ScanUsed(dv);
        var ch = VgmPlayer.ChannelsOf(dv);
        int dm = ch.FindIndex(c => c.Label == "DM");
        check("NSF (synthetic): DPCM memory → VGM data block 0xC2, DM used", block && dm >= 0 && used[dm], block + " " + dm);
    }

    // ------------------------------------------------------------ NSF 확장 음원 (만든 파일)

    /// <summary>init = 주어진 (주소, 값) 쓰기들, play = RTS. 코드는 $8000, FDS면 $8000도 RAM.</summary>
    private static byte[] MakeExpNsf(int exp, params (int addr, int v)[] writes)
    {
        var code = new List<byte>();
        foreach (var (addr, v) in writes) code.AddRange(new byte[] { 0xA9, (byte)v, 0x8D, (byte)addr, (byte)(addr >> 8) });
        code.Add(0x60);
        int play = code.Count;
        code.Add(0x60);
        var d = new byte[0x80 + code.Count];
        Encoding.ASCII.GetBytes("NESM").CopyTo(d, 0);
        d[5] = 1; d[6] = 1; d[7] = 1;
        d[8] = 0x00; d[9] = 0x80; d[10] = 0x00; d[11] = 0x80; d[12] = (byte)play; d[13] = 0x80;
        d[0x6E] = 0x1A; d[0x6F] = 0x41;
        d[0x7B] = (byte)exp;
        code.ToArray().CopyTo(d, 0x80);
        return d;
    }

    private static void ExpCheck(Action<string, bool, string> check, string name, byte[] nsf, string label, double hz, double tol)
    {
        var v = Nsf.Render(nsf, 0, 1, 0);
        var ch = VgmPlayer.ChannelsOf(v);
        var used = VgmPlayer.ScanUsed(v);
        int at = ch.FindIndex(c => c.Label == label);
        var p = new VgmPlayer(); p.Start(v, 0, null);
        var a = new double[Rate];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out _); a[i] = l; }
        double f = Freq(a, 4410, Rate), r = Rms(a, 4410, Rate);
        bool pitchOk = hz <= 0 || Math.Abs(f - hz) < tol;
        check("NSF " + name + ": " + label + " sounds" + (hz > 0 ? " at " + hz.ToString("0") + " Hz" : ""),
            at >= 0 && used[at] && r > 300 && pitchOk && v.UnsupportedChips.Count == 0,
            v.ChipSummary + ", " + string.Join(",", ch.Select(c => c.Label)) + ", f " + f.ToString("0.0") + ", rms " + r.ToString("0") + (v.UnsupportedChips.Count > 0 ? " [" + string.Join(",", v.UnsupportedChips) + "]" : ""));
    }

    private static void NsfExpansions(Action<string, bool, string> check)
    {
        // VRC6 사각파 (듀티 7 = 50%) 440Hz, 톱니 220Hz
        ExpCheck(check, "VRC6", MakeExpNsf(NsfExpansion.Vrc6, (0x9000, 0x7F), (0x9001, 253), (0x9002, 0x80)), "V1", 440, 5);
        ExpCheck(check, "VRC6 saw", MakeExpNsf(NsfExpansion.Vrc6, (0xB000, 42), (0xB001, 580 & 0xFF), (0xB002, 0x80 | (580 >> 8))), "VS", 220, 4);
        // N163: 32칸 네모 파형, 채널 8 하나, 440Hz
        var n = new List<(int, int)> { (0xF800, 0x80) };
        for (int i = 0; i < 16; i++) n.Add((0x4800, i < 8 ? 0xFF : 0x00));
        int freq = 7735;
        n.Add((0xF800, 0x80 | 0x78));
        n.AddRange(new (int, int)[] { (0x4800, freq & 0xFF), (0x4800, 0), (0x4800, (freq >> 8) & 0xFF), (0x4800, 0), (0x4800, 0xE0 | (freq >> 16)), (0x4800, 0), (0x4800, 0), (0x4800, 0x0F) });
        ExpCheck(check, "N163", MakeExpNsf(NsfExpansion.N163, n.ToArray()), "N1", 440, 5);
        // MMC5 사각파 440Hz
        ExpCheck(check, "MMC5", MakeExpNsf(NsfExpansion.Mmc5, (0x5015, 1), (0x5000, 0xBF), (0x5002, 253), (0x5003, 0x08)), "M1", 440, 5);
        // 선소프트 5B = YM2149: 주기 127 → 1789773 / (32 × 127) = 440.4Hz
        ExpCheck(check, "Sunsoft 5B", MakeExpNsf(NsfExpansion.Sunsoft5B, (0xC000, 7), (0xE000, 0x3E), (0xC000, 8), (0xE000, 15), (0xC000, 0), (0xE000, 127), (0xC000, 1), (0xE000, 0)), "A", 440.4, 5);
        // VRC7: 악기 1, 볼륨 최대, F-number 290 블록 4 ≈ 440Hz (FM이라 소리 높이는 채널 표시로)
        var v7 = Nsf.Render(MakeExpNsf(NsfExpansion.Vrc7, (0x9010, 0x30), (0x9030, 0x10), (0x9010, 0x10), (0x9030, 0x22), (0x9010, 0x20), (0x9030, 0x19)), 0, 1, 0);
        var ch7 = VgmPlayer.ChannelsOf(v7);
        var u7 = VgmPlayer.ScanUsed(v7);
        int k1 = ch7.FindIndex(c => c.Label == "K1");
        check("NSF VRC7: YM2413 in VRC7 mode, 6 channels, K1 used", v7.Vrc7 && k1 >= 0 && u7[k1] && ch7.Count(c => c.Label.StartsWith("K")) == 6 && !ch7.Any(c => c.Label == "BD"),
            v7.ChipSummary + ", " + string.Join(",", ch7.Select(c => c.Label)));
        ExpCheck(check, "VRC7", MakeExpNsf(NsfExpansion.Vrc7, (0x9010, 0x30), (0x9030, 0x10), (0x9010, 0x10), (0x9030, 0x22), (0x9010, 0x20), (0x9030, 0x19)), "K1", 0, 0);
        // FDS: 64칸 네모 파형, 직접 볼륨 32, 변조 멈춤, freq 1031 → 440Hz
        var fw = new List<(int, int)> { (0x4023, 0x83), (0x4089, 0x80) };
        for (int i = 0; i < 64; i++) fw.Add((0x4040 + i, i < 32 ? 63 : 0));
        fw.AddRange(new (int, int)[] { (0x4089, 0x00), (0x4080, 0x80 | 32), (0x4087, 0x80), (0x4082, 1031 & 0xFF), (0x4083, 1031 >> 8) });
        ExpCheck(check, "FDS", MakeExpNsf(NsfExpansion.Fds, fw.ToArray()), "FD", 440, 5);
        // 변조가 켜지면 소리 높이가 흔들린다 (같은 칸만 쓰면 변조 카운터가 계속 올라 높아진다)
        var fm = new List<(int, int)>(fw);
        fm.RemoveAt(fm.Count - 3);   // (0x4087, 0x80) 빼고
        for (int i = 0; i < 32; i++) fm.Add((0x4088, 1));
        fm.AddRange(new (int, int)[] { (0x4084, 0x80 | 16), (0x4086, 0x40), (0x4087, 0x00) });
        var mv = Nsf.Render(MakeExpNsf(NsfExpansion.Fds, fm.ToArray()), 0, 1, 0);
        var mp = new VgmPlayer(); mp.Start(mv, 0, null);
        var ma = new double[Rate];
        for (int i = 0; i < ma.Length; i++) { mp.Step(); mp.Generate(out double l, out _); ma[i] = l; }
        // 변조 카운터가 −64~63을 빠르게 돌아 비브라토가 된다: 20ms 창마다 소리 높이를 재어 폭을 본다
        double lo = double.MaxValue, hi = 0;
        for (int w0 = 4410; w0 + 882 <= Rate; w0 += 882) { double fw0 = Freq(ma, w0, w0 + 882); lo = Math.Min(lo, fw0); hi = Math.Max(hi, fw0); }
        check("NSF FDS: modulation bends pitch (vibrato)", Rms(ma, 2205, Rate) > 300 && hi - lo > 100, lo.ToString("0") + " ~ " + hi.ToString("0") + " Hz");
    }

    private static byte[] MakeHes(bool vblankOnly = false)
    {
        var rom = new List<byte>();
        void A(params int[] b) { foreach (var x in b) rom.Add((byte)x); }
        // init ($E000): 파형 채널 0 = 네모 440Hz, 타이머 IRQ 켜기
        A(0xA9, 0x00, 0x8D, 0x00, 0x08);            // 채널 0
        A(0xA9, 0xFF, 0x8D, 0x01, 0x08);            // 전체 좌우
        A(0xA9, 0x00, 0x8D, 0x04, 0x08);            // 끄고 파형 쓰기
        for (int i = 0; i < 32; i++) A(0xA9, i < 16 ? 31 : 0, 0x8D, 0x06, 0x08);
        A(0xA9, 0xFE, 0x8D, 0x02, 0x08, 0xA9, 0x00, 0x8D, 0x03, 0x08);
        A(0xA9, 0xFF, 0x8D, 0x05, 0x08, 0xA9, 0x9F, 0x8D, 0x04, 0x08);
        if (vblankOnly) A(0xA9, 0x05, 0x8D, 0x02, 0x14);   // 타이머 · IRQ2 막고 VDC (IRQ1)만 — VDC 레지스터 5는 안 씀
        else
        {
            A(0xA9, 0x07, 0x8D, 0x00, 0x0C);        // 타이머 8 × 1024
            A(0xA9, 0x01, 0x8D, 0x01, 0x0C);        // 켜기
            A(0xA9, 0x03, 0x8D, 0x02, 0x14);        // IRQ2 · VDC 막고 타이머만
        }
        A(0x58, 0x60);                              // CLI RTS
        while (rom.Count < 0x100) rom.Add(0xEA);
        // 타이머 IRQ ($E100): PHA  LDA #0  STA $0800  STA $1403  PLA  RTI  (VDC IRQ1도 여기: LDA $0000으로 VDC 상태 읽기)
        A(0x48, 0xAD, 0x00, 0x00, 0xA9, 0x00, 0x8D, 0x00, 0x08, 0x8D, 0x03, 0x14, 0x68, 0x40);
        while (rom.Count < 0x2000) rom.Add(0xFF);
        rom[0x1FFA] = 0x00; rom[0x1FFB] = 0xE1;
        rom[0x1FF8] = 0x00; rom[0x1FF9] = 0xE1;
        var d = new List<byte>();
        d.AddRange(Encoding.ASCII.GetBytes("HESM"));
        d.Add(0); d.Add(0); d.Add(0x00); d.Add(0xE0);
        d.AddRange(new byte[] { 0xFF, 0xF8, 0, 0, 0, 0, 0, 0 });
        d.AddRange(Encoding.ASCII.GetBytes("DATA"));
        d.AddRange(BitConverter.GetBytes(rom.Count)); d.AddRange(BitConverter.GetBytes(0)); d.AddRange(BitConverter.GetBytes(0));
        d.AddRange(rom);
        return d.ToArray();
    }

    private static void HesSynthetic(Action<string, bool, string> check)
    {
        var hes = MakeHes();
        var h = Hes.ReadHeader(hes);
        check("HES header: init, MPR", h.InitAddr == 0xE000 && h.Banks[0] == 0xFF && h.Banks[1] == 0xF8, h.InitAddr.ToString("X4"));
        var v = Hes.Render(hes, 0, 1, 0);
        int irq = 0;
        for (int p = v.DataOffset; p < v.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(v.Data, p))) if (v.Data[p] == 0xB9 && v.Data[p + 1] == 0) irq++;
        // 7159090 / 8192 ≈ 874 번/초
        check("HES (synthetic): timer IRQ runs driver (~874/s)", v.Huc6280Clock > 0 && irq > 800 && irq < 950 && v.UnsupportedChips.Count == 0, irq + " " + string.Join(",", v.UnsupportedChips));
        var p1 = new VgmPlayer(); p1.Start(v, 0, null);
        var a = new double[Rate];
        for (int i = 0; i < a.Length; i++) { p1.Step(); p1.Generate(out double l, out double r); a[i] = l; }
        double f = Freq(a, 4410, Rate);
        check("HES (synthetic): wave channel sounds at 440 Hz", Math.Abs(f - 440.4) < 5 && Rms(a, 4410, Rate) > 500, f.ToString("0.0"));
        check("HES probe: song sounds", Hes.Probe(hes, 0, 0.5), "");
        // hoot hes.cpp: VBlank IRQ1은 VDC 레지스터 5를 안 쓴 곡에도 (VDC 설정을 뺀 립)
        var vb = Hes.Render(MakeHes(true), 0, 1, 0);
        int irq1 = 0;
        for (int p = vb.DataOffset; p < vb.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(vb.Data, p))) if (vb.Data[p] == 0xB9 && vb.Data[p + 1] == 0) irq1++;
        check("HES (synthetic): VBlank IRQ1 runs even if VDC register 5 was never written (hoot) ≈ 60/s", irq1 >= 55 && irq1 <= 65, irq1.ToString());
    }

    // ------------------------------------------------------------ 실제 파일

    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        var files = dir != null ? Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".nsf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".hes", StringComparison.OrdinalIgnoreCase)).ToArray() : new string[0];
        if (files.Length == 0) { Console.WriteLine("SKIP  real NSF / HES (_incoming/*.nsf, *.hes not found)"); return; }
        foreach (var f in files.OrderBy(x => x))
        {
            var sw = Stopwatch.StartNew();
            var tracks = ChipSongs.Tracks(f);
            double listMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            int sounding = 0, checkedN = 0;
            string first = "";
            double loadOnly = 0;
            foreach (var t in tracks.Take(3))
            {
                var lw = Stopwatch.StartNew();
                var v = ChipSongs.Load(t.path);
                loadOnly += lw.Elapsed.TotalMilliseconds;
                var p = new VgmPlayer(); p.Start(v, 0, null);
                var a = new double[Rate * 8];
                for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
                double r0 = Rms(a, Rate, a.Length);
                var used = VgmPlayer.ScanUsed(v);
                int n = used.Count(u => u);
                checkedN++;
                if (r0 > 200 && n >= 2 && v.UnsupportedChips.Count == 0) sounding++;
                if (first.Length == 0) first = v.ChipSummary + " rms " + r0.ToString("0") + ", " + n + " ch, " + v.LengthSeconds.ToString("0") + " s" + (v.UnsupportedChips.Count > 0 ? " [" + string.Join(",", v.UnsupportedChips) + "]" : "");
            }
            double loadMs = loadOnly / Math.Max(1, checkedN);
            check("Real " + Path.GetFileName(f) + ": " + tracks.Count + " tracks, first 3 sound", tracks.Count > 1 && sounding == checkedN,
                first + "; list " + listMs.ToString("0") + " ms, load (120 s) " + loadMs.ToString("0") + " ms/track");
        }
    }
}
