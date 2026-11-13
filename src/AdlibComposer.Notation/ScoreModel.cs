using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Notation;

/// <summary>악보 변환용 중간 모델. Song(틱 기반) → Score(마디/음표 기반) → 각 내보내기 형식.</summary>
public sealed class ScoreNote
{
    /// <summary>마디 시작 기준 상대 틱.</summary>
    public int StartInMeasure;
    public int Duration;
    public int Pitch;
    public bool IsRest;
    /// <summary>다음 마디의 같은 음과 붙임줄로 이어짐.</summary>
    public bool TiedToNext;
    public bool TiedFromPrevious;
}

public sealed class ScoreMeasure
{
    public int Number;           // 1부터
    public int StartTick;
    public int LengthTicks;
    public List<ScoreNote> Notes = new List<ScoreNote>();
}

public sealed class ScoreVoice
{
    public int VoiceIndex;
    public string Name;
    /// <summary>화면 보이스 이름 (1~9 또는 BD SD TO CY HH).</summary>
    public string Label;
    public string InstrumentName;
    /// <summary>리듬 모드 드럼 보이스면 그 종류 (한 줄 타악기 보표로 그린다).</summary>
    public DrumKind Drum;
    public List<ScoreMeasure> Measures = new List<ScoreMeasure>();
}

public sealed class Score
{
    public string Title;
    public int TickBeat;
    public int BeatsPerMeasure;
    public float Tempo;
    public List<ScoreVoice> Voices = new List<ScoreVoice>();
}

public sealed class ScoreOptions
{
    /// <summary>0이면 양자화하지 않음. 예: 틱/박 = 48일 때 12 → 16분음표 격자.</summary>
    public int QuantizeTicks = 0;
    public bool IncludeRests = true;
    public bool SkipEmptyVoices = true;
}

/// <summary>Song → Score 변환. 마디 경계에서 음을 나누고 붙임줄 표시를 단다.</summary>
public static class ScoreBuilder
{
    public static Score Build(Song song, ScoreOptions options)
    {
        if (options == null) options = new ScoreOptions();
        var score = new Score
        {
            Title = song.Title, TickBeat = song.TickBeat,
            BeatsPerMeasure = song.BeatsPerMeasure, Tempo = song.BasicTempo
        };
        int measureLen = Math.Max(1, song.MeasureTicks);
        int total = song.LengthTicks;
        int measureCount = Math.Max(1, (total + measureLen - 1) / measureLen);

        for (int v = 0; v < song.VoiceCount; v++)
        {
            var track = song.Voices[v];
            if (options.SkipEmptyVoices && track.Notes.Count == 0) continue;

            var sv = new ScoreVoice
            {
                VoiceIndex = v, Name = track.Name, Label = song.Labels[v],
                Drum = StaffRules.DrumFor(v, song.IsMelodic)
            };
            var t0 = track.TimbreAt(0);
            sv.InstrumentName = t0 != null ? t0.InstrumentName : "";

            var notes = new List<Note>();
            foreach (var n in track.Notes)
            {
                var c = n.Clone();
                if (options.QuantizeTicks > 0)
                {
                    int q = options.QuantizeTicks;
                    int start = (int)Math.Round((double)c.StartTick / q) * q;
                    int end = (int)Math.Round((double)c.EndTick / q) * q;
                    if (end <= start) end = start + q;
                    c.StartTick = start; c.Duration = end - start;
                }
                notes.Add(c);
            }
            notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));

            for (int m = 0; m < measureCount; m++)
            {
                int mStart = m * measureLen, mEnd = mStart + measureLen;
                var measure = new ScoreMeasure { Number = m + 1, StartTick = mStart, LengthTicks = measureLen };
                int cursor = mStart;
                foreach (var n in notes)
                {
                    if (n.EndTick <= mStart || n.StartTick >= mEnd) continue;
                    int s = Math.Max(n.StartTick, mStart);
                    int e = Math.Min(n.EndTick, mEnd);
                    if (s > cursor && options.IncludeRests)
                        measure.Notes.Add(new ScoreNote { StartInMeasure = cursor - mStart, Duration = s - cursor, IsRest = true });
                    measure.Notes.Add(new ScoreNote
                    {
                        StartInMeasure = s - mStart, Duration = e - s, Pitch = n.Pitch,
                        TiedFromPrevious = n.StartTick < mStart, TiedToNext = n.EndTick > mEnd
                    });
                    cursor = e;
                }
                if (cursor < mEnd && options.IncludeRests)
                    measure.Notes.Add(new ScoreNote { StartInMeasure = cursor - mStart, Duration = mEnd - cursor, IsRest = true });
                sv.Measures.Add(measure);
            }
            score.Voices.Add(sv);
        }
        return score;
    }
}

/// <summary>틱 길이 → 음표 이름 (온음표 = 4박 기준).</summary>
public static class NoteValues
{
    private static readonly string[] Names = { "whole", "half", "quarter", "eighth", "16th", "32nd", "64th" };

    public static string Describe(int durationTicks, int tickBeat)
    {
        if (tickBeat <= 0 || durationTicks <= 0) return "?";
        double whole = tickBeat * 4.0;
        for (int i = 0; i < Names.Length; i++)
        {
            double plain = whole / (1 << i);
            if (Math.Abs(durationTicks - plain) < 0.5) return Names[i];
            if (Math.Abs(durationTicks - plain * 1.5) < 0.5) return Names[i] + " dotted";
            if (Math.Abs(durationTicks - plain * 2.0 / 3.0) < 0.5) return Names[i] + " triplet";
        }
        return durationTicks + " ticks";
    }
}

/// <summary>악보 내보내기 확장 지점 (MusicXML, MIDI, 이미지, PDF 등을 이 인터페이스로 추가).</summary>
public interface IScoreExporter
{
    string Id { get; }
    string DisplayName { get; }
    string Extension { get; }
    void Export(Score score, Stream stream);
}

/// <summary>파이프라인 확인용 텍스트 내보내기.</summary>
public sealed class TextScoreExporter : IScoreExporter
{
    public string Id { get { return "text"; } }
    public string DisplayName { get { return "Text score"; } }
    public string Extension { get { return ".txt"; } }

    public void Export(Score score, Stream stream)
    {
        var sb = new StringBuilder();
        sb.AppendLine(score.Title + "  (" + score.BeatsPerMeasure + " beats/measure, tempo " + score.Tempo + ")");
        foreach (var v in score.Voices)
        {
            sb.AppendLine();
            sb.AppendLine("[" + v.Name + "] " + v.InstrumentName);
            foreach (var m in v.Measures)
            {
                sb.Append("  m" + m.Number + ": ");
                foreach (var n in m.Notes)
                {
                    string d = NoteValues.Describe(n.Duration, score.TickBeat);
                    sb.Append(n.IsRest ? "r" : NoteHelper.Name(n.Pitch));
                    sb.Append("(").Append(d).Append(n.TiedToNext ? "~" : "").Append(") ");
                }
                sb.AppendLine();
            }
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }
}
