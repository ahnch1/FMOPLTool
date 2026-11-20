using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AdlibComposer.Formats;

/// <summary>
/// hoot 목록(xml)에 적힌 아카이브 zip 중 hoot 폴더에 없는 것을 찾아 CSV로 쓴다.
/// <para>zip이 들어갈 폴더 = 같은 드라이버(· 종류)의 zip이 이미 가장 많이 있는 폴더 (hoot.ini data_dir에 든 폴더만 배움).
/// 배울 zip이 없으면 기본 표 (<see cref="DefaultFolder"/>).</para>
/// <para>CSV 두 개 (UTF-8 BOM — 엑셀에서 일본어가 깨지지 않게):
/// <c>…_missing.csv</c> = 없는 zip 하나에 한 줄 (폴더 · 파일 · 드라이버 · 제작사 · 게임),
/// <c>…_folders.csv</c> = 폴더 구성 (폴더 · 드라이버/종류마다 zip 전체 · 있음 · 없음).</para>
/// </summary>
public static class HootMissing
{
    public sealed class Missing
    {
        public string Folder = "", Zip = "", Driver = "", Type = "";
        public readonly List<HootGame> Games = new List<HootGame>();
        public string RelPath { get { return (Folder.Length > 0 ? Folder + "/" : "") + Zip; } }
    }

    public sealed class FolderRow
    {
        public string Folder = "", Driver = "", Type = "";
        public int Total, Present, Missing;
    }

    public sealed class Result
    {
        public int Games, Zips, Present, NoArchive;
        public readonly List<Missing> Missing = new List<Missing>();
        public readonly List<FolderRow> Folders = new List<FolderRow>();
        public string MissingCsv = "", FoldersCsv = "";
    }

    /// <summary>
    /// 드라이버 → hoot 폴더 기본 표 (배울 zip이 없을 때). 없는 드라이버(아케이드 기판들)는 roms.
    /// </summary>
    public static string DefaultFolder(string driver)
    {
        switch ((driver ?? "").Trim().ToLowerInvariant())
        {
            case "pc98dos": case "pc98": case "pc98vx": return "pc98";
            case "pc9821dos": case "pc9821": return "pc9821";
            case "pcatdos": return "pc";
            case "pc88": return "pc88";
            case "pc88va": case "pc88vados": return "pc88va";
            case "x68k": return "x68k";
            case "x1": return "x1";
            case "msx": return "msx";
            case "fm7": case "fm77av": return "fm7";
            case "fmtowns": return "fmtowns";
            case "megadrive": case "mucomfm": return "smd";
            case "snes": return "snes";
            case "nes": return "nes";
            case "gb": return "gb";
            case "sc3000": return "sc3000";
            case "pico": return "pico";
            case "mv": return "mv";
            default: return "roms";
        }
    }

    /// <summary>hoot.ini [config] data_dir (';'로 나눈 폴더 이름). 없으면 빈 목록.</summary>
    public static List<string> DataDirs(string hootRoot)
    {
        var l = new List<string>();
        string ini = Path.Combine(hootRoot, "hoot.ini");
        if (!File.Exists(ini)) return l;
        try
        {
            foreach (var line in File.ReadAllLines(ini, Encoding.Latin1))
            {
                var m = Regex.Match(line, @"^\s*data_dir\s*=\s*(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                foreach (var p in m.Groups[1].Value.Split(';'))
                {
                    string d = p.Trim().Replace('\\', '/').Trim('/');
                    if (d.Length > 0 && !l.Contains(d, StringComparer.OrdinalIgnoreCase)) l.Add(d);
                }
            }
        }
        catch (Exception) { }
        return l;
    }

    /// <summary>hoot 폴더 아래 모든 *.zip: 이름(확장자 없음) → hoot 폴더 기준 상대 폴더 ('/' 구분, 바로 아래면 "").</summary>
    public static Dictionary<string, string> IndexZips(string hootRoot)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(hootRoot, "*.zip", SearchOption.AllDirectories))
            {
                string k = Path.GetFileNameWithoutExtension(f);
                if (d.ContainsKey(k)) continue;
                string rel = Path.GetRelativePath(hootRoot, Path.GetDirectoryName(f) ?? hootRoot).Replace('\\', '/');
                d[k] = rel == "." ? "" : rel;
            }
        }
        catch (Exception) { }
        return d;
    }

    /// <summary>항목의 zip 이름들 (archive를 ','로 나눔, 앞뒤 공백 뗌).</summary>
    public static IEnumerable<string> Archives(HootGame g)
    {
        return (g.Archive ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0);
    }

    /// <summary>
    /// 목록 폴더(listDir)의 항목들을 hoot 폴더(hootRoot, 보통 listDir의 위)의 zip과 맞춰 본다.
    /// games를 주면 다시 읽지 않는다. zips를 주면 (이름 → 상대 폴더) 다시 훑지 않는다.
    /// </summary>
    public static Result Check(string listDir, string hootRoot = null, IList<HootGame> games = null, Dictionary<string, string> zips = null)
    {
        hootRoot = hootRoot ?? Path.GetDirectoryName(listDir.TrimEnd('\\', '/')) ?? listDir;
        games = games ?? HootLists.ReadFolder(listDir);
        zips = zips ?? IndexZips(hootRoot);
        var dataDirs = new HashSet<string>(DataDirs(hootRoot), StringComparer.OrdinalIgnoreCase);
        var r = new Result { Games = games.Count };

        // zip 하나 → 그것을 쓰는 항목들 (목록 순서)
        var byZip = new Dictionary<string, List<HootGame>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var g in games)
        {
            bool any = false;
            foreach (var a in Archives(g))
            {
                any = true;
                if (!byZip.TryGetValue(a, out var l)) { byZip[a] = l = new List<HootGame>(); order.Add(a); }
                if (!l.Contains(g)) l.Add(g);
            }
            if (!any) r.NoArchive++;
        }
        r.Zips = order.Count;

        // 있는 zip에서 드라이버(· 종류) → 폴더 배우기 (data_dir를 알면 그 폴더만)
        var learnDt = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var learnD = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        void Vote(Dictionary<string, Dictionary<string, int>> t, string key, string folder)
        {
            if (!t.TryGetValue(key, out var c)) t[key] = c = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            c[folder] = c.TryGetValue(folder, out int n) ? n + 1 : 1;
        }
        foreach (var z in order)
        {
            if (!zips.TryGetValue(z, out var folder)) continue;
            if (dataDirs.Count > 0 && !dataDirs.Contains(folder)) continue;
            var g = byZip[z][0];
            string d = g.Driver.Trim().ToLowerInvariant(), t = g.DriverType.Trim().ToLowerInvariant();
            Vote(learnDt, d + "/" + t, folder);
            Vote(learnD, d, folder);
        }
        string Best(Dictionary<string, int> c) { return c.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key; }
        string Expected(HootGame g)
        {
            string d = g.Driver.Trim().ToLowerInvariant(), t = g.DriverType.Trim().ToLowerInvariant();
            if (learnDt.TryGetValue(d + "/" + t, out var c1)) return Best(c1);
            if (learnD.TryGetValue(d, out var c2)) return Best(c2);
            string def = DefaultFolder(d);
            // data_dir 표기 그대로 (X68k ↔ x68k)
            return dataDirs.FirstOrDefault(x => string.Equals(x, def, StringComparison.OrdinalIgnoreCase)) ?? def;
        }

        var folders = new Dictionary<string, FolderRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in order)
        {
            var g = byZip[z][0];
            bool have = zips.TryGetValue(z, out var at);
            string folder = have ? at : Expected(g);
            string key = folder + "|" + g.Driver.Trim().ToLowerInvariant() + "|" + g.DriverType.Trim().ToLowerInvariant();
            if (!folders.TryGetValue(key, out var fr))
                folders[key] = fr = new FolderRow { Folder = folder, Driver = g.Driver.Trim(), Type = g.DriverType.Trim() };
            fr.Total++;
            if (have) { fr.Present++; r.Present++; continue; }
            fr.Missing++;
            var m = new Missing { Folder = folder, Zip = z + ".zip", Driver = g.Driver.Trim(), Type = g.DriverType.Trim() };
            m.Games.AddRange(byZip[z]);
            r.Missing.Add(m);
        }
        r.Missing.Sort((a, b) =>
        {
            int c = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Zip, b.Zip, StringComparison.OrdinalIgnoreCase);
        });
        r.Folders.AddRange(folders.Values
            .OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Driver, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Type, StringComparer.OrdinalIgnoreCase));
        return r;
    }

    /// <summary>
    /// <see cref="Check"/> 결과를 CSV 두 개로: csvPath(없는 파일 목록)와 그 옆 "…_folders.csv"(폴더 구성).
    /// csvPath 이름이 "_missing"으로 끝나면 폴더 구성 파일은 그 자리를 "_folders"로 바꾼 이름.
    /// </summary>
    public static void WriteCsv(Result r, string csvPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(csvPath);
        string baseStem = stem.EndsWith("_missing", StringComparison.OrdinalIgnoreCase) ? stem.Substring(0, stem.Length - 8) : stem;
        string foldersPath = Path.Combine(dir, baseStem + "_folders.csv");

        var sb = new StringBuilder();
        Row(sb, "폴더", "파일", "경로", "driver", "type", "제작사", "게임 수", "기계", "게임", "곡 수");
        foreach (var m in r.Missing)
        {
            var first = m.Games[0];
            Row(sb, m.Folder, m.Zip, m.RelPath, m.Driver, m.Type,
                string.Join(" | ", m.Games.Select(g => Path.GetFileNameWithoutExtension(g.File)).Distinct(StringComparer.OrdinalIgnoreCase)),
                m.Games.Count.ToString(),
                string.Join(" | ", m.Games.Select(g => g.Platform.Length > 0 ? g.Platform : g.AliasType).Where(s => s.Length > 0).Distinct()),
                string.Join(" | ", m.Games.Select(g => g.Name)),
                m.Games.Sum(g => g.Titles.Count).ToString());
        }
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));

        sb.Clear();
        Row(sb, "폴더", "driver", "type", "zip 전체", "있음", "없음");
        foreach (var f in r.Folders) Row(sb, f.Folder, f.Driver, f.Type, f.Total.ToString(), f.Present.ToString(), f.Missing.ToString());
        foreach (var g in r.Folders.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase))
            Row(sb, g.Key, "(합계)", "", g.Sum(f => f.Total).ToString(), g.Sum(f => f.Present).ToString(), g.Sum(f => f.Missing).ToString());
        Row(sb, "(전체)", "", "", r.Zips.ToString(), r.Present.ToString(), r.Missing.Count.ToString());
        File.WriteAllText(foldersPath, sb.ToString(), new UTF8Encoding(true));

        r.MissingCsv = Path.GetFullPath(csvPath);
        r.FoldersCsv = foldersPath;
    }

    private static void Row(StringBuilder sb, params string[] cells)
    {
        sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
    }

    private static string Cell(string s)
    {
        s = s ?? "";
        if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0 && !Regex.IsMatch(s, @"^-?\d+$")) s = "'" + s;   // 엑셀 수식으로 읽히지 않게
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
