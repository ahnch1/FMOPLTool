using System;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>GXSCC 방식 "8-bit" MIDI 신디 (<see cref="ChipMidiSynth"/>: SCC+ 15음 + PSG 드럼) · MidiPlayer · 엔진 연결.</summary>
public static class ChipMidiTests
{
    private const int Rate = ChipMidiSynth.Rate;

    private static MidiEvent Ev(int tick, int status, int d1, int d2 = 0) { return new MidiEvent { Tick = tick, Status = (byte)status, Data1 = (byte)d1, Data2 = (byte)d2 }; }

    private static MidiSong Song(params MidiEvent[] events)
    {
        var s = new MidiSong { Division = 96 };
        s.Events.AddRange(events);
        s.Events.Add(Ev(96 * 8, 0x80, 0));
        s.Finish();
        return s;
    }

    private static MidiPlayer Player(MidiSong s, PlaybackOptions o = null)
    {
        var p = new MidiPlayer(new OplChip(true)) { Synth = MidiSynth.Chip };
        p.Start(s, 0, o);
        return p;
    }

    /// <summary>n개 샘플: RMS와 0 교차 수 (앞 skip개는 버림).</summary>
    private static void Measure(MidiPlayer p, int skip, int n, out double rms, out int crossings)
    {
        for (int i = 0; i < skip; i++) { p.Step(); p.GenerateChip(out _, out _); }
        double sq = 0; crossings = 0; int prev = 0;
        for (int i = 0; i < n; i++)
        {
            p.Step();
            p.GenerateChip(out double l, out _);
            sq += l * l;
            int sign = l > 1 ? 1 : l < -1 ? -1 : 0;
            if (sign != 0 && prev != 0 && sign != prev) crossings++;
            if (sign != 0) prev = sign;
        }
        rms = Math.Sqrt(sq / n);
    }

    public static void Run(Action<string, bool, string> check)
    {
        // 베이스(프로그램 32) A3 = 220Hz, 0.5초 재기
        var bass = Song(Ev(0, 0xC0, 32), Ev(0, 0x90, 57, 120), Ev(96 * 4, 0x80, 57));
        var p = Player(bass);
        check("8-bit: player uses chip synth at 44100 Hz", p.ActiveSynth == MidiSynth.Chip && p.Rate == Rate, p.ActiveSynth + " " + p.Rate);
        Measure(p, Rate / 20, Rate / 2, out double rms, out int cross);
        check("8-bit bass A3 ≈ 220 Hz (SCC divider)", Math.Abs(cross - 220) < 4, cross.ToString());
        check("8-bit note is audible", rms > 500, rms.ToString("0"));
        check("8-bit uses one SCC voice", p.ChipActiveTones == 1, p.ChipActiveTones.ToString());

        // 키 +12 · 벤드 +2반음
        p = Player(bass, new PlaybackOptions { Transpose = 12 });
        Measure(p, Rate / 20, Rate / 2, out _, out int crossUp);
        check("8-bit transpose +12 → ≈ 440 Hz", Math.Abs(crossUp - 440) < 6, crossUp.ToString());
        var bent = Song(Ev(0, 0xC0, 32), Ev(0, 0xE0, 0x7F, 0x7F), Ev(0, 0x90, 57, 120));
        p = Player(bent);
        Measure(p, Rate / 20, Rate / 2, out _, out int crossBend);
        check("8-bit pitch bend +2 → ≈ 247 Hz", Math.Abs(crossBend - 246.9) < 5, crossBend.ToString());

        // 볼륨 CC7 64 → 작아진다 (GM 곡선, 4비트)
        var quiet = Song(Ev(0, 0xC0, 32), Ev(0, 0xB0, 7, 64), Ev(0, 0x90, 57, 120));
        p = Player(quiet);
        Measure(p, Rate / 20, Rate / 2, out double rmsQuiet, out _);
        check("8-bit CC7 64 is quieter", rmsQuiet < rms * 0.6 && rmsQuiet > 0, (rmsQuiet / rms).ToString("0.00"));

        // 채널 끄기
        var o = new PlaybackOptions();
        o.VoiceMute[0] = true;
        p = Player(bass, o);
        Measure(p, Rate / 20, Rate / 4, out double rmsMuted, out _);
        check("8-bit muted channel is silent", rmsMuted < 20, rmsMuted.ToString("0"));

        // 노트 오프 → 릴리스 뒤 조용
        var shortNote = Song(Ev(0, 0xC0, 32), Ev(0, 0x90, 57, 120), Ev(24, 0x80, 57));   // 0.125초
        p = Player(shortNote);
        Measure(p, Rate / 2, Rate / 4, out double rmsAfter, out _);
        check("8-bit note off releases to silence", rmsAfter < 50 && p.ChipActiveTones == 0, rmsAfter.ToString("0"));

        // 서스테인 페달: 노트 오프 뒤에도 울린다
        var held = Song(Ev(0, 0xC0, 32), Ev(0, 0xB0, 64, 127), Ev(0, 0x90, 57, 120), Ev(24, 0x80, 57));
        p = Player(held);
        Measure(p, Rate / 2, Rate / 4, out double rmsHeld, out _);
        check("8-bit sustain pedal holds the note", rmsHeld > 300 && p.ChipActiveTones == 1, rmsHeld.ToString("0"));

        // 동시에 20음 → SCC+ 세 개 = 15음까지
        var chord = new MidiEvent[21];
        chord[0] = Ev(0, 0xC0, 16);
        for (int i = 0; i < 20; i++) chord[i + 1] = Ev(0, 0x90, 40 + i * 2, 100);
        p = Player(Song(chord));
        Measure(p, 100, 100, out _, out _);
        check("8-bit polyphony capped at 15 SCC voices", p.ChipActiveTones == ChipMidiSynth.ToneVoices, p.ChipActiveTones.ToString());

        // 드럼 (채널 10): 킥 · 스네어 · 하이햇 → PSG 3채널, 곧 사라진다
        var kit = Song(Ev(0, 0x99, 36, 120), Ev(0, 0x99, 38, 120), Ev(0, 0x99, 42, 120));
        p = Player(kit);
        Measure(p, 0, Rate / 20, out double rmsDrum, out _);
        check("8-bit drums play on PSG (3 voices)", p.ChipActiveDrums == 3 && rmsDrum > 300 && p.ChipActiveTones == 0, p.ChipActiveDrums + " " + rmsDrum.ToString("0"));
        Measure(p, Rate / 2, 100, out _, out _);
        check("8-bit drums decay and free their voices", p.ChipActiveDrums == 0, p.ChipActiveDrums.ToString());

        // GS 드럼 파트로 바뀐 채널 2도 PSG 드럼
        var part = Song(new MidiEvent { Tick = 0, Status = 0xF0, SysEx = MidiSysEx.GsReset },
            new MidiEvent { Tick = 0, Status = 0xF0, SysEx = MidiSysEx.GsData(0x40, 0x12, 0x15, 1) }, Ev(1, 0x91, 38, 120));
        p = Player(part);
        Measure(p, Rate / 50, 10, out _, out _);
        check("8-bit GS rhythm part channel uses PSG drums", p.ChipActiveDrums == 1 && p.ChipActiveTones == 0, p.ChipActiveDrums + " / " + p.ChipActiveTones);

        // 엔진 연결
        using (var engine = new AudioEngine { MidiSynth = MidiSynth.Chip })
        {
            engine.PlayMidi(bass);
            var buf = new short[Rate / 2];
            engine.Render(buf, buf.Length);
            double sq = 0;
            for (int i = Rate / 10; i < buf.Length; i++) sq += (double)buf[i] * buf[i];
            double er = Math.Sqrt(sq / (buf.Length - Rate / 10));
            check("AudioEngine plays MIDI with 8-bit chip synth", engine.ActiveMidiSynth == MidiSynth.Chip && er > 500, er.ToString("0"));
            engine.Stop();
        }
        PsgOnly(check, bass);
        RealLoudness(check);
    }

    /// <summary>8-bit PSG (PSG만): 사각파 6음, 드럼은 같은 PSG 방식, 볼륨은 dB 단계.</summary>
    private static void PsgOnly(Action<string, bool, string> check, MidiSong bass)
    {
        check("PSG volume: 1.0 → 15, −6 dB → 13, 0 → 0", ChipMidiSynth.PsgVolume(1) == 15 && ChipMidiSynth.PsgVolume(0.5) == 13 && ChipMidiSynth.PsgVolume(0) == 0,
            ChipMidiSynth.PsgVolume(0.5).ToString());
        MidiPlayer Psg(MidiSong s, PlaybackOptions o = null)
        {
            var p = new MidiPlayer(new OplChip(true)) { Synth = MidiSynth.Chip, ChipSound = ChipSound.Psg };
            p.Start(s, 0, o);
            return p;
        }
        var q = Psg(bass);
        Measure(q, Rate / 20, Rate / 2, out double rms, out int cross);
        check("8-bit PSG: bass A3 ≈ 220 Hz (PSG divider)", Math.Abs(cross - 220) < 4, cross.ToString());
        check("8-bit PSG: audible, one voice", rms > 500 && q.ChipActiveTones == 1, rms.ToString("0"));
        q = Psg(bass, new PlaybackOptions { Transpose = 12 });
        Measure(q, Rate / 20, Rate / 2, out _, out int up);
        check("8-bit PSG: transpose +12 → ≈ 440 Hz", Math.Abs(up - 440) < 6, up.ToString());

        var chord = new MidiEvent[11];
        chord[0] = Ev(0, 0xC0, 16);
        for (int i = 0; i < 10; i++) chord[i + 1] = Ev(0, 0x90, 48 + i * 3, 100);
        q = Psg(Song(chord));
        Measure(q, 100, 100, out _, out _);
        check("8-bit PSG: polyphony capped at 6 voices", q.ChipActiveTones == ChipMidiSynth.PsgToneVoices, q.ChipActiveTones.ToString());

        var kit = Song(Ev(0, 0x99, 36, 120), Ev(0, 0x99, 42, 120));
        q = Psg(kit);
        Measure(q, 0, Rate / 20, out double drumRms, out _);
        check("8-bit PSG: drums on PSG", q.ChipActiveDrums == 2 && drumRms > 200, q.ChipActiveDrums + " " + drumRms.ToString("0"));

        var o = new PlaybackOptions();
        o.VoiceMute[0] = true;
        q = Psg(bass, o);
        Measure(q, Rate / 20, Rate / 4, out double muted, out _);
        check("8-bit PSG: muted channel is silent", muted < 20, muted.ToString("0"));

        using (var engine = new AudioEngine { MidiSynth = MidiSynth.Chip, MidiChipSound = ChipSound.Psg })
        {
            engine.PlayMidi(bass);
            var buf = new short[Rate / 2];
            engine.Render(buf, buf.Length);
            double sq = 0;
            for (int i = Rate / 10; i < buf.Length; i++) sq += (double)buf[i] * buf[i];
            check("AudioEngine plays MIDI with 8-bit PSG", Math.Sqrt(sq / (buf.Length - Rate / 10)) > 500 && engine.MidiChipSound == ChipSound.Psg, "");
        }
    }

    /// <summary>_incoming의 MIDI 곡: 8-bit와 FM의 크기가 비슷한가 (앞 20초 RMS).</summary>
    private static void RealLoudness(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string res = SopTests.FindDir(System.IO.Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        if (dir == null || res == null) { Console.WriteLine("SKIP  8-bit loudness vs FM (no _incoming MIDI)"); return; }
        InstrumentBankHolder.Load(res, out var gm);
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.mid"))
        {
            var song = MidiFiles.Load(path);
            double fm = Render(song, MidiSynth.Fm, gm), chip = Render(song, MidiSynth.Chip, gm), psg = Render(song, MidiSynth.Chip, gm, ChipSound.Psg);
            double ratio = fm > 0 ? chip / fm : 0, ratioPsg = fm > 0 ? psg / fm : 0;
            check("8-bit loudness ≈ FM: " + System.IO.Path.GetFileName(path), ratio > 0.5 && ratio < 2.0, "chip " + chip.ToString("0") + " / fm " + fm.ToString("0") + " = " + ratio.ToString("0.00"));
            check("8-bit PSG loudness ≈ FM: " + System.IO.Path.GetFileName(path), ratioPsg > 0.5 && ratioPsg < 2.0, "psg " + psg.ToString("0") + " / fm " + fm.ToString("0") + " = " + ratioPsg.ToString("0.00"));
        }
    }

    private static double Render(MidiSong s, MidiSynth synth, GmBank bank, ChipSound sound = ChipSound.PsgScc)
    {
        using (var e = new AudioEngine { MidiBank = bank, MidiSynth = synth, MidiChipSound = sound })
        {
            e.PlayMidi(s);
            var buf = new short[Rate * 20];
            e.Render(buf, buf.Length);
            double sq = 0;
            foreach (var v in buf) sq += (double)v * v;
            return Math.Sqrt(sq / buf.Length);
        }
    }

    private static class InstrumentBankHolder
    {
        public static void Load(string resDir, out GmBank bank)
        {
            string op2 = System.IO.Path.Combine(resDir, "GENMIDI.OP2");
            if (System.IO.File.Exists(op2)) { bank = GmBank.LoadOp2(op2); return; }
            using (var fs = System.IO.File.OpenRead(System.IO.Path.Combine(resDir, "STANDARD.BNK"))) bank = GmBank.FromBank(new BnkFormat().Read(fs));
        }
    }
}
