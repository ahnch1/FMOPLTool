using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// NEZplug 확장 m3u: 곡 파일 옆 같은 이름의 .m3u, 한 줄에 곡 하나 —
/// <c>파일::종류,번호,제목,시간,루프,페이드</c> (종류 = KSS · NSF · HES …, 번호는 10진 또는 $16진, 시간 = 초 · 분:초 · 시:분:초).
/// 글자는 Shift-JIS, \, 는 쉼표 그대로.
/// </summary>
public static class NezM3u
{
    public sealed class Entry
    {
        public int Number;
        public string Title = "";
        /// <summary>곡 길이 (초). 없으면 0.</summary>
        public double Seconds;
        /// <summary>페이드 (초). 없으면 -1.</summary>
        public double Fade = -1;
    }

    /// <summary>songPath 옆 .m3u에서 kind(예: "NSF") 줄들. 파일이 없으면 빈 목록.</summary>
    public static List<Entry> Read(string songPath, string kind)
    {
        var list = new List<Entry>();
        string m3u = Path.ChangeExtension(songPath, ".m3u");
        if (!File.Exists(m3u)) return list;
        string tag = "::" + kind + ",";
        foreach (var raw in KoreanText.ShiftJis.GetString(File.ReadAllBytes(m3u)).Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;
            int at = line.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var f = SplitFields(line.Substring(at + tag.Length));
            if (f.Count == 0 || !TryNumber(f[0], out int n)) continue;
            var e = new Entry { Number = n };
            if (f.Count > 1) e.Title = f[1];
            if (f.Count > 2 && TryTime(f[2], out double sec) && sec > 0) e.Seconds = sec;
            if (f.Count > 4 && TryTime(f[4], out double fade)) e.Fade = fade;
            list.Add(e);
        }
        return list;
    }

    /// <summary>쉼표로 나누되 \, 는 글자 그대로.</summary>
    private static List<string> SplitFields(string s)
    {
        var l = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            if (s[i] == ',') { l.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(s[i]);
        }
        l.Add(sb.ToString().Trim());
        return l;
    }

    public static bool TryNumber(string s, out int v)
    {
        s = s.Trim();
        if (s.StartsWith("$")) return int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>"85" · "1:25" · "0:01:25" · "1:25.500" → 초.</summary>
    public static bool TryTime(string s, out double sec)
    {
        sec = 0;
        s = s.Trim().TrimEnd('-');
        if (s.Length == 0) return false;
        double total = 0;
        foreach (var part in s.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
            total = total * 60 + v;
        }
        sec = total;
        return true;
    }

    /// <summary>"파일::n"을 (파일, n)으로. 번호가 없으면 0.</summary>
    public static string SplitTrack(string path, out int track)
    {
        track = 0;
        int sep = path.LastIndexOf("::", StringComparison.Ordinal);
        if (sep > 1 && int.TryParse(path.Substring(sep + 2), out int n)) { track = n; return path.Substring(0, sep); }
        return path;
    }
}
