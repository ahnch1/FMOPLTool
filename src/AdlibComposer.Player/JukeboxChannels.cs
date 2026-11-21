using System;
using System.Collections.Generic;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Player;

/// <summary>채널 믹서 한 줄의 곡 정보 (곡을 열 때 정해진다).</summary>
public sealed class JukeboxChannel
{
    /// <summary>번호 · 이름 (1 · BD · D10 · F1 · A …).</summary>
    public string Label = "";
    /// <summary>재생 전에 보일 악기 (채널이 처음 쓰는 악기, VGM은 칩 종류). 없으면 null.</summary>
    public string Instrument;
    /// <summary>곡에서 음을 내는 채널인가 (안 쓰면 흐리게).</summary>
    public bool Used;
}

/// <summary>
/// Jukebox 조절 · 채널 믹서 (Windows JukeboxView의 속도 · 키 · 볼륨 · 초기화 · ±5초 · 채널 켜기/끄기 · 솔로 · 채널 음높이 · BPM을 화면 없이).
/// </summary>
public sealed partial class JukeboxSession
{
    private readonly List<JukeboxChannel> channels = new List<JukeboxChannel>();

    /// <summary>지금 곡의 채널 (믹서 줄). 곡이 없으면 빈 목록.</summary>
    public IReadOnlyList<JukeboxChannel> Channels { get { return channels; } }

    /// <summary>키 범위 ±12 반음, 채널 음높이 ±24, 속도 50 ~ 200%, 볼륨 0 ~ 150%.</summary>
    public const int MaxKey = 12, MaxVoicePitch = 24;

    // ---------------------------------------------------------------- 조절

    /// <summary>키 (멜로디 전체 조옮김, 반음). 드럼은 그대로. 재생 중에도 바로 (엔진이 Options를 본다).</summary>
    public void SetKey(int semis) { Options.Transpose = Math.Max(-MaxKey, Math.Min(MaxKey, semis)); }

    public void SetMute(int voice, bool mute)
    {
        if (voice < 0 || voice >= PlaybackOptions.Voices) return;
        Options.VoiceMute[voice] = mute;
        if (State == PlayState.Playing) engine.SetVoiceMute(voice, mute);
    }

    /// <summary>이 채널만 켠다. 이미 혼자 켜져 있으면 모두 켠다.</summary>
    public void Solo(int voice)
    {
        int count = channels.Count;
        if (voice < 0 || voice >= count) return;
        bool alreadySolo = !Options.VoiceMute[voice];
        for (int v = 0; v < count; v++) if (v != voice && !Options.VoiceMute[v]) alreadySolo = false;
        for (int v = 0; v < count; v++) SetMute(v, alreadySolo ? false : v != voice);
    }

    /// <summary>채널 음높이 (반음, 드럼 포함).</summary>
    public void SetVoicePitch(int voice, int semis)
    {
        if (voice < 0 || voice >= PlaybackOptions.Voices) return;
        Options.VoiceTranspose[voice] = Math.Max(-MaxVoicePitch, Math.Min(MaxVoicePitch, semis));
    }

    /// <summary>초기화: 속도 · 키 · 채널 켜기/음높이를 처음 상태로 (볼륨은 그대로).</summary>
    public void ResetAdjustments()
    {
        for (int v = 0; v < PlaybackOptions.Voices; v++) { SetMute(v, false); Options.VoiceTranspose[v] = 0; }
        SetSpeed(1.0);
        SetKey(0);
    }

    /// <summary>곡 시간으로 ±초 (뒤로 5초 · 앞으로 5초).</summary>
    public void SeekBy(double deltaSeconds)
    {
        if (!HasSong) return;
        Seek(Math.Max(0, PositionSeconds + deltaSeconds));
    }

    // ---------------------------------------------------------------- 표시

    /// <summary>재생 중이면 엔진의 보이스 상태를 dst에 채우고 true. 아니면 false (dst는 그대로).</summary>
    public bool GetVoiceStates(VoiceState[] dst)
    {
        if (State != PlayState.Playing) return false;
        engine.GetVoiceStates(dst);
        return true;
    }

    /// <summary>채널에 보일 악기 이름: 재생 중 엔진이 알려 준 것, 없으면 곡을 열 때 정한 것.</summary>
    public string InstrumentOf(int voice, VoiceState vs, bool playing)
    {
        if (playing && songChip && Kind == SongKind.Adlib) return TimbreAt(voice, engine.PositionTick);   // 8-bit: 엔진은 GM 이름 → 곡의 악기 이름으로
        if (playing && vs.Instrument != null) return vs.Instrument;
        return voice >= 0 && voice < channels.Count ? channels[voice].Instrument : null;
    }

    /// <summary>악기를 찾았는가 (ROL · IMS · MUS는 뱅크에 있는가, 나머지는 곡 안 악기라 언제나 true).</summary>
    public bool InstrumentFound(string name)
    {
        if (Kind != SongKind.Adlib) return true;
        return name != null && bank != null && bank.Find(name) != null;
    }

    /// <summary>건반에 칠할 실제 음: 음 + 키 + 채널 음높이 (드럼은 키를 더하지 않음).</summary>
    public int SoundingNote(int voice, VoiceState vs)
    {
        if (vs.Note < 0) return -1;
        return vs.Note + (vs.Drum ? 0 : Options.Transpose + (voice >= 0 && voice < PlaybackOptions.Voices ? Options.VoiceTranspose[voice] : 0));
    }

    /// <summary>지금 템포 (♩=, 속도 반영). VGM 계열처럼 템포가 없으면 0.</summary>
    public double Bpm
    {
        get
        {
            if (!HasSong) return 0;
            double basic;
            switch (Kind)
            {
                case SongKind.Midi: basic = midiSong.InitialBpm; break;
                case SongKind.Ext: basic = extSong.Tempo; break;
                case SongKind.Tracker: basic = trackerSong.Kind == TrackerKind.S3m ? trackerSong.Tempo : 125.0 * 6 / Math.Max(1, trackerSong.Tempo >> 4); break;
                case SongKind.Sop: basic = sopSong.BasicTempo; break;
                case SongKind.Adlib: basic = song.BasicTempo; break;
                default: return 0;
            }
            double mult = State == PlayState.Playing ? engine.TempoMultiplier
                : Kind == SongKind.Sop ? sopSong.TempoAt(pausedTick) / (double)sopSong.BasicTempo : 1.0;
            return basic * mult * Options.Speed;
        }
    }

    // ---------------------------------------------------------------- 채널 목록 (곡을 열 때)

    private void BuildChannels()
    {
        channels.Clear();
        switch (Kind)
        {
            case SongKind.Chip:
            {
                var list = VgmPlayer.ChannelsOf(vgmSong);
                var used = VgmPlayer.ScanUsed(vgmSong);
                for (int c = 0; c < Math.Min(list.Count, PlaybackOptions.Voices); c++)
                {
                    var ch = list[c];
                    Add(ch.Label, ch.Chip == VgmPlayer.ChipKind.Psg ? "PSG" : ch.Chip == VgmPlayer.ChipKind.Scc ? "SCC" : "FM", c < used.Length && used[c]);
                }
                break;
            }
            case SongKind.Ext:
            {
                int n = Math.Min(PlaybackOptions.Voices, extSong.Channels);
                var used = new bool[n];
                foreach (int o in extSong.PlayOrders())
                {
                    var p = extSong.Patterns[o];
                    for (int r = 0; r < p.Rows; r++)
                        for (int c = 0; c < Math.Min(n, p.Channels); c++)
                        {
                            var cell = p[r, c];
                            if (cell.Note >= 1 && cell.Note <= 120 && !extSong.ChannelMuted[c]) used[c] = true;
                        }
                }
                for (int c = 0; c < n; c++) Add(extSong.ChannelLabel(c), FirstExtInstrument(c), used[c]);
                break;
            }
            case SongKind.Tracker:
            {
                int n = Math.Min(PlaybackOptions.Voices, trackerSong.UsedChannelCount);
                var used = new bool[n];
                foreach (var p in trackerSong.Patterns)
                    for (int r = 0; r < TrackerPattern.Rows; r++)
                        for (int c = 0; c < n; c++)
                            if (p[r, c].HasNote && trackerSong.IsChannelUsed(c)) used[c] = true;
                for (int c = 0; c < n; c++) Add(trackerSong.ChannelLabel(c), FirstTrackerSample(c), used[c]);
                break;
            }
            case SongKind.Sop:
                for (int t = 0; t < Math.Min(PlaybackOptions.Voices, sopSong.TrackCount); t++)
                    Add(sopSong.TrackLabel(t), FirstSopInstrument(t), sopSong.Tracks[t].NoteCount > 0);
                break;
            case SongKind.Midi:
                for (int c = 0; c < MidiSong.Channels; c++)
                    Add(c == GeneralMidi.DrumChannel ? "D10" : (c + 1).ToString(), MidiInstrument(c), midiSong.ChannelUsed[c]);
                break;
            case SongKind.Adlib:
            {
                var labels = Song.VoiceLabels(song.IsMelodic);
                for (int v = 0; v < Math.Min(labels.Length, song.VoiceCount); v++)
                {
                    var t = song.Voices[v].Timbres;
                    Add(labels[v], t.Count > 0 ? t[0].InstrumentName : null, song.Voices[v].Notes.Count > 0);
                }
                break;
            }
        }
    }

    private void Add(string label, string instrument, bool used)
    {
        channels.Add(new JukeboxChannel { Label = label ?? "", Instrument = instrument, Used = used });
    }

    private string FirstExtInstrument(int c)
    {
        foreach (int o in extSong.PlayOrders())
        {
            var p = extSong.Patterns[o];
            if (c >= p.Channels) return null;
            for (int r = 0; r < p.Rows; r++)
            {
                int i = p[r, c].Instrument;
                if (i > 0) return extSong.InstrumentName(i);
            }
        }
        return null;
    }

    private string FirstTrackerSample(int c)
    {
        foreach (var p in trackerSong.Patterns)
            for (int r = 0; r < TrackerPattern.Rows; r++)
            {
                if (c >= p.Channels) return null;
                int i = p[r, c].Instrument;
                if (i > 0 && i <= trackerSong.Samples.Count)
                {
                    var s = trackerSong.Samples[i - 1];
                    return s.Name.Trim().Length > 0 ? s.Name.Trim() : s.FileName;
                }
            }
        return null;
    }

    private string MidiInstrument(int c)
    {
        if (c == GeneralMidi.DrumChannel) return "Drums";
        int p = midiSong.FirstProgram[c];
        return GeneralMidi.ProgramNames[p < 0 ? 0 : p];
    }

    private string FirstSopInstrument(int t)
    {
        foreach (var e in sopSong.Tracks[t].Events)
            if (e.Type == SopEventType.Instrument && e.Value < sopSong.Instruments.Count && !sopSong.Instruments[e.Value].IsEmpty)
                return sopSong.Instruments[e.Value].Name;
        return null;
    }
}
