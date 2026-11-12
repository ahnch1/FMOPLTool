using System;
using System.Collections.Generic;
using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>Song의 이벤트를 칩 샘플 클럭에 맞춰 OplSynth에 전달한다. Step()은 칩 샘플 1개마다 호출.</summary>
public sealed class Sequencer
{
    private enum EvType { Tempo = 0, Timbre = 1, Volume = 2, Pitch = 3, NoteOff = 4, NoteOn = 5 }

    private struct Ev
    {
        public int Tick;
        public EvType Type;
        public int Voice;
        public int Value;
        public float F;
        public string Name;
    }

    private readonly OplSynth synth;
    private readonly List<Ev> events = new List<Ev>();
    private Func<string, AdlibInstrument> resolver;
    private int next;
    private double tick;
    private double ticksPerSample;
    private double basicTicksPerSecond;
    private int endTick;
    private int loopStart = -1, loopEnd = -1;
    private int tailSamples;
    private bool playing;
    private bool ending;

    public Sequencer(OplSynth synth) { this.synth = synth; }

    public bool IsPlaying { get { return playing; } }
    public double PositionTick { get { return tick; } }

    /// <param name="resolver">ROL의 악기 이름 → 악기 (null 반환 시 기본 악기).</param>
    public void Start(Song song, Func<string, AdlibInstrument> resolver, int startTick, int loopStartTick, int loopEndTick)
    {
        this.resolver = resolver;
        synth.AllNotesOff();
        synth.SetRhythmMode(!song.IsMelodic);
        for (int v = 0; v < OplSynth.VoiceCount; v++)
        {
            synth.SetVolume(v, 1.0);
            synth.SetPitchBend(v, 1.0);
            synth.SetInstrument(v, null);
        }
        basicTicksPerSecond = song.BasicTempo / 60.0 * song.TickBeat;
        BuildEvents(song);
        endTick = song.LengthTicks;
        loopStart = loopStartTick;
        loopEnd = loopEndTick;
        ending = false;
        tailSamples = 0;
        SeekTo(Math.Max(0, startTick));
        playing = true;
    }

    public void Stop()
    {
        playing = false;
        synth.AllNotesOff();
    }

    private void BuildEvents(Song song)
    {
        events.Clear();
        foreach (var t in song.TempoEvents)
            events.Add(new Ev { Tick = t.Tick, Type = EvType.Tempo, F = t.Multiplier });
        for (int v = 0; v < song.VoiceCount; v++)
        {
            var vt = song.Voices[v];
            foreach (var e in vt.Timbres) events.Add(new Ev { Tick = e.Tick, Type = EvType.Timbre, Voice = v, Name = e.InstrumentName });
            foreach (var e in vt.Volumes) events.Add(new Ev { Tick = e.Tick, Type = EvType.Volume, Voice = v, F = e.Volume });
            foreach (var e in vt.Pitches) events.Add(new Ev { Tick = e.Tick, Type = EvType.Pitch, Voice = v, F = e.Pitch });
            foreach (var n in vt.Notes)
            {
                events.Add(new Ev { Tick = n.StartTick, Type = EvType.NoteOn, Voice = v, Value = n.Pitch });
                events.Add(new Ev { Tick = n.EndTick, Type = EvType.NoteOff, Voice = v });
            }
        }
        events.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            if (c != 0) return c;
            c = ((int)a.Type).CompareTo((int)b.Type);
            if (c != 0) return c;
            return a.Voice.CompareTo(b.Voice);
        });
    }

    /// <summary>해당 틱으로 이동하며 그 이전의 상태 이벤트(템포/악기/볼륨/피치)만 적용한다.</summary>
    private void SeekTo(int target)
    {
        synth.AllNotesOff();
        double mult = 1.0;
        next = 0;
        while (next < events.Count && events[next].Tick < target)
        {
            var e = events[next];
            switch (e.Type)
            {
                case EvType.Tempo: mult = e.F; break;
                case EvType.Timbre: ApplyTimbre(e); break;
                case EvType.Volume: synth.SetVolume(e.Voice, e.F); break;
                case EvType.Pitch: synth.SetPitchBend(e.Voice, e.F); break;
            }
            next++;
        }
        tick = target;
        SetTempoMultiplier(mult);
    }

    private void SetTempoMultiplier(double mult)
    {
        if (mult < 0.01) mult = 0.01;
        ticksPerSample = basicTicksPerSecond * mult / synth.NativeRate;
    }

    private void ApplyTimbre(Ev e)
    {
        AdlibInstrument ins = resolver != null ? resolver(e.Name) : null;
        synth.SetInstrument(e.Voice, ins);
    }

    /// <summary>칩 샘플 1개만큼 진행. 재생이 끝나면 false.</summary>
    public bool Step()
    {
        if (!playing) return false;
        while (next < events.Count && events[next].Tick <= tick)
        {
            var e = events[next++];
            switch (e.Type)
            {
                case EvType.Tempo: SetTempoMultiplier(e.F); break;
                case EvType.Timbre: ApplyTimbre(e); break;
                case EvType.Volume: synth.SetVolume(e.Voice, e.F); break;
                case EvType.Pitch: synth.SetPitchBend(e.Voice, e.F); break;
                case EvType.NoteOff: synth.NoteOff(e.Voice); break;
                case EvType.NoteOn: synth.NoteOn(e.Voice, e.Value); break;
            }
        }
        tick += ticksPerSample;

        if (loopStart >= 0 && loopEnd > loopStart && tick >= loopEnd)
        {
            SeekTo(loopStart);
            return true;
        }
        if (!ending && tick >= endTick)
        {
            ending = true;
            synth.AllNotesOff();
        }
        if (ending)
        {
            tailSamples++;
            if (tailSamples > synth.NativeRate)   // 약 1초 여운 후 종료
            {
                playing = false;
                return false;
            }
        }
        return true;
    }
}
