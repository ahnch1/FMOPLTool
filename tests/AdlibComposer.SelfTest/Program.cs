using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        AdlibComposer.Audio.Windows.WindowsAudio.Register();
        if (args.Length > 2 && args[0] == "--hootmerge")
        {
            // hoot 게임 목록 합치기: --hootmerge 출력폴더 묶음폴더1 묶음폴더2 … [hoot.xml]  (뒤 묶음일수록 우선)
            var dirs = args.Skip(2).Where(System.IO.Directory.Exists).ToList();
            string hx = args.Skip(2).FirstOrDefault(a => a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(a));
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            var r = AdlibComposer.Formats.HootLists.Merge(dirs, args[1], hx);
            Console.WriteLine("entries " + r.Entries + " -> " + r.Clusters + " (dropped " + r.Dropped + "), files " + r.Files + ", uncertain merges " + r.Uncertain + ", similar pairs " + r.SimilarPairs + ", unencodable " + r.Unencodable + ", errors " + r.Errors.Count + ", " + sw0.ElapsedMilliseconds + " ms");
            Console.WriteLine("kept from: " + string.Join(", ", r.KeptFrom.Select(k => k.Key + " " + k.Value)));
            Console.WriteLine("report: " + r.ReportPath + (r.HootXmlPath.Length > 0 ? ", hoot: " + r.HootXmlPath : ""));
            return 0;
        }
        if (args.Length > 2 && args[0] == "--hootmissing")
        {
            // hoot 목록에 있지만 hoot 폴더에 없는 zip: --hootmissing 목록폴더 출력.csv [hoot폴더]  (폴더 구성은 출력_folders.csv)
            var r = AdlibComposer.Formats.HootMissing.Check(args[1], args.Length > 3 ? args[3] : null);
            AdlibComposer.Formats.HootMissing.WriteCsv(r, args[2]);
            Console.WriteLine("games " + r.Games + ", zips " + r.Zips + ", present " + r.Present + ", missing " + r.Missing.Count + ", no archive " + r.NoArchive);
            Console.WriteLine("csv: " + r.MissingCsv + ", folders: " + r.FoldersCsv);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--extsurvey") return ExtModuleTests.Survey(args[1]);   // 진단: --extsurvey 폴더 (XM · IT)
        if (args.Length > 1 && args[0] == "--extrender")   // 진단: --extrender 폴더|파일 [초] [wav 폴더]
            return ExtModuleTests.Render(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 20, args.Length > 3 ? args[3] : null);
        if (args.Length > 1 && args[0] == "--gbs")
        {
            // 진단: --gbs 파일 [곡] [초]
            var b = System.IO.File.ReadAllBytes(args[1]);
            int song = args.Length > 2 ? int.Parse(args[2]) : 0;
            double sec = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 2;
            Console.WriteLine(AdlibComposer.Audio.Gbs.Describe(b, song, sec));
            return 0;
        }
        if (args.Length > 1 && args[0] == "--hes")
        {
            // 진단: --hes 파일 [곡] [초]
            if (args.Length > 2 && args[2] == "list")
            {
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                var songs = AdlibComposer.Audio.Hes.SoundingSongs(args[1]);
                Console.WriteLine(songs.Count + " songs in " + sw0.ElapsedMilliseconds + " ms: " + string.Join(",", songs));
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                var bytes0 = System.IO.File.ReadAllBytes(args[1]);
                long worst = 0; int worstSong = -1;
                for (int k = 0; k < 256; k++) { var t0 = System.Diagnostics.Stopwatch.StartNew(); AdlibComposer.Audio.Hes.Signature(bytes0, k, 2.0); if (t0.ElapsedMilliseconds > worst) { worst = t0.ElapsedMilliseconds; worstSong = k; } }
                Console.WriteLine("sequential " + sw1.ElapsedMilliseconds + " ms, worst song " + worstSong + " = " + worst + " ms");
                return 0;
            }
            var b = System.IO.File.ReadAllBytes(args[1]);
            int song = args.Length > 2 ? int.Parse(args[2]) : 0;
            double sec = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 2;
            Console.WriteLine(AdlibComposer.Audio.Hes.Describe(b, song, sec));
            return 0;
        }
        if (args.Length > 1 && args[0] == "--hoot98trace")
            return HootPc98Tests.TraceEntry(args[1], args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 3);
        if (args.Length > 1 && args[0] == "--hootatsurvey")
            return HootPc98Tests.SurveyAt(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        if (args.Length > 1 && args[0] == "--hootmidisurvey")
            return HootPc98Tests.SurveyMidi(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        if (args.Length > 1 && args[0] == "--hoottree") return HootPc98Tests.TreeStats(args[1]);
        if (args.Length > 1 && args[0] == "--hootx68trace")
            return HootX68kTests.TraceZip(args[1], args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 5);
        if (args.Length > 1 && args[0] == "--hootx68midisurvey")
            return HootX68kTests.SurveyMidi(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10);
        if (args.Length > 1 && args[0] == "--hootx68survey")
        {
            // 진단: --hootx68survey X68k폴더 [zip 수] [초]
            return HootX68kTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootkoeitrace") return KoeiTrace.Run(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 10);
        if (args.Length > 1 && args[0] == "--hootmdprobe") return HootMegaDriveTests.Probe(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
        if (args.Length > 1 && args[0] == "--hootmdsurvey")
        {
            // 진단: --hootmdsurvey smd폴더 [zip 수] [초]
            return HootMegaDriveTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootspcmsurvey")
        {
            // 진단: --hootspcmsurvey roms폴더 [zip 수] [초] (세가 PCM 보드)
            return HootSegaPcmTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootcps2survey")
        {
            // 진단: --hootcps2survey roms폴더 [zip 수] [초] [명령 모양 0|1]
            return HootCps2Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6, args.Length > 4 ? int.Parse(args[4]) : 0);
        }
        if (args.Length > 1 && args[0] == "--hoots16survey")
        {
            // 진단: --hoots16survey roms폴더 [zip 수] [초] (세가 System 16A · 16B)
            return HootSegaSystem16Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootms1survey")
        {
            // 진단: --hootms1survey roms폴더 [zip 수] [초] (잘레코 Mega System 1)
            return HootMegaSys1Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootdecosurvey")
        {
            // 진단: --hootdecosurvey roms폴더 [zip 수] [초] (데이터 이스트 HuC6280 보드)
            return HootDecoTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootarcadesurvey")
        {
            // 진단: --hootarcadesurvey roms폴더 [zip 수] [초] (세가 System 1 · 아이렘 M72)
            return HootArcadeZ80Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootopmsurvey")
        {
            // 진단: --hootopmsurvey roms폴더 [zip 수] [초]
            return HootOpmBoardTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hoottaitosurvey")
        {
            // 진단: --hoottaitosurvey roms폴더 [zip 수] [초]
            return HootTaitoTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 100, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--hootneosurvey")
        {
            // 진단: --hootneosurvey roms폴더 [zip 수] [초]
            return HootNeoGeoTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 8);
        }
        if (args.Length > 1 && args[0] == "--hootx1survey")
        {
            // 진단: --hootx1survey 폴더 [zip 수] [초]
            return HootX1Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 8);
        }
        if (args.Length > 1 && args[0] == "--hoot88survey")
        {
            // 진단: --hoot88survey 폴더 [zip 수] [초]
            return HootPc88Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 8);
        }
        if (args.Length > 1 && args[0] == "--hootmsxsurvey")
        {
            // 진단: --hootmsxsurvey 폴더 [zip 수] [초]
            return HootMsxTests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 10);
        }
        if (args.Length > 1 && args[0] == "--hoot98survey")
        {
            // 진단: --hoot98survey 폴더 [zip 수] [초]
            return HootPc98Tests.Survey(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 6);
        }
        if (args.Length > 1 && args[0] == "--test")
        {
            // 빠른 시험: 이름에 글자가 든 묶음만 (예: --test chip)
            var suites = new (string, Action)[]
            {
                ("vgm", () => VgmTests.Run(Check)), ("vgmexport", () => VgmExportTests.Run(Check)), ("vgmtosong", () => VgmToSongTests.Run(Check)), ("newchip", () => NewChipTests.Run(Check)),
                ("chip", () => ChipTests.Run(Check)), ("chipmidi", () => ChipMidiTests.Run(Check)), ("midi", () => MidiTests.Run(Check)), ("jukebox", () => JukeboxTests.Run(Check)),
                ("extra", () => ExtraTests.Run(Check)), ("queue", () => EngineQueueTests.Run(Check)), ("nespce", () => NesPceTests.Run(Check)), ("zip", () => ZipTests.Run(Check)), ("ext", () => ExtModuleTests.Run(Check)), ("droraw", () => DroRawTests.Run(Check)), ("hoot", () => HootTests.Run(Check)), ("spc", () => SpcTests.Run(Check)), ("pcfmt", () => PcFormatTests.Run(Check)), ("ym2610", () => Ym2610Tests.Run(Check)), ("small", () => SmallChipTests.Run(Check)), ("gbs", () => GbsTests.Run(Check)), ("mdx", () => MdxTests.Run(Check)), ("x86", () => X86Tests.Run(Check)), ("dos", () => DosTests.Run(Check)), ("pc98", () => Pc98Tests.Run(Check)), ("hoot98", () => HootPc98Tests.Run(Check)), ("hootgen", () => HootGenericTests.Run(Check)), ("hootmsx", () => HootMsxTests.Run(Check)), ("hoot88", () => HootPc88Tests.Run(Check)), ("hootx1", () => HootX1Tests.Run(Check)), ("hootneo", () => HootNeoGeoTests.Run(Check)), ("hoottaito", () => HootTaitoTests.Run(Check)), ("hoottaitoopm", () => HootTaitoOpmTests.Run(Check)), ("hootopm", () => HootOpmBoardTests.Run(Check)), ("hootarcade", () => HootArcadeZ80Tests.Run(Check)), ("hootdeco", () => HootDecoTests.Run(Check)), ("hootms1", () => HootMegaSys1Tests.Run(Check)), ("hoots16", () => HootSegaSystem16Tests.Run(Check)), ("hootcps2", () => HootCps2Tests.Run(Check)), ("hootspcm", () => HootSegaPcmTests.Run(Check)), ("hootmd", () => HootMegaDriveTests.Run(Check)), ("hootx68", () => HootX68kTests.Run(Check)), ("pcat", () => PcAtTests.Run(Check)),
            };
            foreach (var (name, run) in suites) if (name.Contains(args[1], StringComparison.OrdinalIgnoreCase)) run();
            Console.WriteLine();
            Console.WriteLine("passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }
        if (args.Length > 0 && (args[0] == "--probe" || args[0] == "--render" || args[0] == "--latency" || args[0] == "--drum" || args[0] == "--drumshape")) return Probe.Run(args);
        if (args.Length > 0 && args[0] == "--transcribe") return TranscribeTests.Probe(args);
        if (args.Length > 0 && args[0].StartsWith("--")) return DemoRender.Run(args);
        TestBnk();
        TestIns();
        TestRol();
        TestTempoMap();
        TestEmulatorPitch();
        TestRhythm();
        TestSequencerTiming();
        TestNotation();
        NotationExportTests.Run(Check);
        JukeboxTests.Run(Check);
        SopTests.Run(Check);
        NukedTests.Run(Check);
        MidiTests.Run(Check);
        TranscribeTests.Run(Check);
        SopComposeTests.Run(Check);
        FeatureTests.Run(Check);
        OrgelTests.Run(Check);
        TrackerTests.Run(Check);
        ChipTests.Run(Check);
        VgmTests.Run(Check);
        VgmExportTests.Run(Check);
        VgmToSongTests.Run(Check);
        EngineQueueTests.Run(Check);
        NewChipTests.Run(Check);
        MidiSysExTests.Run(Check);
        ChipMidiTests.Run(Check);
        SongChipTests.Run(Check);
        TestRegistry();
        ExtraTests.Run(Check);
        NesPceTests.Run(Check);
        ZipTests.Run(Check);
        ExtModuleTests.Run(Check);
        DroRawTests.Run(Check);
        HootTests.Run(Check);
        SpcTests.Run(Check);
        PcFormatTests.Run(Check);
        Ym2610Tests.Run(Check);
        SmallChipTests.Run(Check);
        GbsTests.Run(Check);
        MdxTests.Run(Check);
        X86Tests.Run(Check);
        DosTests.Run(Check);
        Pc98Tests.Run(Check);
        HootPc98Tests.Run(Check);
        HootGenericTests.Run(Check);
        HootMsxTests.Run(Check);
        HootPc88Tests.Run(Check);
        HootX1Tests.Run(Check);
        HootNeoGeoTests.Run(Check);
        HootTaitoTests.Run(Check);
        HootOpmBoardTests.Run(Check);
        HootArcadeZ80Tests.Run(Check);
        HootDecoTests.Run(Check);
        HootMegaSys1Tests.Run(Check);
        HootSegaSystem16Tests.Run(Check);
        HootCps2Tests.Run(Check);
        HootSegaPcmTests.Run(Check);
        HootMegaDriveTests.Run(Check);
        HootX68kTests.Run(Check);
        PcAtTests.Run(Check);
        RealFileTests.Run(Check);
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
        ins.Carrier.Multiple = 2; ins.Carrier.TotalLevel = seed; ins.Carrier.WaveSelect = (seed + 1) % 4;
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
        ins.Modulator.TotalLevel = 63;         // 모듈레이터 소리 없음 (TL 63 = 무음)
        ins.Carrier.TotalLevel = 0;
        ins.Carrier.Attack = 15; ins.Carrier.Decay = 0; ins.Carrier.Sustain = 0;
        ins.Carrier.Eg = 1; ins.Carrier.ReleaseRate = release;
        return ins;
    }

    private static void TestEmulatorPitch()
    {
        var synth = new OplSynth();
        synth.SetInstrument(0, PureSine(15));
        synth.NoteOn(0, 69);   // ROL 69 = A4 = 440Hz
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
        Check("OPL pitch: ROL note 69 ~ 440Hz", Math.Abs(hz1 - 440) <= 2.5, "measured=" + hz1.ToString("0.0") + "Hz");
        Check("OPL output level audible", peak > 1000, "peak=" + peak);

        // 한 옥타브 위 = 2배
        synth.NoteOff(0);
        for (int i = 0; i < 2000; i++) synth.Generate();
        synth.NoteOn(0, 81);
        crossings = 0; last = 0;
        for (int i = 0; i < rate; i++)
        {
            short s = synth.Generate();
            if (i > 2000 && last < 0 && s >= 0) crossings++;
            last = s;
        }
        double hz2 = crossings * (double)rate / (rate - 2001);
        Check("OPL pitch: ROL note 81 ~ 880Hz", Math.Abs(hz2 - 880) <= 4, "measured=" + hz2.ToString("0.0") + "Hz");

        // 파일 TL = OPL 레지스터 (0 = 최대). 화면 Output Level = 63 - TL.
        var map = new OplMapping();
        Check("Mapping: file TL 0 -> register 0 (loudest)", map.TlRegister(0, 1.0) == 0);
        Check("Mapping: file TL 63 -> register 63 (silent)", map.TlRegister(63, 1.0) == 63);
        Check("Mapping: volume 0.5 halves amplitude", map.TlRegister(0, 0.5) == 31 || map.TlRegister(0, 0.5) == 32);
        var op = new OplOperator { TotalLevel = 0 };
        Check("UI Output Level = 63 - TL", op.Get(OperatorParameter.OutputLevel) == 63);
        op.Set(OperatorParameter.OutputLevel, 50);
        Check("UI Output Level set -> TL", op.TotalLevel == 13);
        Check("Note names: 60 = C4, 69 = A4", NoteHelper.Name(60) == "C4" && NoteHelper.Name(69) == "A4");
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
            ins.Modulator.TotalLevel = 0; ins.Carrier.TotalLevel = 0;
            int voice = 6 + k;
            synth.SetInstrument(voice, ins);
            synth.NoteOn(voice, 36);
            int peak = PeakOf(synth, 5000);
            Check("Rhythm mode " + names[k] + " produces sound", peak > 1000, "peak=" + peak);
        }

        // 실제 STANDARD.BNK bdrum 값 (두 오퍼레이터 Frequency Multiplier 0 = x0.5)
        {
            var synth = new OplSynth();
            synth.SetRhythmMode(true);
            var bd = new AdlibInstrument { Name = "bdrum" };
            bd.Modulator = new OplOperator { Multiple = 0, Attack = 10, Sustain = 4, Decay = 8, ReleaseRate = 12, TotalLevel = 11, Con = 1 };
            bd.Carrier = new OplOperator { Multiple = 0, Attack = 13, Sustain = 4, Decay = 6, ReleaseRate = 15, TotalLevel = 0, Con = 1 };
            synth.SetInstrument(6, bd);
            synth.NoteOn(6, 36);
            int peak = PeakOf(synth, 5000);
            Check("Rhythm mode BD with bdrum (mult 0) audible", peak > 1000, "peak=" + peak);
        }

        // 탐탐 음높이가 노트를 따라간다 (한 옥타브 위 = 약 2배 주파수)
        {
            double[] hz = new double[2];
            int[] notes = { 48, 60 };
            for (int k = 0; k < 2; k++)
            {
                var synth = new OplSynth();
                synth.SetRhythmMode(true);
                var tom = AdlibInstrument.CreateDefault("TOM");
                tom.Modulator.Attack = 15; tom.Modulator.Decay = 0; tom.Modulator.Sustain = 0; tom.Modulator.Eg = 1; tom.Modulator.TotalLevel = 0;
                synth.SetInstrument(8, tom);
                synth.NoteOn(8, notes[k]);
                int rate = synth.NativeRate, crossings = 0;
                short last = 0;
                for (int i = 0; i < rate / 2; i++)
                {
                    short x = synth.Generate();
                    if (i > 500 && last < 0 && x >= 0) crossings++;
                    last = x;
                }
                hz[k] = crossings * (double)rate / (rate / 2 - 501);
            }
            double ratio = hz[1] / Math.Max(1, hz[0]);
            Check("Rhythm mode TOM pitch follows note (octave ~ x2)", ratio > 1.8 && ratio < 2.2, hz[0].ToString("0") + "Hz -> " + hz[1].ToString("0") + "Hz");
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
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 69 });   // 120BPM, 48틱/박 → 0.5초

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
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 48, Pitch = 69 });
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
        Check("Registry unknown extension -> null", reg.FindSong("a.sop") == null);
        Check("Registry finds IMS", reg.FindSong("A.IMS") is ImsFormat);
        string f = FormatRegistry.BuildFilter(new IFileFormat[] { reg.Songs[0] });
        Check("Registry open filter", f.Contains("*.rol") && f.Contains("*.*"), f);
    }
}
