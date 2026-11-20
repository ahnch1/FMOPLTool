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
/// zip을 풀 때 (<see cref="ZipFolder.Extract"/>) 그 zip을 쓰는 재생할 수 있는 항목(<see cref="Playable"/>: PC-98 DOS 흉내 · SPC · KSS · NSF · GBS)마다
/// 항목 XML 하나를 <see cref="Extension"/> 파일로 푼 폴더에 써 둔다 → 파일 목록 · Jukebox에서 곡이 여럿 든 파일처럼 보인다.
/// <para>목록 찾기: zip이 있는 폴더부터 위로 세 단계까지, 그 폴더나 그 아래 xml_new · xml에 *.xml이 있는 곳 (hoot 배포 모양: hoot.exe 옆 xml\, 기종 폴더 pc98\).
/// 처음 한 번 목록 전체(수십 MB)를 훑어 archive → 항목 색인을 만든다 (기종 흉내 항목만 남김).</para>
/// </summary>
public static class HootArchive
{
    public const string Extension = ".hoot";
    /// <summary>항목을 써 둔 표시 (재생할 수 있는 드라이버가 늘면 이름을 바꿔 이미 푼 zip에도 다시 쓴다: 2 = SPC · KSS · NSF · GBS 더함, 3 = PC/AT DOS, 4 = MIDI 판, 5 = pc98vx · pc98).</summary>
    private const string Mark = ".fmopl_hoot46";

    /// <summary>
    /// 재생할 수 있는 hoot 드라이버 · 형식: pc98dos opn · opna · 86 · beep · pcatdos adlib · soundblaster16 · beep (기계 흉내), 그리고 이미 읽는 파일을 곡 코드로 고르는 것 —
    /// snes spc (rom offset = 곡 코드 → 그 .spc), msx kss · nes nsf · gb gbs (곡 코드 = 파일 안 곡 번호).
    /// 옵션 midiout이 있는 항목(MIDI 판)은 색인에서 뺀다.
    /// </summary>
    public static bool Playable(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant(), t = (type ?? "").Trim().ToLowerInvariant();
        switch (d)
        {
            case "pc98dos": case "pc98vx": case "pc98": return t == "opn" || t == "opna" || t == "86" || t == "beep";
            case "pcatdos": return t == "adlib" || t == "soundblaster16" || t == "beep";
            case "snes": return t == "spc";
            case "msx": return t == "kss" || t == "generic";
            case "pc88": return t == "opn" || t == "opna";
            case "x1": return t == "psg" || t == "opm";
            case "neogeo": return t == "generic";
            case "dataeast": return t == "thndzone" || t == "thunderzone" || t == "supbtime" || t == "deco32" || t == "wolffang" || t == "generic"   // HuC6280 + YM2151 + OKIM6295 × 2
                || t == "midres"   // HuC6280 + YM3812 + YM2203 + OKIM6295 (HootDeco midres 배치)
                || t == "cobracom" || t == "drgninja" || t == "karnov" || t == "triothep" || t == "lastmisn" || t == "makyosen"    // 6502 + YM2203 + YM3812/YM3526 (+ OKIM6295) (HootDeco6502)
                || t == "brkthru" || t == "exprraid";                                                      // 6809 + YM2203 + YM3526 (HootDeco6809)
            case "jaleco": return t == "megasys1";                                                       // 68000 + YM2151 + OKIM6295 × 2
            case "sega": return t == "system1" || t == "system16a" || t == "system16b"                   // Z80 + SN76489 × 2 / Z80 + YM2151 + uPD7759
                || t == "outrun" || t == "shangon" || t == "toutrun" || t == "aburner" || t == "spmonaco" || t == "gforce" || t == "pdrift"   // Z80 + YM2151 + 세가 PCM
                || t == "hangon" || t == "sharrier" || t == "eropnx2";                                       // Z80 + YM2203 + 세가 PCM
            case "bootleg": return t == "outrun";                                                        // Z80 + YM2151 + 세가 PCM (해적판)
            case "irem": return t == "m72"                                                               // Z80 + YM2151
                || t == "m62";                                                                             // M6803 + AY-3-8910 × 2 (HootIremM62)
            case "capcom": return t == "cps1" || t == "cps2" || t == "cps1qs" || t == "zn"              // Z80 + YM2151 + OKIM6295 / Z80 + QSound (cps1qs = Kabuki 암호, zn = 래치 + NMI)
                || t == "opn2" || t == "rushcrsh" || t == "tigerroad";                                     // Z80 + YM2203 × 2 (HootCapcomOpn)
            case "technos": return t == "ddragon2" || t == "ddragon3" || t == "ctribe";
            case "tecmo": return t == "tecmo16"                                                           // Z80 + YM2151 + OKIM6295 (HootOpmBoard.Tecmo16)
                || t == "gaiden"                                                                           // Z80 + YM2203 × 2 + OKIM6295 (HootGaiden)
                || t == "rygar" || t == "gemini";                                                          // Z80 + YM3812 + MSM5205 (HootTecmoAdpcm)
            case "taito": return t == "f2system" || t == "bsystem" || t == "dual68" || t == "xsystem" || t == "fx1a" || t == "sonicb"   // Z80 + YM2610 + TC0140SYT
                || t == "fullt"    // Z80 + YM2151 + MSM5205 + TC0140SYT (HootTaitoOpm)
                || t == "taitosj"; // Z80 + AY × 3 + DAC (HootTaitoSj)
            case "upl": return t == "robokid";                                                           // Z80 + YM2203 × 2 (HootUpl, VGM 듀얼 YM2203)
            case "alpha": return t == "68k2";                                                             // Z80 + YM2203 + YM2413 + DAC (HootAlpha68k)
            case "nichibutsu": return t == "terracre" || t == "terraf";                                  // Z80 + YM3526/YM3812 + DAC (HootNichibutsu)
            case "snk": return t == "3812";                                                               // SNK68: Z80 + YM3812 + uPD7759 (HootSnk68)
            case "toaplan": return t == "generic";                                                        // 토아플랜 1 · Twin Cobra: Z80 + YM3812 (HootToaplan)
            case "namco": return t == "system1"                                                          // 6809 + YM2151 + CUS30 (HootNamcoSys1)
                || t == "wsg6809"                                                                          // 6809 + 15XX (HootNamcoWsg6809)
                || t == "system2"                                                                          // 6809 + YM2151 + C140 (HootNamcoSys2)
                || t == "system86" || t == "wsg63701";                                                     // HD63701 MCU + (YM2151) + CUS30 (HootNamcoSys86)
            case "konami": return t == "scramble" || t == "timeplt"                                      // Z80 + AY-3-8910 × 2 (HootKonamiAy)
                || t == "053260";                                                                          // Z80 + YM2151 + K053260 (HootKonami53260)
            case "atari": return t == "system1";                                                         // 6502 + YM2151 + POKEY (HootAtariSys1, TMS5220 없음)
            case "megadrive": return t == "generic_z80" || t == "iyinc" || t == "1c00" || t == "koei";
            case "x68k": return t == "generic";
            case "nes": return t == "nsf";
            case "gb": return t == "gbs";
        }
        return false;
    }

    /// <summary>MIDI 판(옵션 midiout)을 낼 수 있는 드라이버: 기계 흉내 (pc98dos · pc98vx · pc98 · pcatdos)의 MPU-401 · MPU-PC98.</summary>
    public static bool MidiPlayable(string driver)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant();
        return d == "pc98dos" || d == "pcatdos" || IsPc98Generic(d) || d == "x68k";
    }

    /// <summary>DOS 없는 PC-98 (hoot PC-98generic): pc98vx, 옛 방식 pc98.</summary>
    public static bool IsPc98Generic(string driver)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant();
        return d == "pc98vx" || d == "pc98";
    }

    /// <summary>항목 하나를 재생할 수 있는가 (드라이버 · 형식 · MIDI 판).</summary>
    public static bool PlayableGame(string driver, string type, bool midiOut)
    {
        return midiOut ? MidiPlayable(driver) : Playable(driver, type);
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
                    var dm = DriverRx.Match(g);
                    if (!dm.Success || !(Playable(dm.Groups[2].Value, dm.Groups[1].Value) || (MidiOutRx.IsMatch(g) && MidiPlayable(dm.Groups[2].Value)))) continue;
                    if (MidiOutRx.IsMatch(g) && !MidiPlayable(dm.Groups[2].Value)) continue;   // MIDI로 내는 판: DOS 기계 (MPU-401 · MPU-PC98)만
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
                if (MissingRom(dir, g) != null) continue;   // 이 zip에 그 판의 곡 파일이 없음
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

    /// <summary>
    /// 항목 하나(&lt;game&gt; XML)를 푼 zip 폴더에 <see cref="Extension"/> 파일로 (hoot 트리에서 고른 항목 재생). 같은 내용의 파일이 있으면 그것,
    /// 이름만 같고 내용이 다르면 " (2)" … 를 붙인다. 쓴 (또는 있던) 파일 경로.
    /// </summary>
    public static string WriteEntry(string dir, string gameXml, string fallbackName)
    {
        string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + gameXml.Trim() + "\r\n";
        string name = EntryFileName(gameXml, fallbackName);
        for (int k = 1; ; k++)
        {
            string path = Path.Combine(dir, (k == 1 ? name : name + " (" + k + ")") + Extension);
            if (!File.Exists(path)) { File.WriteAllText(path, text, new UTF8Encoding(true)); return path; }
            string have;
            try { have = File.ReadAllText(path); } catch (Exception) { continue; }
            if (Same(have, text)) return path;
        }
    }

    /// <summary>두 항목 XML이 같은가 (공백 · 줄바꿈 · 머리말 차이는 무시).</summary>
    private static bool Same(string a, string b)
    {
        string Norm(string s)
        {
            int g = s.IndexOf("<game>", StringComparison.Ordinal);
            return Regex.Replace(g >= 0 ? s.Substring(g) : s, @"\s+", "");
        }
        return Norm(a) == Norm(b);
    }

    /// <summary>zip을 푼 폴더에서 rom 이름의 파일 (대소문자 · '/' 무시, 하위 폴더 포함, 이름만 맞아도). 없으면 null.</summary>
    public static string FindFile(string dir, string name)
    {
        string rel = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string direct = Path.Combine(dir, rel);
        if (File.Exists(direct)) return direct;
        string leaf = Path.GetFileName(rel), byLeaf = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetRelativePath(dir, f), rel, StringComparison.OrdinalIgnoreCase)) return f;
            if (byLeaf == null && string.Equals(Path.GetFileName(f), leaf, StringComparison.OrdinalIgnoreCase)) byLeaf = f;
        }
        return byLeaf ?? ByBoardLocation(dir, leaf);
    }

    private static readonly Regex LocationRx = new Regex(@"^(?=.*\d)(?=.*[a-z])[0-9a-z]{2,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 이름이 다른 판의 zip (MAME 롬 이름이 바뀐 것: amidar.5c ↔ s1.5c, 11-7a.bin ↔ s1.7a): 기판 위치 코드 (숫자 + 글자 2~3자, 예 5c · 7a · 3f)가
    /// 같은 파일이 폴더에 꼭 하나 있으면 그것. 없거나 여럿이면 null.
    /// </summary>
    public static string ByBoardLocation(string dir, string leaf)
    {
        var codes = Locations(leaf);
        if (codes.Count == 0) return null;
        string hit = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string n = Path.GetFileName(f);
            if (n.StartsWith(".", StringComparison.Ordinal) || n.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Locations(n).Overlaps(codes)) continue;
            if (hit != null) return null;   // 여럿이면 고르지 않음
            hit = f;
        }
        return hit;
    }

    private static HashSet<string> Locations(string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in name.Split('.', '-', '_', ' '))
            if (LocationRx.IsMatch(t) && !string.Equals(t, "bin", StringComparison.OrdinalIgnoreCase)) set.Add(t);
        return set;
    }

    /// <summary>
    /// 파일을 바로 읽는 항목(kss · nsf · gbs: rom 파일 하나)인데 그 파일이 푼 zip에 없으면 그 이름 (같은 archive 이름을 다른 판과 나눠 쓰는 항목이 있다).
    /// 기계 흉내(pc98dos) · SPC(곡마다 파일)는 null.
    /// </summary>
    public static string MissingRom(string dir, string gameXml)
    {
        var dm = DriverRx.Match(gameXml);
        string driver = dm.Success ? dm.Groups[2].Value.ToLowerInvariant() : "";
        if (driver != "msx" && driver != "nes" && driver != "gb") return null;
        var rom = Regex.Match(gameXml, @"<rom\b[^>]*>\s*([^<]+?)\s*</rom>");
        if (!rom.Success) return "(rom 없음)";
        string name = System.Net.WebUtility.HtmlDecode(rom.Groups[1].Value);
        return FindFile(dir, name) == null ? name : null;
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
        {
            foreach (var t in tl.Elements("title"))
                if (TryNumber((string)t.Attribute("code"), out long code)) r.Titles.Add(((int)code, Clean(t.Value)));
            // 제목 없이 range만 있는 항목 (예: "BGM %02x"): range가 곧 곡 목록 (보통은 효과음이라 뺀다)
            if (r.Titles.Count == 0)
                foreach (var rg in tl.Elements("range"))
                {
                    if (!TryNumber((string)rg.Attribute("min"), out long lo) || !TryNumber((string)rg.Attribute("max"), out long hi)) continue;
                    string fmt = Clean(rg.Value);
                    for (long c = lo; c <= hi && r.Titles.Count < 256; c++) r.Titles.Add(((int)c, RangeTitle(fmt, c)));
                }
        }
        return r;
    }

    public static Entry Load(string path) { return Parse(File.ReadAllText(path)); }

    /// <summary>range 이름 무늬 ("BGM %02x" · "%d") → 그 코드의 제목.</summary>
    public static string RangeTitle(string fmt, long code)
    {
        return Regex.Replace(fmt, @"%0?(\d*)([xXdu])", m =>
        {
            int w = m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value) : 0;
            string s = m.Groups[2].Value == "x" ? code.ToString("x") : m.Groups[2].Value == "X" ? code.ToString("X") : code.ToString();
            return s.PadLeft(w, '0');
        });
    }

    private static string Clean(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }
}
