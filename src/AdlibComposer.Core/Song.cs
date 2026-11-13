using System;
using System.Collections.Generic;

namespace AdlibComposer.Core;

public sealed class Note
{
    public int StartTick;
    public int Duration;
    /// <summary>ROL 노트 번호 (MIDI 번호와 같음, 12..107, 60 = 가운데 도).</summary>
    public int Pitch;
    public int EndTick { get { return StartTick + Duration; } }
    public Note Clone() { return (Note)MemberwiseClone(); }
}

public sealed class TempoEvent { public int Tick; public float Multiplier = 1f; }
public sealed class TimbreEvent { public int Tick; public string InstrumentName = ""; public int Unknown; }
public sealed class VolumeEvent { public int Tick; public float Volume = 1f; }
public sealed class PitchEvent { public int Tick; public float Pitch = 1f; }
/// <summary>SOP 팬 이벤트. 값은 SOP 그대로: 0 = 오른쪽, 1 = 가운데, 2 = 왼쪽.</summary>
public sealed class PanEvent
{
    public const int Right = 0, Center = 1, Left = 2;
    public int Tick; public int Pan = Center;
    public static string Label(int pan) { return pan == Left ? "L" : pan == Right ? "R" : "C"; }
}

/// <summary>ROL의 보이스 1개 = 노트 + 악기교체 + 볼륨 + 피치 트랙 4종.</summary>
public sealed class VoiceTrack
{
    public string Name { get; set; }
    public List<Note> Notes { get; } = new List<Note>();
    public List<TimbreEvent> Timbres { get; } = new List<TimbreEvent>();
    public List<VolumeEvent> Volumes { get; } = new List<VolumeEvent>();
    public List<PitchEvent> Pitches { get; } = new List<PitchEvent>();
    /// <summary>팬 (SOP 모드에서만 재생 · 저장, ROL/IMS에는 없다).</summary>
    public List<PanEvent> Pans { get; } = new List<PanEvent>();
    /// <summary>파일에 기록돼 있던 트랙 총 틱 (마지막 쉼표 보존용).</summary>
    public int DeclaredTicks { get; set; }

    public VoiceTrack(string name) { Name = name; }

    public int EndTick
    {
        get
        {
            int e = DeclaredTicks;
            foreach (var n in Notes) if (n.EndTick > e) e = n.EndTick;
            return e;
        }
    }

    /// <summary>한 보이스는 동시에 한 음만 낼 수 있으므로 겹치는 노트를 잘라내거나 지운다.</summary>
    public void InsertNote(Note n)
    {
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var o = Notes[i];
            if (ReferenceEquals(o, n)) continue;
            if (o.EndTick <= n.StartTick || o.StartTick >= n.EndTick) continue;
            if (o.StartTick < n.StartTick) o.Duration = n.StartTick - o.StartTick;
            else if (o.EndTick > n.EndTick)
            {
                int end = o.EndTick;
                o.StartTick = n.EndTick;
                o.Duration = end - n.EndTick;
            }
            else Notes.RemoveAt(i);
        }
        if (!Notes.Contains(n)) Notes.Add(n);
        SortNotes();
    }

    public void SortNotes() { Notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick)); }

    public Note FindNote(int tick, int pitch)
    {
        foreach (var n in Notes)
            if (n.Pitch == pitch && tick >= n.StartTick && tick < n.EndTick) return n;
        return null;
    }

    /// <summary>해당 틱에 유효한 악기 교체 이벤트 (없으면 null).</summary>
    public TimbreEvent TimbreAt(int tick)
    {
        TimbreEvent best = null;
        foreach (var t in Timbres)
            if (t.Tick <= tick && (best == null || t.Tick >= best.Tick)) best = t;
        return best;
    }

    public void SetTimbre(int tick, string name)
    {
        foreach (var t in Timbres) if (t.Tick == tick) { t.InstrumentName = name; return; }
        Timbres.Add(new TimbreEvent { Tick = tick, InstrumentName = name });
        Timbres.Sort((a, b) => a.Tick.CompareTo(b.Tick));
    }

    public VoiceTrack Clone()
    {
        var c = new VoiceTrack(Name) { DeclaredTicks = DeclaredTicks };
        foreach (var n in Notes) c.Notes.Add(n.Clone());
        foreach (var t in Timbres) c.Timbres.Add(new TimbreEvent { Tick = t.Tick, InstrumentName = t.InstrumentName, Unknown = t.Unknown });
        foreach (var v in Volumes) c.Volumes.Add(new VolumeEvent { Tick = v.Tick, Volume = v.Volume });
        foreach (var p in Pitches) c.Pitches.Add(new PitchEvent { Tick = p.Tick, Pitch = p.Pitch });
        foreach (var p in Pans) c.Pans.Add(new PanEvent { Tick = p.Tick, Pan = p.Pan });
        return c;
    }
}

/// <summary>
/// 곡 전체 (ROL 1개에 해당). 내부 모델은 ROL 구조(틱 기반, 보이스별 4트랙)를 따른다.
/// SOP 모드(<see cref="IsSop"/>)면 보이스 20개 = SOP 트랙 0~19 (리듬 모드: 1~6, BD SD TO CY HH, 12~20), OPL3로 재생 · SOP로 저장한다.
/// </summary>
public sealed class Song
{
    /// <summary>ROL/IMS 보이스 수 (리듬 모드 11).</summary>
    public const int MaxVoices = 11;
    /// <summary>보이스 배열 크기 = SOP 트랙 수.</summary>
    public const int TotalVoices = 20;
    public const int MelodicVoices = 9;
    public const int MinNote = 12;
    public const int MaxNote = 107;
    /// <summary>ROL/MIDI 가운데 도. 실제 ROL 파일과 adlib.c(MID_C 60, 칩 노트 = ROL - 12)로 확인.</summary>
    public const int MiddleC = 60;

    public int VersionMajor { get; set; } = 0;
    public int VersionMinor { get; set; } = 4;
    public string Signature { get; set; } = "\\roll\\default";
    public int TickBeat { get; set; } = 48;
    public int BeatsPerMeasure { get; set; } = 4;
    public int ScaleY { get; set; } = 1;
    public int ScaleX { get; set; } = 1;
    /// <summary>true = 멜로딕 9채널, false = 리듬 모드 11채널 (파일의 isMelodic).</summary>
    public bool IsMelodic { get; set; } = true;
    /// <summary>true = SOP(OPL3) 곡: 보이스 20개, 팬, 4-op 악기. ROL/IMS로는 저장할 수 없다.</summary>
    public bool IsSop { get; set; }
    public float BasicTempo { get; set; } = 120f;
    /// <summary>피치 이벤트 0.0/2.0이 몇 반음인지 (IMS 헤더 pitchBRange, ROL은 기본 1).</summary>
    public int PitchBendRange { get; set; } = 1;
    public string Title { get; set; } = "Untitled";
    public string FilePath { get; set; }
    /// <summary>파일에 적힌 원래 박당 틱 (IMS는 240, 읽으면서 줄인 경우). 0이면 TickBeat와 같다. ISS 가사 틱 변환에 쓴다.</summary>
    public int FileTickBeat { get; set; }
    public List<TempoEvent> TempoEvents { get; } = new List<TempoEvent>();
    /// <summary>SOP 마스터 볼륨 이벤트 (0~1, 제어 트랙). SOP에서 읽은 값을 보존 · 재생한다 (편집 화면은 없음).</summary>
    public List<VolumeEvent> MasterVolumes { get; } = new List<VolumeEvent>();
    /// <summary>보이스 20개 (ROL/IMS 모드는 앞의 9 / 11개만 쓴다).</summary>
    public VoiceTrack[] Voices { get; } = new VoiceTrack[TotalVoices];

    public Song()
    {
        for (int i = 0; i < TotalVoices; i++) Voices[i] = new VoiceTrack("Voix " + i.ToString("00"));
    }

    public int VoiceCount { get { return IsSop ? TotalVoices : IsMelodic ? MelodicVoices : MaxVoices; } }

    /// <summary>리듬 모드의 드럼 보이스(BD SD TO CY HH = 6~10)인지. ROL 리듬 모드와 SOP 리듬 모드가 같은 번호다.</summary>
    public bool IsDrumVoice(int voice) { return !IsMelodic && voice >= 6 && voice <= 10; }

    /// <summary>지금 모드의 보이스 이름 (VoiceCount개).</summary>
    public string[] Labels { get { return VoiceLabels(IsMelodic, IsSop); } }
    public int MeasureTicks { get { return TickBeat * BeatsPerMeasure; } }

    public int LengthTicks
    {
        get
        {
            int e = 0;
            foreach (var v in Voices) if (v.EndTick > e) e = v.EndTick;
            return e;
        }
    }

    public static string[] VoiceLabels(bool melodic, bool sop = false)
    {
        if (sop)
        {
            var l = new string[TotalVoices];
            for (int i = 0; i < l.Length; i++) l[i] = (i + 1).ToString();
            if (!melodic) { l[6] = "BD"; l[7] = "SD"; l[8] = "TO"; l[9] = "CY"; l[10] = "HH"; }
            return l;
        }
        if (melodic) return new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        return new[] { "1", "2", "3", "4", "5", "6", "BD", "SD", "TO", "CY", "HH" };
    }

    public Song Clone()
    {
        var c = new Song
        {
            VersionMajor = VersionMajor, VersionMinor = VersionMinor, Signature = Signature,
            TickBeat = TickBeat, BeatsPerMeasure = BeatsPerMeasure, ScaleX = ScaleX, ScaleY = ScaleY,
            IsMelodic = IsMelodic, IsSop = IsSop, BasicTempo = BasicTempo, PitchBendRange = PitchBendRange, Title = Title, FilePath = FilePath, FileTickBeat = FileTickBeat
        };
        foreach (var t in TempoEvents) c.TempoEvents.Add(new TempoEvent { Tick = t.Tick, Multiplier = t.Multiplier });
        foreach (var m in MasterVolumes) c.MasterVolumes.Add(new VolumeEvent { Tick = m.Tick, Volume = m.Volume });
        for (int i = 0; i < TotalVoices; i++) c.Voices[i] = Voices[i].Clone();
        return c;
    }
}

public static class NoteHelper
{
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string Name(int note)
    {
        if (note < 0) return "-";
        return Names[note % 12] + (note / 12 - 1).ToString();   // MIDI 표기: 60 = C4
    }

    public static bool IsBlack(int note)
    {
        switch (note % 12) { case 1: case 3: case 6: case 8: case 10: return true; }
        return false;
    }

    /// <summary>ROL 노트 번호 → 주파수 (60 = 가운데 도 = 261.63Hz, 69 = 440Hz).</summary>
    public static double Frequency(double note)
    {
        return 261.6255653 * Math.Pow(2.0, (note - Song.MiddleC) / 12.0);
    }
}

/// <summary>틱 ↔ 초 변환 (템포 배수 이벤트 반영).</summary>
public sealed class TempoMap
{
    private readonly List<TempoEvent> events = new List<TempoEvent>();
    private readonly double basicTicksPerSecond;

    public TempoMap(Song song)
    {
        basicTicksPerSecond = song.BasicTempo / 60.0 * song.TickBeat;
        events.AddRange(song.TempoEvents);
        events.Sort((a, b) => a.Tick.CompareTo(b.Tick));
    }

    public double SecondsAt(int tick)
    {
        double seconds = 0, mult = 1.0;
        int last = 0;
        foreach (var e in events)
        {
            if (e.Tick >= tick) break;
            seconds += (e.Tick - last) / (basicTicksPerSecond * mult);
            last = e.Tick;
            mult = e.Multiplier;
        }
        return seconds + (tick - last) / (basicTicksPerSecond * mult);
    }

    /// <summary>초 → 틱 (SecondsAt의 역).</summary>
    public double TickAt(double seconds)
    {
        if (seconds <= 0) return 0;
        double at = 0, mult = 1.0;
        int last = 0;
        foreach (var e in events)
        {
            double seg = (e.Tick - last) / (basicTicksPerSecond * mult);
            if (at + seg >= seconds) break;
            at += seg;
            last = e.Tick;
            mult = e.Multiplier;
        }
        return last + (seconds - at) * basicTicksPerSecond * mult;
    }
}
