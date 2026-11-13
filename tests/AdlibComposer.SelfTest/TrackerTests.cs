using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>Scream Tracker 2/3: STM · S3M 읽기/쓰기, 형식 바꾸기, 재생기(음높이 · 속도 · 길이 · 루프 끝), 실제 파일.</summary>
public static class TrackerTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- S3M: 샘플 1개(32샘플 주기 사각파 → C4Spd 8363이면 C-4 = 261Hz), 노트 2개, AdLib 악기 1개
        var m = TrackerModule.CreateNew(TrackerKind.S3m);
        m.Title = "test s3m";
        m.Samples.Add(Square("SQUARE", 32 * 400, 8363, true));
        m.Samples.Add(new TrackerSample { Type = TrackerSample.TypeAdlibMelody, Name = "organ", Adlib = new byte[] { 0x21, 0x21, 0x10, 0, 0xF2, 0xF2, 0x43, 0x43, 0, 0, 6, 0 }, Volume = 50 });
        var p = m.Patterns[0];
        p[0, 0] = new TrackerCell { Note = TrackerCell.MakeNote(4, 0), Instrument = 1, Volume = 64, Command = 0 };
        p[16, 1] = new TrackerCell { Note = TrackerCell.MakeNote(5, 9), Instrument = 1, Volume = TrackerCell.NoVolume, Command = 8, Info = 0x44 };
        p[32, 0] = new TrackerCell { Note = TrackerCell.NoteOff, Volume = TrackerCell.NoVolume };
        var bytes = S3mFormat.Write(m);
        var back = S3mFormat.Read(bytes);
        check("S3M round trip header", back.Title == "test s3m" && back.Speed == 6 && back.Tempo == 125 && back.Stereo && back.ChannelLabel(0) == "L1" && back.ChannelLabel(1) == "R1", back.Title);
        check("S3M round trip cells", back.Patterns[0][0, 0].Note == 0x40 && back.Patterns[0][16, 1].Command == 8 && back.Patterns[0][16, 1].Info == 0x44 && back.Patterns[0][32, 0].Note == TrackerCell.NoteOff && back.Patterns[0][1, 0].IsEmpty);
        check("S3M round trip samples (8bit unsigned in file)", back.Samples[0].IsSample && back.Samples[0].Length == m.Samples[0].Length && back.Samples[0].Data.SequenceEqual(m.Samples[0].Data) && back.Samples[0].Loop);
        check("S3M round trip AdLib instrument", back.Samples[1].IsAdlib && back.Samples[1].Adlib.SequenceEqual(m.Samples[1].Adlib) && back.Samples[1].Volume == 50);
        check("S3M orders end with 255 and even", back.Orders.Count % 2 == 0 && back.Orders[back.Orders.Count - 1] == TrackerModule.OrderEnd);

        // 재생: C-4 = 261.6Hz
        double hz = Pitch(m, 0.02, 0.25);
        check("S3M C-4 with C4Spd 8363 = 261 Hz", Math.Abs(hz - 261.6) < 8, hz.ToString("0.0") + " Hz");
        // 속도 6 · 템포 125: 줄 = 0.12초, 64줄 = 7.68초
        double len = TrackerPlayer.MeasureSeconds(m);
        check("S3M length speed 6 tempo 125 = 7.68s", Math.Abs(len - 7.68) < 0.05, len.ToString("0.000"));
        m.Patterns[0][8, 2] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 2, Info = 0 };   // B00: 처음으로 → 한 바퀴에서 끝
        len = TrackerPlayer.MeasureSeconds(m);
        check("S3M jump back ends after one pass", Math.Abs(len - 9 * 0.12) < 0.05, len.ToString("0.000"));
        m.Patterns[0][8, 2] = TrackerCell.Empty;
        m.Patterns[0][0, 3] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 1, Info = 3 };   // A03: 속도 3
        len = TrackerPlayer.MeasureSeconds(m);
        check("S3M A03 halves the length", Math.Abs(len - 3.84) < 0.05, len.ToString("0.000"));
        m.Patterns[0][0, 3] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 20, Info = 250 };  // T FA: 템포 250
        len = TrackerPlayer.MeasureSeconds(m);
        check("S3M T250 halves the length", Math.Abs(len - 3.84) < 0.05, len.ToString("0.000"));
        m.Patterns[0][0, 3] = TrackerCell.Empty;

        // 패턴 루프 SB0 / SB1: 0~15줄을 한 번 더
        m.Patterns[0][0, 3] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 19, Info = 0xB0 };
        m.Patterns[0][15, 3] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 19, Info = 0xB1 };
        len = TrackerPlayer.MeasureSeconds(m);
        check("S3M SB loop repeats once", Math.Abs(len - 80 * 0.12) < 0.05, len.ToString("0.000"));
        m.Patterns[0][0, 3] = TrackerCell.Empty; m.Patterns[0][15, 3] = TrackerCell.Empty;

        // AdLib 채널 (A1): 소리가 나야 한다
        var a = TrackerModule.CreateNew(TrackerKind.S3m);
        a.ChannelSettings[0] = 16;   // A1
        a.Samples.Add(m.Samples[1].Clone());
        a.Patterns[0][0, 0] = new TrackerCell { Note = TrackerCell.MakeNote(4, 0), Instrument = 1, Volume = 64 };
        check("S3M AdLib channel sounds", Rms(a, 0.05, 0.3) > 300, Rms(a, 0.05, 0.3).ToString("0"));

        // ---- STM
        var s = TrackerModule.CreateNew(TrackerKind.Stm);
        s.Title = "test stm";
        s.Samples.Add(Square("SQ.SMP", 32 * 400, 8448, false));
        for (int i = 1; i < 31; i++) s.Samples.Add(new TrackerSample());
        s.Patterns[0][0, 0] = new TrackerCell { Note = TrackerCell.MakeNote(2, 0), Instrument = 1, Volume = 64 };
        s.Patterns[0][4, 1] = new TrackerCell { Note = TrackerCell.MakeNote(3, 4), Instrument = 1, Volume = TrackerCell.NoVolume, Command = 4, Info = 0x02 };
        var sb = StmFormat.Write(s);
        var sback = StmFormat.Read(sb);
        check("STM round trip", sback.Title == "test stm" && sback.Tempo == 0x60 && sback.Patterns.Count == 1 && sback.Patterns[0][0, 0].Note == 0x20 && sback.Patterns[0][4, 1].Command == 4
            && sback.Patterns[0][4, 1].Volume == TrackerCell.NoVolume && sback.Samples[0].Data.SequenceEqual(s.Samples[0].Data), sback.Title);
        hz = Pitch(s, 0.02, 0.25);
        check("STM C-2 with C2Spd 8448 = 264 Hz (st2play period)", Math.Abs(hz - 264) < 8, hz.ToString("0.0") + " Hz");
        len = TrackerPlayer.MeasureSeconds(s);
        check("STM tempo 0x60 = 6 ticks x 900 frames per row", Math.Abs(len - 64 * 6 * 900 / 44100.0) < 0.05, len.ToString("0.000"));

        // ---- 형식 바꾸기
        var w = new System.Collections.Generic.List<string>();
        var conv = s.ConvertTo(TrackerKind.S3m, w);
        check("STM → S3M: C-2 becomes C-4, 4 channels L1 R1 R2 L2", conv.Kind == TrackerKind.S3m && conv.Patterns[0][0, 0].Note == 0x40 && conv.ChannelLabel(0) == "L1" && conv.ChannelLabel(2) == "R2" && conv.Speed == 6);
        check("STM → S3M keeps pitch", Math.Abs(Pitch(conv, 0.02, 0.25) - Pitch(s, 0.02, 0.25)) < 8);
        w.Clear();
        m.Patterns[0][5, 6] = new TrackerCell { Note = TrackerCell.MakeNote(4, 2), Instrument = 1, Volume = TrackerCell.NoVolume, Command = 17, Info = 3 };
        var toStm = m.ConvertTo(TrackerKind.Stm, w);
        check("S3M → STM warns about lost things", toStm.Kind == TrackerKind.Stm && w.Count >= 2 && toStm.Patterns[0][0, 0].Note == 0x20, string.Join(" / ", w));
        check("S3M → STM writes", StmFormat.Read(StmFormat.Write(toStm)).Patterns.Count == 1);

        // ---- 시음
        var eng = new AudioEngine();
        eng.TrackerJam(m, 1, TrackerCell.MakeNote(4, 0), 0);
        var buf = new short[4410];
        eng.Render(buf, buf.Length);
        check("Tracker jam plays a note", RmsOf(buf) > 300, RmsOf(buf).ToString("0"));

        RunReal(check);
    }

    private static void RunReal(Action<string, bool, string> check)
    {
        string dir = FindDir(Path.Combine("_incoming", "scream trackers"));
        if (dir == null) { Console.WriteLine("SKIP  real Scream Tracker files (_incoming/scream trackers not found)"); return; }
        var stms = Directory.GetFiles(Path.Combine(dir, "st2 sample music"), "*.stm").OrderBy(x => x).ToList();
        int ok = 0; string bad = "";
        double maxLen = 0;
        foreach (var f in stms)
        {
            try
            {
                var t = StmFormat.Load(f);
                double l = TrackerPlayer.MeasureSeconds(t);
                maxLen = Math.Max(maxLen, l);
                if (t.Patterns.Count > 0 && t.Samples.Count == 31 && l > 1 && l < 1700) ok++; else bad += Path.GetFileName(f) + "(" + l.ToString("0") + "s) ";
            }
            catch (Exception ex) { bad += Path.GetFileName(f) + ":" + ex.Message + " "; }
        }
        check("Real STM files read + end (" + stms.Count + ")", ok == stms.Count, bad.Length > 0 ? bad : "longest " + maxLen.ToString("0") + "s");
        // 재저장해도 같은 패턴 · 샘플
        var first = StmFormat.Load(stms[0]);
        var again = StmFormat.Read(StmFormat.Write(first));
        bool same = again.Patterns.Count == first.Patterns.Count;
        for (int i = 0; same && i < first.Patterns.Count; i++) same = first.Patterns[i].Cells.SequenceEqual(again.Patterns[i].Cells);
        for (int i = 0; same && i < 31; i++) same = first.Samples[i].Data.SequenceEqual(again.Samples[i].Data);
        check("Real STM re-save keeps patterns and samples (" + Path.GetFileName(stms[0]) + ")", same, "");

        string armani = Path.Combine(dir, "scrmt321", "ARMANI.S3M");
        if (File.Exists(armani))
        {
            var a = S3mFormat.Load(armani);
            check("Real S3M ARMANI reads", a.UsedChannelCount == 5 && a.ChannelLabel(0) == "L1" && a.ChannelLabel(1) == "R1" && a.Samples.Count(x => x.IsSample) == 7 && a.Speed == 8, a.Title);
            double l = TrackerPlayer.MeasureSeconds(a);
            check("Real S3M ARMANI ends after one pass (B04 loop)", l > 100 && l < 140, l.ToString("0.0") + "s");
            var r = S3mFormat.Read(S3mFormat.Write(a));
            bool same2 = r.Patterns.Count == a.Patterns.Count && r.Samples.Count == a.Samples.Count;
            for (int i = 0; same2 && i < a.Patterns.Count; i++) same2 = a.Patterns[i].Cells.SequenceEqual(r.Patterns[i].Cells);
            for (int i = 0; same2 && i < a.Samples.Count; i++) same2 = a.Samples[i].Data.SequenceEqual(r.Samples[i].Data) && a.Samples[i].Loop == r.Samples[i].Loop;
            check("Real S3M re-save keeps patterns and samples", same2, "");
            check("Real S3M plays", Rms(a, 0.5, 2.0) > 300, "");
        }
    }

    // ------------------------------------------------------------ 도우미

    internal static TrackerSample Square(string name, int length, int c2, bool loop)
    {
        var d = new short[length];
        for (int i = 0; i < length; i++) d[i] = (short)((i / 16) % 2 == 0 ? 47 << 8 : -(47 << 8));   // 8bit로 정확히 저장되는 값
        return new TrackerSample { Type = TrackerSample.TypeSample, Name = name, FileName = name, Data = d, C2Spd = c2, Volume = 64, Loop = loop, LoopStart = 0, LoopEnd = length };
    }

    private static short[] Render(TrackerModule m, double skip, double seconds)
    {
        var eng = new AudioEngine();
        eng.PlayTracker(m);
        var buf = new short[(int)(skip * 44100)];
        if (buf.Length > 0) eng.Render(buf, buf.Length);
        var b = new short[(int)(seconds * 44100)];
        eng.Render(b, b.Length);
        return b;
    }

    internal static double Pitch(TrackerModule m, double skip, double seconds)
    {
        var b = Render(m, skip, seconds);
        int cross = 0;
        for (int i = 1; i < b.Length; i++) if ((b[i - 1] < 0) != (b[i] < 0)) cross++;
        return cross / 2.0 / seconds;
    }

    internal static double Rms(TrackerModule m, double skip, double seconds) { return RmsOf(Render(m, skip, seconds)); }

    private static double RmsOf(short[] b)
    {
        double s = 0;
        foreach (short v in b) s += (double)v * v;
        return Math.Sqrt(s / Math.Max(1, b.Length));
    }

    private static string FindDir(string rel)
    {
        string d = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && d != null; i++)
        {
            string c = Path.Combine(d, rel);
            if (Directory.Exists(c)) return c;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }
}
