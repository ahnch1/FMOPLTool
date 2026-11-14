using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>
/// ROL · IMS 곡(<see cref="Song"/>)을 재생용 <see cref="MidiSong"/>으로 바꾼다 (8-bit 칩 신디로 듣기 위해, 메모리 안에서만).
/// <para>틱은 그대로 (Division = 박당 틱): Jukebox의 위치 · 가사 · 이동이 ROL/IMS 재생과 같다.
/// 보이스 v = MIDI 채널 v (믹서 · 채널 끄기 · 음높이도 그대로). 리듬 모드의 드럼 보이스(6~10 = BD SD TO CY HH)는
/// GS "Use for rhythm part" SysEx로 드럼 채널로 만들고 GM 드럼 음(36 · 38 · 탐 41~48 · 49 · 42)으로 친다.</para>
/// <para>악기 이름 → GM 프로그램 (<see cref="GmBank.GuessProgram"/>), 템포 = 기본 템포 × 템포 이벤트,
/// 볼륨 이벤트와 악기 캐리어 TL → CC7 (AdLib과 같은 TL 곡선: 진폭 = 10^(−0.75 · TL / 20), CC7 = 127 · √진폭),
/// 피치 이벤트(1.0 기준 0~2) → 피치 벤드, 벤드 범위는 RPN 0으로 1반음 (ROL 기본).</para>
/// </summary>
public static class SongToMidi
{
    /// <summary>리듬 모드 드럼 보이스 → GM 드럼 음.</summary>
    public static int DrumNote(int voice, int pitch)
    {
        switch (voice)
        {
            case 6: return 36;                                         // BD
            case 7: return 38;                                         // SD
            case 8: return pitch < 48 ? 41 : pitch < 55 ? 45 : 48;     // TOM (음높이로 낮은 · 가운데 · 높은 탐)
            case 9: return 49;                                         // CY
            default: return 42;                                        // HH
        }
    }

    /// <summary>AdLib 볼륨(0~1)과 악기 TL(0 = 가장 큼)로 CC7 값.</summary>
    public static int VolumeCc(int totalLevel, double volume)
    {
        if (volume <= 0) return 0;   // TL 63(−47dB)도 칩에서는 들리므로 볼륨 0은 무음으로
        int amplitude = 63 - (totalLevel & 0x3F);
        double tl = 63 - amplitude * Math.Max(0, Math.Min(1, volume));
        double a = Math.Pow(10, -0.75 * tl / 20);
        return Math.Max(0, Math.Min(127, (int)Math.Round(127 * Math.Sqrt(a))));
    }

    /// <param name="resolver">악기 이름 → 악기 (뱅크에 없으면 null: 이름만으로 GM 번호를 고른다).</param>
    public static MidiSong Convert(Song song, Func<string, AdlibInstrument> resolver)
    {
        int division = Math.Max(1, song.TickBeat);
        var m = new MidiSong { Title = song.Title ?? "", FormatName = song.IsMelodic ? "ROL/IMS → 8-bit" : "ROL/IMS → 8-bit (리듬)", Division = division, FilePath = song.FilePath };
        var ev = m.Events;
        bool rhythm = !song.IsMelodic;

        // 템포
        double basic = song.BasicTempo > 0 ? song.BasicTempo : 120;
        var tempos = song.TempoEvents.OrderBy(t => t.Tick).ToList();
        if (tempos.Count == 0 || tempos[0].Tick > 0) ev.Add(Tempo(0, basic));
        foreach (var t in tempos) ev.Add(Tempo(t.Tick, basic * (t.Multiplier > 0.01f ? t.Multiplier : 0.01f)));

        // 드럼 보이스 → GS 드럼 파트
        if (rhythm)
            for (int v = 6; v <= 10; v++)
                if (v != 9) ev.Add(new MidiEvent { Tick = 0, Status = 0xF0, SysEx = MidiSysEx.GsData(0x40, 0x10 | MidiSysEx.ChannelToGsBlock(v), 0x15, 1) });

        for (int v = 0; v < Math.Min(song.VoiceCount, Song.MaxVoices); v++)
        {
            var vt = song.Voices[v];
            if (vt == null) continue;
            int ch = v;
            bool drum = rhythm && v >= 6;
            // 벤드 범위 1반음 (RPN 0)
            ev.Add(Cc(0, ch, 101, 0)); ev.Add(Cc(0, ch, 100, 0)); ev.Add(Cc(0, ch, 6, 1));
            ev.Add(Cc(0, ch, 101, 127)); ev.Add(Cc(0, ch, 100, 127));

            // 악기 · 볼륨: 둘 중 하나가 바뀔 때마다 CC7을 다시 (악기 TL이 크기에 들어간다)
            var changes = new SortedDictionary<int, (string ins, double? vol)>();
            foreach (var t in vt.Timbres) changes[t.Tick] = (t.InstrumentName, changes.TryGetValue(t.Tick, out var c0) ? c0.vol : null);
            foreach (var x in vt.Volumes) changes[x.Tick] = (changes.TryGetValue(x.Tick, out var c1) ? c1.ins : null, x.Volume);
            AdlibInstrument ins = null;
            double volume = 1.0;
            int lastCc = -1;
            if (!changes.ContainsKey(0)) changes[0] = (null, null);
            foreach (var kv in changes)
            {
                if (kv.Value.ins != null)
                {
                    ins = resolver != null ? resolver(kv.Value.ins) : null;
                    if (!drum) ev.Add(new MidiEvent { Tick = kv.Key, Status = (byte)(0xC0 | ch), Data1 = (byte)GmBank.GuessProgram(kv.Value.ins, ins) });
                }
                if (kv.Value.vol.HasValue) volume = kv.Value.vol.Value;
                int tl = ins == null ? 0 : (drum && v != 6 ? ins.Modulator.TotalLevel : ins.Carrier.TotalLevel);
                int cc = VolumeCc(tl, volume);
                if (cc != lastCc) { ev.Add(Cc(kv.Key, ch, 7, cc)); lastCc = cc; }
            }

            foreach (var p in vt.Pitches)
            {
                int b = Math.Max(0, Math.Min(16383, (int)Math.Round(8192 + (p.Pitch - 1.0) * 8192)));
                ev.Add(new MidiEvent { Tick = p.Tick, Status = (byte)(0xE0 | ch), Data1 = (byte)(b & 0x7F), Data2 = (byte)(b >> 7) });
            }

            foreach (var n in vt.Notes)
            {
                if (n.Duration <= 0 || n.Pitch <= 0) continue;
                int key = drum ? DrumNote(v, n.Pitch) : Math.Max(0, Math.Min(127, n.Pitch));
                ev.Add(new MidiEvent { Tick = n.StartTick, Status = (byte)(0x90 | ch), Data1 = (byte)key, Data2 = 127 });
                ev.Add(new MidiEvent { Tick = n.EndTick, Status = (byte)(0x80 | ch), Data1 = (byte)key });
            }
        }
        // 곡 끝 (ROL은 마지막 음 뒤 쉼표까지가 곡 길이)
        int end = song.LengthTicks;
        if (end > 0) ev.Add(Cc(end, 0, 110, 0));
        for (int v = 0; v < Math.Min(song.VoiceCount, MidiSong.Channels); v++) if (song.Voices[v] != null) m.TrackNames.Add(song.Voices[v].Name ?? "");
        m.Finish();
        return m;
    }

    private static MidiEvent Tempo(int tick, double bpm) { return new MidiEvent { Tick = tick, Status = 0xFF, Tempo = (int)Math.Round(60000000.0 / bpm) }; }
    private static MidiEvent Cc(int tick, int ch, int cc, int v) { return new MidiEvent { Tick = tick, Status = (byte)(0xB0 | ch), Data1 = (byte)cc, Data2 = (byte)v }; }
}
