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
