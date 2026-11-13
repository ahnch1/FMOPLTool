using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>SOP 작곡: Song(SOP 모드) ↔ SOP 변환, SOP 쓰기, 왕복, 재생, _incoming/sop 실제 파일 왕복.</summary>
public static class SopComposeTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- 모델
        var song = new Song { IsSop = true, IsMelodic = false, TickBeat = 4, BasicTempo = 100, Title = "Compose" };
        check("SOP mode: 20 voices, labels", song.VoiceCount == 20 && song.Labels.Length == 20 && song.Labels[6] == "BD" && song.Labels[10] == "HH" && song.Labels[11] == "12" && song.Labels[19] == "20");
        check("SOP mode: drums are voices 6~10 only", song.IsDrumVoice(6) && song.IsDrumVoice(10) && !song.IsDrumVoice(11) && !song.IsDrumVoice(5));

        // ---- 악기: 2-op / 4-op / 드럼, 레지스터 왕복
        var two = AdlibInstrument.CreateDefault("piano1");
        two.Modulator.WaveSelect = 5;   // OPL3 웨이브폼 (0~7)
        two.Carrier.Am = 1; two.Carrier.Ksl = 2;
        var four = AdlibInstrument.CreateDefault("organ4");
        four.Sop4OpPair = new byte[] { 0x21, 0x10, 0xF2, 0x24, 0x00, 0x01, 0x22, 0x00, 0xF3, 0x25, 0x01 };
        var bd = AdlibInstrument.CreateDefault("bdrum");
        bd.Percussive = true; bd.VoiceNum = 6;
        var bank = new InstrumentBank();
        bank.Instruments.Clear();
        foreach (var i in new[] { two, four, bd }) bank.Add(i);

        var s2 = SopConvert.ToSop(two);
        var back = SopConvert.ToAdlib(s2, "x");
        check("Instrument 2-op ↔ SOP registers", s2.Type == SopInstrument.Type2Op && s2.Data.Length == 11 && SameOp(back.Modulator, two.Modulator) && SameOp(back.Carrier, two.Carrier)
            && back.Additive == two.Additive && back.Modulator.Feedback == two.Modulator.Feedback);
        var s4 = SopConvert.ToSop(four);
        check("Instrument 4-op keeps operators 3·4", s4.Is4Op && s4.Data.Skip(11).SequenceEqual(four.Sop4OpPair) && SopConvert.ToAdlib(s4, "y").Sop4OpPair.SequenceEqual(four.Sop4OpPair));
        check("Instrument drum type", SopConvert.ToSop(bd).Type == SopInstrument.TypeBd);

        // ---- 곡: 4-op 보이스 1(왼쪽), BD, 둘째 뱅크 보이스 16, 볼륨 · 피치 · 템포 · 마스터 볼륨
        var v0 = song.Voices[0];
        v0.Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "organ4" });
        v0.Pans.Add(new PanEvent { Tick = 0, Pan = PanEvent.Left });
        v0.Volumes.Add(new VolumeEvent { Tick = 4, Volume = 0.5f });
        v0.Pitches.Add(new PitchEvent { Tick = 6, Pitch = 1.5f });
        v0.Notes.Add(new Note { StartTick = 0, Duration = 8, Pitch = 60 });
        v0.Notes.Add(new Note { StartTick = 8, Duration = 4, Pitch = 64 });
        song.Voices[3].Notes.Add(new Note { StartTick = 0, Duration = 4, Pitch = 50 });   // 4-op 짝: 소리 안 남
        song.Voices[6].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "bdrum" });
        song.Voices[6].Notes.Add(new Note { StartTick = 0, Duration = 1, Pitch = 36 });
        song.Voices[15].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "piano1" });
        song.Voices[15].Pans.Add(new PanEvent { Tick = 0, Pan = PanEvent.Right });
        song.Voices[15].Notes.Add(new Note { StartTick = 2, Duration = 6, Pitch = 72 });
        song.Voices[16].Notes.Add(new Note { StartTick = 0, Duration = 2, Pitch = 48 });  // 악기 없음 → 기본 악기
        song.TempoEvents.Add(new TempoEvent { Tick = 8, Multiplier = 1.5f });
        song.MasterVolumes.Add(new VolumeEvent { Tick = 0, Volume = 1f });

        var clone = song.Clone();
        check("Song.Clone keeps SOP mode, pans, master volume", clone.IsSop && clone.Voices[15].Pans.Count == 1 && clone.MasterVolumes.Count == 1 && clone.Voices[15].Notes.Count == 1);

        var sop = SopConvert.FromSong(song, bank.Find);
        check("FromSong header", sop.Percussive && sop.TickBeat == 4 && sop.BasicTempo == 100 && sop.TrackCount == 20);
        check("FromSong 4-op channel mode (track 1 = 4-op, track 4 = pair)", sop.Tracks[0].ChannelMode == 1 && sop.Tracks[3].ChannelMode == 0 && sop.Tracks[15].ChannelMode == 2,
            sop.Tracks[0].ChannelMode + "/" + sop.Tracks[3].ChannelMode);
        check("FromSong only used instruments (+ default)", sop.Instruments.Count == 4, string.Join(",", sop.Instruments.Select(i => i.ShortName)));
        check("FromSong tempo event = absolute BPM", sop.Control.Events.Any(e => e.Type == SopEventType.Tempo && e.Tick == 8 && e.Value == 150));
        check("Silenced 4-op partner voice", SopConvert.IsSilencedPartner(song, bank.Find, 3) && !SopConvert.IsSilencedPartner(song, bank.Find, 4));

        var ms = new MemoryStream();
        SopFormat.Write(sop, ms);
        var read = SopFormat.Read(new MemoryStream(ms.ToArray()));
        check("SOP write → read header", read.Percussive && read.TickBeat == 4 && read.BasicTempo == 100 && read.Title == "Compose" && read.Instruments.Count == 4);
        check("SOP write → read events", Enumerable.Range(0, 20).All(t => SameEvents(sop.Tracks[t], read.Tracks[t])) && SameEvents(sop.Control, read.Control));
        check("SOP write → read instrument data", Enumerable.Range(0, 4).All(i => read.Instruments[i].Type == sop.Instruments[i].Type && read.Instruments[i].Data.SequenceEqual(sop.Instruments[i].Data)));

        List<AdlibInstrument> insts;
        var song2 = SopConvert.ToSong(read, out insts);
        check("ToSong mode", song2.IsSop && !song2.IsMelodic && song2.VoiceCount == 20 && insts.Count == 4);
        check("ToSong notes", Enumerable.Range(0, 20).All(v => SameNotes(song.Voices[v], song2.Voices[v])));
        var p = song2.Voices[0].Pitches.Single();
        var vol = song2.Voices[0].Volumes.Single();
        check("ToSong pan / volume / pitch", song2.Voices[0].Pans.Single().Pan == PanEvent.Left && song2.Voices[15].Pans.Single().Pan == PanEvent.Right
            && Math.Abs(vol.Volume - 0.5f) < 0.01 && Math.Abs(p.Pitch - 1.5f) < 0.001, vol.Volume + " " + p.Pitch);
        check("ToSong tempo multiplier", song2.TempoEvents.Count == 1 && Math.Abs(song2.TempoEvents[0].Multiplier - 1.5f) < 1e-6);
        check("ToSong 4-op instrument", insts.First(i => i.Name == "organ4").Is4Op);

        // 같은 곡을 다시 SOP로 → 같은 바이트
        var bank2 = new InstrumentBank();
        bank2.Instruments.Clear();
        foreach (var i in insts) bank2.Add(i);
        var again = new MemoryStream();
        SopFormat.Write(SopConvert.FromSong(song2, bank2.Find), again);
        check("SOP → Song → SOP same bytes", again.ToArray().SequenceEqual(ms.ToArray()), again.Length + " vs " + ms.Length);

        // 길이 0 노트(다음 노트까지)와 겹친 노트
        var raw = SopFormat.Read(new MemoryStream(SopTests.BuildSop()));
        raw.Tracks[1].Events.Add(new SopEvent { Tick = 0, Type = SopEventType.Note, Value = 60, Duration = 0 });
        raw.Tracks[1].Events.Add(new SopEvent { Tick = 4, Type = SopEventType.Note, Value = 62, Duration = 10 });
        raw.Tracks[1].Events.Add(new SopEvent { Tick = 6, Type = SopEventType.Note, Value = 64, Duration = 2 });
        var rs = SopConvert.ToSong(raw, out insts);
        var n1 = rs.Voices[1].Notes;
        check("ToSong: length 0 → next note, overlap cut", n1.Count == 3 && n1[0].Duration == 4 && n1[1].Duration == 2 && n1[2].Duration == 2,
            string.Join(" ", n1.Select(n => n.StartTick + "+" + n.Duration)));
        check("ToSong skips empty instrument slot", insts.Count == 2);

        // 65535틱보다 먼 이벤트: 특수 이벤트를 끼워 넣는다
        var far = new SopSong { TickBeat = 4, BasicTempo = 120, Tracks = new SopTrack[1] };
        far.Tracks[0] = new SopTrack { ChannelMode = 2 };
        far.Tracks[0].Events.Add(new SopEvent { Tick = 70000, Type = SopEventType.Note, Value = 60, Duration = 4 });
        var fm = new MemoryStream();
        SopFormat.Write(far, fm);
        var fr = SopFormat.Read(new MemoryStream(fm.ToArray()));
        var fn = fr.Tracks[0].Events.First(e => e.Type == SopEventType.Note);
        check("SOP write: long gap", fn.Tick == 70000, fn.Tick.ToString());

        // ROL 리듬 모드 곡도 SOP로 (보이스 0~10 = 트랙 0~10)
        var rol = new Song { IsMelodic = false, TickBeat = 48 };
        rol.Voices[7].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 40 });
        rol.Voices[12].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 40 });   // ROL 모드에서는 안 보이는 보이스: 빠진다
        var rs2 = SopConvert.FromSong(rol, null);
        check("ROL-mode song → SOP", rs2.Percussive && rs2.Tracks[7].NoteCount == 1 && rs2.Tracks[12].NoteCount == 0);
        bool threw = false;
        try { SopConvert.FromSong(new Song { TickBeat = 300 }, null); } catch (InvalidDataException) { threw = true; }
        check("SOP rejects ticks per beat > 255", threw);

        // ---- 재생: 둘째 뱅크 보이스 16(오른쪽)
        var solo = new Song { IsSop = true, IsMelodic = false, TickBeat = 4, BasicTempo = 120 };
        solo.Voices[15].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "piano1" });
        solo.Voices[15].Pans.Add(new PanEvent { Tick = 0, Pan = PanEvent.Right });
        solo.Voices[15].Notes.Add(new Note { StartTick = 0, Duration = 8, Pitch = 69 });
        var engine = new AudioEngine();
        engine.PlaySop(SopConvert.FromSong(solo, bank.Find));
        var buf = new short[2 * 4410];
        double l = 0, r = 0;
        for (int k = 0; k < 3; k++)
        {
            engine.RenderStereo(buf, 4410);
            for (int i = 0; i < 4410; i++) { l += Math.Abs((double)buf[2 * i]); r += Math.Abs((double)buf[2 * i + 1]); }
        }
        check("Composer SOP playback: voice 16 sounds on the right", r > 1000 * 4410 && l < r / 50, (l / 13230).ToString("0") + "/" + (r / 13230).ToString("0"));

        RunReal(check);
    }

    private static bool SameOp(OplOperator a, OplOperator b)
    {
        return a.Am == b.Am && a.Vib == b.Vib && a.Eg == b.Eg && a.Ksr == b.Ksr && a.Multiple == b.Multiple && a.Ksl == b.Ksl && a.TotalLevel == b.TotalLevel
            && a.Attack == b.Attack && a.Decay == b.Decay && a.Sustain == b.Sustain && a.ReleaseRate == b.ReleaseRate && a.WaveSelect == b.WaveSelect;
    }

    private static bool SameEvents(SopTrack a, SopTrack b)
    {
        if (a.ChannelMode != b.ChannelMode || a.Events.Count != b.Events.Count) return false;
        for (int i = 0; i < a.Events.Count; i++)
        {
            var x = a.Events[i]; var y = b.Events[i];
            if (x.Tick != y.Tick || x.Type != y.Type || x.Value != y.Value || x.Duration != y.Duration) return false;
        }
        return true;
    }

    private static bool SameNotes(VoiceTrack a, VoiceTrack b)
    {
        if (a.Notes.Count != b.Notes.Count) return false;
        for (int i = 0; i < a.Notes.Count; i++)
            if (a.Notes[i].StartTick != b.Notes[i].StartTick || a.Notes[i].Duration != b.Notes[i].Duration || a.Notes[i].Pitch != b.Notes[i].Pitch) return false;
        return true;
    }

    // ------------------------------------------------------------ _incoming/sop: 읽기 → Song → SOP → 읽기에서 노트가 같은지

    private static void RunReal(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir(Path.Combine("_incoming", "sop"));
        if (dir == null) { Console.WriteLine("SKIP  real SOP compose round trip (_incoming/sop not found)"); return; }
        foreach (var path in Directory.GetFiles(dir).Where(SopFormat.IsSop).OrderBy(x => x))
        {
            string name = Path.GetFileName(path);
            try
            {
                var s = SopFormat.Load(path);
                List<AdlibInstrument> insts;
                var song = SopConvert.ToSong(s, out insts);
                var bank = new InstrumentBank();
                bank.Instruments.Clear();
                foreach (var i in insts) bank.Add(i);
                var ms = new MemoryStream();
                SopFormat.Write(SopConvert.FromSong(song, bank.Find), ms);
                var r = SopFormat.Read(new MemoryStream(ms.ToArray()));
                int before = Enumerable.Range(0, Math.Min(20, s.TrackCount)).Sum(t => song.Voices[t].Notes.Count);
                int after = r.Tracks.Sum(t => t.NoteCount);
                check("Real SOP " + name + " compose round trip", before == after && Math.Abs(r.SecondsAt(r.LengthTicks) - s.SecondsAt(s.LengthTicks)) < 2.0,
                    before + " → " + after + " notes");
            }
            catch (Exception ex) { check("Real SOP " + name + " compose round trip", false, ex.Message); }
        }
    }
}
