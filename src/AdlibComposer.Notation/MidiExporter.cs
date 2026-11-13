using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Notation;

/// <summary>
/// Standard MIDI File (형식 1) 내보내기. 악보(Score)가 아니라 곡(Song)에서 바로 만들어 틱 그대로 정확하다.
/// PPQ = 박당 틱. 템포 트랙(기본 템포 × 템포 배수) + 보이스마다 트랙 1개.
/// 멜로딕 보이스는 채널 1~9(10번 제외), 리듬 모드 드럼 보이스는 채널 10에 General MIDI 드럼 음
/// (BD 36, SD 38, TO 45, CY 49, HH 42). 볼륨 이벤트 → 노트 세기, 피치 이벤트 → 피치벤드(범위 = 곡의 PitchBendRange).
/// AdLib 악기 이름은 GM 악기와 대응이 없어 트랙 이름으로만 남긴다 (프로그램은 0 = 피아노).
/// </summary>
public sealed class MidiExporter
{
    public string DisplayName { get { return "Standard MIDI file"; } }
    public string Extension { get { return ".mid"; } }

    private struct Ev
    {
        public int Tick;
        public int Order;      // 같은 틱: 노트 오프 < 기타 < 노트 온
        public byte[] Data;
    }

    public void Export(Song song, Stream stream)
    {
        int ppq = Math.Max(1, Math.Min(0x7FFF, song.TickBeat));
        var tracks = new List<byte[]>();
        tracks.Add(TempoTrack(song));

        int melodicChannel = 0;
        for (int v = 0; v < song.VoiceCount; v++)
        {
            var vt = song.Voices[v];
            if (vt.Notes.Count == 0) continue;
            var drum = StaffRules.DrumFor(v, song.IsMelodic);
            int ch;
            if (drum != DrumKind.None) ch = 9;
            else { ch = melodicChannel++; if (ch >= 9) ch++; if (ch > 15) ch = 15; }
            tracks.Add(VoiceTrack(song, v, vt, drum, ch));
        }

        var w = new BinaryWriter(stream, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("MThd"));
        WriteBE32(w, 6);
        WriteBE16(w, 1);
        WriteBE16(w, tracks.Count);
        WriteBE16(w, ppq);
        foreach (var t in tracks)
        {
            w.Write(Encoding.ASCII.GetBytes("MTrk"));
            WriteBE32(w, t.Length);
            w.Write(t);
        }
        w.Flush();
    }

    private static byte[] TempoTrack(Song song)
    {
        var evs = new List<Ev>();
        evs.Add(Meta(0, 0x03, Encoding.UTF8.GetBytes(string.IsNullOrEmpty(song.Title) ? "Untitled" : song.Title)));
        evs.Add(Meta(0, 0x58, new byte[] { (byte)Math.Max(1, Math.Min(255, song.BeatsPerMeasure)), 2, 24, 8 }));
        evs.Add(TempoEvent(0, song.BasicTempo));
        foreach (var t in song.TempoEvents)
            evs.Add(TempoEvent(t.Tick, song.BasicTempo * Math.Max(0.01f, t.Multiplier)));
        return Encode(evs, song.LengthTicks);
    }

    private static Ev TempoEvent(int tick, double bpm)
    {
        int usPerQuarter = (int)Math.Round(60000000.0 / Math.Max(1.0, bpm));
        usPerQuarter = Math.Max(1, Math.Min(0xFFFFFF, usPerQuarter));
        return Meta(tick, 0x51, new byte[] { (byte)(usPerQuarter >> 16), (byte)(usPerQuarter >> 8), (byte)usPerQuarter });
    }

    private static byte[] VoiceTrack(Song song, int v, VoiceTrack vt, DrumKind drum, int ch)
    {
        var evs = new List<Ev>();
        var t0 = vt.TimbreAt(0);
        string label = drum != DrumKind.None ? StaffRules.DrumName(drum) : "Voice " + song.Labels[v];
        evs.Add(Meta(0, 0x03, Encoding.UTF8.GetBytes(t0 != null ? label + " (" + t0.InstrumentName + ")" : label)));
        if (drum == DrumKind.None)
        {
            evs.Add(new Ev { Tick = 0, Order = 1, Data = new byte[] { (byte)(0xC0 | ch), 0 } });
            // 피치벤드 범위 (RPN 0) = 곡의 PitchBendRange 반음
            int range = Math.Max(1, Math.Min(24, song.PitchBendRange));
            evs.Add(new Ev { Tick = 0, Order = 1, Data = new byte[] { (byte)(0xB0 | ch), 101, 0 } });
            evs.Add(new Ev { Tick = 0, Order = 1, Data = new byte[] { (byte)(0xB0 | ch), 100, 0 } });
            evs.Add(new Ev { Tick = 0, Order = 1, Data = new byte[] { (byte)(0xB0 | ch), 6, (byte)range } });
            evs.Add(new Ev { Tick = 0, Order = 1, Data = new byte[] { (byte)(0xB0 | ch), 38, 0 } });
            foreach (var p in vt.Pitches)
            {
                int bend = Math.Max(0, Math.Min(0x3FFF, (int)Math.Round(p.Pitch * 8192.0)));
                evs.Add(new Ev { Tick = p.Tick, Order = 1, Data = new byte[] { (byte)(0xE0 | ch), (byte)(bend & 0x7F), (byte)(bend >> 7) } });
            }
        }

        var vols = new List<VolumeEvent>(vt.Volumes);
        vols.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        foreach (var n in vt.Notes)
        {
            if (n.Duration <= 0) continue;
            float vol = 1f;
            foreach (var e in vols) { if (e.Tick > n.StartTick) break; vol = e.Volume; }
            int vel = Math.Max(1, Math.Min(127, (int)Math.Round(vol * 127)));
            int key = drum != DrumKind.None ? StaffRules.GmDrumNote(drum) : Math.Max(0, Math.Min(127, n.Pitch));
            evs.Add(new Ev { Tick = n.StartTick, Order = 2, Data = new byte[] { (byte)(0x90 | ch), (byte)key, (byte)vel } });
            evs.Add(new Ev { Tick = n.EndTick, Order = 0, Data = new byte[] { (byte)(0x80 | ch), (byte)key, 0 } });
        }
        return Encode(evs, song.LengthTicks);
    }

    private static Ev Meta(int tick, int type, byte[] data)
    {
        var d = new List<byte> { 0xFF, (byte)type };
        d.AddRange(VarLen(data.Length));
        d.AddRange(data);
        return new Ev { Tick = tick, Order = 1, Data = d.ToArray() };
    }

    private static byte[] Encode(List<Ev> evs, int endTick)
    {
        evs.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        var ms = new MemoryStream();
        int last = 0;
        foreach (var e in evs)
        {
            var dl = VarLen(e.Tick - last);
            ms.Write(dl, 0, dl.Length);
            ms.Write(e.Data, 0, e.Data.Length);
            last = e.Tick;
        }
        var end = VarLen(Math.Max(0, endTick - last));
        ms.Write(end, 0, end.Length);
        ms.Write(new byte[] { 0xFF, 0x2F, 0x00 }, 0, 3);
        return ms.ToArray();
    }

    private static byte[] VarLen(int value)
    {
        value = Math.Max(0, value);
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0) { bytes.Insert(0, (byte)((value & 0x7F) | 0x80)); value >>= 7; }
        return bytes.ToArray();
    }

    private static void WriteBE16(BinaryWriter w, int v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
    private static void WriteBE32(BinaryWriter w, int v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
}
