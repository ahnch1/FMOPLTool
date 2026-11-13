using System;
using System.IO;
using System.Text;
using System.Xml;
using AdlibComposer.Core;

namespace AdlibComposer.Notation;

/// <summary>
/// MusicXML 3.1 (score-partwise) 내보내기. 보이스 = 파트, divisions = 박당 틱.
/// 음은 그릴 수 있는 음표로 나누어 붙임줄로 잇고, 드럼 보이스는 한 줄 타악기 보표(unpitched, X 머리)로 쓴다.
/// MuseScore / Finale / Sibelius 등에서 열 수 있다.
/// </summary>
public sealed class MusicXmlExporter : IScoreExporter
{
    public string Id { get { return "musicxml"; } }
    public string DisplayName { get { return "MusicXML"; } }
    public string Extension { get { return ".musicxml"; } }

    public void Export(Score score, Stream stream)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument();
            w.WriteDocType("score-partwise", "-//Recordare//DTD MusicXML 3.1 Partwise//EN", "http://www.musicxml.org/dtds/partwise.dtd", null);
            w.WriteStartElement("score-partwise");
            w.WriteAttributeString("version", "3.1");

            w.WriteStartElement("work");
            w.WriteElementString("work-title", string.IsNullOrEmpty(score.Title) ? "Untitled" : score.Title);
            w.WriteEndElement();
            w.WriteStartElement("identification");
            w.WriteStartElement("encoding");
            w.WriteElementString("software", "FMOPL Tool");
            w.WriteElementString("encoding-date", DateTime.Now.ToString("yyyy-MM-dd"));
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("part-list");
            for (int i = 0; i < score.Voices.Count; i++)
            {
                var v = score.Voices[i];
                w.WriteStartElement("score-part");
                w.WriteAttributeString("id", PartId(i));
                w.WriteElementString("part-name", PartName(v));
                w.WriteElementString("part-abbreviation", v.Label ?? (v.VoiceIndex + 1).ToString());
                w.WriteEndElement();
            }
            w.WriteEndElement();

            for (int i = 0; i < score.Voices.Count; i++) WritePart(w, score, score.Voices[i], PartId(i), i == 0);

            w.WriteEndElement();
            w.WriteEndDocument();
        }
    }

    private static string PartId(int i) { return "P" + (i + 1); }

    public static string PartName(ScoreVoice v)
    {
        string label = v.Drum != DrumKind.None ? StaffRules.DrumName(v.Drum) : "Voice " + (v.Label ?? (v.VoiceIndex + 1).ToString());
        return string.IsNullOrEmpty(v.InstrumentName) ? label : label + " (" + v.InstrumentName + ")";
    }

    private static void WritePart(XmlWriter w, Score score, ScoreVoice v, string id, bool first)
    {
        var clef = StaffRules.ClefFor(v);
        w.WriteStartElement("part");
        w.WriteAttributeString("id", id);
        foreach (var m in v.Measures)
        {
            w.WriteStartElement("measure");
            w.WriteAttributeString("number", m.Number.ToString());
            if (m.Number == 1)
            {
                w.WriteStartElement("attributes");
                w.WriteElementString("divisions", Math.Max(1, score.TickBeat).ToString());
                w.WriteStartElement("key"); w.WriteElementString("fifths", "0"); w.WriteEndElement();
                w.WriteStartElement("time");
                w.WriteElementString("beats", score.BeatsPerMeasure.ToString());
                w.WriteElementString("beat-type", "4");
                w.WriteEndElement();
                if (clef == StaffClef.Percussion)
                {
                    w.WriteStartElement("staff-details"); w.WriteElementString("staff-lines", "1"); w.WriteEndElement();
                    w.WriteStartElement("clef"); w.WriteElementString("sign", "percussion"); w.WriteEndElement();
                }
                else
                {
                    w.WriteStartElement("clef");
                    w.WriteElementString("sign", clef == StaffClef.Bass ? "F" : "G");
                    w.WriteElementString("line", clef == StaffClef.Bass ? "4" : "2");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                if (first)
                {
                    w.WriteStartElement("direction");
                    w.WriteAttributeString("placement", "above");
                    w.WriteStartElement("direction-type");
                    w.WriteStartElement("metronome");
                    w.WriteElementString("beat-unit", "quarter");
                    w.WriteElementString("per-minute", Math.Round(score.Tempo).ToString());
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteStartElement("sound");
                    w.WriteAttributeString("tempo", Math.Round(score.Tempo, 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
            }

            foreach (var n in m.Notes)
            {
                var glyphs = NoteSpelling.Split(n.StartInMeasure, n.Duration, score.TickBeat);
                for (int g = 0; g < glyphs.Count; g++)
                {
                    bool tieStart = !n.IsRest && (g < glyphs.Count - 1 || n.TiedToNext);
                    bool tieStop = !n.IsRest && (g > 0 || n.TiedFromPrevious);
                    WriteNote(w, v, n, glyphs[g], tieStart, tieStop);
                }
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    private static void WriteNote(XmlWriter w, ScoreVoice v, ScoreNote n, NoteGlyph glyph, bool tieStart, bool tieStop)
    {
        w.WriteStartElement("note");
        if (n.IsRest) w.WriteElementString("rest", "");
        else if (v.Drum != DrumKind.None)
        {
            w.WriteStartElement("unpitched");
            w.WriteElementString("display-step", "B");
            w.WriteElementString("display-octave", "4");
            w.WriteEndElement();
        }
        else
        {
            int step, alter, octave;
            NoteSpelling.Spell(n.Pitch, out step, out alter, out octave);
            w.WriteStartElement("pitch");
            w.WriteElementString("step", NoteSpelling.StepNames[step]);
            if (alter != 0) w.WriteElementString("alter", alter.ToString());
            w.WriteElementString("octave", octave.ToString());
            w.WriteEndElement();
        }
        w.WriteElementString("duration", glyph.Duration.ToString());
        if (tieStop) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
        if (tieStart) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
        w.WriteElementString("voice", "1");
        w.WriteElementString("type", NoteSpelling.TypeName(glyph.Value));
        for (int d = 0; d < glyph.Dots; d++) w.WriteElementString("dot", "");
        if (glyph.Triplet)
        {
            w.WriteStartElement("time-modification");
            w.WriteElementString("actual-notes", "3");
            w.WriteElementString("normal-notes", "2");
            w.WriteEndElement();
        }
        if (!n.IsRest && v.Drum == DrumKind.None)
        {
            int step, alter, octave;
            NoteSpelling.Spell(n.Pitch, out step, out alter, out octave);
            if (alter != 0) w.WriteElementString("accidental", "sharp");
        }
        if (!n.IsRest && StaffRules.XHead(v.Drum)) w.WriteElementString("notehead", "x");
        if (tieStart || tieStop)
        {
            w.WriteStartElement("notations");
            if (tieStop) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            if (tieStart) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
}
