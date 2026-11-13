using System;
using System.Collections.Generic;

namespace AdlibComposer.Notation;

/// <summary>음표 종류 (온음표 = 4박 기준). 숫자 = 2의 거듭제곱 분할 횟수.</summary>
public enum NoteValue { Whole = 0, Half = 1, Quarter = 2, Eighth = 3, Sixteenth = 4, ThirtySecond = 5, SixtyFourth = 6 }

/// <summary>악보에 그릴 음표/쉼표 하나 (마디 안 상대 틱, 길이, 종류, 점, 정확한 표현 여부).</summary>
public struct NoteGlyph
{
    public int Start;
    public int Duration;
    public NoteValue Value;
    public int Dots;
    /// <summary>false면 박당 틱으로 정확히 표현할 수 없어 가장 가까운 음표로 그린 것.</summary>
    public bool Exact;
    /// <summary>셋잇단음표 (같은 종류 음표 3개 = 2개 길이).</summary>
    public bool Triplet;

    public override string ToString() { return Value + (Dots > 0 ? new string('.', Dots) : "") + (Triplet ? "3" : "") + "@" + Start + "x" + Duration + (Exact ? "" : "~"); }
}

/// <summary>틱 길이 → 그릴 수 있는 음표들로 나누기, 음 이름 표기(올림표 기준).</summary>
public static class NoteSpelling
{
    public const int MaxValue = (int)NoteValue.SixtyFourth;

    /// <summary>음표 종류의 틱 길이 (박당 틱으로 나누어떨어지지 않으면 0).</summary>
    public static int Ticks(NoteValue v, int dots, int tickBeat)
    {
        int whole = tickBeat * 4;
        int div = 1 << (int)v;
        if (whole % div != 0) return 0;
        int t = whole / div, add = t, total = t;
        for (int i = 0; i < dots; i++)
        {
            if (add % 2 != 0) return 0;
            add /= 2;
            total += add;
        }
        return total;
    }

    /// <summary>셋잇단음표 길이 (보통 음표의 2/3, 정수가 아니면 0).</summary>
    public static int TripletTicks(NoteValue v, int tickBeat)
    {
        int num = tickBeat * 4 * 2, den = 3 * (1 << (int)v);
        return num % den == 0 ? num / den : 0;
    }

    /// <summary>
    /// 마디 안의 한 음(또는 쉼표)을 그릴 수 있는 음표들로 나눈다 (나뉜 음표는 붙임줄로 잇는다).
    /// 박 중간에서 시작하면 먼저 다음 박까지 채워 박 경계를 보이게 한다. 점음표(1개)도 쓴다.
    /// 보통 음표로 정확히 나눌 수 없으면 (예: 박당 3틱 곡의 1틱) 셋잇단음표로 나눈다.
    /// </summary>
    public static List<NoteGlyph> Split(int start, int duration, int tickBeat)
    {
        var binary = SplitBinary(start, duration, tickBeat);
        if (binary.TrueForAll(g => g.Exact)) return binary;
        var triplet = SplitTriplet(start, duration, Math.Max(1, tickBeat));
        return triplet != null ? triplet : binary;
    }

    /// <summary>셋잇단 나누기: 박 단위는 보통 음표, 박 안은 셋잇단음표. 정확히 안 되면 null.</summary>
    private static List<NoteGlyph> SplitTriplet(int start, int duration, int tickBeat)
    {
        var result = new List<NoteGlyph>();
        int pos = start, rem = duration, guard = 0;
        while (rem > 0 && guard++ < 512)
        {
            int limit = rem;
            int off = pos % tickBeat;
            if (off != 0) limit = Math.Min(limit, tickBeat - off);
            bool found = false;
            // 박 경계에서 시작하고 한 박 이상 남았으면 보통 음표 (박 단위)
            if (off == 0 && limit >= tickBeat)
                for (int v = 0; v <= (int)NoteValue.Quarter && !found; v++)
                    for (int dots = 1; dots >= 0 && !found; dots--)
                    {
                        int t = Ticks((NoteValue)v, dots, tickBeat);
                        if (t > 0 && t <= limit && t % tickBeat == 0)
                        {
                            result.Add(new NoteGlyph { Start = pos, Duration = t, Value = (NoteValue)v, Dots = dots, Exact = true });
                            pos += t; rem -= t; found = true;
                        }
                    }
            if (found) continue;
            for (int v = (int)NoteValue.Quarter; v <= MaxValue && !found; v++)
            {
                int t = TripletTicks((NoteValue)v, tickBeat);
                if (t > 0 && t <= limit && (pos % tickBeat) % t == 0)
                {
                    result.Add(new NoteGlyph { Start = pos, Duration = t, Value = (NoteValue)v, Exact = true, Triplet = true });
                    pos += t; rem -= t; found = true;
                }
            }
            if (!found) return null;
        }
        return result;
    }

    private static List<NoteGlyph> SplitBinary(int start, int duration, int tickBeat)
    {
        var result = new List<NoteGlyph>();
        tickBeat = Math.Max(1, tickBeat);
        int pos = start, rem = duration;
        int guard = 0;
        while (rem > 0 && guard++ < 512)
        {
            int limit = rem;
            int off = pos % tickBeat;
            if (off != 0) limit = Math.Min(limit, tickBeat - off);

            NoteGlyph best = default(NoteGlyph);
            bool found = false;
            for (int v = 0; v <= MaxValue && !found; v++)
                for (int dots = 1; dots >= 0; dots--)
                {
                    int t = Ticks((NoteValue)v, dots, tickBeat);
                    if (t > 0 && t <= limit)
                    {
                        best = new NoteGlyph { Start = pos, Duration = t, Value = (NoteValue)v, Dots = dots, Exact = true };
                        found = true;
                        break;
                    }
                }
            if (!found)
            {
                // 박당 틱이 작아 정확한 음표가 없다: 남은 틱을 가장 가까운 음표 모양으로
                best = new NoteGlyph { Start = pos, Duration = limit, Value = Nearest(limit, tickBeat), Dots = 0, Exact = false };
            }
            result.Add(best);
            pos += best.Duration;
            rem -= best.Duration;
        }
        return result;
    }

    /// <summary>틱 길이에 가장 가까운 음표 종류.</summary>
    public static NoteValue Nearest(int ticks, int tickBeat)
    {
        double whole = Math.Max(1, tickBeat) * 4.0;
        NoteValue best = NoteValue.Whole;
        double bestErr = double.MaxValue;
        for (int v = 0; v <= MaxValue; v++)
        {
            double err = Math.Abs(Math.Log(ticks / (whole / (1 << v))));
            if (err < bestErr) { bestErr = err; best = (NoteValue)v; }
        }
        return best;
    }

    private static readonly int[] StepOfSemitone = { 0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6 };   // C C# D D# E F F# G G# A A# B
    private static readonly int[] AlterOfSemitone = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };
    public static readonly string[] StepNames = { "C", "D", "E", "F", "G", "A", "B" };

    /// <summary>음 번호(60 = C4) → 음이름 단계(0=C..6=B), 변화(0 / +1 올림), 옥타브.</summary>
    public static void Spell(int note, out int step, out int alter, out int octave)
    {
        int pc = ((note % 12) + 12) % 12;
        step = StepOfSemitone[pc];
        alter = AlterOfSemitone[pc];
        octave = note / 12 - 1;
    }

    /// <summary>온음계 위치 (C4 = 4*7+0). 오선 위 높이를 계산할 때 쓴다.</summary>
    public static int Diatonic(int note)
    {
        int step, alter, octave;
        Spell(note, out step, out alter, out octave);
        return octave * 7 + step;
    }

    public static string TypeName(NoteValue v)
    {
        switch (v)
        {
            case NoteValue.Whole: return "whole";
            case NoteValue.Half: return "half";
            case NoteValue.Quarter: return "quarter";
            case NoteValue.Eighth: return "eighth";
            case NoteValue.Sixteenth: return "16th";
            case NoteValue.ThirtySecond: return "32nd";
            default: return "64th";
        }
    }
}

/// <summary>오선 종류: 높은음자리표 / 낮은음자리표 / 한 줄 타악기 보표.</summary>
public enum StaffClef { Treble, Bass, Percussion }

/// <summary>리듬 모드 드럼 보이스 (BD=6 ... HH=10).</summary>
public enum DrumKind { None, BassDrum, Snare, Tom, Cymbal, HiHat }

public static class StaffRules
{
    /// <summary>보이스 노트의 평균 음높이로 자리표를 고른다 (가운데 도 미만이면 낮은음자리표).</summary>
    public static StaffClef ClefFor(ScoreVoice v)
    {
        if (v.Drum != DrumKind.None) return StaffClef.Percussion;
        long sum = 0; int n = 0;
        foreach (var m in v.Measures)
            foreach (var x in m.Notes)
                if (!x.IsRest) { sum += x.Pitch; n++; }
        if (n == 0) return StaffClef.Treble;
        return sum / (double)n < 60 ? StaffClef.Bass : StaffClef.Treble;
    }

    public static DrumKind DrumFor(int voice, bool melodic)
    {
        if (melodic) return DrumKind.None;
        switch (voice)
        {
            case 6: return DrumKind.BassDrum;
            case 7: return DrumKind.Snare;
            case 8: return DrumKind.Tom;
            case 9: return DrumKind.Cymbal;
            case 10: return DrumKind.HiHat;
        }
        return DrumKind.None;
    }

    /// <summary>심벌/하이햇은 X 머리 음표.</summary>
    public static bool XHead(DrumKind d) { return d == DrumKind.Cymbal || d == DrumKind.HiHat; }

    /// <summary>General MIDI 드럼 음 (채널 10).</summary>
    public static int GmDrumNote(DrumKind d)
    {
        switch (d)
        {
            case DrumKind.BassDrum: return 36;
            case DrumKind.Snare: return 38;
            case DrumKind.Tom: return 45;
            case DrumKind.Cymbal: return 49;
            case DrumKind.HiHat: return 42;
        }
        return 0;
    }

    public static string DrumName(DrumKind d)
    {
        switch (d)
        {
            case DrumKind.BassDrum: return "Bass drum";
            case DrumKind.Snare: return "Snare drum";
            case DrumKind.Tom: return "Tom";
            case DrumKind.Cymbal: return "Cymbal";
            case DrumKind.HiHat: return "Hi-hat";
        }
        return "";
    }
}
