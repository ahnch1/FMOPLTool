using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>DRO (DOSBox Raw OPL 0.1 · 2.0) · RAW (Rdos): OPL2 · OPL3 · 듀얼 OPL2, 시간 단위, 소리 높이 · 좌우.</summary>
public static class DroRawTests
{
    private const int Rate = 44100;

    /// <summary>채널 0에 사인파 하나: 블록 b, F-number 580 (블록 4 ≈ 440Hz, 블록 5 ≈ 880Hz).</summary>
    private static List<(int reg, int val)> Tone(int block)
    {
        return new List<(int, int)>
        {
            // 0x20 · 0x23의 bit5 = 지속형 엔벨로프 (없으면 공격 뒤 바로 놓음)
            (0x01, 0x20), (0x20, 0x21), (0x23, 0x21), (0x40, 0x3F), (0x43, 0x00), (0x60, 0xF0), (0x63, 0xF0),
            (0x80, 0x0F), (0x83, 0x0F), (0xC0, 0x00), (0xA0, 0x44), (0xB0, 0x20 | (block << 2) | 0x02),
        };
    }

    // ------------------------------------------------------------ 파일 만들기

    private static byte[] Dro1(IEnumerable<byte> data, int hw, bool fourByteHw)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("DBRAWOPL"));
        b.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00 });          // 판 0.1
        var body = data.ToList();
        b.AddRange(BitConverter.GetBytes(1000));                    // 길이 ms (쓰지 않음)
        b.AddRange(BitConverter.GetBytes(body.Count));
        if (fourByteHw) b.AddRange(BitConverter.GetBytes(hw)); else b.Add((byte)hw);
        b.AddRange(body);
        return b.ToArray();
    }

    /// <summary>판 0.1 기록: 레지스터 0~4는 4(탈출)로, 기다림은 1 (16비트).</summary>
    private static List<byte> Dro1Body(params object[] items)
    {
        var l = new List<byte>();
        foreach (var it in items)
        {
            if (it is int ms) { int n = ms - 1; l.AddRange(new byte[] { 1, (byte)n, (byte)(n >> 8) }); continue; }
            if (it is string chip) { l.Add((byte)(chip == "chip1" ? 3 : 2)); continue; }
            foreach (var (reg, val) in (List<(int, int)>)it)
            {
                if (reg <= 4) l.AddRange(new byte[] { 4, (byte)reg, (byte)val });
                else l.AddRange(new byte[] { (byte)reg, (byte)val });
            }
        }
        return l;
    }

    private static byte[] Dro2(int hw, List<(int chip, int reg, int val, int waitMs)> events)
    {
        var regs = events.Where(e => e.waitMs == 0).Select(e => e.reg).Distinct().ToList();
        const int shortCode = 0x7E, longCode = 0x7F;
        var pairs = new List<byte>();
        foreach (var e in events)
        {
            if (e.waitMs > 0)
            {
                int ms = e.waitMs;
                if (ms >= 256) { int hi = ms / 256; pairs.Add(longCode); pairs.Add((byte)(hi - 1)); ms -= hi * 256; }
                if (ms > 0) { pairs.Add(shortCode); pairs.Add((byte)(ms - 1)); }
                continue;
            }
            pairs.Add((byte)(regs.IndexOf(e.reg) | (e.chip == 1 ? 0x80 : 0)));
            pairs.Add((byte)e.val);
        }
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("DBRAWOPL"));
        b.AddRange(new byte[] { 0x02, 0x00, 0x00, 0x00 });          // 판 2.0
        b.AddRange(BitConverter.GetBytes(pairs.Count / 2));
        b.AddRange(BitConverter.GetBytes(1000));
        b.Add((byte)hw); b.Add(0); b.Add(0);
        b.Add(shortCode); b.Add(longCode); b.Add((byte)regs.Count);
        b.AddRange(regs.Select(r => (byte)r));
        b.AddRange(pairs);
        return b.ToArray();
    }

    // ------------------------------------------------------------ 재기

    private static void Render(VgmSong v, double sec, out double[] l, out double[] r)
    {
        var p = new VgmPlayer(); p.Start(v, 0, null);
        int n = (int)(sec * Rate);
        l = new double[n]; r = new double[n];
        for (int i = 0; i < n; i++) { p.Step(); p.Generate(out l[i], out r[i]); }
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
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    public static void Run(Action<string, bool, string> check)
    {
        var off = new List<(int, int)> { (0xB0, 0x12) };

        // ---- DRO 0.1, 하드웨어 4바이트, OPL2: 1초 소리 → 끄고 0.1초
        var v1 = AdlibStreamFormats.ReadDro(Dro1(Dro1Body(Tone(4), 1000, off, 100), 0, true), "t");
        Render(v1, 1, out var l1, out _);
        double f1 = Freq(l1, 4410, Rate);
        check("DRO 0.1: OPL2, ms delays, escaped reg 0x01", v1.Ym3812Clock > 0 && !v1.IsOpl3 && Math.Abs(v1.LengthSeconds - 1.6) < 0.02 && Math.Abs(f1 - 440) < 3,
            v1.SourceFormat + ", " + v1.LengthSeconds.ToString("0.000") + " s, " + f1.ToString("0.0") + " Hz");

        // ---- DRO 0.1, 하드웨어 1바이트 (옛 파일): 첫 데이터 바이트가 0이 아니어야 구별된다
        var tone1 = Tone(4).Where(x => x.reg != 0x01).ToList();   // 탈출(4) 없이 시작
        var v1b = AdlibStreamFormats.ReadDro(Dro1(Dro1Body(tone1, 500), 0, false), "t");
        Render(v1b, 0.5, out var l1b, out _);
        check("DRO 0.1: one-byte hardware type (early files)", Math.Abs(v1b.LengthSeconds - 1.0) < 0.02 && Rms(l1b, 4410, l1b.Length) > 500, v1b.LengthSeconds.ToString("0.000") + " s");

        // ---- DRO 2.0 듀얼 OPL2: 칩 0 = 440Hz 왼쪽, 칩 1 = 880Hz 오른쪽
        var ev = new List<(int, int, int, int)>();
        foreach (var (reg, val) in Tone(4)) ev.Add((0, reg, val, 0));
        foreach (var (reg, val) in Tone(5)) ev.Add((1, reg, val, 0));
        ev.Add((0, 0, 0, 1000));
        var v2 = AdlibStreamFormats.ReadDro(Dro2(1, ev), "t");
        Render(v2, 1, out var l2, out var r2);
        double fl = Freq(l2, 4410, Rate), fr = Freq(r2, 4410, Rate);
        check("DRO 2.0 dual OPL2 → OPL3: chip 0 left, chip 1 right", v2.IsOpl3 && Math.Abs(fl - 440) < 3 && Math.Abs(fr - 880) < 5 && Math.Abs(v2.LengthSeconds - 1.5) < 0.02,
            v2.SourceFormat + ", L " + fl.ToString("0.0") + " / R " + fr.ToString("0.0") + " Hz, " + v2.LengthSeconds.ToString("0.000") + " s");

        // ---- DRO 2.0 OPL3 (칩 0만): OPL3 칩, 양쪽 같은 소리, 긴 기다림 코드 (1.3초 = 256×5 + 20)
        var e3 = new List<(int, int, int, int)>();
        foreach (var (reg, val) in Tone(4)) e3.Add((0, reg, val, 0));
        e3.Add((0, 0, 0, 1300));
        var v3 = AdlibStreamFormats.ReadDro(Dro2(2, e3), "t");
        Render(v3, 1, out var l3, out var r3);
        check("DRO 2.0 OPL3: YMF262, long delay code", v3.IsOpl3 && Math.Abs(v3.LengthSeconds - 1.8) < 0.02 && Math.Abs(Freq(l3, 4410, Rate) - 440) < 3 && Rms(r3, 4410, Rate) > 500,
            v3.SourceFormat + ", " + v3.LengthSeconds.ToString("0.000") + " s");

        // ---- DRO 2.0 OPL2 (칩 0만) → OPL2
        var v4 = AdlibStreamFormats.ReadDro(Dro2(0, e3), "t");
        check("DRO 2.0 OPL2 stays OPL2", !v4.IsOpl3 && v4.Ym3812Clock > 0, v4.ChipSummary);

        // ---- RAW: 클럭 1193 (≈1000Hz 틱) → 250틱 × 4 = 1초, 클럭을 11932 (≈100Hz)로 바꾸고 10틱 = 0.1초, 끝
        var raw = new List<byte>();
        raw.AddRange(Encoding.ASCII.GetBytes("RAWADATA"));
        raw.Add(1193 & 0xFF); raw.Add(1193 >> 8);
        foreach (var (reg, val) in Tone(4)) { raw.Add((byte)val); raw.Add((byte)reg); }
        for (int i = 0; i < 4; i++) { raw.Add(250); raw.Add(0); }
        raw.Add(0); raw.Add(2); raw.Add(11932 & 0xFF); raw.Add(11932 >> 8);
        raw.Add(0x12); raw.Add(0xB0);
        raw.Add(10); raw.Add(0);
        raw.Add(0xFF); raw.Add(0xFF);
        raw.Add(0x00); raw.Add(0xB0);                                 // 끝 뒤: 읽지 않아야 함
        var vr = AdlibStreamFormats.ReadRaw(raw.ToArray(), "t");
        Render(vr, 1, out var lr, out _);
        double expect = 1000 * 1193 / 1193180.0 + 10 * 11932 / 1193180.0 + 0.5;
        check("RAW: ticks from clock, clock change, end marker", !vr.IsOpl3 && Math.Abs(vr.LengthSeconds - expect) < 0.01 && Math.Abs(Freq(lr, 4410, Rate) - 440) < 3,
            vr.LengthSeconds.ToString("0.000") + " / " + expect.ToString("0.000") + " s");

        // ---- RAW 칩 바꾸기 (값 2 = 칩 1) → 듀얼
        var raw2 = new List<byte>(raw.Take(10));
        foreach (var (reg, val) in Tone(4)) { raw2.Add((byte)val); raw2.Add((byte)reg); }
        raw2.Add(2); raw2.Add(2);
        foreach (var (reg, val) in Tone(5)) { raw2.Add((byte)val); raw2.Add((byte)reg); }
        raw2.Add(250); raw2.Add(0);
        var vr2 = AdlibStreamFormats.ReadRaw(raw2.ToArray(), "t");
        Render(vr2, 0.25, out var lr2, out var rr2);
        check("RAW: chip switch → dual OPL2 (left / right)", vr2.IsOpl3 && Math.Abs(Freq(lr2, 2205, lr2.Length) - 440) < 6 && Math.Abs(Freq(rr2, 2205, rr2.Length) - 880) < 8, vr2.SourceFormat);

        // ---- 틀린 파일 · ChipSongs 등록
        bool threw = false;
        try { AdlibStreamFormats.ReadDro(Encoding.ASCII.GetBytes("NOTADROFILE_____________________"), "x"); } catch (InvalidDataException) { threw = true; }
        bool threwRaw = false;
        try { AdlibStreamFormats.ReadRaw(new byte[16], "x"); } catch (InvalidDataException) { threwRaw = true; }
        check("DRO / RAW: wrong header rejected, extensions registered", threw && threwRaw && ChipSongs.IsChipSong("a.DRO") && ChipSongs.IsChipSong("b.raw"), "");

        string dir = Path.Combine(Path.GetTempPath(), "fmopl_dro_test");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "song.dro");
        File.WriteAllBytes(f, Dro2(0, e3));
        try
        {
            var lv = ChipSongs.Load(f);
            check("DRO: ChipSongs.Load (Jukebox path)", lv.FilePath == f && lv.Title == "song" && lv.SourceFormat.StartsWith("DRO 2.0"), lv.Title + " / " + lv.SourceFormat);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
