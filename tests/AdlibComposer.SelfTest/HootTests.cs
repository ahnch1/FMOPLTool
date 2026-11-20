using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 게임 목록 비교 · 합치기 (HootLists): 같은 항목 판단, 남길 것, 바이트 보존, 주석, hoot_new.xml.</summary>
public static class HootTests
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"Shift_JIS\"?>\r\n<?xml-stylesheet type=\"text/xsl\" href=\"hoot.xsl\"?>\r\n";

    private static string Game(string name, string driver, string type, string archive, int titles, string opts = "", int first = 0x10)
    {
        var sb = new StringBuilder();
        sb.Append("<game>\r\n\t\t<name>" + name + "</name>\r\n\t\t<driver type=\"" + type + "\">" + driver + "</driver>\r\n");
        if (opts.Length > 0) sb.Append("\t\t<options>\r\n" + opts + "\t\t</options>\r\n");
        sb.Append("\t\t<romlist archive=\"" + archive + "\">\r\n\t\t\t<rom type=\"file\" offset=\"0\">A.BIN</rom>\r\n\t\t</romlist>\r\n");
        sb.Append("\t\t<titlelist>\r\n");
        for (int i = 0; i < titles; i++) sb.Append("\t\t\t<title code=\"0x" + (first + i).ToString("x2") + "\">Song " + (i + 1) + "</title>\r\n");
        sb.Append("\t\t</titlelist>\r\n\t</game>");
        return sb.ToString();
    }

    private static string Opt(string n, string v) { return "\t\t\t<option name=\"" + n + "\" value=\"" + v + "\"/>\r\n"; }

    private static void WriteList(string dir, string file, string body, Encoding enc)
    {
        Directory.CreateDirectory(dir);
        var bytes = enc.GetBytes(Header + "<gamelist date=\"2005/09/25\">\r\n\t" + body + "\r\n</gamelist>\r\n");
        File.WriteAllBytes(Path.Combine(dir, file), bytes);
    }

    public static void Run(Action<string, bool, string> check)
    {
        var sjis = HootLists.ShiftJis;
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hoot_test");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string a = Path.Combine(root, "xml"), b = Path.Combine(root, "xml3"), outDir = Path.Combine(root, "xml_new");

            // 옛 묶음: 영문 이름 + OPN 식 칩 이름, 같은 파일 안 OPLL / PSG 두 판, 주석으로 막은 게임, 게임을 넘는 주석
            string oldList = string.Join("\r\n\t", new[]
            {
                Game("[PC-9801] Foo Quest (OPN)", "pc98dos", "opn", "foo_98", 3, Opt("clockmul", "8")),
                Game("[PC-9801] Foo Quest (OPNA)", "pc98dos", "opna", "foo_98", 3, Opt("clockmul", "8")),
                Game("[MSX] Bar Story (OPLL)", "msx", "mgs", "bar_msx", 4),
                Game("[MSX] Bar Story (PSG)", "msx", "mgs", "bar_msx", 4),
                Game("[PC-9801] Baz Adventure (OPN)", "pc98dos", "opn", "baz_98", 5),
                Game("[PC-9801] Old Only (OPN)", "pc98dos", "opn", "old_98", 2),
                "<!-- <game><name>Disabled</name><driver>x</driver></game> -->",
                Game("[X68000] Spanning (OPM+MSM)", "x68k", "mxdrv", "span_68", 2).Replace("\t\t</titlelist>", "<!-- note </game> inside -->\r\n\t\t</titlelist>"),
            });
            WriteList(a, "maker.xml", oldList, sjis);

            // 새 묶음: 일본어 + YM 칩 이름, 조정 옵션(fm_mix) 추가, 확장 Shift_JIS 글자(①), 다른 철자의 제작사 파일, 일본어만 있는 이름
            string newList = string.Join("\r\n\t", new[]
            {
                Game("[PC-9801] Foo Quest (フークエスト) (YM2203)", "pc98dos", "opn", "foo_98", 3, Opt("clockmul", "8") + Opt("fm_mix", "0")).Replace("Song 1", "Song ①"),
                Game("[PC-9801] Foo Quest (フークエスト) (YM2608)", "pc98dos", "opna", "foo_98", 3, Opt("clockmul", "10")),
                Game("[MSX] Bar Story (バーストーリー) (YM2413)", "msx", "mgs", "bar_msx", 4),
                Game("[MSX] Bar Story (バーストーリー) (AY-3-8910)", "msx", "mgs", "bar_msx", 4),
                Game("[PC-9801] New Only (ニュー) (YM2608)", "pc98dos", "opna", "new_98", 7),
            });
            WriteList(b, "maker.xml", newList, sjis);
            WriteList(b, "ma_ker.xml", Game("[PC-9801] バズアドベンチャー(OPN)", "pc98dos", "opn", "baz_98", 5), sjis);

            // ---- 읽기
            var errs = new List<string>();
            var ga = HootLists.ReadFolder(a, null, 1, errs);
            var gb = HootLists.ReadFolder(b, null, 2, errs);
            check("Hoot: read lists (commented-out game skipped, spanning comment kept in block)",
                errs.Count == 0 && ga.Count == 7 && gb.Count == 6 && ga.Any(g => g.Name.StartsWith("[X68000] Spanning") && g.Titles.Count == 2 && g.Raw.Contains("note </game> inside")),
                ga.Count + " / " + gb.Count + " " + string.Join(",", errs));
            var foo = gb.First(g => g.Name.Contains("YM2203"));
            check("Hoot: name parsing (platform · Japanese · chip alias)", foo.Platform == "PC-9801" && foo.Japanese == "フークエスト" && foo.Chip == "YM2203"
                && ga.First(g => g.Name.Contains("(OPN)")).Chip == "YM2203" && ga.First(g => g.Name.Contains("Spanning")).Chip == "MSM6258+YM2151", foo.Chip);

            // ---- 묶기
            var cl = HootLists.Cluster(ga.Concat(gb));
            HootCluster Of(Func<HootGame, bool> f) { return cl.FirstOrDefault(c => c.Members.Any(f)); }
            var fooOpn = Of(g => g.Name == "[PC-9801] Foo Quest (OPN)");
            var fooOpna = Of(g => g.Name == "[PC-9801] Foo Quest (OPNA)");
            check("Hoot: same game across sets merged despite tuning options; OPN / OPNA stay apart",
                fooOpn != null && fooOpn.Members.Count == 2 && fooOpna.Members.Count == 2 && fooOpn != fooOpna && fooOpn.Best.Set == "xml3", fooOpn?.Members.Count + " " + fooOpn?.Best);
            var opll = Of(g => g.Name == "[MSX] Bar Story (OPLL)");
            var psg = Of(g => g.Name == "[MSX] Bar Story (PSG)");
            check("Hoot: same-file variants (OPLL / PSG) never merged, each paired with its own", opll != psg && opll.Members.Any(m => m.Chip == "YM2413" && m.Set == "xml3") && psg.Members.Any(m => m.Chip == "AY-3-8910" && m.Set == "xml3"), "");
            var baz = Of(g => g.Name.StartsWith("[PC-9801] Baz"));
            check("Hoot: Japanese-only name matched by song codes", baz.Members.Count == 2 && baz.Members.Any(m => m.File == "ma_ker.xml"), baz.Members.Count.ToString());
            check("Hoot: one-set games kept", Of(g => g.Name.Contains("Old Only")).Members.Count == 1 && Of(g => g.Name.Contains("New Only")).Members.Count == 1, "");
            check("Hoot: cluster count", cl.Count == 8, cl.Count.ToString());

            // ---- 합치기
            File.WriteAllText(Path.Combine(root, "hoot.xml"), Header + "<gamelist>\r\n\t<childlists>\r\n\t\t<list>xml3/maker.xml</list>\r\n\t\t<list>xml3/ma_ker.xml</list>\r\n\t\t<list>xml/maker.xml</list>\r\n\t</childlists>\r\n</gamelist>\r\n");
            var r = HootLists.Merge(new[] { a, b }, outDir, Path.Combine(root, "hoot.xml"));
            var outFiles = Directory.GetFiles(outDir, "*.xml").Select(Path.GetFileName).ToList();
            byte[] merged = File.ReadAllBytes(Path.Combine(outDir, "maker.xml"));
            string mt = sjis.GetString(merged);
            int games = HootLists.GameBlocksAt(mt).Count;
            check("Hoot merge: one file per maker (maker / ma_ker joined), no duplicates", r.Clusters == 8 && r.Dropped == 5 && outFiles.SequenceEqual(new[] { "maker.xml" }) && games == 8,
                r.Clusters + " clusters, dropped " + r.Dropped + ", files " + string.Join(",", outFiles) + ", games " + games);
            byte[] circled = sjis.GetBytes("①");
            bool hasCircled = Enumerable.Range(0, merged.Length - 1).Any(i => merged[i] == circled[0] && merged[i + 1] == circled[1]);
            check("Hoot merge: kept entries are byte-exact (extended Shift_JIS ①), XML header kept", hasCircled && mt.StartsWith("<?xml version=\"1.0\" encoding=\"Shift_JIS\"?>") && mt.Contains("<gamelist date=\"2005/09/25\">"), "");
            var xdoc = System.Xml.Linq.XDocument.Parse(mt);
            check("Hoot merge: output is well-formed XML", xdoc.Root.Elements("game").Count() == 8, "");
            string hn = File.Exists(r.HootXmlPath) ? sjis.GetString(File.ReadAllBytes(r.HootXmlPath)) : "";
            check("Hoot merge: hoot_new.xml lists only xml_new files", hn.Contains("<list>xml_new/maker.xml</list>") && !hn.Contains("<list>xml3/") && !hn.Contains("<list>xml/"), "");
            check("Hoot merge: report written", File.Exists(r.ReportPath) && File.ReadAllText(r.ReportPath).Contains("뺀 중복"), "");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        RunMissing(check);
    }

    /// <summary>없는 zip 목록 · 폴더 구성 CSV (HootMissing): 있는 zip에서 폴더를 배우고, 쉼표 archive는 zip마다, 기본 표로 나머지.</summary>
    private static void RunMissing(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hoot_missing_test");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string list = Path.Combine(root, "xml");
            WriteList(list, "maker.xml", string.Join("\r\n\t", new[]
            {
                Game("[PC-9801] Have (OPN)", "pc98dos", "opn", "have_98", 2),
                Game("[PC-9801] Lost, \"Quoted\" (OPN)", "pc98dos", "opn", "lost_98", 3),
                Game("[MSX] Two Zips (OPLL)", "msx", "kss", "two_msx,fmpac_msx", 1),
                Game("[X68000] Default (OPM)", "x68k", "generic", "def_68", 1),
                Game("[Arcade] Board (YM2151)", "cps1", "capcom", "board", 1),
            }), HootLists.ShiftJis);
            Directory.CreateDirectory(Path.Combine(root, "pc98_mine"));
            File.WriteAllBytes(Path.Combine(root, "pc98_mine", "have_98.zip"), new byte[] { 0x50, 0x4B, 5, 6 });
            Directory.CreateDirectory(Path.Combine(root, "msx"));
            File.WriteAllBytes(Path.Combine(root, "msx", "fmpac_msx.zip"), new byte[] { 0x50, 0x4B, 5, 6 });
            File.WriteAllText(Path.Combine(root, "hoot.ini"), "[config]\r\ndata_dir=pc98_mine;msx;X68k;roms;\r\n");

            var r = HootMissing.Check(list);
            string Where(string zip) { var m = r.Missing.FirstOrDefault(x => x.Zip == zip); return m == null ? "(있음)" : m.RelPath; }
            check("Hoot missing: counts (comma archive = two zips)", r.Games == 5 && r.Zips == 6 && r.Present == 2 && r.Missing.Count == 4, r.Zips + "/" + r.Present + "/" + r.Missing.Count);
            check("Hoot missing: folder learned from existing zips", Where("lost_98.zip") == "pc98_mine/lost_98.zip", Where("lost_98.zip"));
            check("Hoot missing: default folder keeps data_dir spelling, arcade → roms", Where("def_68.zip") == "X68k/def_68.zip" && Where("board.zip") == "roms/board.zip" && Where("two_msx.zip") == "msx/two_msx.zip",
                Where("def_68.zip") + " " + Where("board.zip") + " " + Where("two_msx.zip"));

            string csv = Path.Combine(root, "out_missing.csv");
            HootMissing.WriteCsv(r, csv);
            var b = File.ReadAllBytes(csv);
            string text = File.ReadAllText(csv);
            string folders = File.ReadAllText(Path.Combine(root, "out_folders.csv"));
            check("Hoot missing: CSV is UTF-8 BOM, quoted cells", b[0] == 0xEF && text.Contains("\"[PC-9801] Lost, \"\"Quoted\"\" (OPN)\"") && text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 5, "");
            check("Hoot missing: folders CSV has per-folder rows and total", folders.Contains("pc98_mine,pc98dos,opn,2,1,1") && folders.Contains("(전체),,,6,2,4"), folders.Replace("\r\n", " / "));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
