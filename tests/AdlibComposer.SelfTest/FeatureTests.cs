using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>v1.3.3: 4-op 악기 편집 · OPL3 시음, MOD / NST, 같은 곡 찾기(정리).</summary>
public static class FeatureTests
{
    public delegate void CheckFn(string name, bool ok, string detail = "");
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }
        FourOp(check);
        Mod(check);
        Organizer(check);
    }

    // ------------------------------------------------------------ 4-op

    private static void FourOp(CheckFn check)
    {
        var ins = AdlibInstrument.CreateDefault("organ4");
        check("2-op instrument algorithm name", ins.FourOpAlgorithm == "1 → 2");
        var op3 = ins.Modulator.Clone(); var op4 = ins.Carrier.Clone();
        op3.Multiple = 3; op3.Feedback = 5; op3.Con = 0; op3.WaveSelect = 6;
        op4.TotalLevel = 7; op4.Con = 0;
        ins.SetPairOperators(op3, op4);
        OplOperator b3, b4;
        ins.GetPairOperators(out b3, out b4);
        check("4-op pair operators round trip", ins.Is4Op && b3.Multiple == 3 && b3.Feedback == 5 && b3.Con == 0 && b3.WaveSelect == 6 && b4.TotalLevel == 7 && (ins.Sop4OpPair[5] & 1) == 1);
        check("4-op algorithm name (FM + pair 2 additive)", ins.FourOpAlgorithm == "1→2 + 3→4", ins.FourOpAlgorithm);
        ins.Modulator.Set(OperatorParameter.WaveForm, 7);
        check("OPL3 wave form 0~7 kept", ins.Modulator.WaveSelect == 7);
        check("4-op → SOP keeps edited pair", SopConvert.ToSop(ins).Data.Skip(11).SequenceEqual(ins.Sop4OpPair));

        // 시음: 4-op는 OPL3로 (곡 재생으로 보이지 않음), 놓으면 조용해지고, 2-op 시음으로 돌아갈 수 있다
        ins.Modulator.WaveSelect = 0;
        var engine = new AudioEngine();
        engine.PreviewSetMode(false);
        engine.PreviewSetInstrument(0, ins);
        engine.PreviewNoteOn(0, 60);
        double on = RmsStereo(engine, 8820);
        check("4-op preview sounds (OPL3)", on > 300 && !engine.IsPlaying, on.ToString("0"));
        engine.PreviewNoteOff(0);
        RmsStereo(engine, 88200);
        double off = RmsStereo(engine, 4410);
        check("4-op preview stops on key up", off < on / 20, off.ToString("0"));
        var two = AdlibInstrument.CreateDefault("piano");
        engine.PreviewSetInstrument(0, two);
        engine.PreviewNoteOn(0, 60);
        double two2 = RmsStereo(engine, 8820);
        check("2-op preview after 4-op", two2 > 300, two2.ToString("0"));
        engine.PreviewAllOff();
    }

    private static double RmsStereo(AudioEngine e, int frames)
    {
        var buf = new short[frames * 2];
        e.RenderStereo(buf, frames);
        double s = 0;
        foreach (short v in buf) s += (double)v * v;
        return Math.Sqrt(s / buf.Length);
    }

    // ------------------------------------------------------------ MOD / NST

    private static void Mod(CheckFn check)
    {
        check("MOD period ↔ note (428 = ST3 C-4)", ModFormat.PeriodToNote(428) == 0x40 && ModFormat.NoteToPeriod(0x40) == 428 && ModFormat.NoteToPeriod(0x30) == 856 && ModFormat.PeriodToNote(113) == 0x5B);
        check("Tracker files include MOD / NST", TrackerFiles.IsTracker("a.MOD") && TrackerFiles.IsTracker("b.nst") && TrackerFiles.Extensions.Contains(".nst"));

        var m = TrackerModule.CreateNew(TrackerKind.S3m);
        m.Title = "mod test";
        m.Samples.Add(TrackerTests.Square("SQUARE", 32 * 400, 8363, true));
        var p = m.Patterns[0];
        p[0, 0] = new TrackerCell { Note = 0x40, Instrument = 1, Volume = 48 };
        p[0, 1] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 1, Info = 3 };          // A03 → F03
        p[4, 1] = new TrackerCell { Note = 0x47, Instrument = 1, Volume = TrackerCell.NoVolume, Command = 4, Info = 0x0F };      // D0F → A0F
        p[8, 0] = new TrackerCell { Note = TrackerCell.NoteOff, Volume = TrackerCell.NoVolume };                                // ^^ → C00
        p[9, 2] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 19, Info = 0xB2 };       // SB2 → E62
        p[10, 3] = new TrackerCell { Note = TrackerCell.NoNote, Volume = TrackerCell.NoVolume, Command = 6, Info = 0xF3 };       // FF3 → E13
        var warnings = new List<string>();
        var bytes = ModFormat.Write(m, warnings);
        check("MOD write: M.K. 4 channels, size", Encoding.ASCII.GetString(bytes, 1080, 4) == "M.K." && bytes.Length == 1084 + 64 * 4 * 4 + (m.Samples[0].Length & ~1), bytes.Length + " orders=" + string.Join(",", m.Orders) + " pats=" + m.Patterns.Count + " " + string.Join("; ", warnings));
        var back = ModFormat.Read(bytes);
        var q = back.Patterns[0];
        check("MOD read: title, channels L R R L, Amiga limits", back.Title == "mod test" && back.ChannelLabel(0) == "L1" && back.ChannelLabel(1) == "R1" && back.ChannelLabel(2) == "R2" && back.ChannelLabel(3) == "L2" && (back.Flags & 16) != 0 && back.UsedChannelCount == 4, "[" + back.Title + "] " + back.ChannelLabel(0) + back.ChannelLabel(1) + back.ChannelLabel(2) + back.ChannelLabel(3) + " " + back.Flags + " " + back.UsedChannelCount);
        check("MOD round trip: note, instrument, volume", q[0, 0].Note == 0x40 && q[0, 0].Instrument == 1 && q[0, 0].Volume == 48);
        check("MOD round trip: speed / volume slide / note off / loop / fine porta",
            q[0, 1].Command == 1 && q[0, 1].Info == 3 && q[4, 1].Command == 4 && q[4, 1].Info == 0x0F && q[4, 1].Note == 0x47
            && q[8, 0].Volume == 0 && q[9, 2].Command == 19 && q[9, 2].Info == 0xB2 && q[10, 3].Command == 6 && q[10, 3].Info == 0xF3,
            q[4, 1].Command + "/" + q[4, 1].Info.ToString("X2") + " " + q[8, 0].Volume + " " + q[10, 3].Command + "/" + q[10, 3].Info.ToString("X2"));
        check("MOD round trip: sample data · loop", back.Samples.Count == 31 && back.Samples[0].IsSample && back.Samples[0].Loop
            && back.Samples[0].Data.SequenceEqual(m.Samples[0].Data.Take(back.Samples[0].Length).Select(v => (short)((sbyte)(v >> 8) << 8))));
        // 음 하나만 있는 곡으로 음높이 (다른 채널 음이 섞이지 않게)
        var solo = TrackerModule.CreateNew(TrackerKind.S3m);
        solo.Samples.Add(TrackerTests.Square("SQUARE", 32 * 400, 8363, true));
        solo.Patterns[0][0, 0] = new TrackerCell { Note = 0x40, Instrument = 1, Volume = TrackerCell.NoVolume };
        double s3mHz = TrackerTests.Pitch(solo, 0.02, 0.25);
        double hz = TrackerTests.Pitch(ModFormat.Read(ModFormat.Write(solo, new List<string>())), 0.02, 0.25);
        check("MOD C-2 (period 428) plays like S3M C-4", Math.Abs(hz - s3mHz) < 3 && Math.Abs(hz - 261.6) < 8, hz.ToString("0.0") + " Hz (S3M " + s3mHz.ToString("0.0") + ")");

        // 8채널 · 15샘플 SoundTracker
        var eight = TrackerModule.CreateNew(TrackerKind.S3m);
        for (int c = 0; c < 8; c++) eight.ChannelSettings[c] = (byte)(c < 4 ? c : 8 + c - 4);
        eight.Samples.Add(TrackerTests.Square("S", 64, 8363, false));
        eight.Patterns[0][0, 7] = new TrackerCell { Note = 0x40, Instrument = 1, Volume = TrackerCell.NoVolume };
        var eb = ModFormat.Write(eight, new List<string>());
        var e8 = ModFormat.Read(eb);
        check("MOD 8CHN", Encoding.ASCII.GetString(eb, 1080, 4) == "8CHN" && e8.UsedChannelCount == 8 && e8.Patterns[0][0, 7].Note == 0x40);

        var st = new byte[600 + 1024 + 4];
        Encoding.ASCII.GetBytes("old st").CopyTo(st, 0);
        st[20 + 22] = 0; st[20 + 23] = 2;     // 샘플 1 길이 2워드
        st[20 + 25] = 64; st[20 + 29] = 1;    // 볼륨, 루프 길이 1
        st[470] = 1;                          // 곡 길이
        st[600] = 0x01; st[601] = 0xAC; st[602] = 0x10;   // 주기 428, 샘플 1
        var old = ModFormat.Read(st);
        check("MOD 15-sample SoundTracker", old.Samples.Count == 15 && old.Patterns.Count == 1 && old.Patterns[0][0, 0].Note == 0x40 && old.Patterns[0][0, 0].Instrument == 1 && old.Samples[0].Length == 4);

        string dir = Path.Combine(Path.GetTempPath(), "fmopl_mod_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string nst = Path.Combine(dir, "song.nst");
            var w = new List<string>();
            var saved = TrackerFiles.Save(m, nst, w);
            var loaded = TrackerFiles.Load(nst);
            check("NST save / load through TrackerFiles", saved.FilePath == nst && loaded.Patterns[0][0, 0].Note == 0x40 && TrackerPlayer.MeasureSeconds(loaded) > 0.5);
            var adl = TrackerModule.CreateNew(TrackerKind.S3m);
            adl.ChannelSettings[0] = 16;
            adl.Samples.Add(new TrackerSample { Type = TrackerSample.TypeAdlibMelody, Name = "fm" });
            w.Clear();
            ModFormat.Write(adl, w);
            check("MOD warns about AdLib channels / instruments", w.Any(x => x.Contains("AdLib 채널")) && w.Any(x => x.Contains("AdLib 악기")), string.Join("; ", w));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // ------------------------------------------------------------ 같은 곡 찾기

    private static void Organizer(CheckFn check)
    {
        check("Numeric names", MusicDuplicateFinder.IsNumericName("0001") && MusicDuplicateFinder.IsNumericName("SONG001") && MusicDuplicateFinder.IsNumericName("MUS00012")
            && MusicDuplicateFinder.IsNumericName("12-34") && !MusicDuplicateFinder.IsNumericName("AXEL-F") && !MusicDuplicateFinder.IsNumericName("BLUES2") && !MusicDuplicateFinder.IsNumericName("TETRIS"));

        string dir = Path.Combine(Path.GetTempPath(), "fmopl_org_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            var song = DemoRender.BuildDemoSong();
            var rol = new RolFormat();
            byte[] rolBytes;
            using (var ms = new MemoryStream()) { rol.Write(song, ms); rolBytes = ms.ToArray(); }
            File.WriteAllBytes(Path.Combine(dir, "SONG001.ROL"), rolBytes);                 // 숫자 이름
            File.WriteAllBytes(Path.Combine(dir, "sub", "MYSONG.ROL"), rolBytes);           // 같은 바이트, BNK · ISS 있음
            File.WriteAllBytes(Path.Combine(dir, "sub", "MYSONG.BNK"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "sub", "mysong.iss"), new byte[] { 2 });
            using (var ms = new MemoryStream()) { new ImsFormat().Write(song, ms); File.WriteAllBytes(Path.Combine(dir, "DEMO.IMS"), ms.ToArray()); }   // 같은 음표, 다른 형식
            var other = DemoRender.BuildDemoSong();
            foreach (var nn in other.Voices[0].Notes) nn.Pitch += 2;   // 멜로디 전체를 바꾼 다른 곡 (음표 2/3만 같음)
            using (var ms = new MemoryStream()) { rol.Write(other, ms); File.WriteAllBytes(Path.Combine(dir, "OTHER.ROL"), ms.ToArray()); }
            File.WriteAllBytes(Path.Combine(dir, "BROKEN.ROL"), new byte[] { 0, 4, 9 });
            File.WriteAllBytes(Path.Combine(dir, "README.TXT"), new byte[] { 1 });

            var paths = MusicDuplicateFinder.ListFiles(dir, true);
            check("Organizer lists ROL / IMS / SOP only (recursive)", paths.Count == 5 && MusicDuplicateFinder.ListFiles(dir, false).Count == 4, paths.Count.ToString());
            var infos = paths.Select(MusicDuplicateFinder.Analyze).ToList();
            var groups = MusicDuplicateFinder.FindGroups(infos);
            var g = groups.SingleOrDefault();
            check("Organizer: one group (ROL ×2 + IMS)", g != null && g.Files.Count == 3 && g.Reasons.Contains(MatchKind.SameBytes) && g.Reasons.Contains(MatchKind.SameNotes),
                g == null ? groups.Count + " groups" : string.Join(", ", g.Files.Select(f => f.FileName)) + " / " + g.ReasonText);
            if (g != null)
            {
                check("Organizer keeps the file with BNK + ISS", g.Keeper.FileName == "MYSONG.ROL" && g.Keeper.HasBank && g.Keeper.HasLyrics && g.Keeper.Score == 80, g.Keeper.FileName + " " + g.Keeper.Score);
                var num = g.Files.First(f => f.FileName == "SONG001.ROL");
                check("Organizer ranks numeric name lowest", num.NumericName && g.Files.Last() == num && !num.Keep, string.Join(", ", g.Files.Select(f => f.FileName + ":" + f.Score)));
                var keeper = g.Keeper;
                check("Companions follow the file", MusicDuplicateFinder.CompanionsToTake(keeper, new List<string>()).Count == 2
                    && MusicDuplicateFinder.CompanionsToTake(keeper, new List<string> { Path.Combine(dir, "sub", "MYSONG.IMS") }).Count == 0);
            }
            var near = DemoRender.BuildDemoSong();
            near.Voices[0].Notes[3].Pitch += 2;   // 음 하나만 고친 판 (47/48 같음)
            var nearInfo = MusicDuplicateFinder.Analyze(Path.Combine(dir, "SONG001.ROL"));
            using (var ms = new MemoryStream()) { rol.Write(near, ms); File.WriteAllBytes(Path.Combine(dir, "NEAR.ROL"), ms.ToArray()); }
            var nearGroups = MusicDuplicateFinder.FindGroups(new List<MusicFileInfo> { nearInfo, MusicDuplicateFinder.Analyze(Path.Combine(dir, "NEAR.ROL")) });
            check("Organizer: same size + instruments + 90% notes = same song", nearGroups.Count == 1 && nearGroups[0].Reasons.Single() == MatchKind.SizeAndInstruments);
            File.Delete(Path.Combine(dir, "NEAR.ROL"));
            var broken = infos.First(f => f.FileName == "BROKEN.ROL");
            var oth = infos.First(f => f.FileName == "OTHER.ROL");
            check("Organizer: different song and broken file stay alone", broken.Error != null && broken.Group == null && oth.Group == null && oth.NoteCount == 48, oth.NoteCount.ToString());
            var ims = infos.First(f => f.Format == "IMS");
            check("Organizer reads IMS title / instruments", ims.Title == "demo" && ims.Instruments.Contains("lead") && ims.VoicesUsed == 5, ims.Title + " " + string.Join(",", ims.Instruments));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
