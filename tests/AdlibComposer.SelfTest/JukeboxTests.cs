using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>Jukebox: 조합형 한글, ISS 가사, 재생 조절(속도 · 키 · 채널 끄기), 초 ↔ 틱.</summary>
public static class JukeboxTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- 조합형
        // 가 = 1 00010 00011 00001 = 0x8861, 힣 = ㅎ(20) ㅣ(29) ㅎ(29) = 1 10100 11101 11101 = 0xD3BD
        check("Johab 가", KoreanText.DecodeJohab(0x88, 0x61) == "가", KoreanText.DecodeJohab(0x88, 0x61));
        check("Johab 힣", KoreanText.DecodeJohab(0xD3, 0xBD) == "힣", KoreanText.DecodeJohab(0xD3, 0xBD));
        check("Johab jamo ㄱ", KoreanText.DecodeJohab(0x88, 0x41) == "ㄱ", KoreanText.DecodeJohab(0x88, 0x41));
        check("Johab fill = ideographic space", KoreanText.DecodeJohab(0x84, 0x41) == "　");
        byte[] johab = Johab("말하자면 abc");
        string dj = KoreanText.Decode(johab, 0, johab.Length);
        check("Johab text round trip", dj == "말하자면 abc", dj);
        check("Johab detected", KoreanText.Detect(johab, 0, johab.Length) == KoreanText.Kind.Johab);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] wansung = Encoding.GetEncoding(949).GetBytes("말하자면 사랑해요");
        string dw = KoreanText.Decode(wansung, 0, wansung.Length);
        check("Wansung (CP949) detected and decoded", dw == "말하자면 사랑해요", dw);
        var cells = KoreanText.DecodeCells(johab, 0, johab.Length, KoreanText.Kind.Johab);
        check("Cells: Hangul = 2, ASCII = 1", cells[0].Width == 2 && cells[1].Column == 2 && cells[4].Width == 1 && cells[5].Column == 9,
            string.Join(",", cells.Select(c => c.Text + c.Column + "/" + c.Width)));

        // ---- ISS
        var lines = new[] { "말하자면 처음", "둘째 줄" };
        var recs = new List<int[]>   // tick(÷8), line, startX, width
        {
            new[] { 30, 0, 0, 2 }, new[] { 60, 0, 2, 2 }, new[] { 90, 0, 4, 4 }, new[] { 120, 0, 9, 4 },
            new[] { 150, 1, 0, 4 }, new[] { 180, 1, 5, 4 },
        };
        byte[] issBytes = BuildIss(true, lines, recs);
        var iss = IssLyrics.Read(issBytes);
        check("ISS parse counts", iss.Records.Count == 6 && iss.Lines.Count == 2, iss.Records.Count + "/" + iss.Lines.Count);
        check("ISS V2 tick x8", iss.IsV2 && iss.Records[0].Tick == 240, iss.Records[0].Tick.ToString());
        check("ISS line text (Johab)", iss.Lines[0].Text == "말하자면 처음" && iss.Lines[1].Text == "둘째 줄", iss.Lines[0].Text + " | " + iss.Lines[1].Text);
        check("ISS credits", iss.Writer == "WRITER" && iss.Singer == "가수", iss.Writer + "/" + iss.Singer);
        // 곡 박당 48틱 (IMS 240 → 줄여서 48): 기록 240틱 = 곡 48틱
        var st0 = iss.StateAt(10, 48, 240);
        check("ISS before first record shows first line unlit", st0.Line == 0 && !st0.Lit.Any(x => x) && st0.NextLine == 1, st0.Line + " next " + st0.NextLine);
        var st1 = iss.StateAt(48, 48, 240);
        check("ISS first syllable lit", st1.Lit[0] && st1.Lit[1] && !st1.Lit[2], Lit(st1));
        var st2 = iss.StateAt(48 * 3, 48, 240);   // 레코드 3개 (말하자면)
        check("ISS wipe accumulates", Lit(st2) == "11111111" + new string('0', 5), Lit(st2));
        var st3 = iss.StateAt(48 * 5, 48, 240);
        check("ISS next line clears", st3.Line == 1 && st3.Lit[0] && !st3.Lit[5] && st3.NextLine == -1, st3.Line + " " + Lit(st3));
        byte[] oldBytes = BuildIss(false, lines, recs);
        var old = IssLyrics.Read(oldBytes);
        check("ISS old format tick x10/8*8", !old.IsV2 && old.Records[0].Tick == 30 * 10 / 8 * 8, old.Records[0].Tick.ToString());

        // ---- 초 ↔ 틱
        var song = new Song { TickBeat = 48, BasicTempo = 120 };
        song.TempoEvents.Add(new TempoEvent { Tick = 0, Multiplier = 1f });
        song.TempoEvents.Add(new TempoEvent { Tick = 480, Multiplier = 2f });
        var map = new TempoMap(song);
        double s1 = map.SecondsAt(480), s2 = map.SecondsAt(960);
        check("TempoMap seconds with tempo change", Math.Abs(s1 - 5) < 1e-9 && Math.Abs(s2 - 7.5) < 1e-9, s1 + " / " + s2);
        check("TempoMap TickAt inverse", Math.Abs(map.TickAt(5) - 480) < 1e-6 && Math.Abs(map.TickAt(7.5) - 960) < 1e-6 && Math.Abs(map.TickAt(2.5) - 240) < 1e-6,
            map.TickAt(5) + " " + map.TickAt(7.5) + " " + map.TickAt(2.5));

        // ---- 재생 조절
        var s = new Song { TickBeat = 4, BasicTempo = 120, IsMelodic = true };
        s.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "piano1" });
        s.Voices[1].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "missing" });
        s.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 16, Pitch = 60 });
        s.Voices[1].Notes.Add(new Note { StartTick = 0, Duration = 16, Pitch = 64 });
        var bank = InstrumentBank.CreateNew();
        if (bank.Find("piano1") == null) bank.Add(AdlibInstrument.CreateDefault("piano1"));

        var engine = new AudioEngine();
        var buf = new short[4410];
        var states = new VoiceState[11];
        var opt = new PlaybackOptions();
        engine.Play(s, bank, 0, -1, -1, -1, opt);
        engine.Render(buf, buf.Length);
        engine.GetVoiceStates(states);
        check("Voice state: note on voice 0/1", states[0].Note == 60 && states[1].Note == 64, states[0].Note + "," + states[1].Note);
        check("Voice state: instrument found / missing", states[0].InstrumentFound && states[0].Instrument == "piano1" && !states[1].InstrumentFound,
            states[0].Instrument + "/" + states[1].Instrument);
        double rmsAll = Rms(engine, buf, 4);

        engine.SetVoiceMute(0, true);
        engine.SetVoiceMute(1, true);
        double rmsMuted = Rms(engine, buf, 8);   // 릴리즈가 끝날 때까지
        rmsMuted = Rms(engine, buf, 2);
        check("Mute silences voices", rmsAll > 50 && rmsMuted < rmsAll * 0.05, rmsAll.ToString("0") + " -> " + rmsMuted.ToString("0"));

        // 속도: 2배면 같은 시간에 틱이 2배 간다
        var o1 = new PlaybackOptions();
        engine.Play(s, bank, 0, -1, -1, -1, o1);
        engine.Render(buf, buf.Length);
        double p1 = engine.PositionTick;
        var o2 = new PlaybackOptions { Speed = 2.0 };
        engine.Play(s, bank, 0, -1, -1, -1, o2);
        engine.Render(buf, buf.Length);
        double p2 = engine.PositionTick;
        check("Speed x2 doubles tick rate", Math.Abs(p2 / p1 - 2) < 0.05, p1.ToString("0.00") + " / " + p2.ToString("0.00"));
        engine.SetSpeed(1.0);
        engine.Render(buf, buf.Length);
        double p3 = engine.PositionTick - p2;
        check("SetSpeed live", Math.Abs(p3 / p1 - 1) < 0.05, p3.ToString("0.00"));

        // 키: 조옮김하면 다른 소리 (주파수가 바뀐다) — 영점 교차 수로 비교
        var solo = new Song { TickBeat = 4, BasicTempo = 60, IsMelodic = true };
        solo.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "piano1" });
        solo.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 32, Pitch = 60 });
        int z0 = Crossings(engine, solo, bank, new PlaybackOptions());
        int z12 = Crossings(engine, solo, bank, new PlaybackOptions { Transpose = 12 });
        int zv = Crossings(engine, solo, bank, Voice0(12));
        check("Key +12 doubles frequency", Math.Abs(z12 / (double)z0 - 2) < 0.2, z0 + " -> " + z12);
        check("Voice pitch +12 doubles frequency", Math.Abs(zv / (double)z0 - 2) < 0.2, z0 + " -> " + zv);
        engine.Stop();
        engine.Dispose();

        // ---- 실제 파일: 같은 이름 BNK, IMS 제목 (조합형)
        string inc = FindIncoming();
        if (inc != null && File.Exists(Path.Combine(inc, "WZ-GAME1.IMS")))
        {
            Song ims;
            using (var fs = File.OpenRead(Path.Combine(inc, "WZ-GAME1.IMS"))) ims = new ImsFormat().Read(fs);
            check("Real IMS FileTickBeat = 240", ims.FileTickBeat == 240, ims.FileTickBeat + " (TickBeat " + ims.TickBeat + ")");
        }
        // 부드러운 제한
        check("SoftClip passes quiet samples", AudioEngine.SoftClip(12000) == 12000 && AudioEngine.SoftClip(-24000) == -24000);
        check("SoftClip never exceeds 16-bit", AudioEngine.SoftClip(1e6) < 32767 && AudioEngine.SoftClip(-1e6) > -32768 && AudioEngine.SoftClip(40000) > AudioEngine.SoftClip(30000),
            AudioEngine.SoftClip(40000).ToString("0"));

        // IMS totalTick: IMPLAY처럼 헤더 길이에서 끝낸다 (BLUE-C#2.IMS는 FC까지 2분 무음 꼬리)
        if (inc != null && File.Exists(Path.Combine(inc, "BLUE-C#2.IMS")))
        {
            Song blue;
            using (var fs = File.OpenRead(Path.Combine(inc, "BLUE-C#2.IMS"))) blue = new ImsFormat().Read(fs);
            double sec = new TempoMap(blue).SecondsAt(blue.LengthTicks);
            check("Real IMS BLUE-C#2 ends at header totalTick (~40s, not 159s)", sec > 38 && sec < 42, sec.ToString("0.0") + "s, " + blue.LengthTicks + " ticks");
        }
        if (inc != null)
            foreach (var p in Directory.GetFiles(inc, "*.*").Where(f => f.EndsWith(".ims", StringComparison.OrdinalIgnoreCase)))
            {
                Song t;
                using (var fs = File.OpenRead(p)) t = new ImsFormat().Read(fs);
                check("Real IMS title decoded " + Path.GetFileName(p), !t.Title.Contains('�'), "\"" + t.Title + "\"");
            }
        if (inc != null)
            foreach (var issPath in Directory.GetFiles(inc, "*.*").Where(f => f.EndsWith(".iss", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(issPath);
                byte[] raw = File.ReadAllBytes(issPath);
                var ly = IssLyrics.Read(raw);
                int expect = IssLyrics.HeaderSize + ly.Records.Count * IssLyrics.RecordSize + ly.Lines.Count * IssLyrics.LineSize;
                check("Real ISS " + name + " size = header + records + lines", expect == raw.Length, expect + " vs " + raw.Length);
                int bad = ly.Lines.Sum(l => l.Text.Count(c => c == '�'));
                int hangul = ly.Lines.Sum(l => l.Text.Count(c => c >= 0xAC00 && c <= 0xD7A3));
                check("Real ISS " + name + " decodes Korean", hangul > 20 && bad <= 2, ly.Encoding + " hangul=" + hangul + " bad=" + bad);
                string imsPath = Path.ChangeExtension(issPath, ".IMS");
                if (File.Exists(imsPath))
                {
                    Song sg;
                    using (var fs = File.OpenRead(imsPath)) sg = new ImsFormat().Read(fs);
                    double last = IssLyrics.ToSongTick(ly.Records.Max(r => r.Tick), sg.TickBeat, sg.FileTickBeat);
                    double ratio = last / Math.Max(1, sg.LengthTicks);
                    check("Real ISS " + name + " timing fits song", ratio > 0.5 && ratio < 1.1, ratio.ToString("0.000") + (ly.IsV2 ? " V2" : " old"));
                    var st = ly.StateAt(sg.LengthTicks * 0.5, sg.TickBeat, sg.FileTickBeat);
                    check("Real ISS " + name + " mid-song has lit line", st.Line >= 0 && st.Lit.Any(x => x), st.Line + ": " + (st.Line >= 0 ? ly.Lines[st.Line].Text.Trim() : ""));
                }
            }

        TestToneAndSpeaker(check);
    }

    /// <summary>음색 표준/거칠게, 스피커 기본값/그 시절: 소리가 달라지고, 크기는 기본값과 비슷하게 (±4dB).</summary>
    private static void TestToneAndSpeaker(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        if (dir == null || !File.Exists(Path.Combine(dir, "demo.rol"))) { Console.WriteLine("SKIP  tone/speaker (samples/demo.rol not found)"); return; }
        Song s; InstrumentBank b;
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) s = new RolFormat().Read(fs);
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.bnk"))) b = new BnkFormat().Read(fs);

        double Measure(bool standard, bool vintage, out short[] snap)
        {
            var e = new AudioEngine { ToneStandard = standard, VintageSpeaker = vintage };
            var buf = new short[44100];
            e.Play(s, b, 0, -1, -1, -1, new PlaybackOptions());
            e.Render(buf, 4410);         // 앞 0.1초 건너뛰기 (스피커 페이드 포함)
            double r = Rms(e, buf, 2);   // 2초 (demo.rol은 짧다)
            snap = (short[])buf.Clone();
            return r;
        }
        double raw = Measure(false, false, out var a), std = Measure(true, false, out var c), vin = Measure(false, true, out var d);
        int diffStd = 0, diffVin = 0;
        for (int i = 0; i < a.Length; i++) { if (Math.Abs(a[i] - c[i]) > 4) diffStd++; if (Math.Abs(a[i] - d[i]) > 4) diffVin++; }
        double dbStd = 20 * Math.Log10(std / raw), dbVin = 20 * Math.Log10(vin / raw);
        check("Tone standard changes sound, about same loudness", diffStd > a.Length / 10 && Math.Abs(dbStd) < 3, dbStd.ToString("0.0") + " dB, diff " + diffStd);
        check("Vintage speaker changes sound, loudness within 4 dB", diffVin > a.Length / 2 && Math.Abs(dbVin) < 4, dbVin.ToString("0.0") + " dB, diff " + diffVin);
    }

    private static PlaybackOptions Voice0(int semis) { var o = new PlaybackOptions(); o.VoiceTranspose[0] = semis; return o; }

    private static int Crossings(AudioEngine e, Song s, InstrumentBank b, PlaybackOptions o)
    {
        var buf = new short[44100];
        e.Play(s, b, 0, -1, -1, -1, o);
        e.Render(buf, 4410);   // 어택 건너뛰기
        e.Render(buf, buf.Length);
        int n = 0;
        for (int i = 1; i < buf.Length; i++) if ((buf[i - 1] < 0) != (buf[i] < 0)) n++;
        return n;
    }

    private static double Rms(AudioEngine e, short[] buf, int blocks)
    {
        double sum = 0; long n = 0;
        for (int k = 0; k < blocks; k++)
        {
            e.Render(buf, buf.Length);
            foreach (var v in buf) { sum += (double)v * v; n++; }
        }
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    private static string Lit(LyricState s) { return string.Concat(s.Lit.Take(13).Select(x => x ? '1' : '0')); }

    private static string FindIncoming()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            string p = Path.Combine(d.FullName, "_incoming");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }

    // ---------------------------------------------------------------- 시험용 조합형 인코더 / ISS 만들기

    private const string Ini = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private static readonly int[] MedSlot = { 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 18, 19, 20, 21, 22, 23, 26, 27, 28, 29 };

    public static byte[] Johab(string text)
    {
        var o = new List<byte>();
        foreach (char ch in text)
        {
            if (ch < 0x80) { o.Add((byte)ch); continue; }
            int idx = ch - 0xAC00;
            int cho = idx / 588, jung = idx % 588 / 28, jong = idx % 28;
            int fin = jong == 0 ? 1 : jong <= 16 ? jong + 1 : jong + 2;
            int code = 0x8000 | ((cho + 2) << 10) | (MedSlot[jung] << 5) | fin;
            o.Add((byte)(code >> 8));
            o.Add((byte)(code & 0xFF));
        }
        return o.ToArray();
    }

    private static byte[] BuildIss(bool v2, string[] lines, List<int[]> recs)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var head = new byte[20];
        if (v2) Encoding.ASCII.GetBytes("IMPlay Song V2.0    ").CopyTo(head, 0);
        w.Write(head);
        w.Write(new byte[10]);
        foreach (var f in new[] { "WRITER", "COMPOSER", "가수", "EDITOR" })
        {
            var b = new byte[30];
            Johab(f).CopyTo(b, 0);
            w.Write(b);
        }
        w.Write((ushort)recs.Count);
        w.Write((ushort)lines.Length);
        foreach (var r in recs)
        {
            int t = v2 ? r[0] : r[0];
            w.Write((ushort)t);
            w.Write((byte)r[1]); w.Write((byte)r[2]); w.Write((byte)r[3]);
        }
        foreach (var l in lines)
        {
            var b = new byte[64];
            Johab(l).CopyTo(b, 0);
            w.Write(b);
        }
        return ms.ToArray();
    }
}
