using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>AdLib Visual Composer 곡 (.ROL). 헤더 + 고정 45트랙(템포 1 + 보이스 11 x [노트,악기,볼륨,피치]).</summary>
public sealed class RolFormat : ISongFormat
{
    public const int TrackCount = 45;
    public const int NameLength = 15;

    public string Id { get { return "rol"; } }
    public string DisplayName { get { return "AdLib Visual Composer song"; } }
    public string[] Extensions { get { return new[] { ".rol" }; } }
    public bool CanRead { get { return true; } }
    public bool CanWrite { get { return true; } }

    public Song Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.ASCII, true);
        var song = new Song();
        song.VersionMajor = r.ReadUInt16();
        song.VersionMinor = r.ReadUInt16();
        song.Signature = BinaryUtil.ReadFixedString(r, 40);
        song.TickBeat = r.ReadUInt16();
        song.BeatsPerMeasure = r.ReadUInt16();
        song.ScaleY = r.ReadUInt16();
        song.ScaleX = r.ReadUInt16();
        r.ReadByte();                        // reserved
        song.IsMelodic = r.ReadByte() != 0;
        r.ReadBytes(TrackCount * 2);         // counters (트랙 내용에서 다시 계산)
        r.ReadBytes(38);                     // filler

        // 템포 트랙
        BinaryUtil.ReadFixedString(r, NameLength);
        song.BasicTempo = r.ReadSingle();
        int nTempo = r.ReadUInt16();
        for (int i = 0; i < nTempo; i++)
        {
            int tick = r.ReadUInt16();
            float mult = r.ReadSingle();
            song.TempoEvents.Add(new TempoEvent { Tick = tick, Multiplier = mult });
        }

        for (int v = 0; v < Song.MaxVoices; v++)
        {
            var voice = song.Voices[v];

            // 노트 트랙
            voice.Name = BinaryUtil.ReadFixedString(r, NameLength);
            int nTicks = r.ReadUInt16();
            voice.DeclaredTicks = nTicks;
            int acc = 0;
            while (acc < nTicks)
            {
                if (stream.Position + 4 > stream.Length) throw new EndOfStreamException("ROL 노트 트랙이 잘렸습니다.");
                int note = r.ReadUInt16();
                int dur = r.ReadUInt16();
                if (note != 0 && dur > 0)
                    voice.Notes.Add(new Note { StartTick = acc, Duration = dur, Pitch = note });
                acc += dur;
            }

            // 악기 교체 트랙
            BinaryUtil.ReadFixedString(r, NameLength);
            int nTimbre = r.ReadUInt16();
            for (int i = 0; i < nTimbre; i++)
            {
                int tick = r.ReadUInt16();
                string name = BinaryUtil.ReadFixedString(r, 9);
                r.ReadByte();                // filler
                int unknown = r.ReadUInt16();
                voice.Timbres.Add(new TimbreEvent { Tick = tick, InstrumentName = name, Unknown = unknown });
            }

            // 볼륨 트랙
            BinaryUtil.ReadFixedString(r, NameLength);
            int nVol = r.ReadUInt16();
            for (int i = 0; i < nVol; i++)
            {
                int tick = r.ReadUInt16();
                float vol = r.ReadSingle();
                voice.Volumes.Add(new VolumeEvent { Tick = tick, Volume = vol });
            }

            // 피치 트랙
            BinaryUtil.ReadFixedString(r, NameLength);
            int nPitch = r.ReadUInt16();
            for (int i = 0; i < nPitch; i++)
            {
                int tick = r.ReadUInt16();
                float p = r.ReadSingle();
                voice.Pitches.Add(new PitchEvent { Tick = tick, Pitch = p });
            }
        }
        return song;
    }

    public void Write(Song song, Stream stream)
    {
        var w = new BinaryWriter(stream, Encoding.ASCII, true);

        // 트랙 내용에서 카운터 계산
        var tickCounts = new int[Song.MaxVoices];
        var rows = new List<ushort[]>();
        var voiceBlocks = new List<List<ushort>>();
        for (int v = 0; v < Song.MaxVoices; v++)
        {
            var seq = BuildNoteSequence(song.Voices[v], out tickCounts[v]);
            voiceBlocks.Add(seq);
        }

        w.Write(BinaryUtil.ToUInt16(song.VersionMajor));
        w.Write(BinaryUtil.ToUInt16(song.VersionMinor));
        BinaryUtil.WriteFixedString(w, song.Signature, 40);
        w.Write(BinaryUtil.ToUInt16(song.TickBeat));
        w.Write(BinaryUtil.ToUInt16(song.BeatsPerMeasure));
        w.Write(BinaryUtil.ToUInt16(song.ScaleY));
        w.Write(BinaryUtil.ToUInt16(song.ScaleX));
        w.Write((byte)0);
        w.Write((byte)(song.IsMelodic ? 1 : 0));

        for (int v = 0; v < Song.MaxVoices; v++) w.Write(BinaryUtil.ToUInt16(tickCounts[v]));
        for (int v = 0; v < Song.MaxVoices; v++) w.Write(BinaryUtil.ToUInt16(song.Voices[v].Timbres.Count));
        for (int v = 0; v < Song.MaxVoices; v++) w.Write(BinaryUtil.ToUInt16(song.Voices[v].Volumes.Count));
        for (int v = 0; v < Song.MaxVoices; v++) w.Write(BinaryUtil.ToUInt16(song.Voices[v].Pitches.Count));
        w.Write(BinaryUtil.ToUInt16(song.TempoEvents.Count));
        w.Write(new byte[38]);

        // 템포 트랙
        BinaryUtil.WriteFixedString(w, "Tempo", NameLength);
        w.Write(song.BasicTempo);
        var tempos = new List<TempoEvent>(song.TempoEvents);
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        w.Write(BinaryUtil.ToUInt16(tempos.Count));
        foreach (var t in tempos)
        {
            w.Write(BinaryUtil.ToUInt16(t.Tick));
            w.Write(t.Multiplier);
        }

        for (int v = 0; v < Song.MaxVoices; v++)
        {
            var voice = song.Voices[v];
            string suffix = v.ToString("00");

            BinaryUtil.WriteFixedString(w, string.IsNullOrEmpty(voice.Name) ? "Voix " + suffix : voice.Name, NameLength);
            w.Write(BinaryUtil.ToUInt16(tickCounts[v]));
            foreach (ushort u in voiceBlocks[v]) w.Write(u);

            BinaryUtil.WriteFixedString(w, "Timbre " + suffix, NameLength);
            var timbres = new List<TimbreEvent>(voice.Timbres);
            timbres.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            w.Write(BinaryUtil.ToUInt16(timbres.Count));
            foreach (var t in timbres)
            {
                w.Write(BinaryUtil.ToUInt16(t.Tick));
                BinaryUtil.WriteFixedString(w, AdlibInstrument.NormalizeName(t.InstrumentName), 9);
                w.Write((byte)0);
                w.Write(BinaryUtil.ToUInt16(t.Unknown));
            }

            BinaryUtil.WriteFixedString(w, "Volume " + suffix, NameLength);
            var vols = new List<VolumeEvent>(voice.Volumes);
            vols.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            w.Write(BinaryUtil.ToUInt16(vols.Count));
            foreach (var e in vols)
            {
                w.Write(BinaryUtil.ToUInt16(e.Tick));
                w.Write(e.Volume);
            }

            BinaryUtil.WriteFixedString(w, "Pitch " + suffix, NameLength);
            var pitches = new List<PitchEvent>(voice.Pitches);
            pitches.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            w.Write(BinaryUtil.ToUInt16(pitches.Count));
            foreach (var e in pitches)
            {
                w.Write(BinaryUtil.ToUInt16(e.Tick));
                w.Write(e.Pitch);
            }
        }
        w.Flush();
    }

    /// <summary>절대 시작틱 노트 목록을 ROL의 (note, duration) 연속 배열로 바꾼다 (빈 구간 = note 0).</summary>
    private static List<ushort> BuildNoteSequence(VoiceTrack voice, out int totalTicks)
    {
        var seq = new List<ushort>();
        var notes = new List<Note>(voice.Notes);
        notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
        int cursor = 0;
        foreach (var n in notes)
        {
            int start = Math.Max(n.StartTick, cursor);
            int end = n.EndTick;
            if (end <= start) continue;
            if (start > cursor) AddRun(seq, 0, start - cursor);
            AddRun(seq, Calc.Clamp(n.Pitch, Song.MinNote, Song.MaxNote), end - start);
            cursor = end;
        }
        if (voice.DeclaredTicks > cursor)
        {
            AddRun(seq, 0, voice.DeclaredTicks - cursor);
            cursor = voice.DeclaredTicks;
        }
        totalTicks = cursor;
        return seq;
    }

    private static void AddRun(List<ushort> seq, int note, int duration)
    {
        while (duration > 0)
        {
            int d = Math.Min(duration, 65535);
            seq.Add((ushort)note);
            seq.Add((ushort)d);
            duration -= d;
        }
    }
}
