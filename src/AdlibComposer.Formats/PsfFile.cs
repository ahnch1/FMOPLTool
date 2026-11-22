using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// PSF 컨테이너 (xSF 공통: PSF · PSF2 · SSF · DSF · USF · GSF · 2SF · SNSF · QSF …, Neill Corlett 명세 · psflib과 같은 규칙).
/// <para>머리말 16바이트: "PSF" + 판 바이트 (01 PS1 · 02 PS2 · 11 SSF · 12 DSF · 21 USF · 22 GSF · 23 SNSF · 24 2SF · 41 QSF …),
/// 예약 영역 크기 (4) · 압축된 프로그램 크기 (4) · 그 CRC-32 (4). 다음 예약 영역, zlib 프로그램, 끝에 "[TAG]" + "이름=값" 줄들.</para>
/// <para>라이브러리: 태그 _lib = 먼저 읽을 파일 (같은 폴더, 그 파일의 _lib도 먼저 — 깊이 10까지), 다음 이 파일, 다음 _lib2 · _lib3 … (번호가 끊길 때까지).
/// 같은 이름 태그가 여러 줄이면 줄바꿈으로 잇는다. 길이 태그 length · fade = "분:초.소수" / "시:분:초" / "초".</para>
/// </summary>
public static class PsfFile
{
    public const int MaxDepth = 10;

    public sealed class Part
    {
        public string Path = "";
        public int Version;
        public byte[] Reserved = new byte[0];
        public byte[] Program = new byte[0];
        public readonly Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Tag(string name) { return Tags.TryGetValue(name, out var v) ? v : ""; }
    }

    /// <summary>파일 하나 (라이브러리는 따라가지 않음).</summary>
    public static Part Read(byte[] d, string path = "")
    {
        if (d.Length < 16 || d[0] != 'P' || d[1] != 'S' || d[2] != 'F') throw new InvalidDataException("PSF 파일이 아닙니다 (머리말 PSF가 없음).");
        var p = new Part { Path = path ?? "", Version = d[3] };
        long reserved = U32(d, 4), compressed = U32(d, 8);
        if (16 + reserved + compressed > d.Length) throw new InvalidDataException("PSF 크기가 파일보다 큽니다.");
        p.Reserved = new byte[reserved];
        Array.Copy(d, 16, p.Reserved, 0, reserved);
        if (compressed > 0)
        {
            using var ms = new MemoryStream(d, (int)(16 + reserved), (int)compressed);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            var o = new MemoryStream();
            z.CopyTo(o);
            p.Program = o.ToArray();
        }
        long tagAt = 16 + reserved + compressed;
        if (tagAt + 5 <= d.Length && Encoding.ASCII.GetString(d, (int)tagAt, 5) == "[TAG]")
            ParseTags(Decode(d, (int)tagAt + 5, (int)(d.Length - tagAt - 5)), p.Tags);
        return p;
    }

    /// <summary>태그 글 (UTF-8 표시 "utf8=1"이 없어도 UTF-8로 읽고, 깨지면 Shift-JIS).</summary>
    private static string Decode(byte[] d, int at, int n)
    {
        try { return new UTF8Encoding(false, true).GetString(d, at, n); }
        catch (DecoderFallbackException) { return KoreanText.ShiftJis.GetString(d, at, n); }
    }

    public static void ParseTags(string text, Dictionary<string, string> tags)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
            if (name.Length == 0) continue;
            tags[name] = tags.TryGetValue(name, out var old) ? old + "\n" + value : value;
        }
    }

    /// <summary>
    /// 라이브러리까지 읽는 차례 (psflib과 같이): _lib의 차례 → 이 파일 → _lib2 · _lib3 … 의 차례. 첫째 = 가장 먼저 올릴 것, 마지막에 본 파일의 태그가 들어 있는 것은 <c>Main</c>.
    /// version이 0보다 크면 모든 파일의 판 바이트가 같아야 한다.
    /// </summary>
    public static List<Part> LoadChain(string path, int version, out Part main)
    {
        var list = new List<Part>();
        main = Load(path, version, list, 0);
        return list;
    }

    private static Part Load(string path, int version, List<Part> list, int depth)
    {
        if (depth >= MaxDepth) throw new InvalidDataException("PSF 라이브러리가 너무 깊습니다 (" + MaxDepth + "단계).");
        if (!File.Exists(path)) throw new FileNotFoundException("PSF 라이브러리를 찾지 못했습니다: " + System.IO.Path.GetFileName(path), path);
        var p = Read(File.ReadAllBytes(path), path);
        if (version > 0 && p.Version != version) throw new InvalidDataException("PSF 판이 다릅니다: " + System.IO.Path.GetFileName(path) + " = 0x" + p.Version.ToString("X2") + " (0x" + version.ToString("X2") + "이어야 함).");
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        string lib = p.Tag("_lib");
        if (lib.Length > 0) Load(System.IO.Path.Combine(dir, lib), version, list, depth + 1);
        list.Add(p);
        for (int n = 2; ; n++)
        {
            string ln = p.Tag("_lib" + n);
            if (ln.Length == 0) break;
            Load(System.IO.Path.Combine(dir, ln), version, list, depth + 1);
        }
        return p;
    }

    /// <summary>"3:25.500" · "1:02:03" · "95" · "95.5" → 초 (못 읽으면 0).</summary>
    public static double ParseLength(string s)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        if (s.Length == 0) return 0;
        double sec = 0;
        foreach (var part in s.Split(':'))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            sec = sec * 60 + v;
        }
        return sec;
    }

    private static long U32(byte[] d, int o) { return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)); }
}
