using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// NSFE (NSF 확장, "NSFE" + 청크): 같은 6502 코드 · 데이터에 곡 이름 · 길이 · 페이드 · 재생 순서가 붙은 것.
/// 읽으면 NSF 머리말(0x80)을 만들어 <see cref="Nsf.Render"/>로 돌린다 (참고: modizer libGME <c>Nsfe_Emu.cpp</c>, nesdev 위키 "NSFe").
/// <para>청크 = 길이(4) · 이름(4) · 내용. INFO (load · init · play 주소, PAL 깃발, 확장 음원, 곡 수, 첫 곡(0부터)) → DATA (코드) → NEND.
/// 선택: BANK (뱅크 8개), RATE (NTSC 재생 간격 µs), auth (게임 · 작곡 · 저작권 · 덤프, 0으로 나눈 글), tlbl (곡 이름들), time · fade (곡마다 ms, int32, &lt;0 = 기본),
/// plst (재생 순서: 곡 번호 바이트들). 대문자로 시작하는 모르는 청크는 꼭 알아야 하는 것이라 읽지 않는다 (명세), 소문자는 건너뜀.</para>
/// </summary>
public static class Nsfe
{
    public static readonly string[] Extensions = { ".nsfe" };

    public sealed class Info
    {
        /// <summary>NSF 머리말을 붙인 바이트 (<see cref="Nsf.Render"/>에 그대로).</summary>
        public byte[] Nsf;
        public int Songs, FirstSong, Expansion;
        public string Game = "", Artist = "", Copyright = "", Ripper = "";
        public readonly List<string> Labels = new List<string>();
        public readonly List<int> TimesMs = new List<int>(), FadesMs = new List<int>();
        public readonly List<int> Playlist = new List<int>();
    }

    public static Info Read(byte[] d)
    {
        if (d.Length < 4 || d[0] != 'N' || d[1] != 'S' || d[2] != 'F' || d[3] != 'E') throw new InvalidDataException("NSFE 파일이 아닙니다 (머리말 NSFE가 없음).");
        var info = new Info();
        byte[] head = new byte[0x80];
        Encoding.ASCII.GetBytes("NESM\x1A").CopyTo(head, 0);
        head[5] = 1;
        head[0x6E] = 0x1A; head[0x6F] = 0x41;   // NTSC 16666µs
        head[0x78] = 0x20; head[0x79] = 0x4E;   // PAL 20000µs
        byte[] data = null;
        bool gotInfo = false;
        int p = 4;
        while (p + 8 <= d.Length)
        {
            int size = (int)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            string id = Encoding.ASCII.GetString(d, p + 4, 4);
            p += 8;
            if (size < 0 || p + size > d.Length) throw new InvalidDataException("NSFE 청크가 파일 밖입니다: " + id);
            if (id == "NEND") break;
            switch (id)
            {
                case "INFO":
                    if (size < 8) throw new InvalidDataException("NSFE INFO 청크가 너무 짧습니다.");
                    Array.Copy(d, p, head, 8, 6);                        // load · init · play
                    head[0x7A] = d[p + 6];                               // PAL / NTSC
                    head[0x7B] = d[p + 7];                               // 확장 음원
                    info.Expansion = d[p + 7];
                    info.Songs = size > 8 ? Math.Max(1, (int)d[p + 8]) : 1;
                    info.FirstSong = size > 9 ? d[p + 9] : 0;
                    head[6] = (byte)info.Songs;
                    head[7] = (byte)Math.Min(info.Songs, info.FirstSong + 1);
                    gotInfo = true;
                    break;
                case "DATA":
                    data = new byte[size];
                    Array.Copy(d, p, data, 0, size);
                    break;
                case "BANK":
                    Array.Copy(d, p, head, 0x70, Math.Min(8, size));
                    break;
                case "RATE":
                    if (size >= 2) { head[0x6E] = d[p]; head[0x6F] = d[p + 1]; }
                    if (size >= 4) { head[0x78] = d[p + 2]; head[0x79] = d[p + 3]; }
                    break;
                case "auth":
                {
                    var s = Strings(d, p, size);
                    if (s.Count > 0) info.Game = s[0];
                    if (s.Count > 1) info.Artist = s[1];
                    if (s.Count > 2) info.Copyright = s[2];
                    if (s.Count > 3) info.Ripper = s[3];
                    break;
                }
                case "tlbl": info.Labels.AddRange(Strings(d, p, size)); break;
                case "time": for (int i = 0; i + 4 <= size; i += 4) info.TimesMs.Add(BitConverter.ToInt32(d, p + i)); break;
                case "fade": for (int i = 0; i + 4 <= size; i += 4) info.FadesMs.Add(BitConverter.ToInt32(d, p + i)); break;
                case "plst": for (int i = 0; i < size; i++) info.Playlist.Add(d[p + i]); break;
                default:
                    if (char.IsUpper(id[0])) throw new InvalidDataException("NSFE 필수 청크를 모릅니다: " + id);
                    break;   // 소문자 = 건너뛰어도 되는 청크
            }
            p += size;
        }
        if (!gotInfo || data == null) throw new InvalidDataException("NSFE에 INFO · DATA 청크가 없습니다.");
        info.Nsf = new byte[0x80 + data.Length];
        head.CopyTo(info.Nsf, 0);
        data.CopyTo(info.Nsf, 0x80);
        return info;
    }

    /// <summary>0으로 나뉜 글들 (UTF-8, 깨지면 Shift-JIS).</summary>
    private static List<string> Strings(byte[] d, int at, int size)
    {
        var l = new List<string>();
        int s = at, end = at + size;
        for (int i = at; i <= end; i++)
        {
            if (i == end || d[i] == 0)
            {
                if (i > s || i < end) l.Add(Decode(d, s, i - s));
                s = i + 1;
            }
        }
        return l;
    }

    private static string Decode(byte[] d, int at, int n)
    {
        try { return new UTF8Encoding(false, true).GetString(d, at, n).Trim(); }
        catch (DecoderFallbackException) { return KoreanText.ShiftJis.GetString(d, at, n).Trim(); }
    }

    // ------------------------------------------------------------ 곡 목록 · 열기

    /// <summary>곡 목록: plst가 있으면 그 순서, 없으면 0 ~ 곡 수 − 1. 이름 · 길이 · 페이드는 곡 번호로 찾는다.</summary>
    public static List<Nsf.Track> Tracks(Info info, string fallbackName)
    {
        var order = info.Playlist.Count > 0 ? info.Playlist.Where(s => s < info.Songs).ToList() : Enumerable.Range(0, info.Songs).ToList();
        if (order.Count == 0) order.Add(0);
        string name = info.Game.Length > 0 ? info.Game : fallbackName;
        var list = new List<Nsf.Track>();
        foreach (int s in order)
        {
            var t = new Nsf.Track { Song = s };
            t.Title = s < info.Labels.Count && info.Labels[s].Length > 0 ? info.Labels[s] : name + " #" + (s + 1);
            if (s < info.TimesMs.Count && info.TimesMs[s] > 0) t.Seconds = info.TimesMs[s] / 1000.0;
            if (s < info.FadesMs.Count && info.FadesMs[s] >= 0) t.Fade = info.FadesMs[s] / 1000.0;
            list.Add(t);
        }
        return list;
    }

    public static List<Nsf.Track> Tracks(string path)
    {
        try { return Tracks(Read(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path)); }
        catch (Exception) { return new List<Nsf.Track> { new Nsf.Track() }; }
    }

    /// <summary>"파일.nsfe" 또는 "파일.nsfe::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var info = Read(File.ReadAllBytes(path));
        var tracks = Tracks(info, Path.GetFileNameWithoutExtension(path));
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        // 페이드가 곡 길이에 들어가도록 (NSFE time = 페이드 앞까지)
        var v = Nsf.Render(info.Nsf, t.Song, t.Seconds + t.Fade, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title;
        v.GameEn = info.Game;
        v.AuthorEn = info.Artist;
        v.Ripper = info.Ripper;
        v.ReleaseDate = info.Copyright;
        v.SourceFormat = "NSFE · 곡 " + (t.Song + 1) + " (" + (track + 1) + "/" + tracks.Count + ")" + (info.Expansion != 0 ? " · " + Nsf.ExpansionName(info.Expansion) : "");
        return v;
    }
}
