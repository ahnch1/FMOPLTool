using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdlibComposer.Formats;

/// <summary>
/// hoot 아카이브 zip ↔ hoot 목록(xml) 항목: zip 이름 = 항목의 &lt;romlist archive="…"&gt;.
/// zip을 풀 때 (<see cref="ZipFolder.Extract"/>) 그 zip을 쓰는 기종 흉내 항목(지금은 PC-98 DOS = pc98dos)마다
/// 항목 XML 하나를 <see cref="Extension"/> 파일로 푼 폴더에 써 둔다 → 파일 목록 · Jukebox에서 곡이 여럿 든 파일처럼 보인다.
/// <para>목록 찾기: zip이 있는 폴더부터 위로 세 단계까지, 그 폴더나 그 아래 xml_new · xml에 *.xml이 있는 곳 (hoot 배포 모양: hoot.exe 옆 xml\, 기종 폴더 pc98\).
/// 처음 한 번 목록 전체(수십 MB)를 훑어 archive → 항목 색인을 만든다 (기종 흉내 항목만 남김).</para>
/// </summary>
public static class HootArchive
{
    public const string Extension = ".hoot";
    private const string Mark = ".fmopl_hoot";

    /// <summary>흉내 낼 수 있는 hoot 드라이버 · 형식 (driver "pc98dos" type …). 옵션 midiout이 있는 항목(MIDI 판)은 색인에서 뺀다.</summary>
    public static bool Playable(string driver, string type)
    {
        if (!string.Equals(driver, "pc98dos", StringComparison.OrdinalIgnoreCase)) return false;
        switch ((type ?? "").ToLowerInvariant())
        {
            case "opn": case "opna": case "86": case "beep": return true;
        }
        return false;
    }

    private static readonly object gate = new object();
    private static readonly Dictionary<string, Dictionary<string, List<string>>> indexes = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>zip 옆 (위로 세 단계까지)의 hoot 목록 폴더. 없으면 null.</summary>
    public static string FindListDir(string zipPath)
    {
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)); } catch (Exception) { return null; }
        for (int up = 0; up < 4 && !string.IsNullOrEmpty(dir); up++)
        {
            foreach (var name in new[] { "xml_new", "xml" })
            {
                string d = Path.Combine(dir, name);
                try { if (Directory.Exists(d) && Directory.EnumerateFiles(d, "*.xml").Any()) return d; } catch (Exception) { }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static readonly Regex ArchiveRx = new Regex(@"<romlist[^>]*\barchive=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex MidiOutRx = new Regex(@"<option\s+name=""midiout""", RegexOptions.Compiled);
    private static readonly Regex DriverRx = new Regex(@"<driver(?:\s+type=""([^""]*)"")?\s*>\s*([^<\s]+)\s*</driver>", RegexOptions.Compiled);

    /// <summary>목록 폴더의 색인: archive(소문자) → 흉내 낼 수 있는 항목 XML들 (파일 순서).</summary>
    public static Dictionary<string, List<string>> Index(string listDir)
    {
        lock (gate)
        {
            if (indexes.TryGetValue(listDir, out var have)) return have;
            var idx = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(listDir, "*.xml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string t;
                try { t = HootLists.ReadText(f); } catch (Exception) { continue; }
                foreach (var (at, len) in HootLists.GameBlocksAt(t))
                {
                    string g = t.Substring(at, len);
                    if (g.IndexOf("pc98dos", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var dm = DriverRx.Match(g);
                    if (!dm.Success || !Playable(dm.Groups[2].Value, dm.Groups[1].Value)) continue;
                    if (MidiOutRx.IsMatch(g)) continue;   // MIDI로 내는 판 (GS · MT-32 · Vermouth …): MPU-PC98 흉내 전
                    var am = ArchiveRx.Match(g);
                    if (!am.Success) continue;
                    string a = am.Groups[1].Value.Trim();
                    if (a.Length == 0) continue;
                    if (!idx.TryGetValue(a, out var l)) idx[a] = l = new List<string>();
                    l.Add(g);
                }
            }
            indexes[listDir] = idx;
            return idx;
        }
    }

    /// <summary>
    /// 푼 zip 폴더에 그 zip의 기종 흉내 항목을 <see cref="Extension"/> 파일로 쓴다 (한 번만: 표시 파일).
    /// 목록을 못 찾으면 아무것도 안 하고 표시도 남기지 않는다 (다음에 다시 본다). 쓴 파일 수.
    /// </summary>
    public static int WriteEntries(string zipPath, string dir, string listDir = null)
    {
        if (File.Exists(Path.Combine(dir, Mark))) return 0;
        listDir = listDir ?? FindListDir(zipPath);
        if (listDir == null) return 0;
        var idx = Index(listDir);
        int n = 0;
        if (idx.TryGetValue(Path.GetFileNameWithoutExtension(zipPath).Trim(), out var games))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                string name = EntryFileName(g, Path.GetFileNameWithoutExtension(zipPath));
                string file = name;
                for (int k = 2; !used.Add(file); k++) file = name + " (" + k + ")";
                File.WriteAllText(Path.Combine(dir, file + Extension), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + g + "\r\n", new UTF8Encoding(true));
                n++;
            }
        }
        File.WriteAllText(Path.Combine(dir, Mark), listDir);
        File.SetAttributes(Path.Combine(dir, Mark), FileAttributes.Hidden);
        return n;
    }

    /// <summary>항목 이름 → 파일 이름 ("[PC-9801] " 떼고, 쓸 수 없는 글자 → _, 80자까지).</summary>
    private static string EntryFileName(string game, string fallback)
    {
        string name = Regex.Match(game, @"<name>(.*?)</name>", RegexOptions.Singleline).Groups[1].Value;
        name = Regex.Replace(System.Net.WebUtility.HtmlDecode(name), @"\s+", " ").Trim();
        name = Regex.Replace(name, @"^\[[^\]]*\]\s*", "");
        var bad = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 80) name = name.Substring(0, 80).Trim();
        return name.Length > 0 ? name : fallback;
    }

    // ------------------------------------------------------------ 항목 읽기

    /// <summary>항목 하나: 드라이버 · 형식 · 옵션 · rom 줄(종류 · offset · 글자) · 곡 목록(코드 · 제목).</summary>
    public sealed class Entry
    {
        public string Name = "", Driver = "", Type = "", Archive = "";
        public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Type, long Offset, string Text)> Roms = new List<(string, long, string)>();
        public readonly List<(int Code, string Title)> Titles = new List<(int, string)>();

        public int Option(string name, int fallback)
        {
            return Options.TryGetValue(name, out var v) && TryNumber(v, out long n) ? (int)n : fallback;
        }
    }

    /// <summary>"0x1e" · "30" · "-1" → 수.</summary>
    public static bool TryNumber(string s, out long v)
    {
        s = (s ?? "").Trim();
        bool neg = s.StartsWith("-");
        if (neg) s = s.Substring(1);
        bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v)
            : long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v);
        if (neg) v = -v;
        return ok;
    }

    /// <summary>&lt;game&gt; XML (또는 <see cref="Extension"/> 파일 내용) 읽기.</summary>
    public static Entry Parse(string xml)
    {
        var e = XElement.Parse(xml.Replace("&nbsp;", "&#160;"));
        if (e.Name != "game") e = e.Descendants("game").First();
        var r = new Entry { Name = Clean((string)e.Element("name")) };
        var d = e.Element("driver");
        if (d != null) { r.Driver = Clean(d.Value); r.Type = ((string)d.Attribute("type") ?? "").Trim(); }
        foreach (var o in e.Descendants("option"))
        {
            string n = (string)o.Attribute("name");
            if (n != null) r.Options[n] = (string)o.Attribute("value") ?? "";
        }
        var rl = e.Element("romlist");
        if (rl != null)
        {
            r.Archive = ((string)rl.Attribute("archive") ?? "").Trim();
            foreach (var rom in rl.Elements("rom"))
            {
                TryNumber((string)rom.Attribute("offset") ?? "-1", out long off);
                r.Roms.Add((((string)rom.Attribute("type") ?? "").Trim().ToLowerInvariant(), off, rom.Value.Trim()));
            }
        }
        var tl = e.Element("titlelist");
        if (tl != null)
            foreach (var t in tl.Elements("title"))
                if (TryNumber((string)t.Attribute("code"), out long code)) r.Titles.Add(((int)code, Clean(t.Value)));
        return r;
    }

    public static Entry Load(string path) { return Parse(File.ReadAllText(path)); }

    private static string Clean(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }
}
