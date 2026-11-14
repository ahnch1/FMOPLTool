using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>ROL · IMS → 8-bit: <see cref="SongToMidi"/> 변환 (틱 · 템포 · 악기 → GM · 볼륨 · 피치 · 드럼 보이스), 칩 신디 재생, 실제 곡.</summary>
public static class SongChipTests
{
    private const int Rate = ChipMidiSynth.Rate;

    public static void Run(Action<string, bool, string> check)
    {
        Guess(check);
        Convert(check);
        Play(check);
        Real(check);
    }

    private static void Guess(Action<string, bool, string> check)
    {
        check("GM guess: STANDARD.BNK name BASS1 → 32", GmBank.GuessProgram("BASS1") == 32, GmBank.GuessProgram("BASS1").ToString());
        check("GM guess: name table PIANO1 → 0, FLUTE1 → 73, TRUMPET1 → 56", GmBank.GuessProgram("PIANO1") == 0 && GmBank.GuessProgram("flute1") == 73 && GmBank.GuessProgram("TRUMPET1") == 56, "");
        check("GM guess: word in unknown name (MYSTRING9 → strings)", GmBank.GuessProgram("MYSTRING9") == 48, GmBank.GuessProgram("MYSTRING9").ToString());
        var decaying = new AdlibInstrument { Name = "ZZ1" };
        decaying.Carrier.Eg = 0; decaying.Carrier.Decay = 3;
        var held = new AdlibInstrument { Name = "ZZ2" };
        held.Carrier.Eg = 1; held.Carrier.Attack = 15;
        check("GM guess: unknown name by envelope (decaying → piano, held → synth lead)", GmBank.GuessProgram("ZZ1", decaying) == 0 && GmBank.GuessProgram("ZZ2", held) == 80,
            GmBank.GuessProgram("ZZ1", decaying) + " / " + GmBank.GuessProgram("ZZ2", held));
        check("Volume → CC7: full = 127, 0.5 ≈ 33 (AdLib TL curve), 0 = 0", SongToMidi.VolumeCc(0, 1) == 127 && Math.Abs(SongToMidi.VolumeCc(0, 0.5) - 33) <= 1 && SongToMidi.VolumeCc(0, 0) == 0,
            SongToMidi.VolumeCc(0, 0.5).ToString());
    }

    /// <summary>멜로디 곡: 보이스 0 = BASS1 A3 두 박, 보이스 1 = PIANO1 볼륨 0.5, 템포 120 → 1박 뒤 2배.</summary>
    private static Song Melodic()
    {
        var s = new Song { TickBeat = 48, BasicTempo = 120, IsMelodic = true, Title = "t" };
        s.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "BASS1" });
        s.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 96, Pitch = 57 });
        s.Voices[1].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "PIANO1" });
        s.Voices[1].Volumes.Add(new VolumeEvent { Tick = 0, Volume = 0.5f });
        s.Voices[1].Notes.Add(new Note { StartTick = 48, Duration = 48, Pitch = 64 });
        s.TempoEvents.Add(new TempoEvent { Tick = 48, Multiplier = 2f });
        return s;
    }

    private static void Convert(Action<string, bool, string> check)
    {
        var song = Melodic();
        var m = SongToMidi.Convert(song, null);
        check("ROL → MIDI: division = ticks per beat, same length", m.Division == 48 && m.LengthTicks == song.LengthTicks, m.Division + " " + m.LengthTicks + "/" + song.LengthTicks);
        var map = new TempoMap(song);
        check("ROL → MIDI: tempo events give the same seconds", Math.Abs(m.TickToSeconds(48) - map.SecondsAt(48)) < 1e-6 && Math.Abs(m.TickToSeconds(96) - map.SecondsAt(96)) < 1e-6,
            m.TickToSeconds(96).ToString("0.000") + " / " + map.SecondsAt(96).ToString("0.000"));
        check("ROL → MIDI: voice = channel, instruments → GM programs", m.FirstProgram[0] == 32 && m.FirstProgram[1] == 0 && m.ChannelUsed[0] && m.ChannelUsed[1], m.FirstProgram[0] + " " + m.FirstProgram[1]);
        var cc7 = m.Events.Where(e => e.Kind == 0xB0 && e.Data1 == 7).ToList();
        check("ROL → MIDI: volume 0.5 → CC7 ≈ 33 on channel 2", cc7.Any(e => e.Channel == 1 && Math.Abs(e.Data2 - 33) <= 1) && cc7.Any(e => e.Channel == 0 && e.Data2 == 127), "");

        // 리듬 모드: 드럼 보이스는 GS 드럼 파트 + GM 드럼 음
        var r = new Song { TickBeat = 48, BasicTempo = 120, IsMelodic = false };
        r.Voices[6].Notes.Add(new Note { StartTick = 0, Duration = 24, Pitch = 48 });
        r.Voices[7].Notes.Add(new Note { StartTick = 24, Duration = 24, Pitch = 60 });
        r.Voices[10].Notes.Add(new Note { StartTick = 0, Duration = 12, Pitch = 60 });
        var rm = SongToMidi.Convert(r, null);
        var ons = rm.Events.Where(e => e.IsNoteOn).ToList();
        check("ROL rhythm → MIDI: BD = ch7 note 36, SD = ch8 note 38, HH = ch11 note 42",
            ons.Any(e => e.Channel == 6 && e.Data1 == 36) && ons.Any(e => e.Channel == 7 && e.Data1 == 38) && ons.Any(e => e.Channel == 10 && e.Data1 == 42),
            string.Join(",", ons.Select(e => e.Channel + ":" + e.Data1)));
        check("ROL rhythm → MIDI: GS drum part SysEx for voices 6 7 8 10, song standard GS",
            rm.Events.Count(e => e.IsSysEx && MidiSysEx.Classify(e.SysEx) == SysExKind.GsRhythmPart) == 4 && rm.Standard == MidiStandard.Gs, "");
    }

    private static MidiPlayer Player(MidiSong s)
    {
        var p = new MidiPlayer(new OplChip(true)) { Synth = MidiSynth.Chip };
        p.Start(s, 0);
        return p;
    }

    private static int Crossings(MidiPlayer p, int skip, int n)
    {
        for (int i = 0; i < skip; i++) { p.Step(); p.GenerateChip(out _, out _); }
        int c = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            p.Step(); p.GenerateChip(out double l, out _);
            int sign = l > 1 ? 1 : l < -1 ? -1 : 0;
            if (sign != 0 && prev != 0 && sign != prev) c++;
            if (sign != 0) prev = sign;
        }
        return c;
    }

    private static void Play(Action<string, bool, string> check)
    {
        // A3 베이스 0.3초 재기 (박당 0.5초, 두 박)
        var p = Player(SongToMidi.Convert(Melodic(), null));
        int c = Crossings(p, Rate / 20, (int)(Rate * 0.3));
        check("ROL 8-bit: bass voice A3 ≈ 220 Hz", Math.Abs(c / 0.3 / 2 - 220) < 6, (c / 0.3 / 2).ToString("0"));

        // 피치 이벤트 2.0 = +1반음 (ROL 벤드 범위 1)
        var bent = Melodic();
        bent.Voices[0].Pitches.Add(new PitchEvent { Tick = 0, Pitch = 2f });
        p = Player(SongToMidi.Convert(bent, null));
        int cb = Crossings(p, Rate / 20, (int)(Rate * 0.3));
        check("ROL 8-bit: pitch event 2.0 → +1 semitone (≈ 233 Hz)", Math.Abs(cb / 0.3 / 2 - 233.1) < 6, (cb / 0.3 / 2).ToString("0"));

        // 리듬 모드 드럼은 PSG로
        var r = new Song { TickBeat = 48, BasicTempo = 120, IsMelodic = false };
        r.Voices[6].Notes.Add(new Note { StartTick = 0, Duration = 24, Pitch = 48 });
        r.Voices[10].Notes.Add(new Note { StartTick = 0, Duration = 12, Pitch = 60 });
        p = Player(SongToMidi.Convert(r, null));
        for (int i = 0; i < Rate / 50; i++) { p.Step(); p.GenerateChip(out _, out _); }
        check("ROL 8-bit: rhythm BD + HH play on PSG", p.IsDrum(6) && p.IsDrum(10) && p.ChipActiveDrums == 2 && p.ChipActiveTones == 0, p.ChipActiveDrums + " / " + p.ChipActiveTones);
    }

    /// <summary>samples/demo.rol + STANDARD.BNK: 길이 · 시간이 같고, 크기가 FM과 비슷한가.</summary>
    private static void Real(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        string res = SopTests.FindDir(Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        string rol = dir != null ? Path.Combine(dir, "demo.rol") : null;
        if (rol == null || !File.Exists(rol) || res == null) { Console.WriteLine("SKIP  ROL 8-bit real file (samples/demo.rol not found)"); return; }
        Song song;
        using (var fs = File.OpenRead(rol)) song = new RolFormat().Read(fs);
        InstrumentBank bank;
        using (var fs = File.OpenRead(Path.Combine(res, "STANDARD.BNK"))) bank = new BnkFormat().Read(fs);
        var m = SongToMidi.Convert(song, n => bank.Find(n));
        var map = new TempoMap(song);
        double s1 = m.TickToSeconds(song.LengthTicks), s2 = map.SecondsAt(song.LengthTicks);
        check("demo.rol → 8-bit: same length and seconds", m.LengthTicks == song.LengthTicks && Math.Abs(s1 - s2) < 0.01, s1.ToString("0.00") + " / " + s2.ToString("0.00") + " s");
        int notes = song.Voices.Take(song.VoiceCount).Sum(v => v.Notes.Count(n => n.Pitch > 0 && n.Duration > 0));
        check("demo.rol → 8-bit: every note converted", m.Events.Count(e => e.IsNoteOn) == notes, m.Events.Count(e => e.IsNoteOn) + " / " + notes);

        double fm, chip;
        using (var e = new AudioEngine())
        {
            e.Play(song, bank);
            fm = Rms(e);
        }
        using (var e = new AudioEngine { MidiSynth = MidiSynth.Chip })
        {
            e.PlayMidi(m);
            chip = Rms(e);
        }
        double ratio = fm > 0 ? chip / fm : 0;
        check("demo.rol 8-bit loudness ≈ FM", ratio > 0.5 && ratio < 2.0, "chip " + chip.ToString("0") + " / fm " + fm.ToString("0") + " = " + ratio.ToString("0.00"));
    }

    private static double Rms(AudioEngine e)
    {
        var buf = new short[Rate * 15];
        e.Render(buf, buf.Length);
        double sq = 0;
        foreach (var v in buf) sq += (double)v * v;
        return Math.Sqrt(sq / buf.Length);
    }
}
