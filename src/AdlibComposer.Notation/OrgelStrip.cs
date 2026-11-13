using System;
using System.Collections.Generic;
using AdlibComposer.Core;

namespace AdlibComposer.Notation;

/// <summary>
/// 30음 오르골(손으로 돌리는 종이 띠) 빗살 음계. 낮은 음부터:
/// C D G A B C₁ D₁ E₁ F₁ F♯₁ G₁ G♯₁ A₁ A♯₁ B₁ C₂ C♯₂ D₂ D♯₂ E₂ F₂ F♯₂ G₂ G♯₂ A₂ A♯₂ B₂ C₃ D₃ E₃
/// (띠에 인쇄된 음계 표시. 낮은 옥타브는 C D G A B만, F₁부터 E₂까지는 반음이 다 있다.)
/// </summary>
public static class OrgelComb
{
    public const int Rows = 30;

    /// <summary>빗살 음: 가장 낮은 C로부터의 반음 수.</summary>
    public static readonly int[] Offsets =
    {
        0, 2, 7, 9, 11,                                   // C D G A B
        12, 14, 16, 17, 18, 19, 20, 21, 22, 23,           // C1 D1 E1 F1 F#1 G1 G#1 A1 A#1 B1
        24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,   // C2 C#2 D2 D#2 E2 F2 F#2 G2 G#2 A2 A#2 B2
        36, 38, 40,                                       // C3 D3 E3
    };

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>띠 표시 이름 (예: "F#1"). 첫 옥타브는 숫자 없음.</summary>
    public static string Label(int row)
    {
        int off = Offsets[row];
        string n = Names[off % 12];
        int oct = off / 12;
        return oct == 0 ? n : n + oct;
    }

    /// <summary>음 이름과 아래 숫자를 따로 (그리기용).</summary>
    public static void LabelParts(int row, out string name, out string octave)
    {
        int off = Offsets[row];
        name = Names[off % 12];
        octave = off / 12 == 0 ? "" : (off / 12).ToString();
    }

    /// <summary>음 번호(MIDI, 60 = 가운데 도) → 줄 (없으면 -1). lowestC = 가장 낮은 C의 음 번호.</summary>
    public static int RowOf(int note, int lowestC)
    {
        int off = note - lowestC;
        return Array.IndexOf(Offsets, off);
    }

    public static int NoteOf(int row, int lowestC) { return lowestC + Offsets[row]; }
}

public sealed class OrgelOptions
{
    /// <summary>가장 낮은 빗살(C)의 음 번호. 기본 60 = 가운데 도 (띠 표시 C = C4).</summary>
    public int LowestC = 60;
    /// <summary>곡 전체 조옮김 (반음).</summary>
    public int Transpose;
    /// <summary>빗살에 없는 음을 옥타브를 옮겨 있는 줄에 넣는다.</summary>
    public bool FoldOctaves = true;
    /// <summary>음 시작을 이 틱 격자에 맞춘다 (0 = 그대로).</summary>
    public int QuantizeTicks;
    /// <summary>같은 줄 구멍이 이보다 가까우면 경고 (빗살이 다시 울릴 시간이 모자람). 0 = 검사 안 함.</summary>
    public int MinGapTicks;
    /// <summary>뺄 보이스.</summary>
    public HashSet<int> HiddenVoices = new HashSet<int>();
    /// <summary>리듬 모드의 드럼(BD SD TO CY HH)도 넣을지.</summary>
    public bool IncludeDrums;
}

/// <summary>구멍 1개 (음의 시작점만).</summary>
public sealed class OrgelPunch
{
    public int Tick;
    public int Row;
    /// <summary>원래 음 (조옮김 전).</summary>
    public int SourceNote;
    public int Voice;
    /// <summary>옥타브를 옮겨서 넣은 구멍.</summary>
    public bool Folded;
    /// <summary>같은 줄 앞 구멍과 너무 가깝다.</summary>
    public bool TooClose;
}

/// <summary>빗살에 없어 구멍을 못 내는 음.</summary>
public sealed class OrgelMissing
{
    public int Tick;
    public int SourceNote;
    public int Voice;
}

public sealed class OrgelStrip
{
    public List<OrgelPunch> Punches { get; } = new List<OrgelPunch>();
    public List<OrgelMissing> Missing { get; } = new List<OrgelMissing>();
    public int LengthTicks;
    public int TickBeat;
    public int BeatsPerMeasure;
    public int TooCloseCount { get { int n = 0; foreach (var p in Punches) if (p.TooClose) n++; return n; } }
    public int FoldedCount { get { int n = 0; foreach (var p in Punches) if (p.Folded) n++; return n; } }
}

/// <summary>
/// 곡 → 오르골 띠. 오르골은 음의 시작만 구멍을 내므로 길이와 쉼표는 버리고, 간격은 틱 그대로(템포 변화는 무시, 손으로 돌림).
/// </summary>
public static class OrgelBuilder
{
    public static OrgelStrip Build(Song song, OrgelOptions o)
    {
        var strip = new OrgelStrip { TickBeat = song.TickBeat, BeatsPerMeasure = song.BeatsPerMeasure };
        var seen = new HashSet<long>();
        for (int v = 0; v < song.VoiceCount; v++)
        {
            if (o.HiddenVoices.Contains(v)) continue;
            if (IsDrum(song, v) && !o.IncludeDrums) continue;
            foreach (var n in song.Voices[v].Notes)
            {
                int tick = Quantize(n.StartTick, o.QuantizeTicks);
                int note = n.Pitch + o.Transpose;
                bool folded;
                int row = Place(note, o, out folded);
                if (row < 0)
                {
                    strip.Missing.Add(new OrgelMissing { Tick = tick, SourceNote = n.Pitch, Voice = v });
                    continue;
                }
                if (!seen.Add(((long)tick << 8) | (uint)row)) continue;   // 같은 시점 같은 줄은 구멍 하나
                strip.Punches.Add(new OrgelPunch { Tick = tick, Row = row, SourceNote = n.Pitch, Voice = v, Folded = folded });
            }
            strip.LengthTicks = Math.Max(strip.LengthTicks, song.Voices[v].EndTick);
        }
        strip.Punches.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Row.CompareTo(b.Row));
        strip.Missing.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        if (o.MinGapTicks > 0)
        {
            var last = new int[OrgelComb.Rows];
            for (int r = 0; r < last.Length; r++) last[r] = int.MinValue / 2;
            foreach (var p in strip.Punches)
            {
                if (p.Tick - last[p.Row] < o.MinGapTicks) p.TooClose = true;
                last[p.Row] = p.Tick;
            }
        }
        foreach (var p in strip.Punches) strip.LengthTicks = Math.Max(strip.LengthTicks, p.Tick + 1);
        return strip;
    }

    /// <summary>빗살 줄 찾기. 접기를 켜면 가까운 옥타브부터 위아래로 옮겨 본다.</summary>
    public static int Place(int note, OrgelOptions o, out bool folded)
    {
        folded = false;
        int row = OrgelComb.RowOf(note, o.LowestC);
        if (row >= 0 || !o.FoldOctaves) return row;
        for (int k = 1; k <= 4; k++)
        {
            // 범위 밖이면 범위 쪽으로 먼저, 안이면(반음이 없는 낮은 옥타브) 위로 먼저
            int first = note < o.LowestC + 6 ? 1 : -1;
            foreach (int dir in new[] { first, -first })
            {
                row = OrgelComb.RowOf(note + dir * 12 * k, o.LowestC);
                if (row >= 0) { folded = true; return row; }
            }
        }
        return -1;
    }

    /// <summary>옥타브를 옮기지 않고 그대로 들어가는 음이 가장 많은 조옮김 (같으면 0에 가까운 것, 옥타브 단위 우선).</summary>
    public static int BestTranspose(Song song, OrgelOptions o)
    {
        var notes = new List<int>();
        for (int v = 0; v < song.VoiceCount; v++)
        {
            if (o.HiddenVoices.Contains(v) || (IsDrum(song, v) && !o.IncludeDrums)) continue;
            foreach (var n in song.Voices[v].Notes) notes.Add(n.Pitch);
        }
        if (notes.Count == 0) return 0;
        int best = 0, bestScore = int.MinValue;
        for (int t = -48; t <= 48; t++)
        {
            int score = 0;
            foreach (int n in notes) if (OrgelComb.RowOf(n + t, o.LowestC) >= 0) score++;
            // 같은 점수면: 옥타브 단위 > 0에 가까운 것
            score = score * 1000 - Math.Abs(t) * 2 - (t % 12 == 0 ? 0 : 50);
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    private static bool IsDrum(Song song, int v) { return song.IsDrumVoice(v); }

    private static int Quantize(int tick, int q)
    {
        if (q <= 1) return tick;
        return (int)Math.Floor((double)tick / q + 0.5) * q;   // 반은 올림 (뒤 칸으로)
    }
}
