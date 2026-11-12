using System;
using System.Collections.Generic;
using System.IO;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;
using AdlibComposer.Notation;

namespace AdlibComposer.SelfTest;

/// <summary>외부 테스트 프레임워크 없이 돌아가는 자체 점검 프로그램. 실패가 있으면 종료 코드 1.</summary>
public static class Program
{
    private static int passed, failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passed++; else failed++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--")) return DemoRender.Run(args);
        TestBnk();
        TestIns();
        TestRol();
        TestTempoMap();
        TestEmulatorPitch();
        TestRhythm();
        TestSequencerTiming();
        TestNotation();
        TestRegistry();
        Console.WriteLine();
        Console.WriteLine("passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }

    private static AdlibInstrument MakeInstrument(string name, int seed)
    {
        var ins = AdlibInstrument.CreateDefault(name);
        ins.Modulator.Ksl = seed % 4; ins.Modulator.Multiple = 1 + seed % 10; ins.Modulator.Feedback = seed % 8;
        ins.Modulator.Attack = 15 - seed % 5; ins.Modulator.Sustain = seed % 16; ins.Modulator.Eg = seed % 2;
        ins.Modulator.Decay = seed % 7; ins.Modulator.ReleaseRate = 3 + seed % 9; ins.Modulator.TotalLevel = 20 + seed;
        ins.Modulator.Am = seed % 2; ins.Modulator.Vib = (seed + 1) % 2; ins.Modulator.Ksr = seed % 2; ins.Modulator.Con = 1;
        ins.Modulator.WaveSelect = seed % 4;
        ins.Carrier.Multiple = 2; ins.Carrier.TotalLevel = 63 - seed; ins.Carrier.WaveSelect = (seed + 1) % 4;
        ins.Percussive = seed % 2 == 1; ins.VoiceNum = seed % 5;
        return ins;
    }

    private static bool SameInstrument(AdlibInstrument a, AdlibInstrument b)
    {
        foreach (OperatorParameter p in Enum.GetValues(typeof(OperatorParameter)))
        {
            if (a.Modulator.Get(p) != b.Modulator.Get(p)) return false;
            if (a.Carrier.Get(p) != b.Carrier.Get(p)) return false;
        }
        return a.Modulator.Con == b.Modulator.Con && a.Carrier.Con == b.Carrier.Con;
    }

    private static void TestBnk()
    {
        var bank = new InstrumentBank();
        bank.Instruments.Add(MakeInstrument("ZETA", 3));
        bank.Instruments.Add(MakeInstrument("ALPHA", 5));
        bank.Instruments.Add(MakeInstrument("Mid", 8));
        var fmt = new BnkFormat();
        var ms = new MemoryStream();
        fmt.Write(bank, ms);
        Check("BNK size = 28 + 3*12 + 3*30", ms.Length == 28 + 3 * 12 + 3 * 30, "len=" + ms.Length);
        ms.Position = 0;
        var back = fmt.Read(ms);
        Check("BNK count", back.Instruments.Count == 3);
        Check("BNK names sorted", back.Instruments[0].Name == "ALPHA" && back.Instruments[1].Name == "Mid" && back.Instruments[2].Name == "ZETA");
        bool same = true;
        foreach (var i in back.Instruments)
        {
            var orig = bank.Find(i.Name);
            same &= orig != null && SameInstrument(orig, i) && orig.Percussive == i.Percussive && orig.VoiceNum == i.VoiceNum;
        }
        Check("BNK parameters round trip", same);
        var ms2 = new MemoryStream();
        fmt.Write(back, ms2);
        Check("BNK byte-identical re-write", ms.ToArray().Length == ms2.ToArray().Length && Same(ms.ToArray(), ms2.ToArray()));
        Check("BNK find case-insensitive", back.Find("alpha") != null);
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void TestIns()
    {
        var ins = MakeInstrument("TEST", 6);
        var fmt = new InsFormat();
        var ms = new MemoryStream();
        fmt.Write(ins, ms);
        Check("INS size = 80", ms.Length == 80, "len=" + ms.Length);
        ms.Position = 0;
        var back = fmt.Read(ms, "TEST");
        Check("INS round trip", SameInstrument(ins, back));
        byte[] early = new byte[54];
        Array.Copy(ms.ToArray(), early, 54);
        var e = fmt.Read(new MemoryStream(early), "EARLY");
        Check("INS 54-byte early variant", e.Carrier.Multiple == ins.Carrier.Multiple && e.Modulator.Attack == ins.Modulator.Attack);
        byte[] modOnly = new byte[28];
        Array.Copy(ms.ToArray(), modOnly, 28);
        var m = fmt.Read(new MemoryStream(modOnly), "DRUM");
        Check("INS modulator-only variant", m.Modulator.Multiple == ins.Modulator.Multiple);
    }

    private static Song MakeSong()
    {
        var song = new Song { TickBeat = 48, BeatsPerMeasure = 4, BasicTempo = 120f, IsMelodic = false, Title = "Test" };
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 48 });
        song.Voices[0].Notes.Add(new Note { StartTick = 96, Duration = 24, Pitch = 55 });
        song.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "PIANO", Unknown = 7 });
        song.Voices[0].Volumes.Add(new VolumeEvent { Tick = 0, Volume = 0.75f });
        song.Voices[0].Pitches.Add(new PitchEvent { Tick = 48, Pitch = 1.25f });
        song.Voices[6].Notes.Add(new Note { StartTick = 0, Duration = 12, Pitch = 24 });
        song.TempoEvents.Add(new TempoEvent { Tick = 96, Multiplier = 2f });
        return song;
    }

    private static void TestRol()
    {
        var song = MakeSong();
        var fmt = new RolFormat();
        var ms = new MemoryStream();
        fmt.Write(song, ms);
        byte[] first = ms.ToArray();
        ms.Position = 0;
        var back = fmt.Read(ms);
        Check("ROL header", back.TickBeat == 48 && back.BeatsPerMeasure == 4 && !back.IsMelodic && Math.Abs(back.BasicTempo - 120f) < 0.001f);
        Check("ROL notes", back.Voices[0].Notes.Count == 2 && back.Voices[0].Notes[1].StartTick == 96 && back.Voices[0].Notes[1].Pitch == 55 && back.Voices[6].Notes.Count == 1);
        Check("ROL timbre/volume/pitch", back.Voices[0].Timbres.Count == 1 && back.Voices[0].Timbres[0].InstrumentName == "PIANO" && back.Voices[0].Timbres[0].Unknown == 7
            && Math.Abs(back.Voices[0].Volumes[0].Volume - 0.75f) < 1e-6 && Math.Abs(back.Voices[0].Pitches[0].Pitch - 1.25f) < 1e-6);
        Check("ROL tempo events", back.TempoEvents.Count == 1 && back.TempoEvents[0].Tick == 96 && Math.Abs(back.TempoEvents[0].Multiplier - 2f) < 1e-6);
        var ms2 = new MemoryStream();
        fmt.Write(back, ms2);
        Check("ROL byte-identical re-write", Same(first, ms2.ToArray()), "len=" + first.Length);
        Check("ROL header size 182 and 45 tracks", first.Length > 182);
        // 트레일링 쉼표 보존
        var s2 = MakeSong();
        s2.Voices[1].DeclaredTicks = 200;
        var m3 = new MemoryStream(); fmt.Write(s2, m3); m3.Position = 0;
        Check("ROL trailing rest kept", fmt.Read(m3).Voices[1].DeclaredTicks == 200);
    }

    private static void TestTempoMap()
    {
        var song = new Song { TickBeat = 48, BasicTempo = 120f };
        song.TempoEvents.Add(new TempoEvent { Tick = 96, Multiplier = 2f });
        var map = new TempoMap(song);
        Check("TempoMap 96 ticks = 1.0s", Math.Abs(map.SecondsAt(96) - 1.0) < 1e-9);
        Check("TempoMap 192 ticks = 1.5s", Math.Abs(map.SecondsAt(192) - 1.5) < 1e-9, map.SecondsAt(192).ToString());
    }

    /// <summary>모듈레이터를 끈 additive 사인파 악기 (음높이/타이밍 측정용).</summary>
    private static AdlibInstrument PureSine(int release)
    {
        var ins = AdlibInstrument.CreateDefault("SINE");
        ins.Modulator.Con = 0;                 // additive
        ins.Modulator.TotalLevel = 0;          // 모듈레이터 소리 없음 (Output Level 0)
        ins.Carrier.TotalLevel = 63;
        ins.Carrier.Attack = 15; ins.Carrier.Decay = 0; ins.Carrier.Sustain = 0;
        ins.Carrier.Eg = 1; ins.Carrier.ReleaseRate = release;
        return ins;
    }

    private static void TestEmulatorPitch()
    {
        var synth = new OplSynth();
        synth.SetInstrument(0, PureSine(15));
        synth.NoteOn(0, 57);   // ROL 57 = A4 = 440Hz
        int rate = synth.NativeRate;
        int crossings = 0, peak = 0;
        short last = 0;
        for (int i = 0; i < rate; i++)
        {
            short s = synth.Generate();
            if (i > 2000)
            {
                if (last < 0 && s >= 0) crossings++;
                peak = Math.Max(peak, Math.Abs((int)s));
            }
            last = s;
        }
        double hz1 = crossings * (double)rate / (rate - 2001);
        Check("OPL pitch: ROL note 57 ~ 440Hz", Math.Abs(hz1 - 440) <= 2.5, "measured=" + hz1.ToString("0.0") + "Hz");
        Check("OPL output level audible", peak > 1000, "peak=" + peak);

        // 한 옥타브 위 = 2배
        synth.NoteOff(0);
        for (int i = 0; i < 2000; i++) synth.Generate();
        synth.NoteOn(0, 69);
        crossings = 0; last = 0;
        for (int i = 0; i < rate; i++)
        {
            short s = synth.Generate();
            if (i > 2000 && last < 0 && s >= 0) crossings++;
            last = s;
        }
        double hz2 = crossings * (double)rate / (rate - 2001);
        Check("OPL pitch: ROL note 69 ~ 880Hz", Math.Abs(hz2 - 880) <= 4, "measured=" + hz2.ToString("0.0") + "Hz");

        // Output Level 반전 매핑 방향
        var map = new OplMapping();
        Check("Mapping: output level 63 -> TL 0 (loudest)", map.TlRegister(63, 1.0) == 0);
        Check("Mapping: output level 0 -> TL 63 (silent)", map.TlRegister(0, 1.0) == 63);
        Check("Mapping: volume 0.5 halves level", map.TlRegister(63, 0.5) == 63 - 32 || map.TlRegister(63, 0.5) == 63 - 31);
    }

    private static int PeakOf(OplSynth synth, int samples)
    {
        int peak = 0;
        for (int i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs((int)synth.Generate()));
        return peak;
    }

    private static void TestRhythm()
    {
        var names = new[] { "BD", "SD", "TOM", "CYM", "HH" };
        for (int k = 0; k < 5; k++)
        {
            var synth = new OplSynth();
            synth.SetRhythmMode(true);
            var ins = AdlibInstrument.CreateDefault(names[k]);
            ins.Modulator.Attack = 15; ins.Modulator.Decay = 2; ins.Modulator.Sustain = 4; ins.Modulator.ReleaseRate = 8;
            ins.Modulator.TotalLevel = 63; ins.Carrier.TotalLevel = 63;
            int voice = 6 + k;
            synth.SetInstrument(voice, ins);
            synth.NoteOn(voice, 36);
            int peak = PeakOf(synth, 5000);
            Check("Rhythm mode " + names[k] + " produces sound", peak > 50, "peak=" + peak);
        }
    }

    private static void TestSequencerTiming()
    {
        var bank = new InstrumentBank();
        var sine = PureSine(15);
        sine.Name = "SINE";
        bank.Add(sine);

        var song = new Song { TickBeat = 48, BeatsPerMeasure = 4, BasicTempo = 120f, IsMelodic = true };
        song.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "SINE" });
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 57 });   // 120BPM, 48틱/박 → 0.5초

        var engine = new AudioEngine();
        engine.Play(song, bank);
        int total = AudioEngine.OutputRate * 3;
        var buf = RenderAll(engine, total);

        int lastLoud = 0, firstLoud = -1;
        for (int i = 0; i < total; i++)
            if (Math.Abs((int)buf[i]) > 500) { lastLoud = i; if (firstLoud < 0) firstLoud = i; }
        double endSec = lastLoud / (double)AudioEngine.OutputRate;
        Check("Sequencer: note sounds from t~0", firstLoud >= 0 && firstLoud < 2000, "first=" + firstLoud);
        Check("Sequencer: note ends at ~0.5s (120BPM)", Math.Abs(endSec - 0.5) < 0.03, "end=" + endSec.ToString("0.000") + "s");
        Check("Sequencer: stops after tail", !engine.IsPlaying);

        // 템포 2배 구간 + 시작 위치 이동
        var fast = MakeFastSong();
        engine.Play(fast, bank);
        var buf2 = RenderAll(engine, AudioEngine.OutputRate * 2);
        int last2 = 0;
        for (int i = 0; i < buf2.Length; i++) if (Math.Abs((int)buf2[i]) > 500) last2 = i;
        Check("Sequencer: tempo multiplier x2 halves duration", Math.Abs(last2 / (double)AudioEngine.OutputRate - 0.25) < 0.03, "end=" + (last2 / (double)AudioEngine.OutputRate).ToString("0.000"));
    }

    private static Song MakeFastSong()
    {
        var song = new Song { TickBeat = 48, BeatsPerMeasure = 4, BasicTempo = 120f };
        song.TempoEvents.Add(new TempoEvent { Tick = 0, Multiplier = 2f });
        song.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "SINE" });
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 57 });
        return song;
    }

    private static short[] RenderAll(AudioEngine engine, int total)
    {
        var all = new short[total];
        var chunk = new short[1000];
        for (int off = 0; off < total; off += chunk.Length)
        {
            int n = Math.Min(chunk.Length, total - off);
            engine.Render(chunk, n);
            Array.Copy(chunk, 0, all, off, n);
        }
        return all;
    }

    private static void TestNotation()
    {
        var song = new Song { TickBeat = 48, BeatsPerMeasure = 4, Title = "N" };
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48 * 6, Pitch = 48 });   // 6박 = 2마디에 걸침
        song.Voices[0].Notes.Add(new Note { StartTick = 48 * 6, Duration = 24, Pitch = 50 });
        var score = ScoreBuilder.Build(song, new ScoreOptions());
        Check("Score: one non-empty voice", score.Voices.Count == 1);
        var v = score.Voices[0];
        Check("Score: 2 measures", v.Measures.Count == 2, "measures=" + v.Measures.Count);
        Check("Score: tie across barline", v.Measures[0].Notes[0].TiedToNext && v.Measures[1].Notes[0].TiedFromPrevious);
        Check("Score: measure 1 fully filled", SumTicks(v.Measures[0]) == 192 && SumTicks(v.Measures[1]) == 192);
        Check("NoteValues quarter", NoteValues.Describe(48, 48) == "quarter");
        Check("NoteValues dotted eighth", NoteValues.Describe(36, 48) == "eighth dotted");
        var ms = new MemoryStream();
        new TextScoreExporter().Export(score, ms);
        Check("TextScoreExporter writes output", ms.Length > 20);
    }

    private static int SumTicks(ScoreMeasure m)
    {
        int t = 0;
        foreach (var n in m.Notes) t += n.Duration;
        return t;
    }

    private static void TestRegistry()
    {
        var reg = FormatRegistry.CreateDefault();
        Check("Registry finds ROL/BNK/INS by extension",
            reg.FindSong("a.ROL") != null && reg.FindBank("x.bnk") != null && reg.FindInstrument("y.ins") != null);
        Check("Registry unknown extension -> null", reg.FindSong("a.ims") == null);
        string f = FormatRegistry.BuildFilter(new IFileFormat[] { reg.Songs[0] });
        Check("Registry open filter", f.Contains("*.rol") && f.Contains("*.*"), f);
    }
}
