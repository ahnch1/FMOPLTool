using System;
using System.Collections.Generic;
using System.IO;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>
/// SOP 곡 ↔ Composer 곡(<see cref="Song"/> SOP 모드) 변환.
/// <list type="bullet">
/// <item>트랙 0~19 = 보이스 0~19 (리듬 모드면 6~10 = BD SD TO CY HH). 노트 길이 0(다음 노트까지)은 다음 노트까지 늘리고, 겹치면 앞 노트를 자른다 (재생기도 새 노트에서 다시 친다)</item>
/// <item>악기: SOP 곡 안 악기 ↔ 뱅크 악기(<see cref="AdlibInstrument"/>). 레지스터 값 그대로, 4-op는 오퍼레이터 3·4를 <see cref="AdlibInstrument.Sop4OpPair"/>에</item>
/// <item>볼륨 0~127 ↔ 0~1, 피치 0~200(100 = 그대로, 100당 1반음) ↔ 0~2(피치 범위 1반음), 팬 0 R · 1 C · 2 L 그대로, 템포(절대 BPM) ↔ 기본 템포 배수</item>
/// <item>채널 모드: 보이스 1·2·3·12·13·14가 4-op 악기를 쓰면 4-op(모드 1), 그 짝 보이스(+3)는 소리가 나지 않는다 (모드 0). 나머지는 2-op(모드 2)</item>
/// </list>
/// </summary>
public static class SopConvert
{
    private static readonly int[] FourOpTracks = { 0, 1, 2, 11, 12, 13 };

    // ------------------------------------------------------------ 악기

    public static AdlibInstrument ToAdlib(SopInstrument s, string name)
    {
        var d = new byte[22];
        Array.Copy(s.Data, d, Math.Min(22, s.Data.Length));
        var a = new AdlibInstrument { Name = name, LongName = s.LongName, Modulator = ToOperator(d, 0), Carrier = ToOperator(d, 6) };
        a.Modulator.Feedback = (d[5] >> 1) & 7;
        a.Modulator.Con = (d[5] & 1) != 0 ? 0 : 1;   // BNK connector 0 = 가산(C0 비트 0 = 1)
        a.Carrier.Con = a.Modulator.Con;
        if (s.Type >= SopInstrument.TypeBd && s.Type <= SopInstrument.TypeHh) { a.Percussive = true; a.VoiceNum = s.Type; }
        if (s.Is4Op)
        {
            a.Sop4OpPair = new byte[11];
            Array.Copy(d, 11, a.Sop4OpPair, 0, 11);
        }
        return a;
    }

    public static SopInstrument ToSop(AdlibInstrument a)
    {
        int type = a.Is4Op ? SopInstrument.Type4Op
            : a.Percussive && a.VoiceNum >= SopInstrument.TypeBd && a.VoiceNum <= SopInstrument.TypeHh ? a.VoiceNum : SopInstrument.Type2Op;
        var d = new byte[type == SopInstrument.Type4Op ? 22 : 11];
        FromOperator(a.Modulator, d, 0);
        d[5] = (byte)(((a.Modulator.Feedback & 7) << 1) | (a.Additive ? 1 : 0));
        FromOperator(a.Carrier, d, 6);
        if (type == SopInstrument.Type4Op) Array.Copy(a.Sop4OpPair, 0, d, 11, 11);
        return new SopInstrument { Type = type, ShortName = a.Name ?? "", LongName = a.LongName ?? "", Data = d };
    }

    private static OplOperator ToOperator(byte[] d, int i)
    {
        return new OplOperator
        {
            Am = (d[i] >> 7) & 1, Vib = (d[i] >> 6) & 1, Eg = (d[i] >> 5) & 1, Ksr = (d[i] >> 4) & 1, Multiple = d[i] & 0x0F,
            Ksl = d[i + 1] >> 6, TotalLevel = d[i + 1] & 0x3F,
            Attack = d[i + 2] >> 4, Decay = d[i + 2] & 0x0F,
            Sustain = d[i + 3] >> 4, ReleaseRate = d[i + 3] & 0x0F,
            WaveSelect = d[i + 4] & 7,
        };
    }

    private static void FromOperator(OplOperator op, byte[] d, int i)
    {
        d[i] = (byte)((op.Am != 0 ? 0x80 : 0) | (op.Vib != 0 ? 0x40 : 0) | (op.Eg != 0 ? 0x20 : 0) | (op.Ksr != 0 ? 0x10 : 0) | (op.Multiple & 0x0F));
        d[i + 1] = (byte)(((op.Ksl & 3) << 6) | (op.TotalLevel & 0x3F));
        d[i + 2] = (byte)(((op.Attack & 0x0F) << 4) | (op.Decay & 0x0F));
        d[i + 3] = (byte)(((op.Sustain & 0x0F) << 4) | (op.ReleaseRate & 0x0F));
        d[i + 4] = (byte)(op.WaveSelect & 7);
    }

    // ------------------------------------------------------------ SOP → Song

    /// <summary>SOP 곡을 SOP 모드 Song으로. instruments = 곡 안 악기 (이름은 서로 겹치지 않게 바꾼다, 빈 칸은 빠진다).</summary>
    public static Song ToSong(SopSong sop, out List<AdlibInstrument> instruments)
    {
        var song = new Song
        {
            IsSop = true, IsMelodic = !sop.Percussive,
            TickBeat = Math.Max(1, sop.TickBeat), BeatsPerMeasure = Math.Max(1, sop.BeatsPerMeasure),
            BasicTempo = Math.Max(1, sop.BasicTempo), PitchBendRange = 1,
            Title = sop.Title.Length > 0 ? sop.Title : Path.GetFileNameWithoutExtension(sop.FileName ?? ""),
        };

        instruments = new List<AdlibInstrument>();
        var names = new string[sop.Instruments.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sop.Instruments.Count; i++)
        {
            var si = sop.Instruments[i];
            if (si.IsEmpty) continue;
            string n = AdlibInstrument.NormalizeName(si.ShortName.Length > 0 ? si.ShortName : si.LongName);
            if (n.Length == 0) n = "INS" + i;
            n = Unique(n, used);
            used.Add(n);
            names[i] = n;
            instruments.Add(ToAdlib(si, n));
        }

        for (int t = 0; t < sop.TrackCount && t < Song.TotalVoices; t++)
        {
            var vt = song.Voices[t];
            var notes = new List<Note>();
            foreach (var e in sop.Tracks[t].Events)
            {
                switch (e.Type)
                {
                    case SopEventType.Note:
                        notes.Add(new Note { StartTick = e.Tick, Duration = e.Duration, Pitch = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, e.Value)) });
                        break;
                    case SopEventType.Instrument:
                        if (e.Value < names.Length && names[e.Value] != null)
                        {
                            vt.Timbres.RemoveAll(x => x.Tick == e.Tick);
                            vt.Timbres.Add(new TimbreEvent { Tick = e.Tick, InstrumentName = names[e.Value] });
                        }
                        break;
                    case SopEventType.Volume:
                        vt.Volumes.RemoveAll(x => x.Tick == e.Tick);
                        vt.Volumes.Add(new VolumeEvent { Tick = e.Tick, Volume = Math.Min(127, e.Value) / 127f });
                        break;
                    case SopEventType.Pitch:
                        if (e.Value > 200) break;
                        vt.Pitches.RemoveAll(x => x.Tick == e.Tick);
                        vt.Pitches.Add(new PitchEvent { Tick = e.Tick, Pitch = e.Value / 100f });
                        break;
                    case SopEventType.Pan:
                        if (e.Value > 2) break;
                        vt.Pans.RemoveAll(x => x.Tick == e.Tick);
                        vt.Pans.Add(new PanEvent { Tick = e.Tick, Pan = e.Value });
                        break;
                }
            }
            notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                int nextStart = i + 1 < notes.Count ? notes[i + 1].StartTick : -1;
                if (n.Duration <= 0) n.Duration = nextStart >= 0 ? nextStart - n.StartTick : song.TickBeat;
                if (nextStart >= 0 && n.EndTick > nextStart) n.Duration = nextStart - n.StartTick;
                if (n.Duration > 0) vt.Notes.Add(n);
            }
            vt.Timbres.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            vt.Volumes.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            vt.Pitches.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            vt.Pans.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        }

        foreach (var e in sop.Control.Events)
        {
            if (e.Type == SopEventType.Tempo)
            {
                int bpm = e.Value == 0 ? sop.BasicTempo : e.Value;
                song.TempoEvents.RemoveAll(x => x.Tick == e.Tick);
                song.TempoEvents.Add(new TempoEvent { Tick = e.Tick, Multiplier = bpm / (float)song.BasicTempo });
            }
            else if (e.Type == SopEventType.MasterVolume)
                song.MasterVolumes.Add(new VolumeEvent { Tick = e.Tick, Volume = Math.Min(127, e.Value) / 127f });
        }
        song.TempoEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return song;
    }

    private static string Unique(string n, HashSet<string> used)
    {
        if (!used.Contains(n)) return n;
        for (int k = 2; k < 1000; k++)
        {
            string suffix = k.ToString();
            string stem = n.Length + suffix.Length > AdlibInstrument.MaxNameLength ? n.Substring(0, AdlibInstrument.MaxNameLength - suffix.Length) : n;
            if (!used.Contains(stem + suffix)) return stem + suffix;
        }
        return n;
    }

    // ------------------------------------------------------------ Song → SOP

    /// <summary>
    /// Song(보통 SOP 모드)을 SOP 곡으로. resolve = 악기 이름 → 뱅크 악기 (없으면 기본 악기를 그 이름으로 넣는다).
    /// 곡이 쓰는 악기만 쓰인 순서대로 넣는다. SOP 모드가 아닌 곡은 보이스 0~10을 트랙 0~10으로 (리듬 모드 번호가 같다).
    /// </summary>
    public static SopSong FromSong(Song song, Func<string, AdlibInstrument> resolve)
    {
        if (song.TickBeat > 255) throw new InvalidDataException("SOP는 박당 틱이 255까지입니다 (지금 " + song.TickBeat + "). Ticks per Beat를 줄여 주세요.");
        int basic = Math.Max(1, Math.Min(255, (int)Math.Round(song.BasicTempo)));
        var s = new SopSong
        {
            VersionMajor = 0, VersionMinor = 1,
            FileName = song.FilePath != null ? Path.GetFileName(song.FilePath).ToUpperInvariant() : "",
            Title = song.Title ?? "",
            Percussive = !song.IsMelodic,
            TickBeat = Math.Max(1, song.TickBeat),
            BeatsPerMeasure = Math.Max(1, Math.Min(255, song.BeatsPerMeasure)),
            BasicTempo = basic,
            FilePath = song.FilePath,
        };

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fourOp = new bool[Song.TotalVoices];
        Func<string, int> instrumentIndex = name =>
        {
            string key = name ?? "";
            int i;
            if (index.TryGetValue(key, out i)) return i;
            var a = resolve != null && name != null ? resolve(name) : null;
            if (a == null) a = AdlibInstrument.CreateDefault(name ?? "DEFAULT");
            var si = ToSop(a);
            si.ShortName = AdlibInstrument.NormalizeName(name ?? a.Name);
            i = s.Instruments.Count;
            if (i >= 255) throw new InvalidDataException("SOP 악기는 255개까지입니다.");
            s.Instruments.Add(si);
            index[key] = i;
            return i;
        };

        int voices = Math.Min(Song.TotalVoices, song.VoiceCount);
        s.Tracks = new SopTrack[Song.TotalVoices];
        for (int t = 0; t < Song.TotalVoices; t++)
        {
            var tr = new SopTrack { ChannelMode = 2 };
            s.Tracks[t] = tr;
            if (t >= voices) continue;
            var vt = song.Voices[t];
            var ev = new List<KeyValuePair<int, SopEvent>>();   // (같은 틱 순서, 이벤트)

            // 첫 노트 전에 악기가 없으면 기본 악기 (OPL2 재생기의 기본 악기와 같다)
            bool needDefault = vt.Notes.Count > 0 && vt.TimbreAt(vt.Notes[0].StartTick) == null;
            if (needDefault) ev.Add(Ev(0, new SopEvent { Tick = 0, Type = SopEventType.Instrument, Value = instrumentIndex(null) }));
            foreach (var x in vt.Timbres)
            {
                int i = instrumentIndex(x.InstrumentName);
                if (s.Instruments[i].Is4Op) fourOp[t] = true;
                ev.Add(Ev(0, new SopEvent { Tick = x.Tick, Type = SopEventType.Instrument, Value = i }));
            }
            foreach (var x in vt.Pans) ev.Add(Ev(0, new SopEvent { Tick = x.Tick, Type = SopEventType.Pan, Value = Math.Max(0, Math.Min(2, x.Pan)) }));
            foreach (var x in vt.Volumes)
                ev.Add(Ev(0, new SopEvent { Tick = x.Tick, Type = SopEventType.Volume, Value = Math.Max(0, Math.Min(127, (int)Math.Round(x.Volume * 127))) }));
            int range = Math.Max(1, song.PitchBendRange);
            foreach (var x in vt.Pitches)
                ev.Add(Ev(0, new SopEvent { Tick = x.Tick, Type = SopEventType.Pitch, Value = Math.Max(0, Math.Min(200, (int)Math.Round(100 + (x.Pitch - 1.0) * range * 100))) }));
            foreach (var n in vt.Notes)
                ev.Add(Ev(1, new SopEvent { Tick = n.StartTick, Type = SopEventType.Note, Value = n.Pitch, Duration = Math.Min(0xFFFF, n.Duration) }));
            var keyed = new List<int>();
            for (int i = 0; i < ev.Count; i++) keyed.Add(i);
            keyed.Sort((a, b) =>
            {
                int c = ev[a].Value.Tick.CompareTo(ev[b].Value.Tick);
                if (c != 0) return c;
                c = ev[a].Key.CompareTo(ev[b].Key);
                return c != 0 ? c : a.CompareTo(b);
            });
            foreach (int i in keyed) tr.Events.Add(ev[i].Value);
        }
        foreach (int t in FourOpTracks)
        {
            if (!fourOp[t]) continue;
            s.Tracks[t].ChannelMode = 1;
            s.Tracks[t + 3].ChannelMode = 0;
        }

        foreach (var x in song.TempoEvents)
            s.Control.Events.Add(new SopEvent { Tick = x.Tick, Type = SopEventType.Tempo, Value = Math.Max(1, Math.Min(255, (int)Math.Round(song.BasicTempo * x.Multiplier))) });
        foreach (var x in song.MasterVolumes)
            s.Control.Events.Add(new SopEvent { Tick = x.Tick, Type = SopEventType.MasterVolume, Value = Math.Max(0, Math.Min(127, (int)Math.Round(x.Volume * 127))) });
        s.Control.Events.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return s;
    }

    private static KeyValuePair<int, SopEvent> Ev(int order, SopEvent e) { return new KeyValuePair<int, SopEvent>(order, e); }

    /// <summary>
    /// SOP 모드에서 이 보이스가 4-op 짝이라 소리가 나지 않는지: 보이스 4·5·6·15·16·17이고, 3 앞 보이스(1·2·3·12·13·14)가 4-op 악기를 쓸 때.
    /// </summary>
    public static bool IsSilencedPartner(Song song, Func<string, AdlibInstrument> resolve, int voice)
    {
        if (!song.IsSop || resolve == null) return false;
        int owner = voice - 3;
        if (Array.IndexOf(FourOpTracks, owner) < 0) return false;
        foreach (var t in song.Voices[owner].Timbres)
        {
            var a = resolve(t.InstrumentName);
            if (a != null && a.Is4Op) return true;
        }
        return false;
    }
}
