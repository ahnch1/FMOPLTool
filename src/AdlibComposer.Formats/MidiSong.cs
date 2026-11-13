using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>MIDI 이벤트 하나. <see cref="Status"/>가 0xFF면 템포 바꾸기 (<see cref="Tempo"/> = 4분음표당 µs).</summary>
public struct MidiEvent
{
    public int Tick;
    public byte Status;
    public byte Data1;
    public byte Data2;
    /// <summary>Status = 0xFF일 때 4분음표당 마이크로초.</summary>
    public int Tempo;
    /// <summary>원래 트랙 번호 (정보용).</summary>
    public int Track;
    /// <summary>Status = 0xF0일 때 SysEx 전체 (F0 … F7). MT-32 사용자 음색 · 리버브 설정 등.</summary>
    public byte[] SysEx;

    public int Channel { get { return Status & 0x0F; } }
    public int Kind { get { return Status & 0xF0; } }
    public bool IsTempo { get { return Status == 0xFF; } }
    public bool IsSysEx { get { return Status == 0xF0; } }
    public bool IsNoteOn { get { return Kind == 0x90 && Data2 > 0; } }
    public bool IsNoteOff { get { return Kind == 0x80 || (Kind == 0x90 && Data2 == 0); } }
}

/// <summary>
/// MIDI 곡 (표준 MIDI 파일 .mid 또는 Cakewalk .wrk에서 읽음). 이벤트는 틱 순서로 한 목록에 모은다
/// (같은 틱: 노트 끄기 → 나머지 → 노트 켜기, 그 안에서는 파일 순서).
/// </summary>
public sealed class MidiSong
{
    public const int Channels = 16;
    public const int DefaultTempo = 500000;   // 120 BPM

    public string FilePath { get; set; }
    public string Title { get; set; } = "";
    /// <summary>형식 설명 (예: "MIDI 1", "Cakewalk WRK").</summary>
    public string FormatName { get; set; } = "MIDI";
    /// <summary>4분음표당 틱.</summary>
    public int Division { get; set; } = 120;
    public List<MidiEvent> Events { get; } = new List<MidiEvent>();
    public List<string> TrackNames { get; } = new List<string>();
    /// <summary>곡 안의 설명 글 (저작권 · 메모 · WRK 주석).</summary>
    public string Comment { get; set; } = "";
    /// <summary>마지막 이벤트 틱.</summary>
    public int LengthTicks { get; private set; }

    /// <summary>채널별로 쓰이는지 (노트가 있는지).</summary>
    public bool[] ChannelUsed { get; } = new bool[Channels];
    /// <summary>채널의 첫 프로그램 (없으면 -1).</summary>
    public int[] FirstProgram { get; } = Enumerable.Repeat(-1, Channels).ToArray();

    private int[] tempoTicks = new int[0];
    private double[] tempoSeconds = new double[0];
    private int[] tempoValues = new int[0];

    /// <summary>처음 템포 (BPM).</summary>
    public double InitialBpm { get { return 60000000.0 / (tempoValues.Length > 0 && tempoTicks[0] == 0 ? tempoValues[0] : DefaultTempo); } }

    /// <summary>이벤트를 정렬하고 길이 · 채널 정보 · 템포 지도를 만든다 (읽은 뒤 한 번).</summary>
    public void Finish()
    {
        var indexed = Events.Select((e, i) => (e, i)).ToList();
        indexed.Sort((a, b) =>
        {
            int c = a.e.Tick.CompareTo(b.e.Tick);
            if (c != 0) return c;
            c = Order(a.e).CompareTo(Order(b.e));
            return c != 0 ? c : a.i.CompareTo(b.i);
        });
        Events.Clear();
        Events.AddRange(indexed.Select(x => x.e));
        LengthTicks = Events.Count > 0 ? Events[Events.Count - 1].Tick : 0;
        foreach (var e in Events)
        {
            if (e.IsNoteOn) ChannelUsed[e.Channel] = true;
            if (e.Kind == 0xC0 && FirstProgram[e.Channel] < 0) FirstProgram[e.Channel] = e.Data1;
        }
        // 템포 지도
        var ticks = new List<int> { 0 };
        var values = new List<int> { DefaultTempo };
        foreach (var e in Events.Where(x => x.IsTempo && x.Tempo > 0))
        {
            if (e.Tick == ticks[ticks.Count - 1]) values[values.Count - 1] = e.Tempo;
            else { ticks.Add(e.Tick); values.Add(e.Tempo); }
        }
        tempoTicks = ticks.ToArray();
        tempoValues = values.ToArray();
        tempoSeconds = new double[tempoTicks.Length];
        for (int k = 1; k < tempoTicks.Length; k++)
            tempoSeconds[k] = tempoSeconds[k - 1] + (tempoTicks[k] - tempoTicks[k - 1]) * tempoValues[k - 1] / 1e6 / Division;
    }

    private static int Order(MidiEvent e) { return e.IsNoteOff ? 0 : e.IsNoteOn ? 2 : 1; }

    public double TickToSeconds(double tick)
    {
        int k = Array.BinarySearch(tempoTicks, (int)Math.Floor(tick));
        if (k < 0) k = ~k - 1;
        if (k < 0) k = 0;
        return tempoSeconds[k] + (tick - tempoTicks[k]) * tempoValues[k] / 1e6 / Division;
    }

    public double SecondsToTick(double seconds)
    {
        int k = tempoSeconds.Length - 1;
        while (k > 0 && tempoSeconds[k] > seconds) k--;
        return tempoTicks[k] + (seconds - tempoSeconds[k]) * 1e6 * Division / tempoValues[k];
    }

    public double LengthSeconds { get { return TickToSeconds(LengthTicks); } }
}

/// <summary>.mid / .midi / .wrk 열기.</summary>
public static class MidiFiles
{
    public static readonly string[] Extensions = { ".mid", ".midi", ".wrk" };

    public static bool IsMidi(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        return Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static MidiSong Load(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        MidiSong s;
        if (WrkFormat.IsWrk(d)) s = WrkFormat.Read(d);
        else s = MidiFileFormat.Read(d);
        s.FilePath = path;
        if (string.IsNullOrWhiteSpace(s.Title)) s.Title = Path.GetFileNameWithoutExtension(path);
        return s;
    }

    internal static string Text(byte[] d, int at, int len)
    {
        var t = Encoding.GetEncoding(28591).GetString(d, at, Math.Max(0, Math.Min(len, d.Length - at)));
        return t.Replace("\0", "").Trim();
    }
}

/// <summary>
/// 표준 MIDI 파일 (SMF 형식 0 · 1 · 2, "MThd" / "MTrk", RIFF RMID 포장도). 러닝 스테이터스, 메타(템포 · 트랙 이름 · 글), SysEx(F0 메시지는 이벤트로 남김, F7 이어 보내기는 건너뜀).
/// SMPTE 분할은 초당 틱으로 바꿔 템포 120 BPM에 맞춘다.
/// </summary>
public static class MidiFileFormat
{
    public static MidiSong Read(byte[] d)
    {
        int p = 0;
        if (d.Length > 20 && Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "RMID")
        {
            p = 12;
            while (p + 8 <= d.Length && Encoding.ASCII.GetString(d, p, 4) != "data") p += 8 + (int)U32LE(d, p + 4);
            p += 8;
        }
        if (p + 14 > d.Length || Encoding.ASCII.GetString(d, p, 4) != "MThd") throw new InvalidDataException("MIDI 파일이 아닙니다 (MThd 없음).");
        int hlen = (int)U32(d, p + 4);
        int format = U16(d, p + 8), ntrk = U16(d, p + 10), div = U16(d, p + 12);
        var s = new MidiSong { FormatName = "MIDI " + format };
        bool smpte = (div & 0x8000) != 0;
        if (smpte)
        {
            int fps = 256 - (div >> 8), tpf = div & 0xFF;
            s.Division = Math.Max(1, fps * tpf / 2);   // 120 BPM = 초당 2박
        }
        else s.Division = Math.Max(1, div);
        p += 8 + hlen;
        var comments = new List<string>();
        int tickBase = 0;
        for (int t = 0; t < ntrk && p + 8 <= d.Length; t++)
        {
            string id = Encoding.ASCII.GetString(d, p, 4);
            int len = (int)U32(d, p + 4);
            p += 8;
            int end = Math.Min(d.Length, p + len);
            if (id != "MTrk") { p = end; t--; continue; }
            int tick = format == 2 ? tickBase : 0;
            int running = 0;
            string trackName = null;
            int q = p;
            while (q < end)
            {
                tick += ReadVar(d, ref q, end);
                if (q >= end) break;
                int st = d[q];
                if (st >= 0x80) { q++; if (st < 0xF0) running = st; }
                else st = running;
                if (st == 0) break;
                if (st == 0xFF)
                {
                    if (q >= end) break;
                    int type = d[q++];
                    int ml = ReadVar(d, ref q, end);
                    if (type == 0x51 && ml >= 3 && q + 3 <= end)
                        s.Events.Add(new MidiEvent { Tick = tick, Status = 0xFF, Tempo = (d[q] << 16) | (d[q + 1] << 8) | d[q + 2], Track = t });
                    else if (type == 0x03 && trackName == null) trackName = MidiFiles.Text(d, q, ml);
                    else if ((type == 0x01 || type == 0x02) && ml > 0 && comments.Count < 20) comments.Add(MidiFiles.Text(d, q, ml));
                    q += ml;
                    if (type == 0x2F) break;
                    continue;
                }
                if (st == 0xF0 || st == 0xF7)
                {
                    int sl = ReadVar(d, ref q, end);
                    if (st == 0xF0 && sl > 0 && q + sl <= end)
                    {
                        var sx = new byte[sl + 1];
                        sx[0] = 0xF0;
                        Array.Copy(d, q, sx, 1, sl);
                        s.Events.Add(new MidiEvent { Tick = tick, Status = 0xF0, SysEx = sx, Track = t });
                    }
                    q += sl;
                    continue;
                }
                if (st >= 0xF0) continue;   // 실시간 / 공통 메시지 (파일에는 거의 없음)
                int kind = st & 0xF0;
                int n = kind == 0xC0 || kind == 0xD0 ? 1 : 2;
                if (q + n > end) break;
                var e = new MidiEvent { Tick = tick, Status = (byte)st, Data1 = (byte)(d[q] & 0x7F), Data2 = n > 1 ? (byte)(d[q + 1] & 0x7F) : (byte)0, Track = t };
                q += n;
                s.Events.Add(e);
            }
            if (format == 2) tickBase = tick;
            s.TrackNames.Add(trackName ?? "");
            p = end;
        }
        if (smpte) s.Events.Add(new MidiEvent { Tick = 0, Status = 0xFF, Tempo = MidiSong.DefaultTempo });
        s.Title = s.TrackNames.FirstOrDefault(x => x.Length > 0) ?? "";
        s.Comment = string.Join("\n", comments.Where(c => c.Length > 0).Distinct());
        s.Finish();
        return s;
    }

    private static int ReadVar(byte[] d, ref int q, int end)
    {
        int v = 0;
        for (int k = 0; k < 4 && q < end; k++)
        {
            int b = d[q++];
            v = (v << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }

    private static int U16(byte[] d, int i) { return (d[i] << 8) | d[i + 1]; }
    private static uint U32(byte[] d, int i) { return (uint)((d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3]); }
    private static uint U32LE(byte[] d, int i) { return (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)); }
}

/// <summary>
/// Cakewalk 작업 파일 (.wrk, Cakewalk for DOS / Windows). 머리 "CAKEWALK" 0x1A + 판 2바이트, 이어서 덩어리(종류 1바이트 · 길이 4바이트 LE · 내용), 0xFF로 끝.
/// <list type="bullet">
/// <item>0x01 트랙: 번호(2) · 이름 두 줄(길이+글) · 채널(255 = 이벤트의 채널) · 음높이 · 벨로시티 · 포트 · 플래그(2 = 꺼짐)</item>
/// <item>0x02 스트림: 트랙(2) · 개수(2) · 이벤트 8바이트 = 시각(3) · 상태 · 데이터1 · 데이터2 · 길이(2, 노트만)</item>
/// <item>0x04 템포: 개수(2) · 항목 18바이트 = 시각(4) · ?(4) · 템포(2, BPM 또는 BPM×100) · ?(8)</item>
/// <item>0x0A 박당 틱 (없으면 120), 0x08 주석, 0x24 · 0x2D · 0x0F: 새 판(Windows)의 트랙 · 스트림 · 템포</item>
/// </list>
/// </summary>
public static class WrkFormat
{
    public static bool IsWrk(byte[] d)
    {
        return d.Length > 11 && Encoding.ASCII.GetString(d, 0, 8) == "CAKEWALK";
    }

    private sealed class Trk { public string Name = ""; public int Channel = -1; public int Pitch; public int Velocity; public bool Muted; }

    public static MidiSong Read(byte[] d)
    {
        if (!IsWrk(d)) throw new InvalidDataException("Cakewalk WRK 파일이 아닙니다.");
        var s = new MidiSong { FormatName = "Cakewalk WRK " + d[10] + "." + d[9] };
        var tracks = new Dictionary<int, Trk>();
        var streams = new List<(int track, List<(int tick, int st, int d1, int d2, int dur)> evs)>();
        var tempos = new List<(int tick, int value)>();
        int p = 11;
        while (p < d.Length)
        {
            int id = d[p++];
            if (id == 0xFF || p + 4 > d.Length) break;
            int len = (int)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            p += 4;
            int end = Math.Min(d.Length, p + len);
            try
            {
                switch (id)
                {
                    case 0x01: ReadTrack(d, p, end, tracks); break;
                    case 0x24: ReadNewTrack(d, p, end, tracks); break;
                    case 0x02: streams.Add(ReadStream(d, p, end, false)); break;
                    case 0x2D: streams.Add(ReadStream(d, p, end, true)); break;
                    case 0x04:
                    case 0x0F:
                    {
                        int n = U16(d, p);
                        for (int k = 0; k < n && p + 2 + k * 18 + 10 <= end; k++)
                        {
                            int at = p + 2 + k * 18;
                            tempos.Add(((int)U32(d, at), U16(d, at + 8)));
                        }
                        break;
                    }
                    case 0x0A: s.Division = Math.Max(1, U16(d, p)); break;
                    case 0x08:
                    {
                        int n = U16(d, p);
                        s.Comment = MidiFiles.Text(d, p + 2, Math.Min(n, end - p - 2)).Replace("\r", "");
                        break;
                    }
                }
            }
            catch (IndexOutOfRangeException) { }
            p = end;
        }

        foreach (var (track, evs) in streams)
        {
            tracks.TryGetValue(track, out var info);
            if (info != null && info.Muted) continue;
            foreach (var (tick, st, d1, d2, dur) in evs)
            {
                int kind = st & 0xF0;
                int ch = info != null && info.Channel >= 0 && info.Channel < 16 ? info.Channel : st & 0x0F;
                int status = kind | ch;
                if (kind == 0x90)
                {
                    int note = Clamp7(d1 + (info?.Pitch ?? 0));
                    int vel = Clamp7(d2 + (info?.Velocity ?? 0));
                    if (vel == 0) continue;
                    s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)status, Data1 = (byte)note, Data2 = (byte)vel, Track = track });
                    s.Events.Add(new MidiEvent { Tick = tick + Math.Max(1, dur), Status = (byte)(0x80 | ch), Data1 = (byte)note, Track = track });
                }
                else if (kind >= 0xA0 && kind <= 0xE0)
                    s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)status, Data1 = (byte)(d1 & 0x7F), Data2 = (byte)(d2 & 0x7F), Track = track });
            }
        }
        foreach (var (tick, value) in tempos)
        {
            double bpm = value > 1000 ? value / 100.0 : value;   // 새 판은 BPM × 100
            if (bpm < 8 || bpm > 1000) continue;
            s.Events.Add(new MidiEvent { Tick = tick, Status = 0xFF, Tempo = (int)Math.Round(60000000.0 / bpm) });
        }
        foreach (var k in tracks.Keys.OrderBy(x => x)) s.TrackNames.Add(tracks[k].Name);
        s.Title = s.Comment.Length > 0 ? s.Comment.Split('\n')[0].Trim() : "";
        if (s.Title.Length > 60) s.Title = "";
        s.Finish();
        return s;
    }

    private static int Clamp7(int v) { return v < 0 ? 0 : v > 127 ? 127 : v; }

    private static void ReadTrack(byte[] d, int p, int end, Dictionary<int, Trk> tracks)
    {
        int no = U16(d, p); p += 2;
        var names = new string[2];
        for (int k = 0; k < 2; k++) { int n = d[p++]; names[k] = MidiFiles.Text(d, p, n); p += n; }
        var t = new Trk { Name = (names[0] + " " + names[1]).Trim() };
        int ch = d[p];
        t.Channel = ch == 255 ? -1 : ch;
        t.Pitch = (sbyte)d[p + 1];
        t.Velocity = (sbyte)d[p + 2];
        t.Muted = (d[p + 4] & 0x02) != 0;
        tracks[no] = t;
    }

    /// <summary>NTRACK (0x24, Cakewalk 판 3 이후): 번호(2) · 이름(길이+글) · 플래그 · 채널 · 음높이 · 벨로시티 ….</summary>
    private static void ReadNewTrack(byte[] d, int p, int end, Dictionary<int, Trk> tracks)
    {
        int no = U16(d, p); p += 2;
        int n = d[p++];
        var t = new Trk { Name = MidiFiles.Text(d, p, n) };
        p += n;
        int ch = d[p++];
        t.Channel = ch == 255 ? -1 : ch;
        t.Pitch = (sbyte)d[p++];
        t.Velocity = (sbyte)d[p++];
        p++;   // 포트
        t.Muted = (d[p] & 0x02) != 0;
        tracks[no] = t;
    }

    private static (int, List<(int, int, int, int, int)>) ReadStream(byte[] d, int p, int end, bool named)
    {
        var list = new List<(int, int, int, int, int)>();
        int track = U16(d, p); p += 2;
        if (named) { int nl = U16(d, p); p += 2 + nl; }   // NSTREAM: 세그먼트 이름
        int n = U16(d, p); p += 2;
        for (int k = 0; k < n && p + 8 <= end; k++, p += 8)
        {
            int tick = d[p] | (d[p + 1] << 8) | (d[p + 2] << 16);
            int st = d[p + 3];
            if (st < 0x90) continue;
            list.Add((tick, st, d[p + 4], d[p + 5], (st & 0xF0) == 0x90 ? U16(d, p + 6) : 0));
        }
        return (track, list);
    }

    private static int U16(byte[] d, int i) { return d[i] | (d[i + 1] << 8); }
    private static uint U32(byte[] d, int i) { return (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)); }
}
