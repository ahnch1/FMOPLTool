using System;
using System.Collections.Generic;
using System.IO;

namespace AdlibComposer.Formats;

/// <summary>SOP 악기 1개. Type 0 = 4-오퍼레이터(22바이트), 1~10 = 2-오퍼레이터(11바이트), 11·12 = 빈 칸(데이터 없음).</summary>
public sealed class SopInstrument
{
    public const int Type4Op = 0, Type2Op = 1, TypeBd = 6, TypeSd = 7, TypeTom = 8, TypeCym = 9, TypeHh = 10, TypeWave = 11, TypeNone = 12;

    public int Type;
    public string ShortName = "";
    public string LongName = "";
    /// <summary>
    /// 레지스터 값 그대로. 2-op: [0..4] 모듈레이터 20/40/60/80/E0, [5] C0(피드백·연결), [6..10] 캐리어 20/40/60/80/E0.
    /// 4-op: [0..10] 첫 채널(오퍼레이터 1·2), [11..21] 짝 채널(오퍼레이터 3·4) 같은 순서. 단일 오퍼레이터 드럼은 [0..4]를 쓴다.
    /// </summary>
    public byte[] Data = new byte[0];

    public bool Is4Op { get { return Type == Type4Op && Data.Length >= 22; } }
    public bool IsEmpty { get { return Data.Length < 11; } }
    public string Name { get { return ShortName.Length > 0 ? ShortName : LongName; } }
}

public enum SopEventType { Special = 1, Note = 2, Tempo = 3, Volume = 4, Pitch = 5, Instrument = 6, Pan = 7, MasterVolume = 8 }

/// <summary>SOP 이벤트 (절대 틱). Note는 Value = 음 번호, Duration = 길이(틱, 0이면 다음 노트까지).</summary>
public struct SopEvent
{
    public int Tick;
    public SopEventType Type;
    public int Value;
    public int Duration;
}

public sealed class SopTrack
{
    /// <summary>헤더의 채널 모드: 0 = 쓰지 않음(4-op 짝으로 쓰임), 1 = 4-op, 2 = 2-op.</summary>
    public int ChannelMode;
    public List<SopEvent> Events { get; } = new List<SopEvent>();
    public int NoteCount { get { int n = 0; foreach (var e in Events) if (e.Type == SopEventType.Note) n++; return n; } }

    public int EndTick
    {
        get
        {
            int end = 0;
            foreach (var e in Events)
            {
                int t = e.Type == SopEventType.Note ? e.Tick + e.Duration : e.Tick;
                if (t > end) end = t;
            }
            return end;
        }
    }
}

/// <summary>
/// SOP 곡 (Sopepos의 OPL3/YMF262용 곡 형식, "sopepos" 서명). 트랙 최대 20개 + 제어 트랙(템포 · 마스터 볼륨).
/// 트랙 → 칩 채널: 0~8 = 첫 뱅크 채널 0~8 (리듬 모드면 6 BD, 7 SD, 8 TOM, 9 CY, 10 HH), 11~19 = 둘째 뱅크 채널 0~8.
/// 채널 모드 1(4-op)인 트랙 0·1·2·11·12·13은 채널 +3과 짝을 지어 오퍼레이터 4개로 소리 낸다.
/// </summary>
public sealed class SopSong
{
    public const int MaxTracks = 20;

    public int VersionMajor;
    public int VersionMinor;
    public string FileName = "";
    public string Title = "";
    public string Comment = "";
    /// <summary>true = 리듬(퍼커션) 모드 (트랙 6~10 = BD SD TOM CY HH).</summary>
    public bool Percussive;
    public int TickBeat = 12;
    public int BeatsPerMeasure = 4;
    public int BasicTempo = 120;
    public string FilePath;
    public List<SopInstrument> Instruments { get; } = new List<SopInstrument>();
    public SopTrack[] Tracks = new SopTrack[0];
    /// <summary>제어 트랙 (템포 · 마스터 볼륨).</summary>
    public SopTrack Control = new SopTrack();

    public int TrackCount { get { return Tracks.Length; } }

    public int LengthTicks
    {
        get
        {
            int e = Control.EndTick;
            foreach (var t in Tracks) if (t.EndTick > e) e = t.EndTick;
            return e;
        }
    }

    /// <summary>리듬 모드의 드럼 트랙이면 0x10(BD) ~ 0x01(HH) 비트, 아니면 0.</summary>
    public int DrumBit(int track)
    {
        if (!Percussive) return 0;
        switch (track) { case 6: return 0x10; case 7: return 0x08; case 8: return 0x04; case 9: return 0x02; case 10: return 0x01; }
        return 0;
    }

    /// <summary>믹서 등에 보일 트랙 이름: 1~20, 리듬 모드의 드럼은 BD SD TO CY HH.</summary>
    public string TrackLabel(int track)
    {
        if (Percussive && track >= 6 && track <= 10) return new[] { "BD", "SD", "TO", "CY", "HH" }[track - 6];
        return (track + 1).ToString();
    }

    /// <summary>템포 이벤트 (제어 트랙, 절대 BPM). 0이면 기본 템포.</summary>
    private List<KeyValuePair<int, int>> TempoChanges()
    {
        var l = new List<KeyValuePair<int, int>>();
        foreach (var e in Control.Events)
            if (e.Type == SopEventType.Tempo) l.Add(new KeyValuePair<int, int>(e.Tick, e.Value == 0 ? BasicTempo : e.Value));
        return l;
    }

    private double TicksPerSecond(int bpm) { return Math.Max(1, bpm) * Math.Max(1, TickBeat) / 60.0; }

    public double SecondsAt(double tick)
    {
        double sec = 0, last = 0;
        int bpm = BasicTempo;
        foreach (var c in TempoChanges())
        {
            if (c.Key >= tick) break;
            sec += (c.Key - last) / TicksPerSecond(bpm);
            last = c.Key;
            bpm = c.Value;
        }
        return sec + (tick - last) / TicksPerSecond(bpm);
    }

    public double TickAt(double seconds)
    {
        if (seconds <= 0) return 0;
        double at = 0, last = 0;
        int bpm = BasicTempo;
        foreach (var c in TempoChanges())
        {
            double seg = (c.Key - last) / TicksPerSecond(bpm);
            if (at + seg >= seconds) break;
            at += seg;
            last = c.Key;
            bpm = c.Value;
        }
        return last + (seconds - at) * TicksPerSecond(bpm);
    }

    /// <summary>해당 틱의 템포 (BPM).</summary>
    public int TempoAt(double tick)
    {
        int bpm = BasicTempo;
        foreach (var c in TempoChanges()) { if (c.Key > tick) break; bpm = c.Value; }
        return bpm;
    }
}

/// <summary>
/// SOP 읽기. 구조 (리틀 엔디언):
/// <code>
/// 0x00 "sopepos"(7) · 버전 major(1) minor(1) · 예약(1) · 파일 이름(13) · 제목(31)
/// 0x36 퍼커션(1) · 예약(1) · 박당 틱(1) · 예약(1) · 마디당 박(1) · 기본 템포(1) · 설명(13)
/// 0x49 트랙 수(1) · 악기 수(1) · 예약(1) · 채널 모드 × 트랙 수
/// 악기 × 악기 수: 종류(1) · 짧은 이름(8) · 긴 이름(19) · 데이터 (종류 0 = 22, 11·12 = 0, 나머지 = 11)
/// 트랙 × (트랙 수 + 1): 이벤트 수(u16) · 크기(u32) · 이벤트들
/// 이벤트: 앞 이벤트부터의 틱(u16) · 종류(1) · 값 (노트 = 음(1) + 길이(u16), 나머지 = 1바이트)
/// </code>
/// </summary>
public static class SopFormat
{
    public static readonly string[] Extensions = { ".sop" };

    /// <summary>파일 대화상자 필터용 설명.</summary>
    public static readonly IFileFormat FileInfo = new SopFileInfo();

    private sealed class SopFileInfo : IFileFormat
    {
        public string Id { get { return "sop"; } }
        public string DisplayName { get { return "SOP song (OPL3)"; } }
        public string[] Extensions { get { return SopFormat.Extensions; } }
    }

    public static bool IsSop(string path)
    {
        return string.Equals(Path.GetExtension(path), ".sop", StringComparison.OrdinalIgnoreCase);
    }

    public static SopSong Load(string path)
    {
        SopSong s;
        using (var fs = File.OpenRead(path)) s = Read(fs);
        s.FilePath = path;
        return s;
    }

    public static SopSong Read(Stream stream)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] d = ms.ToArray();
        if (d.Length < 0x4C || d[0] != 's' || d[1] != 'o' || d[2] != 'p' || d[3] != 'e' || d[4] != 'p' || d[5] != 'o' || d[6] != 's')
            throw new InvalidDataException("SOP 파일이 아닙니다 (sopepos 서명 없음).");

        var s = new SopSong
        {
            VersionMajor = d[7], VersionMinor = d[8],
            FileName = KoreanText.Decode(d, 0x0A, Field(d, 0x0A, 13)),
            Title = KoreanText.Decode(d, 0x17, Field(d, 0x17, 31)).Trim(),
            Percussive = d[0x36] != 0,
            TickBeat = Math.Max(1, (int)d[0x38]),
            BeatsPerMeasure = Math.Max(1, (int)d[0x3A]),
            BasicTempo = d[0x3B] == 0 ? 120 : d[0x3B],
            Comment = KoreanText.Decode(d, 0x3C, Field(d, 0x3C, 13)).Trim(),
        };
        int nTracks = d[0x49], nInsts = d[0x4A];
        if (nTracks == 0 || nTracks > SopSong.MaxTracks) throw new InvalidDataException("SOP 트랙 수가 이상합니다: " + nTracks);
        int p = 0x4C;
        Need(d, p, nTracks);
        var modes = new int[nTracks];
        for (int t = 0; t < nTracks; t++) modes[t] = d[p + t];
        p += nTracks;

        for (int i = 0; i < nInsts; i++)
        {
            Need(d, p, 28);
            var ins = new SopInstrument
            {
                Type = d[p],
                ShortName = KoreanText.Decode(d, p + 1, Field(d, p + 1, 8)).Trim(),
                LongName = KoreanText.Decode(d, p + 9, Field(d, p + 9, 19)).Trim(),
            };
            p += 28;
            int len = ins.Type == SopInstrument.Type4Op ? 22 : ins.Type == SopInstrument.TypeWave || ins.Type == SopInstrument.TypeNone ? 0 : 11;
            Need(d, p, len);
            ins.Data = new byte[len];
            Array.Copy(d, p, ins.Data, 0, len);
            p += len;
            s.Instruments.Add(ins);
        }

        s.Tracks = new SopTrack[nTracks];
        for (int t = 0; t <= nTracks; t++)
        {
            Need(d, p, 6);
            int size = (int)BitConverter.ToUInt32(d, p + 2);
            p += 6;
            if (size < 0 || p + size > d.Length) throw new InvalidDataException("SOP 트랙 " + t + " 크기가 파일보다 큽니다.");
            var tr = new SopTrack { ChannelMode = t < nTracks ? modes[t] : 0 };
            ParseEvents(d, p, size, tr);
            p += size;
            if (t < nTracks) s.Tracks[t] = tr; else s.Control = tr;
        }
        return s;
    }

    // ------------------------------------------------------------ 쓰기

    public static void Save(SopSong s, string path)
    {
        var ms = new MemoryStream();
        Write(s, ms);
        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>SOP 쓰기 (Read와 같은 구조). 앞 이벤트와 65535틱보다 멀면 사이에 특수 이벤트(1, 값 0)를 끼워 넣는다.</summary>
    public static void Write(SopSong s, Stream stream)
    {
        if (s.TrackCount == 0 || s.TrackCount > SopSong.MaxTracks) throw new InvalidDataException("SOP 트랙 수는 1~20이어야 합니다: " + s.TrackCount);
        if (s.Instruments.Count > 255) throw new InvalidDataException("SOP 악기는 255개까지입니다: " + s.Instruments.Count);
        var w = new BinaryWriter(stream);
        w.Write(System.Text.Encoding.ASCII.GetBytes("sopepos"));
        w.Write((byte)s.VersionMajor); w.Write((byte)s.VersionMinor); w.Write((byte)0);
        w.Write(Text(s.FileName, 13));
        w.Write(Text(s.Title, 31));
        w.Write((byte)(s.Percussive ? 1 : 0)); w.Write((byte)0);
        w.Write(Byte(s.TickBeat, 1)); w.Write((byte)0);
        w.Write(Byte(s.BeatsPerMeasure, 1)); w.Write(Byte(s.BasicTempo, 1));
        w.Write(Text(s.Comment, 13));
        w.Write((byte)s.TrackCount); w.Write((byte)s.Instruments.Count); w.Write((byte)0);
        foreach (var t in s.Tracks) w.Write((byte)t.ChannelMode);
        foreach (var ins in s.Instruments)
        {
            w.Write((byte)ins.Type);
            w.Write(Text(ins.ShortName, 8));
            w.Write(Text(ins.LongName, 19));
            int len = ins.Type == SopInstrument.Type4Op ? 22 : ins.Type == SopInstrument.TypeWave || ins.Type == SopInstrument.TypeNone ? 0 : 11;
            var d = new byte[len];
            Array.Copy(ins.Data, d, Math.Min(len, ins.Data.Length));
            w.Write(d);
        }
        for (int t = 0; t <= s.TrackCount; t++) WriteTrack(w, t < s.TrackCount ? s.Tracks[t] : s.Control);
        w.Flush();
    }

    private static void WriteTrack(BinaryWriter w, SopTrack tr)
    {
        var ev = new List<SopEvent>(tr.Events);
        // 같은 틱 안에서는 원래 순서를 지킨다 (안정 정렬)
        var idx = new List<int>();
        for (int i = 0; i < ev.Count; i++) idx.Add(i);
        idx.Sort((a, b) => { int c = ev[a].Tick.CompareTo(ev[b].Tick); return c != 0 ? c : a.CompareTo(b); });
        var ms = new MemoryStream();
        var e = new BinaryWriter(ms);
        int count = 0, last = 0;
        foreach (int i in idx)
        {
            var x = ev[i];
            int delta = Math.Max(0, x.Tick - last);
            while (delta > 0xFFFF)
            {
                e.Write((ushort)0xFFFF); e.Write((byte)SopEventType.Special); e.Write((byte)0);
                count++;
                delta -= 0xFFFF;
            }
            e.Write((ushort)delta);
            e.Write((byte)x.Type);
            if (x.Type == SopEventType.Note)
            {
                e.Write(Byte(x.Value, 0));
                e.Write((ushort)Math.Max(0, Math.Min(0xFFFF, x.Duration)));
            }
            else e.Write(Byte(x.Value, 0));
            count++;
            last = Math.Max(last, x.Tick);
        }
        if (count > 0xFFFF) throw new InvalidDataException("SOP 트랙 이벤트가 너무 많습니다: " + count);
        w.Write((ushort)count);
        w.Write((uint)ms.Length);
        w.Write(ms.ToArray());
    }

    private static byte Byte(int v, int min) { return (byte)Math.Max(min, Math.Min(255, v)); }

    /// <summary>고정 길이 NUL 채움 문자열 (마지막 바이트는 항상 0, 2바이트 글자는 자르지 않는다).</summary>
    private static byte[] Text(string s, int length)
    {
        var buf = new byte[length];
        byte[] src = KoreanText.Encode(s ?? "");
        int n = 0;
        while (n < src.Length)
        {
            int w = src[n] >= 0x80 ? 2 : 1;
            if (n + w > length - 1) break;
            n += w;
        }
        Array.Copy(src, buf, Math.Min(n, src.Length));
        return buf;
    }

    private static void ParseEvents(byte[] d, int start, int size, SopTrack tr)
    {
        int q = start, end = start + size, tick = 0;
        while (q + 3 <= end)
        {
            tick += d[q] | (d[q + 1] << 8);
            int type = d[q + 2];
            q += 3;
            var e = new SopEvent { Tick = tick, Type = (SopEventType)type };
            if (type == (int)SopEventType.Note)
            {
                if (q + 3 > end) break;
                e.Value = d[q];
                e.Duration = d[q + 1] | (d[q + 2] << 8);
                q += 3;
            }
            else
            {
                if (q + 1 > end) break;
                e.Value = d[q];
                q += 1;
                if (type < 1 || type > 8) continue;   // 모르는 이벤트: 값 1바이트로 보고 건너뛴다
            }
            tr.Events.Add(e);
        }
    }

    /// <summary>NUL 앞까지의 길이.</summary>
    private static int Field(byte[] d, int offset, int length)
    {
        int n = 0;
        while (n < length && offset + n < d.Length && d[offset + n] != 0) n++;
        return n;
    }

    private static void Need(byte[] d, int p, int count)
    {
        if (p + count > d.Length) throw new EndOfStreamException("SOP 파일이 중간에 끝났습니다.");
    }
}
