using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Notation;

namespace AdlibComposer.SelfTest;

/// <summary>Orgel Note: 30음 빗살 음계, 구멍 위치(시작점만), 옥타브 옮기기, 자동 조옮김, 가까운 구멍 경고, 오르골 소리.</summary>
public static class OrgelTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        string labels = string.Join(" ", Enumerable.Range(0, OrgelComb.Rows).Select(OrgelComb.Label));
        check("Orgel comb = strip labels (30)", labels == "C D G A B C1 D1 E1 F1 F#1 G1 G#1 A1 A#1 B1 C2 C#2 D2 D#2 E2 F2 F#2 G2 G#2 A2 A#2 B2 C3 D3 E3", labels);
        check("Orgel comb rows ascend", OrgelComb.Offsets.Zip(OrgelComb.Offsets.Skip(1), (a, b) => b > a).All(x => x));
        check("Orgel row of C4/E7 with C = C4", OrgelComb.RowOf(60, 60) == 0 && OrgelComb.RowOf(100, 60) == 29 && OrgelComb.RowOf(61, 60) == -1);

        // 곡: 보이스 1에 C4(길게) · E4(짧게, 빗살에 없음) · C4 바로 다시 · 범위 위 F7, 보이스 2에 같은 시점 C4 (구멍 하나로)
        var song = new Song { TickBeat = 4, BeatsPerMeasure = 4 };
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 16, Pitch = 60 });
        song.Voices[0].Notes.Add(new Note { StartTick = 16, Duration = 1, Pitch = 64 });
        song.Voices[0].Notes.Add(new Note { StartTick = 17, Duration = 3, Pitch = 60 });
        song.Voices[0].Notes.Add(new Note { StartTick = 24, Duration = 2, Pitch = 101 });
        song.Voices[1].Notes.Add(new Note { StartTick = 0, Duration = 2, Pitch = 60 });

        var o = new OrgelOptions { LowestC = 60, FoldOctaves = false, MinGapTicks = 2 };
        var s = OrgelBuilder.Build(song, o);
        check("Orgel punches only at note starts (duplicate merged)", s.Punches.Count == 2 && s.Punches.All(p => p.Row == 0) && s.Punches[0].Tick == 0 && s.Punches[1].Tick == 17,
            string.Join(",", s.Punches.Select(p => p.Tick + ":" + p.Row)));
        check("Orgel missing notes without folding", s.Missing.Count == 2, s.Missing.Count.ToString());

        o.FoldOctaves = true;
        s = OrgelBuilder.Build(song, o);
        var e = s.Punches.FirstOrDefault(p => p.Tick == 16);
        check("Orgel fold E4 → E5 (E1 row)", e != null && e.Folded && OrgelComb.Label(e.Row) == "E1", e != null ? OrgelComb.Label(e.Row) : "none");
        var hi = s.Punches.FirstOrDefault(p => p.Tick == 24);
        check("Orgel fold F7 → F6 (F2 row)", hi != null && hi.Folded && OrgelComb.Label(hi.Row) == "F2", hi != null ? OrgelComb.Label(hi.Row) : "none");
        check("Orgel nothing missing when folding", s.Missing.Count == 0);

        o.QuantizeTicks = 2;
        s = OrgelBuilder.Build(song, o);
        var c2 = s.Punches.Where(p => p.Row == 0).Select(p => p.Tick).ToList();
        check("Orgel quantize to grid", c2.SequenceEqual(new[] { 0, 18 }), string.Join(",", c2));
        o.QuantizeTicks = 0;
        o.MinGapTicks = 4;
        song.Voices[0].Notes.Add(new Note { StartTick = 2, Duration = 1, Pitch = 60 });
        s = OrgelBuilder.Build(song, o);
        check("Orgel same row closer than min gap flagged", s.Punches.Count(p => p.TooClose) == 1 && s.Punches.First(p => p.TooClose).Tick == 2);

        // 자동 조옮김: C 장조 음계를 C3에 쓰면(빗살 C = C4) 한 옥타브 올리는 게 아니라 반음이 다 있는 줄로 옮긴다
        var scale = new Song { TickBeat = 4 };
        int[] cmaj = { 48, 50, 52, 53, 55, 57, 59, 60 };
        for (int i = 0; i < cmaj.Length; i++) scale.Voices[0].Notes.Add(new Note { StartTick = i * 4, Duration = 4, Pitch = cmaj[i] });
        var ao = new OrgelOptions { LowestC = 60, FoldOctaves = false };
        int t = OrgelBuilder.BestTranspose(scale, ao);
        ao.Transpose = t;
        var st = OrgelBuilder.Build(scale, ao);
        check("Orgel auto transpose fits whole C major scale", st.Missing.Count == 0 && t % 12 == 0, "transpose " + t);

        // 드럼은 기본으로 빠진다
        var rh = new Song { TickBeat = 4, IsMelodic = false };
        rh.Voices[6].Notes.Add(new Note { StartTick = 0, Duration = 1, Pitch = 60 });
        rh.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 1, Pitch = 62 });
        check("Orgel drums excluded by default", OrgelBuilder.Build(rh, new OrgelOptions()).Punches.Count == 1);

        // 오르골 소리
        var eng = new AudioEngine();
        eng.PlayMusicBox(new[] { new KeyValuePair<int, int>(0, 81), new KeyValuePair<int, int>(4, 84) }, 8.0, 0, 8);
        var buf = new short[4410];
        eng.Render(buf, buf.Length);
        int cross = 0; double sum = 0;
        for (int i = 1; i < buf.Length; i++) { if ((buf[i - 1] < 0) != (buf[i] < 0)) cross++; sum += (double)buf[i] * buf[i]; }
        double hz = cross / 2.0 / 0.1;
        check("Music box plucks A5 (880 Hz) with sound", Math.Sqrt(sum / buf.Length) > 500 && Math.Abs(hz - 880) < 60, hz.ToString("0") + " Hz, rms " + Math.Sqrt(sum / buf.Length).ToString("0"));
        double first = Rms(buf);
        for (int k = 0; k < 5; k++) eng.Render(buf, buf.Length);   // 0.6초: 두 번째 음(틱 4 = 0.5초) 뒤
        check("Music box position advances", eng.PositionTick > 4 && eng.IsPlaying, eng.PositionTick.ToString("0.0"));
        bool ended = false;
        for (int k = 0; k < 60 && !ended; k++) { eng.Render(buf, buf.Length); ended = !eng.IsPlaying; }
        check("Music box playback ends and decays", ended && Rms(buf) < first * 0.5, Rms(buf).ToString("0"));
    }

    private static double Rms(short[] b)
    {
        double s = 0;
        foreach (short v in b) s += (double)v * v;
        return Math.Sqrt(s / b.Length);
    }
}
