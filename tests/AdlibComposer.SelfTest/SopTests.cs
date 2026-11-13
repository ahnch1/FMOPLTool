using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>SOP(OPL3): 파일 읽기, OPL3 에뮬레이터(4-op · 스테레오 · 웨이브폼), SopPlayer 재생, _incoming/sop 실제 파일.</summary>
public static class SopTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- 직접 만든 SOP: 4-op 트랙 0(왼쪽), 2-op 트랙 6(오른쪽), 제어 트랙 템포 120 → 240
        var bytes = BuildSop();
        var s = SopFormat.Read(new MemoryStream(bytes));
        check("SOP header", s.TickBeat == 4 && s.BasicTempo == 120 && s.TrackCount == 20 && !s.Percussive && s.Title == "Test song", s.Title);
        check("SOP instruments (4-op 22 bytes, 2-op 11, empty 0)", s.Instruments.Count == 3 && s.Instruments[0].Is4Op && s.Instruments[1].Data.Length == 11 && s.Instruments[2].IsEmpty);
        check("SOP track 0 events", s.Tracks[0].Events.Count == 4 && s.Tracks[0].NoteCount == 1 && s.Tracks[0].ChannelMode == 1);
        var note = s.Tracks[0].Events.First(e => e.Type == SopEventType.Note);
        check("SOP note tick/value/duration", note.Tick == 2 && note.Value == 60 && note.Duration == 8, note.Tick + "/" + note.Value + "/" + note.Duration);
        check("SOP length = note end", s.LengthTicks == 10, s.LengthTicks.ToString());
        // 4틱 @120(8틱/초) = 0.5초, 템포 240 이후 6틱 = 6/16 = 0.375초
        check("SOP seconds with tempo change", Math.Abs(s.SecondsAt(10) - 0.875) < 1e-9, s.SecondsAt(10).ToString("0.000"));
        check("SOP seconds ↔ tick", Math.Abs(s.TickAt(s.SecondsAt(7)) - 7) < 1e-6);
        check("SOP track labels", s.TrackLabel(0) == "1" && s.TrackLabel(19) == "20");
        bool threw = false;
        try { SopFormat.Read(new MemoryStream(new byte[100])); } catch (InvalidDataException) { threw = true; }
        check("SOP rejects non-SOP data", threw);

        // ---- OPL3 에뮬레이터
        var chip = new Opl3Emulator();
        chip.Write(0x105, 1);
        WriteSine(chip, 0x000, 0, 0x10);    // 첫 뱅크 채널 0: 왼쪽만
        WriteSine(chip, 0x100, 0, 0x20);    // 둘째 뱅크 채널 0: 오른쪽만
        KeyOn(chip, 0x000, 0, true);
        double l, r;
        Rms(chip, 4000, out l, out r);
        check("OPL3 left-only channel", l > 1000 && r < 1, l.ToString("0") + "/" + r.ToString("0"));
        KeyOn(chip, 0x000, 0, false);
        KeyOn(chip, 0x100, 0, true);
        Rms(chip, 30000, out l, out r);   // 왼쪽 릴리스가 끝날 때까지
        Rms(chip, 4000, out l, out r);
        check("OPL3 bank 2 right-only channel", r > 1000 && l < 50, l.ToString("0") + "/" + r.ToString("0"));

        // 4-op: 채널 0+3, 연결 11(1 + 2→3 + 4). 채널 0은 무음, 소리는 채널 3 캐리어(오퍼레이터 4)에만 있다
        var c4 = new Opl3Emulator();
        c4.Write(0x105, 1);
        WriteSine(c4, 0x000, 0, 0x31);
        c4.Write(0x40 + CarOff[0], 0x3F);
        WriteSine(c4, 0x000, 3, 0x31);
        KeyOn(c4, 0x000, 0, true);
        Rms(c4, 4000, out l, out r);
        double twoOp = l;
        c4.Write(0x104, 0x01);
        KeyOn(c4, 0x000, 0, false); Rms(c4, 100, out l, out r);
        KeyOn(c4, 0x000, 0, true);
        Rms(c4, 4000, out l, out r);
        check("OPL3 4-op pair keys operators 3·4 from channel 0", twoOp < 100 && l > 1000, twoOp.ToString("0") + " → " + l.ToString("0"));

        // OPL2 모드(NEW=0)에서는 C0의 좌우 비트와 상관없이 양쪽으로 나온다
        var c2 = new Opl3Emulator();
        WriteSine(c2, 0x000, 0, 0x00);
        KeyOn(c2, 0x000, 0, true);
        Rms(c2, 4000, out l, out r);
        check("OPL3 in OPL2 mode outputs both sides", l > 1000 && Math.Abs(l - r) < 1, l.ToString("0") + "/" + r.ToString("0"));

        // ---- 재생: 팬 0(오른쪽) 4-op 트랙 + 팬 2(왼쪽) 2-op 트랙, 뮤트, 음높이
        // 노트: 틱 2 = 0.25초부터. 0.4초까지 렌더하면 두 트랙이 울리는 중, 0.5초(틱 4)에 템포 240
        var engine = new AudioEngine();
        engine.PlaySop(s);
        var buf = new short[2 * 4410];
        double sl = 0, sr = 0;
        for (int k = 0; k < 4; k++)
        {
            engine.RenderStereo(buf, 4410);
            if (k < 3) continue;
            for (int i = 0; i < buf.Length; i += 2) { sl += (double)buf[i] * buf[i]; sr += (double)buf[i + 1] * buf[i + 1]; }
        }
        check("SOP playback: both pans sound", sl > 0 && sr > 0, Math.Sqrt(sl / 4410).ToString("0") + "/" + Math.Sqrt(sr / 4410).ToString("0"));
        var states = new VoiceState[PlaybackOptions.Voices];
        engine.GetVoiceStates(states);
        check("SOP voice state: track 0 note + instrument", states[0].Note == 60 && states[0].Instrument == "FOUR", states[0].Note + " " + states[0].Instrument);
        engine.RenderStereo(buf, 4410); engine.RenderStereo(buf, 4410);
        check("SOP tempo multiplier after tempo event", Math.Abs(engine.TempoMultiplier - 2.0) < 1e-9, engine.TempoMultiplier.ToString("0.00"));
        var muted = new PlaybackOptions();
        muted.VoiceMute[6] = true;
        engine.PlaySop(s, 0, muted);
        for (int k = 0; k < 4; k++) engine.RenderStereo(buf, 4410);
        sr = 0;
        for (int i = 0; i < buf.Length; i += 2) sr += (double)buf[i] * buf[i];
        check("SOP mute track 6 (pan 2 = left) → left silent", sr == 0, Math.Sqrt(sr / 4410).ToString("0"));
        // 음높이 (AD262SOP 표): 트랙 6 = 사인, 노트 60 = 가운데 도 261.6Hz. 트랙 0을 끄고 왼쪽의 0 교차로 잰다
        var only6 = new PlaybackOptions();
        only6.VoiceMute[0] = true;
        var e2 = new AudioEngine();
        e2.PlaySop(s, 0, only6);
        for (int k = 0; k < 3; k++) e2.RenderStereo(buf, 4410);
        e2.RenderStereo(buf, 4410);
        int cross = 0;
        for (int i = 2; i < buf.Length; i += 2) if ((buf[i - 2] < 0) != (buf[i] < 0)) cross++;
        double hz = cross / 2.0 / 0.1;
        check("SOP note 60 = middle C (261.6 Hz)", Math.Abs(hz - 261.6) < 12, hz.ToString("0.0") + " Hz");
        bool ended = false;
        for (int k = 0; k < 40 && !ended; k++) { engine.RenderStereo(buf, 4410); ended = !engine.IsPlaying; }
        check("SOP playback ends", ended);

        RunReal(check);
    }

    // ------------------------------------------------------------ _incoming/sop

    private static void RunReal(Action<string, bool, string> check)
    {
        string dir = FindDir(Path.Combine("_incoming", "sop"));
        if (dir == null) { Console.WriteLine("SKIP  real SOP files (_incoming/sop not found)"); return; }
        foreach (var p in Directory.GetFiles(dir).Where(SopFormat.IsSop).OrderBy(x => x))
        {
            string name = Path.GetFileName(p);
            SopSong s;
            try { s = SopFormat.Load(p); }
            catch (Exception ex) { check("Real SOP " + name + " reads", false, ex.Message); continue; }
            int notes = s.Tracks.Sum(t => t.NoteCount);
            bool inRange = s.Tracks.All(t => t.Events.Where(e => e.Type == SopEventType.Note).All(e => e.Value >= 0 && e.Value < 128));
            bool instOk = s.Tracks.All(t => t.Events.Where(e => e.Type == SopEventType.Instrument).All(e => e.Value < s.Instruments.Count));
            check("Real SOP " + name + " reads", notes > 0 && inRange && instOk, s.TrackCount + " tracks, " + s.Instruments.Count + " insts, " + notes + " notes, " + s.SecondsAt(s.LengthTicks).ToString("0") + "s");

            var pcm = DemoRender.RenderSop(s, 3.0, 0);
            int peak = 0;
            foreach (short v in pcm) peak = Math.Max(peak, Math.Abs((int)v));
            check("Real SOP " + name + " renders sound", peak > 200, "peak=" + peak);
        }
        string iss = Path.Combine(dir, "SV6-GOSV.ISS"), sop = Path.Combine(dir, "SV6-GOSV.SOP");
        if (File.Exists(iss) && File.Exists(sop))
        {
            var lyr = IssLyrics.Load(iss);
            var s = SopFormat.Load(sop);
            double last = IssLyrics.ToSongTick(lyr.Records.Max(r => r.Tick), s.TickBeat, 0);
            check("Real SOP SV6-GOSV.ISS lyrics end with the song", last <= s.LengthTicks && last > s.LengthTicks * 0.95, last.ToString("0") + "/" + s.LengthTicks);
        }
    }

    internal static string FindDir(string rel)
    {
        string d = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && d != null; i++)
        {
            string c = Path.Combine(d, rel);
            if (Directory.Exists(c)) return c;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    // ------------------------------------------------------------ 도우미

    private static readonly int[] ModOff = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly int[] CarOff = { 3, 4, 5, 11, 12, 13, 19, 20, 21 };

    /// <summary>채널 ch에 순수 사인(모듈레이터 무음, 캐리어 최대) + C0 값.</summary>
    private static void WriteSine(Opl3Emulator c, int bank, int ch, int c0)
    {
        foreach (var (off, tl) in new[] { (ModOff[ch], 0x3F), (CarOff[ch], 0x00) })
        {
            c.Write(bank | (0x20 + off), 0x21);
            c.Write(bank | (0x40 + off), tl);
            c.Write(bank | (0x60 + off), 0xF0);
            c.Write(bank | (0x80 + off), 0x0F);
            c.Write(bank | (0xE0 + off), 0);
        }
        c.Write(bank | (0xC0 + ch), c0);
        c.Write(bank | (0xA0 + ch), 0x41);
    }

    private static void KeyOn(Opl3Emulator c, int bank, int ch, bool on)
    {
        c.Write(bank | (0xB0 + ch), (on ? 0x20 : 0) | (4 << 2) | 0x01);
    }

    private static void Rms(Opl3Emulator c, int n, out double l, out double r)
    {
        double sl = 0, sr = 0;
        for (int i = 0; i < n; i++) { int a, b; c.GenerateStereo(out a, out b); sl += (double)a * a; sr += (double)b * b; }
        l = Math.Sqrt(sl / n); r = Math.Sqrt(sr / n);
    }

    /// <summary>테스트용 SOP: 트랙 20개(0 = 4-op 모드, 3 = 짝, 나머지 2-op), 악기 3개, 트랙 0(팬 0 = 오른쪽) · 6(팬 2 = 왼쪽)에 노트.</summary>
    internal static byte[] BuildSop()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("sopepos"));
        w.Write((byte)0); w.Write((byte)1); w.Write((byte)0);
        w.Write(Fixed("TEST.SOP", 13));
        w.Write(Fixed("Test song", 31));
        w.Write((byte)0); w.Write((byte)0);   // 퍼커션 아님
        w.Write((byte)4); w.Write((byte)0);   // 박당 4틱
        w.Write((byte)4); w.Write((byte)120);
        w.Write(Fixed("", 13));
        w.Write((byte)20); w.Write((byte)3); w.Write((byte)0);
        for (int t = 0; t < 20; t++) w.Write((byte)(t == 0 ? 1 : t == 3 ? 0 : 2));

        byte[] op = { 0x21, 0x00, 0xF0, 0x0F, 0x00 };
        byte[] silentMod = { 0x21, 0x3F, 0xF0, 0x0F, 0x00 };
        // 0: 4-op (연결 1, 1 → 모두 캐리어)
        w.Write((byte)0); w.Write(Fixed("FOUR", 8)); w.Write(Fixed("Four op", 19));
        w.Write(op); w.Write((byte)0x01); w.Write(op); w.Write(op); w.Write((byte)0x01); w.Write(op);
        // 1: 2-op (FM, 모듈레이터 무음 = 사인)
        w.Write((byte)1); w.Write(Fixed("TWO", 8)); w.Write(Fixed("Two op", 19));
        w.Write(silentMod); w.Write((byte)0x00); w.Write(op);
        // 2: 빈 칸
        w.Write((byte)12); w.Write(Fixed("", 8)); w.Write(Fixed("", 19));

        for (int t = 0; t <= 20; t++)
        {
            var ev = new MemoryStream();
            var e = new BinaryWriter(ev);
            int count = 0;
            if (t == 0 || t == 6)
            {
                e.Write((ushort)0); e.Write((byte)6); e.Write((byte)(t == 0 ? 0 : 1)); count++;   // 악기
                e.Write((ushort)0); e.Write((byte)7); e.Write((byte)(t == 0 ? 0 : 2)); count++;   // 팬
                e.Write((ushort)0); e.Write((byte)6); e.Write((byte)2); count++;                  // 빈 악기: 무시
                e.Write((ushort)2); e.Write((byte)2); e.Write((byte)60); e.Write((ushort)8); count++;   // 노트
            }
            if (t == 20)
            {
                e.Write((ushort)4); e.Write((byte)3); e.Write((byte)240); count++;   // 템포
            }
            w.Write((ushort)count);
            w.Write((uint)ev.Length);
            w.Write(ev.ToArray());
        }
        return ms.ToArray();
    }

    private static byte[] Fixed(string s, int n)
    {
        var b = new byte[n];
        var src = Encoding.ASCII.GetBytes(s);
        Array.Copy(src, b, Math.Min(src.Length, n - 1));
        return b;
    }
}
