using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>GM · GM2 · GS · XG 리셋 SysEx, GS 드럼 파트, 뱅크 셀렉트 (XG · GM2 드럼 뱅크), 곡 규격 판단.</summary>
public static class MidiSysExTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Messages(check);
        Detect(check);
        Player(check);
        Smf(check);
        Windows(check);
    }

    /// <summary>Windows MIDI 장치가 있으면 SysEx를 midiOutLongMsg로 보내 본다 (음은 없음: 리셋 · 드럼 파트만).</summary>
    private static void Windows(Action<string, bool, string> check)
    {
        var s = Song(Sx(0, MidiSysEx.GsReset), Sx(1, MidiSysEx.GsData(0x40, 0x12, 0x15, 1)), Sx(2, MidiSysEx.GsData(0x40, 0x12, 0x15, 0)));
        using (var p = new MidiPlayer(new OplChip(true)) { Synth = MidiSynth.Windows })
        {
            p.Start(s, 0);
            if (p.ActiveSynth != MidiSynth.Windows) { check("Windows SysEx (no MIDI device — skipped)", true, ""); return; }
            for (int i = 0; i < 49716 / 10; i++) p.Step();
            check("Windows SysEx sent via midiOutLongMsg (start reset + 3 from song)", p.SysExSent == 4, p.SysExSent + " sent, " + p.SysExPending + " pending");
        }
    }

    private static void Messages(Action<string, bool, string> check)
    {
        check("SysEx GM On", MidiSysEx.Classify(MidiSysEx.GmOn) == SysExKind.GmOn, "");
        check("SysEx GM2 On", MidiSysEx.Classify(MidiSysEx.Gm2On) == SysExKind.Gm2On, "");
        check("SysEx GS Reset", MidiSysEx.Classify(MidiSysEx.GsReset) == SysExKind.GsReset && MidiSysEx.RolandChecksumOk(MidiSysEx.GsReset), "");
        check("SysEx XG On", MidiSysEx.Classify(MidiSysEx.XgOn) == SysExKind.XgOn, "");
        check("SysEx GM Off", MidiSysEx.Classify(new byte[] { 0xF0, 0x7E, 0x10, 0x09, 0x02, 0xF7 }) == SysExKind.GmOff, "");
        // 채널 3 (블록 3 = 파트 3 = 채널 2) 드럼 맵 1
        var rp = MidiSysEx.GsData(0x40, 0x13, 0x15, 1);
        var kind = MidiSysEx.Classify(rp, out int ch, out int val);
        check("SysEx GS rhythm part: block 3 → channel 2, map 1, checksum", kind == SysExKind.GsRhythmPart && ch == 2 && val == 1 && MidiSysEx.RolandChecksumOk(rp), ch + " " + val);
        check("SysEx GS rhythm part block 0 = channel 10", MidiSysEx.GsBlockToChannel(0) == 9 && MidiSysEx.GsBlockToChannel(0x0A) == 10 && MidiSysEx.GsBlockToChannel(1) == 0, "");
        bool round = Enumerable.Range(0, 16).All(c => MidiSysEx.GsBlockToChannel(MidiSysEx.ChannelToGsBlock(c)) == c);
        check("SysEx GS block ↔ channel round trip", round, "");
        check("SysEx other (MT-32 DT1) not a reset", MidiSysEx.Classify(new byte[] { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x10, 0x00, 0x01, 0x05, 0x6A, 0xF7 }) == SysExKind.Other, "");
        check("GS drum kit names", MidiSysEx.GsDrumKitName(25) == "TR-808" && MidiSysEx.GsDrumKitName(0) == "Standard" && MidiSysEx.GsDrumKitName(3) == "Kit 4", "");
    }

    private static MidiEvent Sx(int tick, byte[] sx) { return new MidiEvent { Tick = tick, Status = 0xF0, SysEx = sx }; }
    private static MidiEvent Cc(int tick, int ch, int cc, int v) { return new MidiEvent { Tick = tick, Status = (byte)(0xB0 | ch), Data1 = (byte)cc, Data2 = (byte)v }; }
    private static MidiEvent Pc(int tick, int ch, int p) { return new MidiEvent { Tick = tick, Status = (byte)(0xC0 | ch), Data1 = (byte)p }; }
    private static MidiEvent On(int tick, int ch, int n) { return new MidiEvent { Tick = tick, Status = (byte)(0x90 | ch), Data1 = (byte)n, Data2 = 100 }; }

    private static MidiSong Song(params MidiEvent[] events)
    {
        var s = new MidiSong { Division = 96 };
        s.Events.AddRange(events);
        s.Events.Add(new MidiEvent { Tick = 960, Status = 0x80, Data1 = 60 });
        s.Finish();
        return s;
    }

    private static void Detect(Action<string, bool, string> check)
    {
        check("Detect: none", Song(On(0, 0, 60)).Standard == MidiStandard.None, "");
        check("Detect: GM", Song(Sx(0, MidiSysEx.GmOn)).Standard == MidiStandard.Gm, "");
        check("Detect: GM + GS reset → GS", Song(Sx(0, MidiSysEx.GmOn), Sx(1, MidiSysEx.GsReset)).Standard == MidiStandard.Gs, "");
        check("Detect: GS block only (reverb) → GS", Song(Sx(0, MidiSysEx.GsData(0x40, 0x01, 0x30, 4))).Standard == MidiStandard.Gs, "");
        check("Detect: XG", Song(Sx(0, MidiSysEx.GmOn), Sx(1, MidiSysEx.XgOn)).Standard == MidiStandard.Xg, "");
        check("Detect: GM2", Song(Sx(0, MidiSysEx.Gm2On)).Standard == MidiStandard.Gm2, "");
        check("Detect: MT-32", Song(Sx(0, new byte[] { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x10, 0x00, 0x01, 0x05, 0x6A, 0xF7 })).Standard == MidiStandard.Mt32, "");
        check("Bank select use detected", Song(Cc(0, 0, 0, 8), Pc(0, 0, 0)).UsesBankSelect && !Song(Cc(0, 0, 0, 0)).UsesBankSelect, "");
    }

    /// <summary>FM 재생기를 startTick까지 앞으로 감은 뒤의 채널 상태.</summary>
    private static (MidiPlayer p, VoiceState[] st) At(MidiSong s, int tick)
    {
        var p = new MidiPlayer(new OplChip(true));
        p.Start(s, tick);
        var st = new VoiceState[PlaybackOptions.Voices];
        p.CopyStates(st);
        return (p, st);
    }

    private static void Player(Action<string, bool, string> check)
    {
        // GS: 리셋 → 채널 2를 드럼 파트로 → 25번 세트(TR-808) → GM 리셋으로 되돌림
        var gs = Song(Sx(0, MidiSysEx.GsReset), Sx(1, MidiSysEx.GsData(0x40, 0x12, 0x15, 1)), Pc(2, 1, 25), Sx(100, MidiSysEx.GmOn));
        var (p, st) = At(gs, 10);
        check("GS mode after GS reset", p.Mode == MidiStandard.Gs, p.Mode.ToString());
        check("GS rhythm part: channel 2 is drums", p.IsDrum(1) && st[1].Drum && p.IsDrum(9) && !p.IsDrum(0), st[1].Instrument);
        check("GS drum kit name on channel 2", st[1].Instrument == "Drums · TR-808", st[1].Instrument);
        (p, st) = At(gs, 200);
        check("GM On resets drum parts and mode", p.Mode == MidiStandard.Gm && !p.IsDrum(1) && p.IsDrum(9) && !st[1].Drum, p.Mode + " " + st[1].Instrument);

        // XG: 뱅크 MSB 127 + 프로그램 체인지 → 드럼, MSB 0 → 다시 멜로디. 채널 10은 처음부터 드럼
        var xg = Song(Sx(0, MidiSysEx.XgOn), Cc(1, 3, 0, 127), Pc(2, 3, 0), Cc(50, 3, 0, 0), Cc(50, 3, 32, 0), Pc(51, 3, 5), Cc(60, 4, 0, 126), Pc(61, 4, 0));
        (p, st) = At(xg, 10);
        check("XG bank 127 on channel 4 → drums", p.Mode == MidiStandard.Xg && p.IsDrum(3) && p.IsDrum(9), st[3].Instrument);
        (p, st) = At(xg, 55);
        check("XG bank 0 → melodic again", !p.IsDrum(3) && st[3].Instrument == GeneralMidi.ProgramNames[5], st[3].Instrument);
        (p, st) = At(xg, 70);
        check("XG bank 126 → SFX drums", p.IsDrum(4) && st[4].Instrument == "SFX Drums", st[4].Instrument);

        // GM2: 뱅크 MSB 120 → 드럼
        var gm2 = Song(Sx(0, MidiSysEx.Gm2On), Cc(1, 5, 0, 120), Pc(2, 5, 0));
        (p, _) = At(gm2, 10);
        check("GM2 bank 120 → drums", p.Mode == MidiStandard.Gm2 && p.IsDrum(5), "");

        // 리셋 없는 곡에서 MSB 127은 드럼이 아니다 (GS 변형 번호일 뿐)
        var plain = Song(Cc(0, 2, 0, 127), Pc(1, 2, 0), Cc(2, 0, 0, 8), Pc(3, 0, 4));
        (p, st) = At(plain, 10);
        check("No reset: bank 127 is not drums", p.Mode == MidiStandard.None && !p.IsDrum(2), "");
        check("Bank select shown in instrument name", st[0].Instrument == GeneralMidi.ProgramNames[4] + " · 뱅크 8", st[0].Instrument);

        // 재생 중 드럼 파트가 된 채널의 음은 키(조옮김)를 받지 않는다
        var keyed = Song(Sx(0, MidiSysEx.GsReset), Sx(1, MidiSysEx.GsData(0x40, 0x12, 0x15, 1)), On(10, 1, 36), On(10, 0, 60));
        var opts = new PlaybackOptions { Transpose = 5 };
        var mp = new MidiPlayer(new OplChip(true));
        mp.Start(keyed, 0, opts);
        for (int i = 0; i < 49716 / 2; i++) mp.Step();
        var s2 = new VoiceState[PlaybackOptions.Voices];
        mp.CopyStates(s2);
        check("Drum part notes reported (channel 2 note 36, drum flag)", s2[1].Note == 36 && s2[1].Drum && !s2[0].Drum, s2[1].Note + " " + s2[1].Drum);
    }

    private static void Smf(Action<string, bool, string> check)
    {
        // F7로 끝나지 않는 SysEx는 F7을 붙여 둔다
        byte[] Track(params byte[] ev)
        {
            var t = new List<byte>(ev);
            t.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });
            var l = new List<byte> { (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, (byte)(t.Count >> 8), (byte)t.Count };
            l.AddRange(t);
            return l.ToArray();
        }
        var head = new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 };
        var smf = head.Concat(Track(0x00, 0xF0, 0x04, 0x7E, 0x7F, 0x09, 0x01)).ToArray();
        var s = MidiFileFormat.Read(smf);
        var e = s.Events.First(x => x.IsSysEx);
        check("SMF SysEx without F7 gets F7, read as GM On", e.SysEx.Length == 6 && e.SysEx[5] == 0xF7 && MidiSysEx.Classify(e.SysEx) == SysExKind.GmOn && s.Standard == MidiStandard.Gm,
            BitConverter.ToString(e.SysEx));
    }
}
