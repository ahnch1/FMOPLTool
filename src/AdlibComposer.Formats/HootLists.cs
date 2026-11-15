using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdlibComposer.Formats;

/// <summary>hoot 게임 목록의 &lt;game&gt; 하나 (원래 글자 그대로 + 읽은 값).</summary>
public sealed class HootGame
{
    /// <summary>목록 묶음 이름 (폴더 이름: xml · xml2 · xml3 …) · 우선순위 (클수록 새 묶음).</summary>
    public string Set = "";
    public int SetRank;
    /// <summary>제작사 파일 이름 (예: "13cm.xml") · 파일 안 순서.</summary>
    public string File = "";
    public int Index;
    /// <summary>&lt;game&gt; … &lt;/game&gt; 원래 글자.</summary>
    public string Raw = "";
    /// <summary>원래 바이트 (Shift_JIS 파일은 그대로 잘라 낸 것: 외자 · 확장 문자도 바뀌지 않게). 합친 파일에 이것을 쓴다.</summary>
    public byte[] RawBytes = Array.Empty<byte>();
    public string Date = "";

    public string Name = "", Driver = "", DriverType = "", Alias = "", AliasType = "", Archive = "";
    public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Code, string Text)> Titles = new List<(string, string)>();
    public readonly List<(string Type, string Text)> Roms = new List<(string, string)>();

    // 이름 풀이: "[기계] 영문 제목 (일본어 제목) (칩)"
    public string Platform = "", Base = "", Japanese = "", ChipLabel = "", Chip = "", Credit = "";
    /// <summary>제목 낱말 (영문 소문자, 칩 · 일본어 · 만든 이 괄호는 뺌): 이름 닮음 비교.</summary>
    public HashSet<string> Words = new HashSet<string>();
    /// <summary>제목 바탕(영문, 글자 · 숫자만 소문자). 일본어만 있는 이름이면 비교할 수 없다 (빈 값).</summary>
    public string BaseKey = "";

    /// <summary>같은 게임 · 같은 판인지 볼 때 꼭 같아야 하는 것: 아카이브 · 드라이버 · 종류 · 장치 고르는 옵션.</summary>
    public string StructKey = "";

    public override string ToString() { return Set + "/" + File + ": " + Name; }
}

/// <summary>같은 게임 · 같은 판으로 본 항목들 (묶음마다 다를 수 있음) + 남길 것 하나.</summary>
public sealed class HootCluster
{
    public readonly List<HootGame> Members = new List<HootGame>();
    public HootGame Best;
    /// <summary>합친 결과의 제작사 파일 이름.</summary>
    public string OutFile = "";
    /// <summary>이름만 다르고 나머지가 같은 다른 묶음 (사람이 확인할 것).</summary>
    public readonly List<HootCluster> Similar = new List<HootCluster>();
    /// <summary>근거가 약했지만 (서로 하나뿐인 짝, 다른 묶음끼리) 합친 묶음: 확인 권장.</summary>
    public bool Uncertain;
    public int Id;

    public IEnumerable<string> Sets { get { return Members.Select(m => m.Set).Distinct(); } }
    public bool InSet(string set) { return Members.Any(m => m.Set == set); }
    public HootGame Of(string set) { return Members.Where(m => m.Set == set).OrderByDescending(m => Score(m)).FirstOrDefault(); }

    /// <summary>묶음마다 내용이 다른가 (이름 · 곡 · 옵션 · 롬).</summary>
    public string Differences
    {
        get
        {
            if (Members.Count < 2) return "";
            var l = new List<string>();
            if (Members.Select(m => m.Name).Distinct().Count() > 1) l.Add("이름");
            if (Members.Select(m => m.Titles.Count).Distinct().Count() > 1) l.Add("곡 수");
            else if (Members.Select(m => string.Join("\n", m.Titles.Select(t => t.Code + "=" + t.Text))).Distinct().Count() > 1) l.Add("곡 제목");
            if (Members.Select(m => string.Join(";", m.Options.OrderBy(o => o.Key).Select(o => o.Key + "=" + o.Value))).Distinct().Count() > 1) l.Add("옵션");
            if (Members.Select(m => string.Join(";", m.Roms.Select(r => r.Type + ":" + r.Text))).Distinct().Count() > 1) l.Add("롬");
            if (Members.Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) l.Add("파일");
            return string.Join(" · ", l);
        }
    }

    /// <summary>
    /// 남길 것 고르기: 곡 목록이 있는 것 → 새 이름 형식(일본어 제목 + 칩 이름) → 새 묶음 → 곡 수 → 옵션 수.
    /// </summary>
    public static long Score(HootGame g)
    {
        long s = 0;
        s += g.Titles.Count > 0 ? 1L << 40 : 0;
        s += (g.ChipLabel.Length > 0 ? 1L << 38 : 0) + (g.Japanese.Length > 0 ? 1L << 37 : 0);
        s += (long)Math.Min(g.SetRank, 15) << 32;
        s += (long)Math.Min(g.Titles.Count, 0xFFFF) << 12;
        s += Math.Min(g.Options.Count, 0xFFF);
        return s;
    }
}

/// <summary>
/// hoot 게임 목록 (xml · xml2 · xml3 …: 제작사마다 파일 하나, &lt;gamelist&gt; 안에 &lt;game&gt;) 읽기 · 비교 · 중복 없이 합치기.
/// <para>같은 항목 판단 (같은 &lt;romlist archive&gt; 안에서):
/// 드라이버 · 종류 · 장치 옵션(midiout · midiout_type · subtype)이 같고, 칩 이름이 맞고 (OPN = YM2203처럼 같은 칩으로 바꿔 봄, 없으면 아무거나),
/// 제목이 같거나 (영문 바탕), 곡 코드가 80% 넘게 겹치거나, 제목이 절반 넘게 닮고 곡도 절반 넘게 겹쳐야 한다. 근거가 강한 짝부터 합친다.
/// 칩 이름 뒤의 괄호는 만든 이로 보고 제목에서 뺀다 ((OPNA) (Firecracker)).
/// 같은 파일 안의 두 항목은 일부러 나눈 판(OPLL / PSG, 데모 / 정식 …)이라 합치지 않는다.
/// 섞기 · 클럭 같은 조정 옵션(fm_mix · clockmul …)은 달라도 같은 항목으로 본다.</para>
/// </summary>
public static class HootLists
{
    public static readonly string[] DeviceOptions = { "midiout", "midiout_type", "subtype" };

    public static Encoding ShiftJis
    {
        get
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }
            catch (Exception) { return Encoding.UTF8; }
        }
    }

    // ------------------------------------------------------------ 읽기

    private static readonly Regex GameRx = new Regex(@"<game>.*?</game>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DateRx = new Regex(@"<gamelist[^>]*\bdate=""([^""]*)""", RegexOptions.Compiled);

    public static string ReadText(string path)
    {
        var b = System.IO.File.ReadAllBytes(path);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        string head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 100));
        if (Regex.IsMatch(head, @"encoding=""utf-8""", RegexOptions.IgnoreCase)) return Encoding.UTF8.GetString(b);
        return ShiftJis.GetString(b);
    }

    /// <summary>폴더 하나(묶음)의 *.xml을 모두 읽는다. rank = 우선순위 (새 묶음일수록 크게).</summary>
    public static List<HootGame> ReadFolder(string dir, string set = null, int rank = 0, List<string> errors = null)
    {
        set = set ?? Path.GetFileName(dir.TrimEnd('\\', '/'));
        var l = new List<HootGame>();
        foreach (var f in Directory.GetFiles(dir, "*.xml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try { l.AddRange(ReadFile(f, set, rank)); }
            catch (Exception ex) { errors?.Add(Path.GetFileName(f) + ": " + ex.Message); }
        }
        return l;
    }

    /// <summary>
    /// 파일 하나 읽기. Shift_JIS 파일은 바이트에서 &lt;game&gt; … &lt;/game&gt;을 그대로 잘라 둔다
    /// ('&lt;'(0x3C)는 2바이트 글자의 둘째 바이트가 될 수 없어 바이트로 찾아도 안전). UTF-8 파일은 글자를 Shift_JIS로 바꿔 둔다.
    /// </summary>
    public static List<HootGame> ReadFile(string path, string set, int rank)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        bool utf8 = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            || Regex.IsMatch(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 100)), @"encoding=""utf-8""", RegexOptions.IgnoreCase);
        var sjis = ShiftJis;
        var lossy = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        // Latin-1 = 바이트 하나가 글자 하나: 위치를 바이트 그대로 찾는다
        string probe = utf8 ? ReadText(path) : Encoding.Latin1.GetString(bytes);
        var dm = DateRx.Match(probe);
        string date = dm.Success ? dm.Groups[1].Value : "";
        var l = new List<HootGame>();
        int i = 0;
        foreach (var m in GameBlocks(probe))
        {
            var g = new HootGame { Set = set, SetRank = rank, File = Path.GetFileName(path), Index = i++, Date = date };
            if (utf8) { g.Raw = m.Value; g.RawBytes = lossy.GetBytes(m.Value); }
            else
            {
                g.RawBytes = new byte[m.Length];
                Array.Copy(bytes, m.Index, g.RawBytes, 0, m.Length);
                g.Raw = sjis.GetString(g.RawBytes);
            }
            Fill(g);
            l.Add(g);
        }
        return l;
    }

    /// <summary>
    /// &lt;game&gt; … &lt;/game&gt; 위치들. 주석은 건너뛴다: 주석 안의 게임(꺼 둔 항목)은 빼고, 게임 안에서 시작한 주석이
    /// &lt;/game&gt;을 넘어 이어지면 (목록 일부를 주석으로 막은 것) 주석이 끝난 뒤의 &lt;/game&gt;까지를 한 덩어리로.
    /// </summary>
    public static List<(int Index, int Length)> GameBlocksAt(string t)
    {
        var l = new List<(int, int)>();
        int p = 0;
        while (p < t.Length)
        {
            int c = t.IndexOf("<!--", p, StringComparison.Ordinal);
            int g = t.IndexOf("<game>", p, StringComparison.Ordinal);
            if (g < 0) break;
            if (c >= 0 && c < g)
            {
                int ce = t.IndexOf("-->", c + 4, StringComparison.Ordinal);
                if (ce < 0) break;
                p = ce + 3;
                continue;
            }
            int q = g + 6;
            int end = -1;
            while (q < t.Length)
            {
                int c2 = t.IndexOf("<!--", q, StringComparison.Ordinal);
                int e2 = t.IndexOf("</game>", q, StringComparison.Ordinal);
                if (e2 < 0) break;
                if (c2 >= 0 && c2 < e2)
                {
                    int ce = t.IndexOf("-->", c2 + 4, StringComparison.Ordinal);
                    if (ce < 0) break;
                    q = ce + 3;
                    continue;
                }
                end = e2 + 7;
                break;
            }
            if (end < 0) break;
            l.Add((g, end - g));
            p = end;
        }
        return l;
    }

    private static IEnumerable<Match> GameBlocks(string t)
    {
        foreach (var (index, length) in GameBlocksAt(t)) yield return SliceRx.Match(t, index, length);
    }

    private static readonly Regex SliceRx = new Regex(@"[\s\S]*", RegexOptions.Compiled);

    private static void Fill(HootGame g)
    {
        XElement e;
        try { e = XElement.Parse(g.Raw.Replace("&nbsp;", "&#160;")); }
        catch (Exception) { e = null; }
        if (e != null)
        {
            g.Name = Clean((string)e.Element("name"));
            var d = e.Element("driver");
            if (d != null) { g.Driver = Clean(d.Value); g.DriverType = (string)d.Attribute("type") ?? ""; }
            var a = e.Element("driveralias");
            if (a != null) { g.Alias = Clean(a.Value); g.AliasType = (string)a.Attribute("type") ?? ""; }
            foreach (var o in e.Descendants("option"))
            {
                string n = (string)o.Attribute("name");
                if (n != null) g.Options[n] = (string)o.Attribute("value") ?? "";
            }
            var rl = e.Element("romlist");
            if (rl != null)
            {
                g.Archive = ((string)rl.Attribute("archive") ?? "").Trim();
                foreach (var r in rl.Elements("rom")) g.Roms.Add(((string)r.Attribute("type") ?? "", Clean(r.Value)));
            }
            var tl = e.Element("titlelist");
            if (tl != null)
                foreach (var t in tl.Elements())
                {
                    if (t.Name == "title") g.Titles.Add((((string)t.Attribute("code") ?? "").Trim().ToLowerInvariant(), Clean(t.Value)));
                    else if (t.Name == "range") g.Titles.Add(("range:" + (string)t.Attribute("min") + "-" + (string)t.Attribute("max"), Clean(t.Value)));
                }
        }
        else
        {
            // 망가진 항목: 이름 · 아카이브만이라도
            g.Name = Clean(Regex.Match(g.Raw, @"<name>(.*?)</name>", RegexOptions.Singleline).Groups[1].Value);
            g.Archive = Regex.Match(g.Raw, @"archive=""([^""]*)""").Groups[1].Value;
        }
        ParseName(g);
        g.StructKey = (g.Archive.ToLowerInvariant() + "|" + g.Driver.ToLowerInvariant() + "|" + g.DriverType.ToLowerInvariant() + "|" +
            string.Join("|", DeviceOptions.Select(o => g.Options.TryGetValue(o, out var v) ? NormNum(v) : "")));
    }

    private static string Clean(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

    /// <summary>"0x04" · "4" → "4" (옵션 값 비교).</summary>
    private static string NormNum(string v)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        if (v.StartsWith("0x") && long.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) return h.ToString(CultureInfo.InvariantCulture);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return n.ToString(CultureInfo.InvariantCulture);
        return v;
    }

    // ------------------------------------------------------------ 이름 풀기 · 칩 이름

    /// <summary>칩 · 음원 이름 → 같은 칩끼리 같은 이름 (xml은 OPN, xml3은 YM2203 …).</summary>
    private static readonly Dictionary<string, string> ChipAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "OPN", "YM2203" }, { "OPNA", "YM2608" }, { "OPNB", "YM2610" }, { "OPN2", "YM2612" }, { "OPNC", "YM2612" },
        { "OPM", "YM2151" }, { "OPP", "YM2164" }, { "OPL", "YM3526" }, { "OPL2", "YM3812" }, { "OPL3", "YMF262" }, { "OPL4", "YMF278B" },
        { "OPLL", "YM2413" }, { "FM-PAC", "YM2413" }, { "FMPAC", "YM2413" }, { "MSX-MUSIC", "YM2413" },
        { "MSX-AUDIO", "Y8950" }, { "MSXAUDIO", "Y8950" },
        { "PSG", "AY-3-8910" }, { "SSG", "AY-3-8910" }, { "AY8910", "AY-3-8910" }, { "AY-3-8912", "AY-3-8910" }, { "YM2149", "AY-3-8910" },
        { "SN76496", "SN76489" }, { "SN76489A", "SN76489" },
        { "MSM", "MSM6258" }, { "ADPCM", "MSM6258" }, { "SCC", "K051649" }, { "SCC+", "K052539" },
        { "SC55", "SC-55" }, { "SC-55MK2", "SC-55" }, { "MT32", "MT-32" }, { "TG100", "TG-100" },
    };

    private static readonly Regex ChipWord = new Regex(
        @"^(YM\w+|YMF\w+|Y8950|AY-?3-?89\d\d|AY\d+|SN7\d+\w*|MSM\d+\w*|OKIM?\d*|M6295|K0\d+|HUC6280|SAA\d+|RF5C\d+|SPC700|S-?DSP|SCC\+?|OP[A-Z0-9]*|PSG|SSG|MSX-?AUDIO|MSX-?MUSIC|FM-?PAC|FM ?SOUND.*|GS|GM|XG|SC-?\d+\w*|MT-?\d+|CM-?\d+\w*|TG-?\d+|SB\d*|MIDI|BEEP|PCM\d*|ADPCM[\w-]*|DAC|CD-?DA|VERMOUTH|GRAVIS.*|PC-?\d+-?\d*|SPEAK ?BOARD|SOUND ?BOARD.*|26K|86|73|ES\d+|Q?SOUND|C\d{3}|SEGA ?PCM|POKEY|DMG|APU|2A03|VRC\d|N163|FDS|MMC5|5B|SID\d*|ROLAND.*|\d+CH|MSN\d+|X1-?010|UPD\d+\w*|DSP|SPU|SCSP|MULTIPCM|TOWNS.*|RF\d+|MA-?\d+|OPX|PSG\d|SPU\d*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool HasCjk(string s) { return s.Any(c => c > 0x2E7F); }

    /// <summary>"[기계] 제목 (일본어) (칩)" → 기계 · 바탕 · 일본어 · 칩 이름 · 정규화한 칩.</summary>
    public static void ParseName(HootGame g)
    {
        string n = g.Name;
        // "… (칩) <- 메모" 같은 꼬리글은 뗀다
        int note = n.IndexOf(" <-", StringComparison.Ordinal);
        if (note > 0) n = n.Substring(0, note);
        var pm = Regex.Match(n, @"^\s*\[([^\]]*)\]\s*");
        if (pm.Success) { g.Platform = pm.Groups[1].Value.Trim(); n = n.Substring(pm.Length); }
        // 끝에서부터 괄호 덩어리를 떼어 본다
        var tail = new List<string>();
        while (true)
        {
            n = n.TrimEnd();
            if (!n.EndsWith(")")) break;
            int depth = 0, i = n.Length - 1;
            for (; i >= 0; i--)
            {
                if (n[i] == ')') depth++;
                else if (n[i] == '(' && --depth == 0) break;
            }
            if (i <= 0) break;
            tail.Insert(0, n.Substring(i + 1, n.Length - i - 2).Trim());
            n = n.Substring(0, i);
        }
        var keep = new List<string>();
        bool afterChip = false;
        foreach (var p in tail)
        {
            if (IsChipLabel(p)) { g.ChipLabel = g.ChipLabel.Length == 0 ? p : g.ChipLabel + "+" + p; afterChip = true; continue; }
            if (HasCjk(p) && g.Japanese.Length == 0 && !afterChip) { g.Japanese = p; continue; }
            if (afterChip) { g.Credit = g.Credit.Length == 0 ? p : g.Credit + ", " + p; continue; }   // (OPNA) (Firecracker) = 만든 이
            if (HasCjk(p)) continue;
            keep.Add(p);
        }
        g.Base = (n.Trim() + (keep.Count > 0 ? " (" + string.Join(") (", keep) + ")" : "")).Trim();
        g.Chip = NormChip(g.ChipLabel);
        g.BaseKey = HasCjk(n) ? "" : Regex.Replace(g.Base.ToLowerInvariant().Replace("&amp;", "&"), @"[^a-z0-9]", "");
        g.Words = new HashSet<string>(Regex.Matches(Regex.Replace(g.Base.ToLowerInvariant(), @"[^\x00-\x7f]", " "), @"[a-z0-9]+").Cast<Match>()
            .Select(m => m.Value).Where(w => w.Length > 0 && !StopWords.Contains(w)));
    }

    public static bool IsChipLabel(string p)
    {
        if (p.Length == 0 || HasCjk(p)) return false;
        var tokens = SplitChip(p);
        return tokens.Count > 0 && tokens.Any(t => ChipWord.IsMatch(t));
    }

    private static readonly HashSet<string> StopWords = new HashSet<string> { "the", "a", "of", "no", "and", "version", "ver", "vol", "disk", "music", "edition" };

    private static List<string> SplitChip(string p)
    {
        return Regex.Split(p, @"\s*[+,/:&]\s*|\s+and\s+|\s+with\s+").Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
    }

    /// <summary>칩 이름 → 같은 칩끼리 같은 글자 (정렬한 집합, 대문자).</summary>
    public static string NormChip(string label)
    {
        if (string.IsNullOrEmpty(label)) return "";
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in SplitChip(label))
        {
            string u = Regex.Replace(t.ToUpperInvariant(), @"\s+(EMULATION|SIMULATION|EMU|SIM)$", "").Trim();
            if (u == "MUNT" || u == "EMULATION" || u == "SIMULATION" || u.Length == 0) continue;
            if (u.Contains("MT-32") || u.Contains("MT32")) u = "MT-32";
            else if (u == "TANDY") u = "SN76489";
            else if (u == "SPB" || u.Replace(" ", "") == "SPEAKBOARD") { set.Add("YM2608"); u = "SPEAKBOARD"; }
            else if (u == "PC-9801-86" || u == "86") { set.Add("YM2608"); u = "PC-9801-86"; }
            if (ChipAlias.TryGetValue(u, out var a)) u = a.ToUpperInvariant();
            set.Add(u);
        }
        return string.Join("+", set);
    }

    // ------------------------------------------------------------ 묶기

    /// <summary>
    /// 남길 것: 점수(<see cref="HootCluster.Score"/>)가 가장 높은 것. 단 다른 항목의 곡 목록이 뚜렷이 길면 (1.5배 이상이고 5곡 넘게 많음) 그것 — 더 완전한 목록.
    /// </summary>
    public static HootGame PickBest(IList<HootGame> members)
    {
        var best = members.OrderByDescending(HootCluster.Score).First();
        var longest = members.OrderByDescending(m => m.Titles.Count).ThenByDescending(HootCluster.Score).First();
        if (longest != best && longest.Titles.Count >= best.Titles.Count * 1.5 && longest.Titles.Count >= best.Titles.Count + 5) return longest;
        return best;
    }

    /// <summary>칩이 맞는가: 같거나, 한쪽이 없거나, 한쪽이 다른 쪽에 들어감 (OPM+MT-32 ⊇ MT-32).</summary>
    public static bool ChipCompatible(HootGame a, HootGame b)
    {
        if (a.Chip.Length == 0 || b.Chip.Length == 0 || a.Chip == b.Chip) return true;
        var x = a.Chip.Split('+'); var y = b.Chip.Split('+');
        return x.All(y.Contains) || y.All(x.Contains);
    }

    private static bool SameFile(HootGame a, HootGame b) { return a.Set == b.Set && string.Equals(a.File, b.File, StringComparison.OrdinalIgnoreCase); }

    /// <summary>한 묶음에 함께 둘 수 있는가 (같은 파일 아님 · 구조 같음 · 칩 맞음).</summary>
    public static bool CanJoin(HootGame a, HootGame b)
    {
        if (a.Raw == b.Raw) return true;
        return !SameFile(a, b) && a.StructKey == b.StructKey && ChipCompatible(a, b);
    }

    /// <summary>제목 닮음 0~1: 바탕이 같으면 1, 아니면 낱말 겹침 (일본어만 있으면 0).</summary>
    public static double NameSimilarity(HootGame a, HootGame b)
    {
        if (a.BaseKey.Length > 0 && a.BaseKey == b.BaseKey) return 1;
        if (a.Words.Count == 0 || b.Words.Count == 0) return 0;
        int inter = a.Words.Count(b.Words.Contains);
        return inter / (double)(a.Words.Count + b.Words.Count - inter);
    }

    /// <summary>
    /// 같은 항목이라고 볼 근거 점수 (0 = 아님): 제목 닮음 × 2 + 곡 코드 겹침.
    /// 받아들이는 경우: 제목 바탕이 같음, 곡 코드 80% 이상 겹침, 제목 절반 이상 닮고 곡도 절반 이상 겹침(또는 한쪽에 곡 목록 없음).
    /// </summary>
    public static double MatchScore(HootGame a, HootGame b)
    {
        if (a.Raw == b.Raw) return 10;
        if (!CanJoin(a, b)) return 0;
        double n = NameSimilarity(a, b), t = TitleOverlap(a, b);
        bool noTitles = a.Titles.Count == 0 || b.Titles.Count == 0;
        bool ok = n >= 0.999 || t >= 0.8 || (n >= 0.5 && (t >= 0.5 || noTitles));
        return ok ? n * 2 + t + 0.01 : 0;
    }

    /// <summary>두 항목이 같은 게임 · 같은 판인가 (근거 점수 &gt; 0).</summary>
    public static bool Same(HootGame a, HootGame b) { return MatchScore(a, b) > 0; }

    public static double TitleOverlap(HootGame a, HootGame b)
    {
        if (a.Titles.Count == 0 || b.Titles.Count == 0) return 0;
        var x = new HashSet<string>(a.Titles.Select(t => t.Code));
        var y = new HashSet<string>(b.Titles.Select(t => t.Code));
        int inter = x.Count(y.Contains);
        int uni = x.Count + y.Count - inter;
        return uni == 0 ? 0 : inter / (double)uni;
    }

    /// <summary>
    /// 모든 항목을 같은 것끼리 묶는다 (아카이브마다): 같다고 볼 근거가 있는 짝을 점수 높은 것부터 합치되,
    /// 합친 묶음 안에 같은 파일의 두 항목이 생기거나 구조 · 칩이 안 맞는 짝이 생기면 합치지 않는다.
    /// 그래서 "Last Armageddon"은 다른 묶음의 "Last Armageddon"과, "… Demo"는 "… Demo"와 짝이 된다.
    /// </summary>
    public static List<HootCluster> Cluster(IEnumerable<HootGame> games)
    {
        var all = new List<HootCluster>();
        foreach (var grp in games.GroupBy(g => g.Archive.Length > 0 ? "a:" + g.Archive.ToLowerInvariant() : "n:" + g.BaseKey + "|" + g.Name))
        {
            var e = grp.ToList();
            int n = e.Count;
            var owner = new int[n];
            var groups = new List<List<int>>();
            for (int i = 0; i < n; i++) { owner[i] = i; groups.Add(new List<int> { i }); }
            var edges = new List<(double s, int a, int b)>();
            for (int i = 0; i < n; i++)
                for (int k = i + 1; k < n; k++)
                {
                    double sc = MatchScore(e[i], e[k]);
                    if (sc > 0) edges.Add((sc, i, k));
                }
            foreach (var (sc, a, b) in edges.OrderByDescending(x => x.s))
            {
                int ga = owner[a], gb = owner[b];
                if (ga == gb) continue;
                if (!groups[ga].All(x => groups[gb].All(y => CanJoin(e[x], e[y])))) continue;
                foreach (var y in groups[gb]) owner[y] = ga;
                groups[ga].AddRange(groups[gb]);
                groups[gb].Clear();
            }
            var live = groups.Where(x => x.Count > 0).ToList();
            // 근거가 모자라 남은 짝: 함께 둘 수 있고(같은 파일 아님 · 구조 · 칩), 서로 하나뿐인 짝이며, 겹치는 묶음(xml · xml3 …)이 없으면
            // 같은 게임이 묶음마다 이름 · 곡 목록만 다르게 적힌 것 → 합치고 '확인 권장'으로 표시
            var uncertain = new HashSet<List<int>>();
            bool Joinable(List<int> p, List<int> q) { return p.All(x => q.All(y => CanJoin(e[x], e[y]))); }
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < live.Count && !merged; i++)
                {
                    var cand = live.Where((q, k) => k != i && Joinable(live[i], q)).ToList();
                    if (cand.Count != 1) continue;
                    var other = cand[0];
                    if (live.Count(q => q != other && Joinable(other, q)) != 1) continue;
                    var setsA = live[i].Select(x => e[x].Set); var setsB = other.Select(x => e[x].Set);
                    if (setsA.Intersect(setsB).Any()) continue;
                    live[i].AddRange(other);
                    live.Remove(other);
                    uncertain.Add(live.First(q => q.Count > 0 && q.Contains(cand[0][0])));
                    merged = true;
                }
            }
            var local = new List<HootCluster>();
            foreach (var gl in live)
            {
                var c = new HootCluster { Uncertain = uncertain.Contains(gl) };
                c.Members.AddRange(gl.Select(x => e[x]).OrderBy(x => x.Set, StringComparer.Ordinal).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase));
                c.Best = PickBest(c.Members);
                local.Add(c);
            }
            // 확인 필요: 함께 둘 수 있는데 근거가 모자라 따로 남은 묶음 (위에서 합치지 못한 것)
            for (int i = 0; i < local.Count; i++)
                for (int k = i + 1; k < local.Count; k++)
                    if (local[i].Members.All(x => local[k].Members.All(y => CanJoin(x, y))))
                    { local[i].Similar.Add(local[k]); local[k].Similar.Add(local[i]); }
            all.AddRange(local);
        }
        AssignFiles(all);
        int id = 0;
        foreach (var c in all.OrderBy(c => c.OutFile, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Best.Set).ThenBy(c => c.Best.Index)) c.Id = id++;
        return all.OrderBy(c => c.Id).ToList();
    }

    /// <summary>제작사 파일 이름: 밑줄 · 붙임표만 다른 이름(birdysoft · birdy_soft)은 하나로 (가장 많이 쓰인 이름, 같으면 밑줄 있는 이름).</summary>
    private static void AssignFiles(List<HootCluster> all)
    {
        string Norm(string f) { return Regex.Replace(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), @"[_\-\s]", ""); }
        var counts = all.GroupBy(c => c.Best.File.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var pick = all.Select(c => c.Best.File.ToLowerInvariant()).Distinct()
            .GroupBy(Norm)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => counts[f]).ThenByDescending(f => f.Contains("_")).ThenBy(f => f).First());
        foreach (var c in all) c.OutFile = pick[Norm(c.Best.File)];
    }

    // ------------------------------------------------------------ 합치기

    public sealed class MergeResult
    {
        public int Entries, Clusters, Dropped, Files, SimilarPairs, Unencodable, Uncertain;
        public readonly Dictionary<string, int> PerSet = new Dictionary<string, int>();
        public readonly Dictionary<string, int> KeptFrom = new Dictionary<string, int>();
        public readonly List<string> Errors = new List<string>();
        public string ReportPath = "", HootXmlPath = "";
    }

    /// <summary>
    /// 묶음 폴더들(먼저 = 오래된 묶음, 뒤 = 새 묶음)을 읽어 중복 없이 outDir에 쓴다: 제작사 파일마다 남긴 항목(원래 글자 그대로, Shift_JIS),
    /// hoot.xsl · drivers.xsl · hoot.css, _report.txt (남김 · 뺌 · 확인 필요). hootXml이 있으면 그 목록 부분을 outDir로 바꾼 hoot_new.xml도.
    /// </summary>
    public static MergeResult Merge(IList<string> setDirs, string outDir, string hootXml = null)
    {
        var r = new MergeResult();
        var games = new List<HootGame>();
        for (int i = 0; i < setDirs.Count; i++)
        {
            var g = ReadFolder(setDirs[i], null, i + 1, r.Errors);
            r.PerSet[Path.GetFileName(setDirs[i].TrimEnd('\\', '/'))] = g.Count;
            games.AddRange(g);
        }
        var clusters = Cluster(games);
        r.Entries = games.Count; r.Clusters = clusters.Count; r.Dropped = games.Count - clusters.Count;
        r.SimilarPairs = clusters.Sum(c => c.Similar.Count) / 2;
        r.Uncertain = clusters.Count(c => c.Uncertain);

        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.xml")) System.IO.File.Delete(old);
        // 머리말 (prolog + DOCTYPE): 가장 새 묶음의 첫 파일에서
        string header = DefaultHeader;
        var newest = setDirs.LastOrDefault(d => Directory.GetFiles(d, "*.xml").Length > 0);
        if (newest != null)
        {
            string t = ReadText(Directory.GetFiles(newest, "*.xml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First());
            int at = t.IndexOf("<gamelist", StringComparison.Ordinal);
            if (at > 0) header = t.Substring(0, at);
        }
        header = Regex.Replace(header, @"encoding=""[^""]*""", @"encoding=""Shift_JIS""");
        var enc = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        var strict = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var file in clusters.GroupBy(c => c.OutFile))
        {
            var ms = new MemoryStream();
            void Put(string text) { var b = enc.GetBytes(text); ms.Write(b, 0, b.Length); }
            Put(header);
            string date = file.Select(c => c.Best.Date).Where(d => d.Length > 0).DefaultIfEmpty("").Max(StringComparer.Ordinal);
            Put("<gamelist" + (date.Length > 0 ? " date=\"" + date + "\"" : "") + ">\r\n");
            foreach (var c in file)
            {
                if (c.Best.RawBytes.Length == 0)
                {
                    try { strict.GetBytes(c.Best.Raw); } catch (EncoderFallbackException) { r.Unencodable++; }
                    c.Best.RawBytes = enc.GetBytes(c.Best.Raw);
                }
                // 원래 바이트 그대로 (줄 끝만 CRLF로)
                var raw = c.Best.RawBytes;
                var line = new List<byte>(raw.Length + 64) { (byte)'\t' };
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == (byte)'\n' && (i == 0 || raw[i - 1] != (byte)'\r')) line.Add((byte)'\r');
                    line.Add(raw[i]);
                }
                line.Add((byte)'\r'); line.Add((byte)'\n');
                ms.Write(line.ToArray(), 0, line.Count);
                r.KeptFrom[c.Best.Set] = r.KeptFrom.TryGetValue(c.Best.Set, out int k) ? k + 1 : 1;
            }
            Put("</gamelist>\r\n");
            System.IO.File.WriteAllBytes(Path.Combine(outDir, file.Key), ms.ToArray());
            r.Files++;
        }
        // 스타일 파일
        foreach (var name in new[] { "hoot.xsl", "drivers.xsl", "hoot.css" })
        {
            var src = setDirs.Reverse().Select(d => Path.Combine(d, name)).FirstOrDefault(System.IO.File.Exists);
            if (src != null) System.IO.File.Copy(src, Path.Combine(outDir, name), true);
        }
        r.ReportPath = Path.Combine(outDir, "_report.txt");
        System.IO.File.WriteAllText(r.ReportPath, Report(r, clusters, setDirs), new UTF8Encoding(true));
        if (!string.IsNullOrEmpty(hootXml) && System.IO.File.Exists(hootXml))
        {
            r.HootXmlPath = Path.Combine(Path.GetDirectoryName(hootXml), "hoot_new.xml");
            WriteHootXml(hootXml, r.HootXmlPath, Path.GetFileName(outDir.TrimEnd('\\', '/')), clusters.Select(c => c.OutFile).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        return r;
    }

    private const string DefaultHeader = "<?xml version=\"1.0\" encoding=\"Shift_JIS\"?>\r\n<?xml-stylesheet type=\"text/xsl\" href=\"hoot.xsl\"?>\r\n";

    /// <summary>hoot.xml을 베껴 &lt;list&gt;xml…/파일&lt;/list&gt; 줄을 모두 빼고 첫 자리에 새 폴더의 목록을 넣는다.</summary>
    private static void WriteHootXml(string src, string dst, string folder, IEnumerable<string> files)
    {
        string t = ReadText(src);
        var lines = t.Replace("\r\n", "\n").Split('\n').ToList();
        var listRx = new Regex(@"^\s*<list>\s*xml\d*/[^<]*</list>\s*$", RegexOptions.IgnoreCase);
        int first = lines.FindIndex(l => listRx.IsMatch(l));
        if (first < 0) return;
        string indent = Regex.Match(lines[first], @"^\s*").Value;
        lines = lines.Where(l => !listRx.IsMatch(l)).ToList();
        int at = Math.Min(first, lines.Count);
        lines.InsertRange(at, files.Select(f => indent + "<list>" + folder + "/" + f + "</list>"));
        var enc = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        System.IO.File.WriteAllBytes(dst, enc.GetBytes(string.Join("\r\n", lines)));
    }

    private static string Report(MergeResult r, List<HootCluster> clusters, IList<string> setDirs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("hoot 게임 목록 합치기 — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine("묶음 (뒤일수록 우선): " + string.Join(", ", setDirs.Select(d => Path.GetFileName(d.TrimEnd('\\', '/')) + " " + r.PerSet[Path.GetFileName(d.TrimEnd('\\', '/'))] + "개")));
        sb.AppendLine("항목 " + r.Entries + "개 → " + r.Clusters + "개 (뺀 중복 " + r.Dropped + "개), 제작사 파일 " + r.Files + "개");
        sb.AppendLine("남긴 항목의 출처: " + string.Join(", ", r.KeptFrom.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value)));
        sb.AppendLine("근거가 약해 합친 묶음 (확인 권장): " + r.Uncertain + "개 · 따로 남긴 비슷한 항목 쌍: " + r.SimilarPairs + "개 · Shift_JIS로 못 쓴 글자가 있는 항목: " + r.Unencodable + "개");
        if (r.Errors.Count > 0) { sb.AppendLine("읽지 못한 파일:"); foreach (var e in r.Errors) sb.AppendLine("  " + e); }
        sb.AppendLine();
        sb.AppendLine("같은 항목 판단 (같은 아카이브 안): 드라이버 · 종류 · 장치 옵션(midiout · midiout_type · subtype)이 같고 칩이 맞으며 (OPN = YM2203, MT-32 Emulation = Munt …), 제목이 같거나 곡 코드가 80% 이상 겹치거나 제목 · 곡이 절반 넘게 닮음 — 근거 강한 짝부터.");
        sb.AppendLine("같은 파일 안 두 항목은 합치지 않음. 남길 것: 곡 목록 있음 → 새 이름 형식(일본어 + 칩) → 새 묶음 → 곡 수.");
        sb.AppendLine();
        sb.AppendLine("==================== 따로 남김 (비슷함: 같은 게임일 수도) ====================");
        var seen = new HashSet<(int, int)>();
        foreach (var c in clusters)
            foreach (var o in c.Similar)
            {
                var k = (Math.Min(c.Id, o.Id), Math.Max(c.Id, o.Id));
                if (!seen.Add(k)) continue;
                sb.AppendLine("  [" + c.Best.Archive + "] " + c.Best.Set + "/" + c.Best.File + ": " + c.Best.Name + " (곡 " + c.Best.Titles.Count + ")");
                sb.AppendLine("  " + new string(' ', c.Best.Archive.Length + 2) + " " + o.Best.Set + "/" + o.Best.File + ": " + o.Best.Name + " (곡 " + o.Best.Titles.Count + ")");
            }
        sb.AppendLine();
        sb.AppendLine("==================== 합침 (근거가 약함: 확인 권장) ====================");
        foreach (var c in clusters.Where(c => c.Uncertain))
        {
            sb.AppendLine("  [" + c.Best.Archive + "] 남김 " + c.Best.Set + "/" + c.Best.File + ": " + c.Best.Name + " (곡 " + c.Best.Titles.Count + ")");
            foreach (var m in c.Members.Where(m => m != c.Best))
                sb.AppendLine("      ← " + m.Set + "/" + m.File + ": " + m.Name + " (곡 " + m.Titles.Count + ")");
        }
        sb.AppendLine();
        sb.AppendLine("==================== 뺀 중복 (남김 ← 뺌) ====================");
        foreach (var c in clusters.Where(c => c.Members.Count > 1))
        {
            sb.AppendLine("  " + c.OutFile + " | " + c.Best.Set + "/" + c.Best.File + ": " + c.Best.Name + " (곡 " + c.Best.Titles.Count + ")");
            foreach (var m in c.Members.Where(m => m != c.Best))
                sb.AppendLine("      ← " + m.Set + "/" + m.File + ": " + m.Name + " (곡 " + m.Titles.Count + ")");
        }
        return sb.ToString();
    }
}
