using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AdlibComposer.Core;
using AdlibComposer.Formats;
using AdlibComposer.Notation;

namespace AdlibComposer.SelfTest;

/// <summary>악보(Note 페이지) 기능: 음표 나누기, 음 이름, MusicXML / MIDI 내보내기.</summary>
public static class NotationExportTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- 음표 나누기
        string S(int start, int dur, int tb) { return string.Join(" ", NoteSpelling.Split(start, dur, tb).Select(g => NoteSpelling.TypeName(g.Value) + new string('.', g.Dots) + (g.Exact ? "" : "~"))); }
        check("Split quarter (48 tpb)", S(0, 48, 48) == "quarter", S(0, 48, 48));
        check("Split dotted quarter", S(0, 72, 48) == "quarter.", S(0, 72, 48));
        check("Split dotted half", S(0, 144, 48) == "half.", S(0, 144, 48));
        check("Split off-beat eighth + quarter", S(24, 72, 48) == "eighth quarter", S(24, 72, 48));
        check("Split 5 sixteenths = quarter + 16th", S(0, 60, 48) == "quarter 16th", S(0, 60, 48));
        check("Split whole measure", S(0, 192, 48) == "whole", S(0, 192, 48));
        check("Split 12 tpb sixteenth", S(0, 3, 12) == "16th", S(0, 3, 12));
        check("Split 4 tpb: 1 tick = 16th", S(0, 1, 4) == "16th", S(0, 1, 4));
        string T(int start, int dur, int tb) { return string.Join(" ", NoteSpelling.Split(start, dur, tb).Select(g => NoteSpelling.TypeName(g.Value) + (g.Triplet ? "3" : "") + (g.Exact ? "" : "~"))); }
        check("Split 3 tpb: 1 tick = eighth triplet", T(0, 1, 3) == "eighth3", T(0, 1, 3));
        check("Split 3 tpb: 2 ticks = quarter triplet", T(0, 2, 3) == "quarter3", T(0, 2, 3));
        check("Split 3 tpb: 4 ticks = quarter + eighth triplet", T(0, 4, 3) == "quarter eighth3", T(0, 4, 3));
        check("Split 48 tpb: 16 ticks = eighth triplet", T(16, 16, 48) == "eighth3", T(16, 16, 48));
        check("Split 48 tpb: binary preferred when exact", T(0, 24, 48) == "eighth", T(0, 24, 48));
        int sum = NoteSpelling.Split(5, 100, 48).Sum(g => g.Duration);
        check("Split keeps total length", sum == 100, sum.ToString());

        // ---- 음 이름
        int step, alter, oct;
        NoteSpelling.Spell(60, out step, out alter, out oct);
        check("Spell 60 = C4", step == 0 && alter == 0 && oct == 4);
        NoteSpelling.Spell(70, out step, out alter, out oct);
        check("Spell 70 = A#4", NoteSpelling.StepNames[step] == "A" && alter == 1 && oct == 4);

        // ---- 작은 곡: 멜로디(박을 넘는 음) + 베이스 + 드럼
        var song = new Song { TickBeat = 48, BeatsPerMeasure = 4, Title = "T", IsMelodic = false, BasicTempo = 100f };
        song.Voices[0].Notes.Add(new Note { StartTick = 0, Duration = 72, Pitch = 72 });        // 점4분 C5
        song.Voices[0].Notes.Add(new Note { StartTick = 72, Duration = 168, Pitch = 73 });      // 다음 마디까지 C#5
        song.Voices[1].Notes.Add(new Note { StartTick = 0, Duration = 192, Pitch = 36 });       // 온음표 C2 (낮은음자리표)
        for (int i = 0; i < 8; i++) song.Voices[10].Notes.Add(new Note { StartTick = i * 24, Duration = 12, Pitch = 36 });   // HH
        song.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "piano1" });
        song.Voices[0].Volumes.Add(new VolumeEvent { Tick = 0, Volume = 0.5f });
        var score = ScoreBuilder.Build(song, new ScoreOptions());

        check("Clef: high voice treble", StaffRules.ClefFor(score.Voices.First(v => v.VoiceIndex == 0)) == StaffClef.Treble);
        check("Clef: low voice bass", StaffRules.ClefFor(score.Voices.First(v => v.VoiceIndex == 1)) == StaffClef.Bass);
        check("Clef: HH percussion", StaffRules.ClefFor(score.Voices.First(v => v.VoiceIndex == 10)) == StaffClef.Percussion);

        // ---- MusicXML
        var ms = new MemoryStream();
        new MusicXmlExporter().Export(score, ms);
        XDocument doc = null;
        try { doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(ms.ToArray()), LoadOptions.None); } catch (Exception ex) { check("MusicXML well-formed", false, ex.Message); }
        if (doc != null)
        {
            check("MusicXML well-formed", true, "");
            var parts = doc.Root.Elements("part").ToList();
            check("MusicXML 3 parts", parts.Count == 3, parts.Count.ToString());
            bool measuresOk = true;
            foreach (var p in parts)
                foreach (var m in p.Elements("measure"))
                {
                    int dur = m.Elements("note").Sum(n => (int)n.Element("duration"));
                    if (dur != 192) measuresOk = false;
                }
            check("MusicXML every measure = 4 beats", measuresOk);
            var mel = parts[0].Descendants("note").Where(n => n.Element("pitch") != null).ToList();
            check("MusicXML melody: dotted quarter first", (string)mel[0].Element("type") == "quarter" && mel[0].Element("dot") != null);
            check("MusicXML C# has alter + accidental", mel.Any(n => (string)n.Element("pitch").Element("alter") == "1" && (string)n.Element("accidental") == "sharp"));
            check("MusicXML tie across barline", parts[0].Elements("measure").ElementAt(1).Elements("note").First().Elements("tie").Any(t => (string)t.Attribute("type") == "stop"));
            check("MusicXML percussion clef + x heads", parts[2].Descendants("sign").Any(s => (string)s == "percussion") && parts[2].Descendants("notehead").Any(h => (string)h == "x"));
            check("MusicXML bass clef F", parts[1].Descendants("sign").Any(s => (string)s == "F"));
        }

        // ---- MIDI
        var mid = new MemoryStream();
        new MidiExporter().Export(song, mid);
        var parsed = ParseMidi(mid.ToArray());
        check("MIDI header (format 1, PPQ 48)", parsed.Format == 1 && parsed.Ppq == 48, "format " + parsed.Format + " ppq " + parsed.Ppq);
        check("MIDI tracks = tempo + 3 voices", parsed.Tracks == 4, parsed.Tracks.ToString());
        check("MIDI note count", parsed.Notes.Count == 11, parsed.Notes.Count.ToString());
        check("MIDI tempo 100 BPM", parsed.FirstTempoUs == 600000, parsed.FirstTempoUs.ToString());
        check("MIDI hi-hat on channel 10 as GM 42", parsed.Notes.Count(n => n.Channel == 9 && n.Key == 42) == 8);
        check("MIDI melody velocity from volume 0.5", parsed.Notes.Where(n => n.Channel == 0).All(n => n.Velocity == 64));
        check("MIDI note times exact", parsed.Notes.Any(n => n.Channel == 0 && n.Start == 72 && n.Length == 168));

        // ---- 실제 곡
        string dir = FindDir("_incoming");
        if (dir != null && File.Exists(Path.Combine(dir, "WZ-GAME1.ROL")))
        {
            Song real;
            using (var fs = File.OpenRead(Path.Combine(dir, "WZ-GAME1.ROL"))) real = new RolFormat().Read(fs);
            var sc = ScoreBuilder.Build(real, new ScoreOptions());
            var xs = new MemoryStream();
            new MusicXmlExporter().Export(sc, xs);
            var xd = XDocument.Parse(System.Text.Encoding.UTF8.GetString(xs.ToArray()));
            int measureLen = real.MeasureTicks;
            bool ok = xd.Root.Elements("part").All(p => p.Elements("measure").All(m => m.Elements("note").Sum(n => (int)n.Element("duration")) == measureLen));
            check("Real WZ-GAME1.ROL -> MusicXML measures complete", ok, xd.Root.Elements("part").Count() + " parts");
            var rm = new MemoryStream();
            new MidiExporter().Export(real, rm);
            int realNotes = real.Voices.Take(real.VoiceCount).Sum(v => v.Notes.Count);
            var rp = ParseMidi(rm.ToArray());
            check("Real WZ-GAME1.ROL -> MIDI all notes", rp.Notes.Count == realNotes, rp.Notes.Count + "/" + realNotes);
        }
    }

    // ------------------------------------------------------------ 간단한 MIDI 파서 (검증용)

    public sealed class MidiNote { public int Channel, Key, Velocity, Start, Length; }
    public sealed class MidiInfo { public int Format, Tracks, Ppq, FirstTempoUs; public List<MidiNote> Notes = new List<MidiNote>(); }

    public static MidiInfo ParseMidi(byte[] b)
    {
        var info = new MidiInfo();
        int p = 0;
        int BE16() { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; }
        int BE32() { int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return v; }
        int VL() { int v = 0; byte c; do { c = b[p++]; v = (v << 7) | (c & 0x7F); } while ((c & 0x80) != 0); return v; }
        p = 8; info.Format = BE16(); info.Tracks = BE16(); info.Ppq = BE16();
        for (int t = 0; t < info.Tracks; t++)
        {
            p += 4; int len = BE32(); int end = p + len; int tick = 0; int status = 0;
            var open = new Dictionary<int, MidiNote>();
            while (p < end)
            {
                tick += VL();
                int s = b[p];
                if ((s & 0x80) != 0) { status = s; p++; }
                if (status == 0xFF)
                {
                    int type = b[p++]; int l = VL();
                    if (type == 0x51 && info.FirstTempoUs == 0) info.FirstTempoUs = (b[p] << 16) | (b[p + 1] << 8) | b[p + 2];
                    p += l;
                    if (type == 0x2F) break;
                    continue;
                }
                int hi = status & 0xF0, ch = status & 0x0F;
                if (hi == 0x90 || hi == 0x80)
                {
                    int key = b[p++], vel = b[p++];
                    int id = ch * 128 + key;
                    if (hi == 0x90 && vel > 0) open[id] = new MidiNote { Channel = ch, Key = key, Velocity = vel, Start = tick };
                    else if (open.TryGetValue(id, out var n)) { n.Length = tick - n.Start; info.Notes.Add(n); open.Remove(id); }
                }
                else if (hi == 0xC0 || hi == 0xD0) p += 1;
                else p += 2;
            }
            p = end;
        }
        return info;
    }

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null) { string c = Path.Combine(d.FullName, name); if (Directory.Exists(c)) return c; d = d.Parent; }
        return null;
    }
}
