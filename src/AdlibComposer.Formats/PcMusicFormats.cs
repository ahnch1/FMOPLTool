using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>
/// DOS(PC/AT) 게임 음악 형식 — MIDI로 바꿔 이미 있는 MIDI 재생(FM · Windows · MT-32)으로 낸다:
/// <list type="bullet">
/// <item><see cref="XmiFormat"/>: Miles AIL XMIDI (.xmi) — 곡 여럿, 노트 온에 길이, 120Hz 고정 시간</item>
/// <item><see cref="MdiFormat"/>: Ad Lib MIDI Driver (.mdi) — 표준 MIDI + AdLib 악기 메타 이벤트 (곡이 FM 악기를 직접 정한다)</item>
/// <item><see cref="AilTimbres"/>: Miles AIL 음색 묶음 (.ad · .opl, "Global Timbre Library") → XMI를 FM으로 낼 때 그 게임의 AdLib 악기</item>
/// <item><see cref="AdlibMus"/> · <see cref="SndBankFormat"/>: Ad Lib MUS (.mus, IMS와 같은 이벤트) + 음색 뱅크 (.snd)</item>
/// </list>
/// </summary>
public static class PcMusicFormats
{
    public static readonly string[] MidiLikeExtensions = { ".xmi", ".mdi" };

    /// <summary>표준 MIDI 가변 길이 수.</summary>
    internal static int Vlq(byte[] d, ref int p)
    {
        int v = 0;
        for (int i = 0; i < 4 && p < d.Length; i++)
        {
            int b = d[p++];
            v = (v << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }

    internal static uint BE32(byte[] d, int p) { return (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]); }
    internal static string Tag(byte[] d, int p) { return p + 4 <= d.Length ? Encoding.ASCII.GetString(d, p, 4) : ""; }

    /// <summary>
    /// AdLib 악기 28개 값 (BNK와 같은 순서: 모듈레이터 13 · 캐리어 13 · 파형 2 — KSL, MULT, FB, AR, SL, EG, DR, RR, TL, AM, VIB, KSR, CON).
    /// words = 값마다 2바이트 (.SND · .INS), 아니면 1바이트 (MDI 메타).
    /// </summary>
    public static AdlibInstrument InstrumentFromParams(byte[] d, int at, bool words, string name)
    {
        int k = 0;
        int Next() { int v = words ? d[at + 2 * k] | (d[at + 2 * k + 1] << 8) : d[at + k]; k++; return v; }
        var ins = new AdlibInstrument { Name = name };
        foreach (var op in new[] { ins.Modulator, ins.Carrier })
        {
            op.Ksl = Next() & 3; op.Multiple = Next() & 15; op.Feedback = Next() & 7; op.Attack = Next() & 15; op.Sustain = Next() & 15;
            op.Eg = Next() & 1; op.Decay = Next() & 15; op.ReleaseRate = Next() & 15; op.TotalLevel = Next() & 63; op.Am = Next() & 1;
            op.Vib = Next() & 1; op.Ksr = Next() & 1; op.Con = Next() & 1;
        }
        ins.Modulator.WaveSelect = Next() & 3;
        ins.Carrier.WaveSelect = Next() & 3;
        ins.Carrier.Con = ins.Modulator.Con;
        return ins;
    }
}

/// <summary>
/// Miles Design AIL XMIDI (.xmi). IFF: "FORM" XDIR(INFO = 곡 수) + "CAT " XMID 안에 곡마다 "FORM" XMID (TIMB · RBRN · EVNT), 곡 하나면 "FORM" XMID만.
/// EVNT: 0x80 미만 바이트들 = 기다림(합), 노트 온 = 음 · 세기 · 길이(가변 길이) — 노트 끄기는 길이로 만든다, 러닝 스테이터스 없음,
/// 시간은 120Hz 고정 (템포 메타는 쓰지 않음, Exult · AIL과 같은 관례). AIL 전용 컨트롤러(110~120: 채널 잠금 · 뱅크 · 반복 · 콜백)는 뺀다.
/// </summary>
public static class XmiFormat
{
    public static bool IsXmi(byte[] d) { return d.Length >= 12 && PcMusicFormats.Tag(d, 0) == "FORM" && (PcMusicFormats.Tag(d, 8) == "XDIR" || PcMusicFormats.Tag(d, 8) == "XMID"); }

    /// <summary>곡마다 EVNT 위치 (시작, 길이).</summary>
    public static List<(int at, int len)> Sequences(byte[] d)
    {
        var l = new List<(int, int)>();
        Walk(d, 0, d.Length, l);
        return l;
    }

    private static void Walk(byte[] d, int p, int end, List<(int, int)> l)
    {
        while (p + 8 <= end)
        {
            string id = PcMusicFormats.Tag(d, p);
            int size = (int)Math.Min(PcMusicFormats.BE32(d, p + 4), (uint)(end - p - 8));
            int body = p + 8;
            if (id == "FORM" || id == "CAT ")
            {
                // 형식 이름(4) 뒤가 하위 덩어리
                Walk(d, body + 4, body + size, l);
            }
            else if (id == "EVNT") l.Add((body, size));
            p = body + size + (size & 1);
        }
    }

    public static int SequenceCount(byte[] d) { return Sequences(d).Count; }

    public static MidiSong Read(byte[] d, int sequence = 0)
    {
        if (!IsXmi(d)) throw new InvalidDataException("XMI 파일이 아닙니다 (FORM XDIR / XMID가 없음).");
        var seqs = Sequences(d);
        if (seqs.Count == 0) throw new InvalidDataException("XMI 안에 곡(EVNT)이 없습니다.");
        var (at, len) = seqs[Math.Max(0, Math.Min(sequence, seqs.Count - 1))];
        var s = new MidiSong { Division = 60, FormatName = "XMI (Miles AIL)" + (seqs.Count > 1 ? " · 곡 " + (sequence + 1) + "/" + seqs.Count : "") };
        int p = at, end = at + len, tick = 0;
        while (p < end)
        {
            int b = d[p];
            if (b < 0x80) { tick += b; p++; continue; }   // 기다림 바이트들을 더한다
            p++;
            int kind = b & 0xF0, ch = b & 0x0F;
            if (b == 0xFF)
            {
                if (p >= end) break;
                int type = d[p++];
                int mlen = PcMusicFormats.Vlq(d, ref p);
                if (type == 0x2F) break;
                if (type == 0x03 && mlen > 0 && s.TrackNames.Count == 0) s.TrackNames.Add(Encoding.GetEncoding(28591).GetString(d, p, Math.Min(mlen, end - p)).Trim());
                p += mlen;
                continue;
            }
            if (b == 0xF0 || b == 0xF7)
            {
                int slen = PcMusicFormats.Vlq(d, ref p);
                if (b == 0xF0 && p + slen <= end)
                {
                    var sx = new byte[slen + 1];
                    sx[0] = 0xF0;
                    Array.Copy(d, p, sx, 1, slen);
                    if (sx[sx.Length - 1] != 0xF7) { Array.Resize(ref sx, sx.Length + 1); sx[sx.Length - 1] = 0xF7; }
                    s.Events.Add(new MidiEvent { Tick = tick, Status = 0xF0, SysEx = sx });
                }
                p += slen;
                continue;
            }
            switch (kind)
            {
                case 0x90:
                    {
                        if (p + 2 > end) { p = end; break; }
                        int note = d[p++], vel = d[p++];
                        int dur = PcMusicFormats.Vlq(d, ref p);
                        if (vel == 0) { s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)(0x80 | ch), Data1 = (byte)note }); break; }
                        s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)b, Data1 = (byte)note, Data2 = (byte)vel });
                        s.Events.Add(new MidiEvent { Tick = tick + Math.Max(1, dur), Status = (byte)(0x80 | ch), Data1 = (byte)note });
                        break;
                    }
                case 0x80: case 0xA0: case 0xE0:
                    if (p + 2 > end) { p = end; break; }
                    s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)b, Data1 = d[p], Data2 = d[p + 1] });
                    p += 2;
                    break;
                case 0xB0:
                    if (p + 2 > end) { p = end; break; }
                    if (d[p] < 110 || d[p] > 120) s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)b, Data1 = d[p], Data2 = d[p + 1] });
                    p += 2;
                    break;
                case 0xC0: case 0xD0:
                    if (p + 1 > end) { p = end; break; }
                    s.Events.Add(new MidiEvent { Tick = tick, Status = (byte)b, Data1 = d[p] });
                    p += 1;
                    break;
                default: p = end; break;   // 알 수 없음
            }
        }
        s.Finish();
        return s;
    }
}

/// <summary>
/// Ad Lib MIDI Driver 곡 (.mdi): 표준 MIDI (형식 0) + 순서기 전용 메타 FF 7F "00 00 3F"(Ad Lib) + 종류 2바이트:
/// 1 = 악기 (채널 1바이트 + AdLib 값 28개, BNK 순서), 2 = 멜로디(0) / 타악기(1) 모드, 3 = 피치벤드 범위.
/// 악기를 곡 안의 FM 뱅크(<see cref="MidiSong.CustomBank"/>)로 모으고 그 자리에 프로그램 바꾸기를 넣는다.
/// 타악기 모드면 채널 11~15 (BD · SD · TOM · CYM · HH) = GM 드럼 채널 10의 36 · 38 · 45 · 49 · 42로.
/// </summary>
public static class MdiFormat
{
    private static readonly int[] DrumNote = { 36, 38, 45, 49, 42 };

    public static bool IsMdi(byte[] d)
    {
        if (d.Length < 14 || PcMusicFormats.Tag(d, 0) != "MThd") return false;
        for (int i = 14; i + 6 < d.Length && i < 4096; i++)
            if (d[i] == 0xFF && d[i + 1] == 0x7F && d[i + 3] == 0 && d[i + 4] == 0 && d[i + 5] == 0x3F) return true;
        return false;
    }

    public static MidiSong Read(byte[] d)
    {
        var s = MidiFileFormat.Read(d);
        s.FormatName = "MDI (Ad Lib MIDI)";
        var bank = new GmBank();
        var patchOf = new Dictionary<string, int>();
        var extra = new List<MidiEvent>();
        bool perc = false;
        int[] lastPatch = Enumerable.Repeat(-1, 16).ToArray();
        // 트랙마다 메타를 시간과 함께 찾는다
        int p = 8 + (int)PcMusicFormats.BE32(d, 4);
        while (p + 8 <= d.Length)
        {
            string id = PcMusicFormats.Tag(d, p);
            int size = (int)Math.Min(PcMusicFormats.BE32(d, p + 4), (uint)(d.Length - p - 8));
            int q = p + 8, end = q + size, tick = 0, status = 0;
            if (id == "MTrk")
            {
                while (q < end)
                {
                    tick += PcMusicFormats.Vlq(d, ref q);
                    if (q >= end) break;
                    int b = d[q];
                    if (b >= 0x80) { status = b; q++; }
                    if (status == 0xFF)
                    {
                        int type = d[q++];
                        int len = PcMusicFormats.Vlq(d, ref q);
                        if (type == 0x7F && len >= 6 && d[q] == 0 && d[q + 1] == 0 && d[q + 2] == 0x3F)
                        {
                            int kind = (d[q + 3] << 8) | d[q + 4];
                            if (kind == 1 && len >= 6 + 28)
                            {
                                int ch = d[q + 5] & 15;
                                string key = BitConverter.ToString(d, q + 6, 28);
                                if (!patchOf.TryGetValue(key, out int idx))
                                {
                                    idx = patchOf.Count & 127;
                                    patchOf[key] = idx;
                                    var ins = PcMusicFormats.InstrumentFromParams(d, q + 6, false, "MDI" + idx);
                                    bank.Melodic[idx] = new GmPatch { Name = "AdLib #" + idx, Voice1 = ins };
                                }
                                lastPatch[ch] = idx;
                                extra.Add(new MidiEvent { Tick = tick, Status = (byte)(0xC0 | ch), Data1 = (byte)idx });
                            }
                            else if (kind == 2 && len >= 6) perc = d[q + 5] != 0;
                        }
                        q += len;
                        status = 0;
                    }
                    else if (status == 0xF0 || status == 0xF7) { int len = PcMusicFormats.Vlq(d, ref q); q += len; status = 0; }
                    else
                    {
                        int k = status & 0xF0;
                        q += k == 0xC0 || k == 0xD0 ? 1 : 2;
                    }
                }
            }
            p = end;
        }
        s.Events.AddRange(extra);
        if (perc)
        {
            // 타악기 채널 → GM 드럼 (그 채널의 악기를 드럼 소리로)
            for (int i = 0; i < s.Events.Count; i++)
            {
                var e = s.Events[i];
                int ch = e.Channel;
                if (e.IsTempo || e.IsSysEx || ch < 11) continue;
                int n = DrumNote[ch - 11];
                if (e.Kind == 0x90 || e.Kind == 0x80) { e.Status = (byte)(e.Kind | 9); e.Data1 = (byte)n; s.Events[i] = e; }
                else if (e.Kind == 0xC0)
                {
                    var src = bank.Melodic[e.Data1];
                    if (src != null) bank.Drums[n] = src;
                    e.Status = 0xB0 | 9; e.Data1 = 0x7E; e.Data2 = 0; s.Events[i] = e;   // 프로그램 대신 아무 일 없는 컨트롤
                }
            }
        }
        s.CustomBank = bank;
        s.Finish();
        return s;
    }
}

/// <summary>
/// Miles AIL "Global Timbre Library" (.ad · .opl): 머리 (프로그램 1, 뱅크 1, 위치 4) 반복, 프로그램 = 뱅크 = 0xFF로 끝.
/// 음색: 길이 2 · 음 옮김 1 · 모듈레이터 (20 · 40 · 60 · 80 · E0) · C0 · 캐리어 (20 · 40 · 60 · 80 · E0). 뱅크 0 = 멜로디, 127 = 드럼 (프로그램 = 음 번호).
/// 4-op(OPL3) 음색은 앞 2-op만.
/// </summary>
public static class AilTimbres
{
    public static readonly string[] Extensions = { ".ad", ".opl" };

    public static GmBank Read(byte[] d, string source = "")
    {
        var b = new GmBank();
        int count = 0;
        for (int p = 0; p + 6 <= d.Length; p += 6)
        {
            int prog = d[p], bank = d[p + 1];
            if (prog == 0xFF && bank == 0xFF) break;
            int at = (int)BitConverter.ToUInt32(d, p + 2);
            if (at < 0 || at + 14 > d.Length) continue;
            int len = d[at] | (d[at + 1] << 8);
            if (len < 14) continue;
            var ins = new AdlibInstrument
            {
                Name = (bank == 127 ? "D" : "P") + prog,
                Modulator = OplOperator.FromRegisters(d, at + 3),
                Carrier = OplOperator.FromRegisters(d, at + 9),
            };
            int c0 = d[at + 8];
            ins.Modulator.Feedback = (c0 >> 1) & 7;
            ins.Modulator.Con = (c0 & 1) != 0 ? 0 : 1;
            ins.Carrier.Con = ins.Modulator.Con;
            var patch = new GmPatch { Name = "AIL " + (bank == 127 ? "drum " : "") + prog, Voice1 = ins, Offset1 = (sbyte)d[at + 2] };
            if (bank == 127) { if (prog < 128) b.Drums[prog] = patch; }
            else if (bank == 0 && prog < 128) b.Melodic[prog] = patch;
            else continue;
            count++;
        }
        if (count == 0) throw new InvalidDataException("AIL 음색 묶음이 아닙니다.");
        b.SetSource(source + " (" + count + "개)");
        return b;
    }
}

/// <summary>Ad Lib MUS (.mus): IMS와 같은 머리말(70바이트) · 이벤트, 악기 = 음색 뱅크(.snd)의 번호 (이름표 없음 → "INS" + 번호).</summary>
public static class AdlibMus
{
    public static bool IsAdlibMus(byte[] d)
    {
        if (d.Length < ImsFormat.HeaderSize + 1 || d[0] != 1 || d[1] != 0) return false;
        uint dataSize = BitConverter.ToUInt32(d, 42);
        return dataSize > 0 && dataSize <= d.Length - ImsFormat.HeaderSize && d[36] > 0;
    }
}

/// <summary>.mus를 곡 형식으로 (읽기만): 머리말을 보고 IMS 읽기로.</summary>
public sealed class AdlibMusFormat : ISongFormat
{
    public string Id { get { return "mus"; } }
    public string DisplayName { get { return "Ad Lib MUS"; } }
    public string[] Extensions { get { return new[] { ".mus" }; } }
    public bool CanRead { get { return true; } }
    public bool CanWrite { get { return false; } }

    public Song Read(Stream stream)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var d = ms.ToArray();
        if (!AdlibMus.IsAdlibMus(d)) throw new InvalidDataException("Ad Lib MUS 파일이 아닙니다 (게임마다 다른 .MUS 형식일 수 있음).");
        return new ImsFormat().Read(new MemoryStream(d));
    }

    public void Write(Song song, Stream stream) { throw new NotSupportedException(); }
}

/// <summary>
/// Ad Lib 음색 뱅크 (.snd, MUS용): 1 · 0 · 개수(2) · 데이터 위치(2), [이름 9바이트 × 개수], 데이터 위치부터 음색마다 값 28개 × 2바이트.
/// 이름표가 없는 판도 있다 (데이터 위치 = 6). MUS는 번호로 악기를 고르므로 이름은 "INS" + 번호로 붙인다.
/// </summary>
public sealed class SndBankFormat : IBankFormat
{
    public string Id { get { return "snd"; } }
    public string DisplayName { get { return "Ad Lib timbre bank"; } }
    public string[] Extensions { get { return new[] { ".snd" }; } }
    public bool CanWrite { get { return false; } }

    public static bool IsSnd(byte[] d)
    {
        if (d.Length < 6 || d[0] != 1 || d[1] != 0) return false;
        int n = d[2] | (d[3] << 8), at = d[4] | (d[5] << 8);
        return n > 0 && at >= 6 && at + n * 56 <= d.Length;
    }

    public InstrumentBank Read(Stream stream)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var d = ms.ToArray();
        if (!IsSnd(d)) throw new InvalidDataException("Ad Lib 음색 뱅크(.snd)가 아닙니다.");
        int n = d[2] | (d[3] << 8), at = d[4] | (d[5] << 8);
        var bank = new InstrumentBank();
        for (int i = 0; i < n; i++)
        {
            var ins = PcMusicFormats.InstrumentFromParams(d, at + i * 56, true, "INS" + i);
            ins.IsUsed = true;
            bank.Instruments.Add(ins);
        }
        return bank;
    }

    public void Write(InstrumentBank bank, Stream stream) { throw new NotSupportedException(); }
}
