using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// modizer 대조로 더한 형식 (docs/modizer-plan.md): 만든 파일 + 실제 표본 (D:\Retro\fmopldata\Modizer, 없으면 건너뜀 —
/// 환경 변수 FMOPL_MODIZER_SAMPLES로 바꿀 수 있음).
/// </summary>
public static class ModizerFormatTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Gym(check);
        NsfeTest(check);
        SapTest(check);
        KssDriverTest(check);
        SgcTest(check);
    }

    internal static string SampleDir()
    {
        string env = Environment.GetEnvironmentVariable("FMOPL_MODIZER_SAMPLES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        const string d = @"D:\Retro\fmopldata\Modizer";
        return Directory.Exists(d) ? d : null;
    }

    internal static string FirstSample(string pattern)
    {
        string dir = SampleDir();
        if (dir == null) return null;
        var f = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
        Array.Sort(f, StringComparer.OrdinalIgnoreCase);
        return f.Length > 0 ? f[0] : null;
    }

    internal static double[] Render(VgmSong s, double sec, int skip = 0)
    {
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        for (int i = 0; i < skip; i++) { p.Step(); p.Generate(out _, out _); }
        var o = new double[(int)(sec * 44100)];
        for (int i = 0; i < o.Length; i++) { p.Step(); p.Generate(out double l, out double r); o[i] = (l + r) / 2; }
        return o;
    }

    internal static double Rms(double[] a)
    {
        double s = 0;
        foreach (var x in a) s += x * x;
        return a.Length > 0 ? Math.Sqrt(s / a.Length) : 0;
    }

    // ------------------------------------------------------------ M1-1 GYM

    private static void Gym(Action<string, bool, string> check)
    {
        // 만든 GYM: GYMX 머리말 + YM2612 채널 1 음 하나 + SN76489 음 + DAC 몇 개, 120프레임, 루프 = 프레임 61
        var body = new List<byte>();
        void Fm(int port, int r, int v) { body.Add((byte)(port + 1)); body.Add((byte)r); body.Add((byte)v); }
        // 채널 1 (포트 0, 채널 0): 알고리즘 7, 모든 오퍼레이터 TL 0 · AR 31 · RR 15, 주파수, 키 온
        Fm(0, 0xB0, 0x07); Fm(0, 0xB4, 0xC0);
        foreach (int op in new[] { 0, 4, 8, 12 })
        {
            Fm(0, 0x30 + op, 0x01); Fm(0, 0x40 + op, 0x00); Fm(0, 0x50 + op, 0x1F);
            Fm(0, 0x60 + op, 0x00); Fm(0, 0x70 + op, 0x00); Fm(0, 0x80 + op, 0x0F);
        }
        Fm(0, 0xA4, 0x22); Fm(0, 0xA0, 0x69); Fm(0, 0x28, 0xF0);
        body.Add(3); body.Add(0x8E); body.Add(3); body.Add(0x0F); body.Add(3); body.Add(0x90);   // SN 톤 0: 주기 0xFE, 음량 최대
        for (int f = 0; f < 120; f++)
        {
            if (f == 10) { Fm(0, 0x2B, 0x80); for (int k = 0; k < 8; k++) Fm(0, 0x2A, 0x80 + (k & 1) * 0x60); }
            body.Add(0);
        }
        var d = new byte[GymFormat.HeaderSize + body.Count];
        "GYMX"u8.ToArray().CopyTo(d, 0);
        "Test Song"u8.ToArray().CopyTo(d, 4);
        "Test Game"u8.ToArray().CopyTo(d, 0x24);
        BitConverter.GetBytes(61u).CopyTo(d, 0x1A4);
        body.CopyTo(d, GymFormat.HeaderSize);

        var v = GymFormat.Read(d, "x");
        check("GYM: header title / game", v.TitleEn == "Test Song" && v.GameEn == "Test Game", v.TitleEn + " / " + v.GameEn);
        check("GYM: length 120 frames = 2 s", v.TotalSamples == 120 * 735, v.TotalSamples.ToString());
        check("GYM: loop at frame 61 (60 frames)", v.LoopSamples == 60 * 735, v.LoopSamples.ToString());
        check("GYM: YM2612 + SN76489 clocks", v.Ym2612Clock == GymFormat.Ym2612Clock && v.Sn76489Clock == GymFormat.Sn76489Clock, v.Ym2612Clock + " / " + v.Sn76489Clock);
        double r = Rms(Render(v, 1));
        check("GYM: renders sound", r > 100, r.ToString("0"));

        // 머리말 없는 GYM (예전 형식) · zlib 압축 GYMX
        var raw = GymFormat.Read(body.ToArray(), "Raw");
        check("GYM: headerless body", raw.TotalSamples == 120 * 735 && raw.LoopSamples == 0 && raw.TitleEn == "Raw", raw.TotalSamples + " / " + raw.LoopSamples);
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(body.ToArray());
        var packed = new byte[GymFormat.HeaderSize + ms.Length];
        Array.Copy(d, packed, GymFormat.HeaderSize);
        BitConverter.GetBytes((uint)body.Count).CopyTo(packed, 0x1A8);
        ms.ToArray().CopyTo(packed, GymFormat.HeaderSize);
        var pv = GymFormat.Read(packed);
        check("GYM: zlib packed = same length", pv.TotalSamples == v.TotalSamples && pv.SourceFormat.Contains("압축"), pv.SourceFormat);
        check("GYM: ChipSongs knows .gym", ChipSongs.Extensions.Contains(".gym"), "");

        string real = FirstSample("*.gym");
        if (real == null) { Console.WriteLine("SKIP  GYM real file (sample folder not found)"); return; }
        var rv = ChipSongs.Load(real);
        double rr = Rms(Render(rv, 3, 44100));
        check("GYM real " + Path.GetFileName(real) + ": loads, sounds", rv.LengthSeconds > 10 && rr > 100, rv.LengthSeconds.ToString("0") + " s, rms " + rr.ToString("0") + ", " + rv.SourceFormat);
    }

    // ------------------------------------------------------------ M1-2 NSFE

    internal static byte[] Chunk(string id, byte[] data)
    {
        var o = new byte[8 + data.Length];
        BitConverter.GetBytes(data.Length).CopyTo(o, 0);
        System.Text.Encoding.ASCII.GetBytes(id).CopyTo(o, 4);
        data.CopyTo(o, 8);
        return o;
    }

    private static void NsfeTest(Action<string, bool, string> check)
    {
        var nsf = NesPceTests.MakeNsf();
        var body = new byte[nsf.Length - 0x80];
        Array.Copy(nsf, 0x80, body, 0, body.Length);
        var info = new byte[] { 0x00, 0x80, 0x00, 0x80, 0x40, 0x80, 0, 0, 2, 0 };
        var time = new List<byte>(); time.AddRange(BitConverter.GetBytes(1500)); time.AddRange(BitConverter.GetBytes(-1));
        var fade = new List<byte>(); fade.AddRange(BitConverter.GetBytes(500)); fade.AddRange(BitConverter.GetBytes(-1));
        var f = new List<byte>(System.Text.Encoding.ASCII.GetBytes("NSFE"));
        f.AddRange(Chunk("INFO", info));
        f.AddRange(Chunk("auth", System.Text.Encoding.UTF8.GetBytes("Test Game\0Tester\0(C) 1990\0Ripper\0")));
        f.AddRange(Chunk("tlbl", System.Text.Encoding.UTF8.GetBytes("Pulse\0Ｄ ＭＣ\0")));
        f.AddRange(Chunk("time", time.ToArray()));
        f.AddRange(Chunk("fade", fade.ToArray()));
        f.AddRange(Chunk("plst", new byte[] { 1, 0 }));
        f.AddRange(Chunk("DATA", body));
        f.AddRange(Chunk("NEND", new byte[0]));
        var ni = Nsfe.Read(f.ToArray());
        var tracks = Nsfe.Tracks(ni, "x");
        check("NSFE: chunks → songs 2, auth, labels (UTF-8)", ni.Songs == 2 && ni.Game == "Test Game" && ni.Artist == "Tester" && ni.Ripper == "Ripper" && ni.Labels.Count == 2 && ni.Labels[1] == "Ｄ ＭＣ", ni.Game + " / " + string.Join("|", ni.Labels));
        check("NSFE: playlist order 1, 0 + time / fade per song", tracks.Count == 2 && tracks[0].Song == 1 && tracks[1].Song == 0 && tracks[1].Seconds == 1.5 && tracks[1].Fade == 0.5 && tracks[0].Seconds == Nsf.DefaultSeconds,
            string.Join(", ", tracks.Select(t => t.Song + ":" + t.Title + ":" + t.Seconds + "+" + t.Fade)));
        check("NSFE: built NSF header = same code / addresses", Nsf.ReadHeader(ni.Nsf).InitAddr == 0x8000 && Nsf.ReadHeader(ni.Nsf).PlayAddr == 0x8040 && ni.Nsf.Length == nsf.Length, "");

        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_nsfe_test.nsfe");
        File.WriteAllBytes(tmp, f.ToArray());
        try
        {
            var list = ChipSongs.Tracks(tmp);
            var v = ChipSongs.Load(list[1].path);
            double r = Rms(Render(v, 1));
            check("NSFE: ChipSongs tracks + load song 1 (1.5 s + 0.5 s fade), sounds", list.Count == 2 && list[1].title == "Pulse" && Math.Abs(v.LengthSeconds - 2.0) < 0.05 && r > 300 && v.TitleEn == "Pulse" && v.SourceFormat.StartsWith("NSFE"),
                list.Count + " " + v.LengthSeconds.ToString("0.00") + " s, rms " + r.ToString("0") + ", " + v.SourceFormat);
        }
        finally { File.Delete(tmp); }
        // 모르는 필수 청크 → 오류
        var bad = new List<byte>(System.Text.Encoding.ASCII.GetBytes("NSFE"));
        bad.AddRange(Chunk("INFO", info)); bad.AddRange(Chunk("ZZZZ", new byte[2])); bad.AddRange(Chunk("DATA", body));
        bool threw = false;
        try { Nsfe.Read(bad.ToArray()); } catch (InvalidDataException) { threw = true; }
        check("NSFE: unknown required (upper-case) chunk rejected", threw, "");
    }

    // ------------------------------------------------------------ M1-3 SAP

    internal static byte[] MakeSap(string tags, int start, byte[] code)
    {
        var f = new List<byte>(System.Text.Encoding.ASCII.GetBytes("SAP\r\n" + tags.Replace("\n", "\r\n")));
        f.Add(0xFF); f.Add(0xFF);
        int end = start + code.Length - 1;
        f.Add((byte)start); f.Add((byte)(start >> 8)); f.Add((byte)end); f.Add((byte)(end >> 8));
        f.AddRange(code);
        return f.ToArray();
    }

    private static int CountWrites(VgmSong v, int reg)
    {
        int n = 0;
        for (int p = v.DataOffset; p < v.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(v.Data, p)))
            if (v.Data[p] == 0xBB && v.Data[p + 1] == reg) n++;
        return n;
    }

    private static void SapTest(Action<string, bool, string> check)
    {
        // TYPE B: INIT $2000 = 채널 1 순음 음량 8 + 곡 번호를 $81에 / PLAYER $2010 = $80을 늘려 AUDF1에
        var code = new byte[0x20];
        new byte[] { 0x85, 0x81, 0xA9, 0xA8, 0x8D, 0x01, 0xD2, 0xA9, 0x40, 0x8D, 0x00, 0xD2, 0x60 }.CopyTo(code, 0);
        new byte[] { 0xE6, 0x80, 0xA5, 0x80, 0x8D, 0x00, 0xD2, 0x60 }.CopyTo(code, 0x10);
        var b = MakeSap("AUTHOR \"Me\"\nNAME \"Test Tune\"\nDATE \"1987\"\nSONGS 2\nTYPE B\nINIT 2000\nPLAYER 2010\nTIME 00:01.500\nTIME 00:02.000 LOOP\n", 0x2000, code);
        var h = Sap.ReadHeader(b);
        var tr = Sap.Tracks(h, "x");
        check("SAP: header tags (B, INIT / PLAYER, songs 2)", h.Type == 'B' && h.Init == 0x2000 && h.Player == 0x2010 && h.Songs == 2 && h.Name == "Test Tune" && h.Author == "Me" && h.Date == "1987" && !h.Stereo && !h.Ntsc, h.Name);
        check("SAP: TIME per song (no LOOP = ends, LOOP = + fade)", tr.Count == 2 && tr[0].Seconds == 1.5 && !tr[0].Loop && tr[1].Loop && tr[1].Seconds == 2 + Sap.DefaultFade, string.Join(", ", tr.Select(t => t.Seconds + (t.Loop ? " loop" : ""))));
        var v = Sap.Render(b, 1, 1, 0);
        int plays = CountWrites(v, 0x00);
        check("SAP B: PLAYER every PAL frame (≈ 50 / s) → POKEY VGM", v.PokeyClock == Sap.PalClock && plays >= 49 && plays <= 52 && v.UnsupportedChips.Count == 0, plays + " AUDF1 writes, " + string.Join(",", v.UnsupportedChips));
        double r = Rms(Render(v, 0.5));
        check("SAP B: sounds", r > 200, r.ToString("0"));

        // NTSC + FASTPLAY 156 (= 주사선 156개마다, 두 배 빠르게)
        var bn = MakeSap("TYPE B\nNTSC\nFASTPLAY 131\nINIT 2000\nPLAYER 2010\n", 0x2000, code);
        int pn = CountWrites(Sap.Render(bn, 0, 1, 0), 0x00);
        check("SAP B NTSC FASTPLAY 131: ≈ 120 plays / s", pn >= 118 && pn <= 122, pn.ToString());

        // TYPE C: PLAYER+3 = init (A=$70 → MUSIC 주소를 $82/$83에, A=0 → X=곡), PLAYER+6 = play
        var cc = new byte[0x40];
        new byte[] { 0x4C, 0x20, 0x30, 0x4C, 0x10, 0x30, 0x4C, 0x00, 0x30 }.CopyTo(cc, 0);                        // $3000+0 · +3 · +6 → 아래
        // $3000+0x10 (init): CMP #$70 BNE + / STX $82 STY $83 RTS / +: LDA #$A8 STA $D201 RTS
        new byte[] { 0xC9, 0x70, 0xD0, 0x05, 0x86, 0x82, 0x84, 0x83, 0x60, 0xA9, 0xA8, 0x8D, 0x01, 0xD2, 0x60 }.CopyTo(cc, 0x10);
        new byte[] { 0x60 }.CopyTo(cc, 0x20);
        var cBytes = MakeSap("TYPE C\nMUSIC 4000\nPLAYER 3000\n", 0x3000, cc);
        // PLAYER+6 → JMP $3000 → 그 자리에 play 코드가 없으니 바꿔 넣음: $3000+0x30 = INC $80 LDA $80 STA $D200 RTS, +6 = JMP $3030
        cc[7] = 0x30; cc[8] = 0x30;
        new byte[] { 0xE6, 0x80, 0xA5, 0x80, 0x8D, 0x00, 0xD2, 0x60 }.CopyTo(cc, 0x30);
        cBytes = MakeSap("TYPE C\nMUSIC 4000\nPLAYER 3000\n", 0x3000, cc);
        var cv = Sap.Render(cBytes, 0, 1, 0);
        int cp = CountWrites(cv, 0x00);
        check("SAP C: init (A=$70, X/Y = MUSIC) + PLAYER+6 each frame", cp >= 49 && cp <= 52 && Rms(Render(cv, 0.5)) > 200, cp.ToString());

        // TYPE D: INIT은 돌아오지 않는 고리 (음량을 세우고 JMP *), PLAYER는 인터럽트처럼 (A · X · Y가 고리에서 그대로여야)
        var dc = new byte[0x30];
        // $5000: LDA #$A8 STA $D201 LDX #$33 / loop: CPX #$33 BNE bad / JMP loop / bad: STX $90 JMP bad
        new byte[] { 0xA9, 0xA8, 0x8D, 0x01, 0xD2, 0xA2, 0x33, 0xE0, 0x33, 0xD0, 0x03, 0x4C, 0x07, 0x50, 0x86, 0x90, 0x4C, 0x0E, 0x50 }.CopyTo(dc, 0);
        // $5020 PLAYER: LDX #$00 INC $80 LDA $80 STA $D200 RTS (X를 망가뜨려도 틀이 되돌림)
        new byte[] { 0xA2, 0x00, 0xE6, 0x80, 0xA5, 0x80, 0x8D, 0x00, 0xD2, 0x60 }.CopyTo(dc, 0x20);
        var dv = Sap.Render(MakeSap("TYPE D\nINIT 5000\nPLAYER 5020\n", 0x5000, dc), 0, 1, 0);
        int dp = CountWrites(dv, 0x00);
        check("SAP D: endless INIT + PLAYER as interrupt (registers kept)", dp >= 49 && dp <= 52 && dv.UnsupportedChips.Count == 0 && Rms(Render(dv, 0.5)) > 200, dp + " " + string.Join(",", dv.UnsupportedChips));

        // STEREO: 둘째 POKEY ($D210~)만 소리 → 오른쪽만
        var sc = new byte[0x20];
        new byte[] { 0xA9, 0xAF, 0x8D, 0x11, 0xD2, 0xA9, 0x30, 0x8D, 0x10, 0xD2, 0x60 }.CopyTo(sc, 0);
        sc[0x10] = 0x60;
        var sv = Sap.Render(MakeSap("TYPE B\nSTEREO\nINIT 2000\nPLAYER 2010\n", 0x2000, sc), 0, 1, 0);
        var sp = new VgmPlayer(); sp.Start(sv, 0, null);
        double sl = 0, sr = 0;
        for (int i = 0; i < 22050; i++) { sp.Step(); sp.Generate(out double l, out double rr); sl += l * l; sr += rr * rr; }
        check("SAP STEREO: 2nd POKEY ($D210) → right only", sv.PokeyClock2 > 0 && sv.PokeyStereo && sr > 1e6 && sl < sr / 100, sv.ChipSummary + ", L " + Math.Sqrt(sl / 22050).ToString("0") + " R " + Math.Sqrt(sr / 22050).ToString("0"));

        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_sap_test.sap");
        File.WriteAllBytes(tmp, b);
        try
        {
            var list = ChipSongs.Tracks(tmp);
            var lv = ChipSongs.Load(list[0].path);
            check("SAP: ChipSongs 2 songs, song 1 = 1.5 s (TIME)", list.Count == 2 && Math.Abs(lv.LengthSeconds - 1.5) < 0.05 && lv.SourceFormat.StartsWith("SAP B"), list.Count + " " + lv.LengthSeconds.ToString("0.00") + " " + lv.SourceFormat);
        }
        finally { File.Delete(tmp); }
    }

    // ------------------------------------------------------------ M1-4 MGS · BGM · MPK · OPX (KSS 드라이버 틀)

    /// <summary>KSSX (RAM, 곡 0) 이미지: 코드를 load부터.</summary>
    private static byte[] MakeKss(int load, byte[] code, int init, int play)
    {
        var b = new byte[0x20 + code.Length];
        System.Text.Encoding.ASCII.GetBytes("KSSX").CopyTo(b, 0);
        b[4] = (byte)load; b[5] = (byte)(load >> 8); b[6] = (byte)code.Length; b[7] = (byte)(code.Length >> 8);
        b[8] = (byte)init; b[9] = (byte)(init >> 8); b[10] = (byte)play; b[11] = (byte)(play >> 8);
        b[0x0E] = 0x10; b[0x0F] = 0x05;
        code.CopyTo(b, 0x20);
        return b;
    }

    private static void KssDriverTest(Action<string, bool, string> check)
    {
        // 확장 I/O 끝 알림: init = PSG 톤 A 켜기 + OUT (40h),7Fh / play = 프레임 셈 ($C000), 120프레임(2초)이 되면 OUT (41h),1
        var code = new byte[0x200];
        new byte[]
        {
            0x3E, 0x07, 0xD3, 0xA0, 0x3E, 0x3E, 0xD3, 0xA1,   // R7 = 3Eh (톤 A만)
            0x3E, 0x08, 0xD3, 0xA0, 0x3E, 0x0F, 0xD3, 0xA1,   // R8 = 15
            0x3E, 0x00, 0xD3, 0xA0, 0x3E, 0x80, 0xD3, 0xA1,   // R0 = 80h
            0x3E, 0x7F, 0xD3, 0x40, 0xAF, 0xD3, 0x41, 0xD3, 0x42,
            0xC9,
        }.CopyTo(code, 0);
        // play ($4100): LD HL,($C000) INC HL LD ($C000),HL LD A,L CP 120 RET NZ LD A,1 OUT (41h),A RET
        new byte[] { 0x2A, 0x00, 0xC0, 0x23, 0x22, 0x00, 0xC0, 0x7D, 0xFE, 120, 0xC0, 0x3E, 0x01, 0xD3, 0x41, 0xC9 }.CopyTo(code, 0x100);
        var stopKss = MakeKss(0x4000, code, 0x4000, 0x4100);
        var sv = Kss.Render(stopKss, 0, 120, 4, 60, 2);
        check("KSS ext I/O 41h stop → length = stop + 1 s", Math.Abs(sv.LengthSeconds - 3.0) < 0.1 && Rms(Render(sv, 1)) > 100, sv.LengthSeconds.ToString("0.00") + " s");
        // 되풀이 횟수: 60프레임마다 OUT (42h),n → 2번째 (2초) + 페이드 4초
        var loopCode = (byte[])code.Clone();
        // play: $C000 = 프레임 (60에서 0으로), $C001 = 초 → OUT (42h)
        new byte[] { 0x21, 0x00, 0xC0, 0x34, 0x7E, 0xFE, 60, 0x20, 0x04, 0x36, 0x00, 0x23, 0x34, 0x3A, 0x01, 0xC0, 0xD3, 0x42, 0xC9 }.CopyTo(loopCode, 0x100);
        var lv = Kss.Render(MakeKss(0x4000, loopCode, 0x4000, 0x4100), 0, 120, 4, 60, 2);
        check("KSS ext I/O 42h loop count 2 → length = loop + fade", Math.Abs(lv.LengthSeconds - 6.0) < 0.2, lv.LengthSeconds.ToString("0.00") + " s");
        var nv = Kss.Render(MakeKss(0x4000, code.Take(0x100).Concat(new byte[] { 0xC9 }).ToArray(), 0x4000, 0x4100), 0, 3, 1, 10, 2);
        check("KSS ext I/O: nothing reported → given length", Math.Abs(nv.LengthSeconds - 3.0) < 0.05, nv.LengthSeconds.ToString("0.00") + " s");

        check("KSS drivers: ChipSongs knows .mgs .bgm .mpk .opx", new[] { ".mgs", ".bgm", ".mpk", ".opx" }.All(e => ChipSongs.Extensions.Contains(e)), "");
        var hb = KssDrivers.ParseCHeader("/* MGSDRV 0x99 */\n0xc3,0x9d, // 0x11\n0x18,0x4D\n");
        check("KSS drivers: C header parse skips comments", hb.Length == 4 && hb[0] == 0xC3 && hb[3] == 0x4D, string.Join(" ", hb.Select(x => x.ToString("X2"))));

        // 드라이버 없으면 알기 쉬운 오류
        var saved = KssDrivers.Folders.ToList();
        KssDrivers.Folders.Clear();
        KssDrivers.Folders.Add(Path.Combine(Path.GetTempPath(), "fmopl_no_such_dir"));
        bool missing = false;
        try { KssDrivers.Mgs(System.Text.Encoding.ASCII.GetBytes("MGS314\r\n\x1aNo driver\r\n".PadRight(64, '\0'))); }
        catch (FileNotFoundException e) { missing = e.Message.Contains("mgsdrv"); }
        check("KSS drivers: missing driver → FileNotFoundException naming the file", missing, "");
        KssDrivers.Folders.Clear();
        KssDrivers.Folders.AddRange(saved);

        // 목록 제목은 드라이버 없이 머리말에서 (돌리지 않음)
        string tmg = Path.Combine(Path.GetTempPath(), "fmopl_title_test.mgs");
        File.WriteAllBytes(tmg, System.Text.Encoding.ASCII.GetBytes("MGS314\r\nQuick\tTitle\r\n\x1a".PadRight(64, '\0')));
        try { check("MGS: ChipSongs.Title from header (no driver run)", ChipSongs.Title(tmg) == "Quick Title", ChipSongs.Title(tmg)); }
        finally { File.Delete(tmg); }

        // libkss kss-drivers 헤더 (modizer 참고 소스가 있으면)
        string inc = SopTests.FindDir("_incoming");
        string drvDir = inc == null ? null : Path.Combine(inc, "modizer-master", "libs", "libkss", "libkss", "modules", "drivers");
        if (drvDir == null || !Directory.Exists(drvDir)) { Console.WriteLine("SKIP  KSS drivers from libkss headers (_incoming/modizer-master not found)"); return; }
        KssDrivers.Folders.Insert(0, drvDir);
        try
        {
            var mgsdrv = KssDrivers.Find(KssDrivers.Driver.Mgsdrv);
            check("KSS drivers: mgsdrv.h → MGSDRV.COM bytes", mgsdrv != null && mgsdrv.Length > 0x1000 && System.Text.Encoding.ASCII.GetString(mgsdrv, 3, 6) == "MGSDRV", mgsdrv == null ? "null" : mgsdrv.Length.ToString());
            var all = Enum.GetValues(typeof(KssDrivers.Driver)).Cast<KssDrivers.Driver>().Select(d => (d, b: KssDrivers.Find(d))).ToList();
            check("KSS drivers: all 5 headers found", all.All(x => x.b != null && x.b.Length > 0x800), string.Join(", ", all.Select(x => x.d + " " + (x.b?.Length ?? 0))));
            var fake = new byte[0x400];
            System.Text.Encoding.ASCII.GetBytes("MGS314\r\nTest\tTitle\r\n\x1a").CopyTo(fake, 0);
            var c = KssDrivers.Mgs(fake);
            var h = Kss.ReadHeader(c.Kss);
            check("MGS → KSS layout (load 200h, init 8000h, play 8100h, MGSDRV+0Dh at 6000h)", h.LoadAddr == 0x200 && h.InitAddr == 0x8000 && h.PlayAddr == 0x8100 && c.Kss[0x20 + 0x6000 - 0x200] == mgsdrv[0x0D] && c.Title == "Test Title" && h.FmPac,
                c.Title + " / " + c.Format);
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var v = Kss.Render(c.Kss, 0, 2, 1, 10, 2);   // 잘못된 곡 자료: 멈추지 않고 끝나기만
            check("MGS → KSS: driver runs (bad song data, no hang)", v.LengthSeconds > 0.5 && t0.ElapsedMilliseconds < 20000, v.LengthSeconds.ToString("0.0") + " s, " + t0.ElapsedMilliseconds + " ms");
            var mp = new byte[0x200];
            System.Text.Encoding.ASCII.GetBytes("MPK106\r\nSong\r\nComposer X\r\nArr Y\r\nMemo\r\n").CopyTo(mp, 0);
            var mc = KssDrivers.Mpk(mp);
            var mh = Kss.ReadHeader(mc.Kss);
            check("MPK 1.06 → KSS (driver 4000h, song 200h patched into init)", mc.Driver == KssDrivers.Driver.Mpk106 && mh.InitAddr == 0x6000 && mc.Kss[0x20 + 0x6000 - 0x200 + 14] == 0x00 && mc.Kss[0x20 + 0x6000 - 0x200 + 15] == 0x02 && mc.Title == "Song" && mc.Extra.Contains("Composer X"),
                mc.Title + " | " + mc.Extra);
            var opx = new byte[0x200];
            System.Text.Encoding.ASCII.GetBytes("OPX Title".PadRight(53)).CopyTo(opx, 0);
            opx[0x7D] = 0x1A;
            var oc = KssDrivers.Opx(opx);
            check("OPX → KSS (driver 100h, OPLL tag 401Ch, FMTONE 4C00h)", oc.Title == "OPX Title" && System.Text.Encoding.ASCII.GetString(oc.Kss, 0x20 + 0x401C - 0x100, 4) == "OPLL" && oc.Kss[0x20 + 0x4C00 - 0x100] == 0x3A, oc.Title);
        }
        finally { KssDrivers.Folders.Remove(drvDir); }
    }

    // ------------------------------------------------------------ M1-5 SGC

    internal static byte[] MakeSgc(int system, int load, byte[] data, int init, int play, int songs = 1, int firstSfx = 0, int lastSfx = 0, bool pal = false, byte[] mapper = null)
    {
        var d = new byte[0xA0 + data.Length];
        System.Text.Encoding.ASCII.GetBytes("SGC\x1a").CopyTo(d, 0);
        d[4] = 1; d[5] = (byte)(pal ? 1 : 0);
        d[8] = (byte)load; d[9] = (byte)(load >> 8); d[0x0A] = (byte)init; d[0x0B] = (byte)(init >> 8);
        d[0x0C] = (byte)play; d[0x0D] = (byte)(play >> 8); d[0x0E] = 0xF0; d[0x0F] = 0xDF;
        (mapper ?? new byte[] { 0, 0, 1, 2 }).CopyTo(d, 0x20);
        d[0x25] = (byte)songs; d[0x26] = (byte)firstSfx; d[0x27] = (byte)lastSfx; d[0x28] = (byte)system;
        System.Text.Encoding.ASCII.GetBytes("SGC Test").CopyTo(d, 0x40);
        System.Text.Encoding.ASCII.GetBytes("Tester").CopyTo(d, 0x60);
        data.CopyTo(d, 0xA0);
        return d;
    }

    private static int CountSn(VgmSong v, int mask, int value)
    {
        int n = 0;
        for (int p = v.DataOffset; p < v.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(v.Data, p)))
            if (v.Data[p] == 0x50 && (v.Data[p + 1] & mask) == value) n++;
        return n;
    }

    private static void SgcTest(Action<string, bool, string> check)
    {
        // SMS: 0400h init = 톤 0 주기 0FEh · 음량 최대, GG 스테레오 (포트 06h) · FM 유닛 (F0h/F1h)도 써 봄 / 0420h play = $C000을 늘려 톤 0 낮은 4비트로
        var code = new byte[0x40];
        new byte[]
        {
            0x3E, 0x8E, 0xD3, 0x7F, 0x3E, 0x0F, 0xD3, 0x7F, 0x3E, 0x90, 0xD3, 0x7F,   // 톤 0
            0x3E, 0xF0, 0xD3, 0x06,                                                   // GG 스테레오 (SMS면 무시)
            0x3E, 0x30, 0xD3, 0xF0, 0x3E, 0x10, 0xD3, 0xF1,                           // FM 유닛 레지스터 30h
            0xC9,
        }.CopyTo(code, 0);
        new byte[] { 0x3A, 0x00, 0xC0, 0x3C, 0x32, 0x00, 0xC0, 0xE6, 0x0F, 0xF6, 0x80, 0xD3, 0x7F, 0xC9 }.CopyTo(code, 0x20);
        var sms = MakeSgc(0, 0x400, code, 0x400, 0x420, 3, 3, 4);
        var h = Sgc.ReadHeader(sms);
        var tr = Sgc.Tracks(h, "x");
        check("SGC: header (SMS, songs 3 + SFX 3~4)", h.System == 0 && h.Songs == 3 && h.FirstSfx == 3 && h.LastSfx == 4 && h.TotalSongs == 5 && tr.Count == 5 && tr[3].Song == 3 && tr[3].Title.Contains("SFX") && h.Title == "SGC Test", tr.Count + " " + h.Title);
        var v = Sgc.Render(sms, 0, 1, 0);
        int plays = CountSn(v, 0xF0, 0x80);
        check("SGC SMS: play each NTSC frame (≈ 60 / s) → SN76489 VGM", plays >= 59 && plays <= 63 && v.Sn76489Clock == Sgc.CpuClock && v.SnFeedback == 9 && v.SnWidth == 16, plays + " tone writes, fb " + v.SnFeedback + " w " + v.SnWidth);
        check("SGC SMS: FM unit → YM2413, GG stereo port ignored", v.Ym2413Clock > 0 && !HasCmd(v, 0x4F), v.ChipSummary);
        check("SGC SMS: sounds", Rms(Render(v, 0.5)) > 200, Rms(Render(v, 0.5)).ToString("0"));
        int palPlays = CountSn(Sgc.Render(MakeSgc(0, 0x400, code, 0x400, 0x420, pal: true), 0, 1, 0), 0xF0, 0x80);
        check("SGC PAL: ≈ 50 plays / s", palPlays >= 49 && palPlays <= 53, palPlays.ToString());

        // GG: 스테레오 포트 → VGM 4Fh, FM 유닛 없음
        var gg = Sgc.Render(MakeSgc(1, 0x400, code, 0x400, 0x420), 0, 0.5, 0);
        check("SGC GG: stereo 06h → VGM 4Fh, no FM", HasCmd(gg, 0x4F) && gg.Ym2413Clock == 0, gg.ChipSummary);

        // 뱅크: play 코드를 뱅크 3 (파일 안 0xC000)에 두고 init에서 FFFFh = 3으로 8000h에 비춤
        var big = new byte[0x10000 - 0x400];
        new byte[] { 0x3E, 0x03, 0x32, 0xFF, 0xFF, 0x3E, 0x90, 0xD3, 0x7F, 0xC9 }.CopyTo(big, 0);          // init: FFFFh = 3, 음량
        new byte[] { 0x3A, 0x00, 0xC0, 0x3C, 0x32, 0x00, 0xC0, 0xE6, 0x0F, 0xF6, 0x80, 0xD3, 0x7F, 0xC9 }.CopyTo(big, 0xC000 - 0x400);   // 뱅크 3 처음 = 8000h
        int bp = CountSn(Sgc.Render(MakeSgc(0, 0x400, big, 0x400, 0x8000), 0, 1, 0), 0xF0, 0x80);
        check("SGC SMS: mapper FFFFh switches 8000h bank (play in bank 3)", bp >= 59 && bp <= 63, bp.ToString());

        // 콜레코비전: 8000h에 코드, 포트 FFh = SN76489, RAM 7000h
        var cvc = new byte[0x40];
        new byte[] { 0x3E, 0x90, 0xD3, 0xFF, 0xC9 }.CopyTo(cvc, 0);
        new byte[] { 0x3A, 0x00, 0x70, 0x3C, 0x32, 0x00, 0x70, 0xE6, 0x0F, 0xF6, 0x80, 0xD3, 0xFF, 0xC9 }.CopyTo(cvc, 0x20);
        var cvd = MakeSgc(2, 0x8000, cvc, 0x8000, 0x8020);
        cvd[0x0E] = 0xF0; cvd[0x0F] = 0x73;   // 스택 73F0h
        var cv = Sgc.Render(cvd, 0, 1, 0);
        int cp = CountSn(cv, 0xF0, 0x80);
        check("SGC ColecoVision: ROM 8000h, RAM 6000h, port FFh, SN76489AN noise", cp >= 59 && cp <= 63 && cv.SnFeedback == 3 && cv.SnWidth == 15, cp + " fb " + cv.SnFeedback);

        string tmp = Path.Combine(Path.GetTempPath(), "fmopl_sgc_test.sgc");
        File.WriteAllBytes(tmp, sms);
        try
        {
            var list = ChipSongs.Tracks(tmp);
            var lv = ChipSongs.Load(list[1].path);
            check("SGC: ChipSongs 5 tracks (3 songs + 2 SFX), load song 2", list.Count == 5 && lv.SourceFormat.StartsWith("SGC Sega Master System · 곡 2") && lv.GameEn == "SGC Test", list.Count + " " + lv.SourceFormat);
        }
        finally { File.Delete(tmp); }
    }

    private static bool HasCmd(VgmSong v, int cmd)
    {
        for (int p = v.DataOffset; p < v.DataEnd; p += Math.Max(1, VgmFormat.CommandLength(v.Data, p)))
            if (v.Data[p] == cmd) return true;
        return false;
    }
}
