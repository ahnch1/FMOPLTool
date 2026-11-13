using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>MIDI (.mid) · Cakewalk (.wrk) 읽기, GM FM 뱅크, MidiPlayer 재생.</summary>
public static class MidiTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }
        // ---- 만든 SMF: 형식 1, 트랙 2개, 템포 60 BPM, 러닝 스테이터스
        var smf = BuildSmf();
        var s = MidiFileFormat.Read(smf);
        int notes = s.Events.Count(e => e.IsNoteOn);
        check("SMF format 1 parsed", s.FormatName == "MIDI 1" && s.Division == 96 && notes == 3, s.FormatName + " div " + s.Division + " notes " + notes);
        check("SMF running status note-off (vel 0) read", s.Events.Count(e => e.IsNoteOff) == 3);
        check("SMF tempo 60 BPM: 1 beat = 1 s", Math.Abs(s.TickToSeconds(96) - 1.0) < 1e-9 && Math.Abs(s.SecondsToTick(2.0) - 192) < 1e-6, s.TickToSeconds(96).ToString("0.000"));
        check("SMF track name = title", s.Title == "Test Song", s.Title);
        check("SMF program on channel 1", s.FirstProgram[1] == 40 && s.ChannelUsed[0] && s.ChannelUsed[1] && !s.ChannelUsed[2]);
        check("Events sorted: note-off before note-on at same tick", OffBeforeOn(s));

        // ---- GENMIDI.OP2 (만든 파일)
        string op2 = Path.Combine(Path.GetTempPath(), "fmopl_test_genmidi.op2");
        File.WriteAllBytes(op2, BuildOp2());
        var gm = GmBank.LoadOp2(op2);
        File.Delete(op2);
        var p0 = gm.Melodic[0];
        check("OP2 melodic 0: registers, additive bit, double voice", p0.Voice1.Modulator.Multiple == 1 && p0.Voice1.Carrier.TotalLevel == 5
            && p0.Voice1.Modulator.Feedback == 3 && p0.Voice1.Additive && p0.Voice2 != null && p0.Offset1 == -12 && p0.Name == "Piano",
            "mult " + p0.Voice1.Modulator.Multiple + " tl " + p0.Voice1.Carrier.TotalLevel + " fb " + p0.Voice1.Modulator.Feedback + " add " + p0.Voice1.Additive + " off " + p0.Offset1);
        check("OP2 drum 35 fixed note", gm.Drums[35] != null && gm.Drums[35].FixedNote == 40, gm.Drums[35]?.FixedNote.ToString());

        // ---- BNK 이름으로 맞춘 GM 뱅크 + 재생
        string bnkDir = SopTests.FindDir(Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        InstrumentBank std = null;
        if (bnkDir != null)
            using (var fs = File.OpenRead(Path.Combine(bnkDir, "STANDARD.BNK"))) { std = new BnkFormat().Read(fs); std.FilePath = Path.Combine(bnkDir, "STANDARD.BNK"); }
        var bank = GmBank.FromBank(std);
        int named = Enumerable.Range(0, 128).Select(i => bank.Melodic[i].Name).Distinct().Count();
        check("GM bank from STANDARD.BNK: many different instruments", std == null || named > 60, named + " distinct");

        var engine = new AudioEngine { MidiBank = bank };
        var buf = new short[44100];
        engine.PlayMidi(s);
        engine.Render(buf, buf.Length);
        double rms = Rms(buf);
        check("MIDI FM plays", rms > 100, rms.ToString("0"));
        var states = new VoiceState[PlaybackOptions.Voices];
        engine.GetVoiceStates(states);
        check("MIDI voice states: channel 1 = Violin", states[1].Instrument == "Violin", states[1].Instrument);
        for (int c = 0; c < 16; c++) engine.SetVoiceMute(c, true);
        engine.Render(buf, buf.Length);
        engine.Render(buf, buf.Length);
        check("MIDI mute silences", Rms(buf) < rms * 0.05, Rms(buf).ToString("0"));
        var o2 = new PlaybackOptions { Speed = 2.0 };
        engine.PlayMidi(s, 0, o2);
        engine.Render(buf, 22050);
        double t2 = engine.PositionTick;
        check("MIDI speed x2: 0.5 s = 1 beat", Math.Abs(t2 - 96) < 4, t2.ToString("0.0"));
        engine.PlayMidi(s, 96);
        engine.Render(buf, 100);
        check("MIDI start mid-song", engine.PositionTick >= 96, engine.PositionTick.ToString("0.0"));

        // ---- SysEx는 이벤트로 남는다 (MT-32 사용자 음색 등)
        var sx = MidiFileFormat.Read(BuildSmfWithSysEx());
        var sysex = sx.Events.FirstOrDefault(e => e.IsSysEx);
        check("SMF SysEx kept as event (F0 … F7)", sysex.SysEx != null && sysex.SysEx.Length == 11 && sysex.SysEx[0] == 0xF0 && sysex.SysEx[10] == 0xF7 && sysex.SysEx[1] == 0x41,
            sysex.SysEx == null ? "none" : BitConverter.ToString(sysex.SysEx));

        // ---- MT-32 모듈, 에뮬레이터 없음: MT-32 음색 번호를 GeneralMidi.Mt32ToGm으로 GM에 맞춘다
        var mtSong = MidiFileFormat.Read(BuildSmf());
        var em = new AudioEngine { MidiBank = bank, MidiModule = MidiModule.Mt32 };
        em.PlayMidi(mtSong);
        em.Render(buf, 4410);
        em.GetVoiceStates(states);
        check("MT-32 without emulator: program mapped to GM", em.ActiveMidiSynth == MidiSynth.Fm && states[1].Instrument == GeneralMidi.ProgramNames[GeneralMidi.Mt32ToGm[40]],
            em.ActiveMidiSynth + " / " + states[1].Instrument);
        check("MT-32 → GM table: strings, harp, piano", GeneralMidi.Mt32ToGm[48] == 48 && GeneralMidi.Mt32ToGm[0] == 0 && GeneralMidi.Mt32ToGm.Length == 128);

        // ---- MT-32 에뮬레이터 (mt32emu.dll + _incoming의 ROM이 있을 때)
        string ver = Mt32Emu.LibraryVersion();
        string inc = SopTests.FindDir("_incoming");
        if (ver == null) Console.WriteLine("SKIP  MT-32 emulator (mt32emu.dll not built)");
        else
        {
            check("mt32emu.dll loads", ver.Length > 0, ver);
            var e32 = new AudioEngine { MidiModule = MidiModule.Mt32 };
            string romDir = inc != null && Directory.Exists(Path.Combine(inc, "MT32")) ? Path.Combine(inc, "MT32") : inc;
            string err = null;
            string rom = romDir != null ? e32.OpenMt32(romDir, out err) : null;
            if (rom == null && romDir != null) Console.WriteLine("      MT-32: " + err);
            if (rom == null) Console.WriteLine("SKIP  MT-32 emulator playback (no MT-32 ROM in _incoming)");
            else
            {
                e32.PlayMidi(mtSong);
                var b32 = new short[44100];
                e32.Render(b32, b32.Length);
                check("MT-32 emulator plays (" + rom + ")", e32.ActiveMidiSynth == MidiSynth.Mt32 && Rms(b32) > 100, Rms(b32).ToString("0"));
                foreach (var p in Directory.GetFiles(inc).Where(MidiFiles.IsMidi).OrderBy(x => x).Take(3))
                {
                    var ms = MidiFiles.Load(p);
                    e32.PlayMidi(ms, ms.SecondsToTick(10));
                    e32.Render(b32, b32.Length);
                    check("MT-32 emulator plays " + Path.GetFileName(p), Rms(b32) > 50, Rms(b32).ToString("0"));
                }
            }
        }

        // ---- 실제 GENMIDI.OP2 (Freedoom, 기본 뱅크 폴더)
        if (bnkDir != null && File.Exists(Path.Combine(bnkDir, "GENMIDI.OP2")))
        {
            var fd = GmBank.LoadOp2(Path.Combine(bnkDir, "GENMIDI.OP2"));
            int gmNames = Enumerable.Range(0, 128).Count(i => fd.Melodic[i] != null && fd.Melodic[i].Voice1 != null && fd.Melodic[i].Name.Length > 0);
            int drums = Enumerable.Range(GeneralMidi.FirstDrum, GeneralMidi.LastDrum - GeneralMidi.FirstDrum + 1).Count(n => fd.Drums[n] != null && fd.Drums[n].Voice1 != null);   // 고정 음이 없는 드럼은 자기 음 번호로 친다
            check("Freedoom GENMIDI.OP2: 128 melodic + 47 drums", gmNames == 128 && drums == 47 && fd.Melodic[0].Name == "Acoustic Grand Piano",
                gmNames + " melodic, " + drums + " drums, [0] " + fd.Melodic[0].Name + ", [35] " + fd.Drums[35].Name);
            var ef = new AudioEngine { MidiBank = fd };
            ef.PlayMidi(s);
            ef.Render(buf, buf.Length);
            check("MIDI FM plays with GENMIDI.OP2", Rms(buf) > 100, Rms(buf).ToString("0"));
            bank = fd;   // 실제 곡도 이 뱅크로
        }

        RunReal(report, bank);
    }

    private static bool OffBeforeOn(MidiSong s)
    {
        for (int i = 1; i < s.Events.Count; i++)
            if (s.Events[i].Tick == s.Events[i - 1].Tick && s.Events[i].IsNoteOff && s.Events[i - 1].IsNoteOn) return false;
        return true;
    }

    private static double Rms(short[] b) { double x = 0; foreach (var v in b) x += (double)v * v; return Math.Sqrt(x / b.Length); }

    // ------------------------------------------------------------ _incoming

    private static void RunReal(Action<string, bool, string> report, GmBank bank)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }
        string dir = SopTests.FindDir("_incoming");
        if (dir == null) { Console.WriteLine("SKIP  real MIDI / WRK files (_incoming not found)"); return; }
        foreach (var p in Directory.GetFiles(dir).Where(MidiFiles.IsMidi).OrderBy(x => x))
        {
            string name = Path.GetFileName(p);
            MidiSong s;
            try { s = MidiFiles.Load(p); }
            catch (Exception ex) { check("Real MIDI " + name + " loads", false, ex.Message); continue; }
            int notes = s.Events.Count(e => e.IsNoteOn), offs = s.Events.Count(e => e.IsNoteOff);
            double sec = s.LengthSeconds;
            int chans = s.ChannelUsed.Count(u => u);
            check("Real MIDI " + name + " notes, channels, length", notes > 50 && offs >= notes * 0.9 && sec > 20 && sec < 900,
                s.FormatName + ", " + notes + " notes, " + chans + " ch, " + TimeSpan.FromSeconds(sec).ToString(@"m\:ss") + ", " + s.InitialBpm.ToString("0") + " BPM, div " + s.Division);
            var e = new AudioEngine { MidiBank = bank };
            var buf = new short[44100 * 3];
            e.PlayMidi(s, s.SecondsToTick(10));
            e.Render(buf, buf.Length);
            check("Real MIDI " + name + " plays (FM)", Rms(buf) > 100, Rms(buf).ToString("0"));
        }
        string last = Path.Combine(dir, "LASTDATE.WRK");
        if (File.Exists(last))
        {
            var w = MidiFiles.Load(last);
            check("WRK LASTDATE: 110 BPM, 120 ticks/beat, channels 1-4",
                Math.Abs(w.InitialBpm - 110) < 0.5 && w.Division == 120 && w.ChannelUsed[0] && w.ChannelUsed[3] && !w.ChannelUsed[4] && w.TrackNames.Count == 4,
                w.InitialBpm.ToString("0.0") + " BPM, div " + w.Division + ", tracks " + string.Join(" / ", w.TrackNames));
        }
        string bway = Path.Combine(dir, "BWAY2.WRK");
        if (File.Exists(bway))
        {
            var w = MidiFiles.Load(bway);
            check("WRK BWAY2: comment, track channel overrides event channel",
                w.Comment.Contains("On Broadway") && w.ChannelUsed[3] && w.Events.Where(x => x.Track == 0 && x.IsNoteOn).All(x => x.Channel == 3),
                w.Title);
        }
    }

    // ------------------------------------------------------------ 만든 파일

    private static byte[] BuildSmf()
    {
        var ms = new MemoryStream();
        void W(params int[] b) { foreach (var x in b) ms.WriteByte((byte)x); }
        void Str(string t) { foreach (var c in Encoding.ASCII.GetBytes(t)) ms.WriteByte(c); }
        Str("MThd"); W(0, 0, 0, 6, 0, 1, 0, 2, 0, 96);
        // 트랙 0: 이름 + 템포 60 BPM (1,000,000 µs)
        var t0 = new List<int> { 0, 0xFF, 0x03, 9 };
        t0.AddRange(Encoding.ASCII.GetBytes("Test Song").Select(x => (int)x));
        t0.AddRange(new[] { 0, 0xFF, 0x51, 3, 0x0F, 0x42, 0x40, 0, 0xFF, 0x2F, 0 });
        Str("MTrk"); W(0, 0, 0, t0.Count); W(t0.ToArray());
        // 트랙 1: ch0 C4 1박, ch1 프로그램 40 + E4 · G4 (러닝 스테이터스, 벨로시티 0 = 끄기)
        var t1 = new[] { 0, 0xC1, 40, 0, 0x90, 60, 100, 0, 0x91, 64, 90, 0, 67, 90, 0x60, 0x90, 60, 0, 0, 0x91, 64, 0, 0, 67, 0, 0, 0xFF, 0x2F, 0 };
        Str("MTrk"); W(0, 0, 0, t1.Length); W(t1);
        return ms.ToArray();
    }

    /// <summary>형식 0, 트랙 1개: Roland SysEx (MT-32 마스터 볼륨) + 노트 하나.</summary>
    private static byte[] BuildSmfWithSysEx()
    {
        var t = new List<int> { 0, 0xF0, 10, 0x41, 0x10, 0x16, 0x12, 0x10, 0x00, 0x16, 0x64, 0x76, 0xF7 };
        t.AddRange(new[] { 0, 0x90, 60, 100, 0x60, 0x80, 60, 0, 0, 0xFF, 0x2F, 0 });
        var ms = new MemoryStream();
        foreach (var c in Encoding.ASCII.GetBytes("MThd")) ms.WriteByte(c);
        foreach (var b in new[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 }) ms.WriteByte((byte)b);
        foreach (var c in Encoding.ASCII.GetBytes("MTrk")) ms.WriteByte(c);
        foreach (var b in new[] { 0, 0, 0, t.Count }) ms.WriteByte((byte)b);
        foreach (var b in t) ms.WriteByte((byte)b);
        return ms.ToArray();
    }

    private static byte[] BuildOp2()
    {
        var d = new byte[8 + 175 * 36 + 175 * 32];
        Encoding.ASCII.GetBytes("#OPL_II#").CopyTo(d, 0);
        // 0번: 두 보이스, 보이스 1 = mod 20=0x01, car TL 5, C0 = fb 3 · 가산(1), 음 옮김 −12
        int at = 8;
        d[at] = 4; d[at + 2] = 128;
        byte[] v = { 0x01, 0xF0, 0x00, 0x00, 0x40, 0x10, (3 << 1) | 1, 0x01, 0xF0, 0x00, 0x00, 0x00, 0x05, 0, 0xF4, 0xFF };
        v.CopyTo(d, at + 4);
        v.CopyTo(d, at + 20);
        // 128번 = 드럼 35, 고정 음 40
        at = 8 + 128 * 36;
        d[at] = 1; d[at + 3] = 40;
        v.CopyTo(d, at + 4);
        Encoding.ASCII.GetBytes("Piano").CopyTo(d, 8 + 175 * 36);
        return d;
    }
}
