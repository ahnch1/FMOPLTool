using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Shell;

/// <summary>
/// hoot 목록 보기 (왼쪽 목록의 [hoot 열기]): xml 폴더(제작사마다 .xml 하나: xml · xml_new …)를 읽어 제작사 → 장비 → 게임 → 곡 트리로.
/// <para>제작사 = xml 파일 이름, 장비 = 게임 이름 앞의 [기계] (없으면 driveralias type), 게임 = 나머지 이름, 곡 = titlelist의 title (range = 효과음은 맨 끝에 한 줄).
/// 아래 단계는 펼칠 때 만든다 (게임 1만 개 · 곡 15만 줄). 찾기 칸: 게임 이름 · 제작사 · 아카이브에 든 글자로 거른다.</para>
/// <para>곡 두 번 클릭 / Enter: 아카이브 zip을 xml 폴더 위 폴더(hoot 폴더) 아래에서 찾아,
/// 흉내 낼 수 있는 항목(PC-98 DOS)이면 zip을 풀고 그 항목을 .hoot 파일로 써서 그 곡을 재생 (<see cref="SongActivated"/>),
/// 아니면 zip을 폴더 목록에서 연다 (<see cref="ZipRequested"/> — 안의 KSS · NSF · SPC · MDX … 는 그대로 재생).</para>
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class HootTree : UserControl
{
    private sealed class SongTag { public HootGame Game; public int Index; public string Code; }

    private readonly TreeView tree = new TreeView();
    private readonly TextBox search = new TextBox();
    private readonly Label status = new Label();
    private readonly ImageList icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private List<HootGame> games = new List<HootGame>();
    private Dictionary<string, string> zipIndex;
    private Task<Dictionary<string, string>> zipTask;
    private string listDir, playingHoot;
    private int loadId;

    /// <summary>곡 재생 요청 ("항목.hoot::n").</summary>
    public event Action<string> SongActivated;
    /// <summary>흉내 낼 수 없는 항목: 그 아카이브 zip을 폴더 목록에서 열어 달라.</summary>
    public event Action<string> ZipRequested;

    /// <summary>읽은 xml 폴더 (없으면 null).</summary>
    public string ListDir { get { return listDir; } }
    public string Summary { get; private set; } = "";
    public event Action SummaryChanged;

    public HootTree()
    {
        BackColor = Color.White;
        icons.Images.Add("maker", GlyphImages.Get(Glyphs.Folder, 16, Color.FromArgb(214, 160, 40)));
        icons.Images.Add("machine", GlyphImages.Get(Glyphs.Tracker, 16, Color.FromArgb(120, 130, 200)));
        icons.Images.Add("game", GlyphImages.Get(Glyphs.Jukebox, 16, ShellTheme.Accent));
        icons.Images.Add("song", GlyphImages.Get(Glyphs.Play, 16, ShellTheme.Muted));
        icons.Images.Add("se", GlyphImages.Get(Glyphs.Composer, 16, Color.FromArgb(180, 184, 190)));

        search.Dock = DockStyle.Top;
        search.Font = ShellTheme.Ui;
        search.PlaceholderText = "찾기: 게임 · 제작사 · 아카이브 (Enter)";
        search.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Rebuild(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { search.Text = ""; Rebuild(); e.Handled = e.SuppressKeyPress = true; }
        };

        status.Dock = DockStyle.Fill;
        status.Font = ShellTheme.Ui;
        status.ForeColor = ShellTheme.Muted;
        status.TextAlign = ContentAlignment.MiddleCenter;

        tree.Dock = DockStyle.Fill;
        tree.BorderStyle = BorderStyle.None;
        tree.Font = ShellTheme.Ui;
        tree.ImageList = icons;
        tree.HideSelection = false;
        tree.ShowNodeToolTips = true;
        tree.ShowLines = true;
        tree.BeforeExpand += (s, e) => Fill(e.Node);
        tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is SongTag) Activate(e.Node); };
        tree.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter && tree.SelectedNode != null)
            {
                if (tree.SelectedNode.Tag is SongTag) Activate(tree.SelectedNode);
                else tree.SelectedNode.Toggle();
                e.Handled = e.SuppressKeyPress = true;
            }
        };

        var menu = new ContextMenuStrip { Font = ShellTheme.Ui };
        menu.Items.Add("없는 파일 목록 · 폴더 구성 CSV로 저장…", null, delegate { ExportMissing(); });
        menu.Opening += (s, e) => menu.Items[0].Enabled = listDir != null && zipTask != null;
        tree.ContextMenuStrip = menu;

        Controls.Add(tree);
        Controls.Add(status);
        Controls.Add(search);
        ShowStatus("[hoot 열기]로 hoot 목록 폴더(xml · xml_new …)를 고르세요");
    }

    private void ShowStatus(string text)
    {
        status.Text = text;
        status.Visible = true;
        tree.Visible = false;
    }

    private void SetSummary(string s) { Summary = s; SummaryChanged?.Invoke(); }

    // ---------------------------------------------------------------- 읽기

    /// <summary>hoot 목록 폴더를 읽는다 (뒤에서, 수 초). 아카이브 zip 찾기 색인도 함께 (xml 폴더의 위 폴더 아래 *.zip).</summary>
    public void Open(string dir)
    {
        listDir = dir;
        int id = ++loadId;
        ShowStatus("hoot 목록 읽는 중…\n" + dir);
        SetSummary("hoot: " + Path.GetFileName(dir.TrimEnd('\\')) + " — 읽는 중…");
        string hootRoot = Path.GetDirectoryName(dir.TrimEnd('\\')) ?? dir;
        zipIndex = null;
        zipTask = Task.Run(() => IndexZips(hootRoot));
        var errors = new List<string>();
        var zips = zipTask;
        Task.Run(() => { var l = HootLists.ReadFolder(dir, null, 0, errors); zips.Wait(); return l; }).ContinueWith(t =>
        {
            if (IsDisposed || id != loadId) return;
            BeginInvoke(new Action(() =>
            {
                if (id != loadId) return;
                if (t.IsFaulted) { ShowStatus("읽지 못했습니다: " + t.Exception?.GetBaseException().Message); SetSummary("hoot: 읽기 실패"); return; }
                games = t.Result;
                zipIndex = zips.Result;
                if (errors.Count > 0) AppLog.Warn("hoot", "읽지 못한 목록 파일 " + errors.Count + "개", string.Join("\n", errors.Take(20)));
                AppLog.Info("hoot", "hoot 목록: " + dir, "제작사 " + games.Select(g => g.File).Distinct().Count() + " · 게임 " + games.Count
                    + " · 곡 파일(zip) 없음 " + games.Count(g => !Has(g)) + " (찾은 곳: " + hootRoot + " 아래)");
                Rebuild();
            }));
        });
    }

    private static Dictionary<string, string> IndexZips(string root)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
            {
                string k = Path.GetFileNameWithoutExtension(f);
                if (!d.ContainsKey(k)) d[k] = f;
            }
        }
        catch (Exception) { }
        return d;
    }

    /// <summary>빨간색: 목록에는 있지만 곡 파일(아카이브 zip)이 없는 것. 회색: 곡 파일은 있지만 이 기종을 아직 재생하지 못하는 것.</summary>
    private static readonly Color MissingColor = Color.FromArgb(200, 40, 40), NotYetColor = Color.FromArgb(150, 154, 162);

    /// <summary>게임 줄 · 곡 줄 색 (재생할 수 있으면 기본색).</summary>
    private Color GameColor(HootGame g) { return !Has(g) ? MissingColor : !Playable(g) ? NotYetColor : Color.Empty; }

    /// <summary>게임의 곡 파일(아카이브 zip)이 있는가 (곡 목록이 비어도 없음으로 본다).</summary>
    private bool Has(HootGame g)
    {
        return g.Titles.Count > 0 && g.Archive.Length > 0 && zipIndex != null && zipIndex.ContainsKey(g.Archive);
    }

    /// <summary>
    /// 묶음 줄 글자 · 색: "이름  (n)", 섞여 있으면 "(n · 재생 p · 없음 m)". 모두 없으면 빨간색, 재생할 수 있는 게 없으면 회색.
    /// </summary>
    private TreeNode GroupNode(string name, List<HootGame> list, string image)
    {
        int missing = list.Count(g => !Has(g)), playable = list.Count(g => Has(g) && Playable(g));
        string counts = list.Count.ToString();
        if (playable > 0 && playable < list.Count) counts += " · 재생 " + playable;
        if (missing > 0 && missing < list.Count) counts += " · 없음 " + missing;
        var n = new TreeNode(name + "  (" + counts + ")") { ImageKey = image, SelectedImageKey = image, Tag = list };
        var tip = new List<string>();
        if (missing == list.Count) { n.ForeColor = MissingColor; tip.Add("목록에는 있지만 곡 파일(zip)이 하나도 없음"); }
        else
        {
            if (playable == 0) n.ForeColor = NotYetColor;
            tip.Add("재생할 수 있는 게임 " + playable + "개, 아직 못 하는 게임 " + (list.Count - missing - playable) + "개 (회색)");
            if (missing > 0) tip.Add("곡 파일(zip)이 없는 게임 " + missing + "개 (빨간색)");
        }
        n.ToolTipText = string.Join("\n", tip);
        n.Nodes.Add(new TreeNode(""));   // 펼침 표시 (펼칠 때 채움)
        return n;
    }

    private static string MakerOf(HootGame g) { return Path.GetFileNameWithoutExtension(g.File); }
    private static string MachineOf(HootGame g) { return g.Platform.Length > 0 ? g.Platform : g.AliasType.Length > 0 ? g.AliasType : "(기계 모름)"; }

    private static string GameText(HootGame g)
    {
        string n = g.Name;
        if (g.Platform.Length > 0)
        {
            int close = n.IndexOf(']');
            if (n.TrimStart().StartsWith("[") && close > 0) n = n.Substring(close + 1).Trim();
        }
        return n.Length > 0 ? n : "(이름 없음)";
    }

    /// <summary>찾기 칸대로 맨 위(제작사) 단계를 다시 만든다.</summary>
    private void Rebuild()
    {
        string q = search.Text.Trim();
        var shown = q.Length == 0 ? games : games.Where(g =>
            g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || MakerOf(g).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || g.Archive.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || g.Alias.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        tree.BeginUpdate();
        tree.Nodes.Clear();
        foreach (var grp in shown.GroupBy(MakerOf, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var list = grp.ToList();
            var n = GroupNode(grp.Key, list, "maker");
            n.ToolTipText = grp.First().File + (n.ToolTipText.Length > 0 ? "\n" + n.ToolTipText : "");
            tree.Nodes.Add(n);
        }
        // 찾은 게 적으면 다 펼쳐 보인다
        if (q.Length > 0 && shown.Count <= 30) foreach (TreeNode n in tree.Nodes) { n.Expand(); foreach (TreeNode m in n.Nodes) m.Expand(); }
        tree.EndUpdate();
        tree.Visible = true;
        status.Visible = false;
        if (tree.Nodes.Count == 0) ShowStatus(games.Count == 0 ? "이 폴더에 hoot 목록(.xml)이 없습니다" : "찾는 게임이 없습니다");
        int makers = shown.Select(MakerOf).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int missingAll = shown.Count(g => !Has(g)), playableAll = shown.Count(g => Has(g) && Playable(g));
        SetSummary("hoot: " + Path.GetFileName(listDir.TrimEnd('\\')) + " — 제작사 " + makers + " · 게임 " + shown.Count + " (재생 " + playableAll + (missingAll > 0 ? " · 곡 없음 " + missingAll : "") + ")" + (q.Length > 0 ? " (찾기 '" + q + "')" : ""));
    }

    /// <summary>펼칠 때 아래 단계를 채운다: 제작사 → 장비, 장비 → 게임, 게임 → 곡.</summary>
    private void Fill(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null || node.Nodes[0].Text.Length != 0) return;
        tree.BeginUpdate();
        node.Nodes.Clear();
        if (node.Tag is List<HootGame> list && node.Parent == null)
        {
            foreach (var grp in list.GroupBy(MachineOf, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sub = grp.ToList();
                node.Nodes.Add(GroupNode(grp.Key, sub, "machine"));
            }
        }
        else if (node.Tag is List<HootGame> games2)
        {
            foreach (var g in games2.OrderBy(GameText, StringComparer.OrdinalIgnoreCase))
            {
                int songs = g.Titles.Count(t => !t.Code.StartsWith("range:"));
                var n = new TreeNode(GameText(g) + "  (" + songs + ")") { ImageKey = "game", SelectedImageKey = "game", Tag = g, ToolTipText = GameTip(g) };
                n.ForeColor = GameColor(g);
                if (g.Titles.Count > 0) n.Nodes.Add(new TreeNode(""));
                node.Nodes.Add(n);
            }
        }
        else if (node.Tag is HootGame g)
        {
            int i = 0;
            foreach (var (code, text) in g.Titles)
            {
                if (code.StartsWith("range:"))
                {
                    var r = new TreeNode("효과음 " + code.Substring(6) + "  " + text) { ImageKey = "se", SelectedImageKey = "se", ForeColor = ShellTheme.Muted, ToolTipText = "효과음 범위 (재생 목록에는 없음)" };
                    node.Nodes.Add(r);
                    continue;
                }
                var tag = new SongTag { Game = g, Index = i++, Code = code };
                var n = new TreeNode(code + "  " + text)
                {
                    ImageKey = "song", SelectedImageKey = "song", Tag = tag, ForeColor = GameColor(g),
                    ToolTipText = !Has(g) ? "곡 파일 없음: " + g.Archive + ".zip" : Playable(g) ? "두 번 클릭 / Enter: 재생" : "아직 재생하지 못하는 기종 (두 번 클릭: zip을 폴더 목록에서 열기)",
                };
                node.Nodes.Add(n);
            }
        }
        tree.EndUpdate();
    }

    private string GameTip(HootGame g)
    {
        string zip = g.Archive.Length == 0 ? "없음 (romlist archive 없음)" : g.Archive + ".zip" + (zipIndex != null && zipIndex.TryGetValue(g.Archive, out var zp) ? " — " + zp : " — 파일 없음");
        var l = new List<string> { g.Name, "드라이버: " + g.Driver + (g.DriverType.Length > 0 ? " / " + g.DriverType : ""), "아카이브: " + zip };
        if (g.Titles.Count == 0) l.Add("곡 목록(titlelist) 없음");
        if (g.Alias.Length > 0) l.Add("별칭: " + g.Alias + (g.AliasType.Length > 0 ? " (" + g.AliasType + ")" : ""));
        if (g.Options.Count > 0) l.Add("옵션: " + string.Join(", ", g.Options.Select(o => o.Key + "=" + o.Value)));
        l.Add(!Playable(g) ? "재생: 아직 못 함 — zip을 폴더 목록에서 열기"
            : g.Options.ContainsKey("midiout") ? "재생: " + (g.Driver.Equals("pcatdos", StringComparison.OrdinalIgnoreCase) ? "PC/AT" : "PC-98") + " DOS 흉내 → MIDI (MPU-401, Jukebox MIDI 설정으로)"
            : "재생: " + (g.Driver.Equals("pc98dos", StringComparison.OrdinalIgnoreCase) ? "PC-98 DOS 흉내" : HootArchive.IsPc98Generic(g.Driver) ? "PC-98 흉내 (DOS 없음)" : g.Driver.Equals("pcatdos", StringComparison.OrdinalIgnoreCase) ? "PC/AT DOS 흉내" : g.DriverType.ToUpperInvariant() + " 파일 (곡 코드 = 곡 번호)"));
        return string.Join("\n", l);
    }

    private static bool Playable(HootGame g)
    {
        return HootArchive.PlayableGame(g.Driver, g.DriverType, g.Options.ContainsKey("midiout"));
    }

    // ---------------------------------------------------------------- 없는 파일 CSV

    /// <summary>
    /// 오른쪽 클릭 [없는 파일 목록 · 폴더 구성 CSV로 저장]: 목록의 아카이브 zip 중 hoot 폴더에 없는 것 (<see cref="HootMissing"/>)을
    /// "…_missing.csv"(zip마다 한 줄: 들어갈 폴더 · 파일 · 게임)와 "…_folders.csv"(폴더 · 드라이버마다 있음 / 없음)로.
    /// </summary>
    public void ExportMissing()
    {
        if (listDir == null || zipTask == null) return;
        string hootRoot = Path.GetDirectoryName(listDir.TrimEnd('\\')) ?? listDir;
        string path;
        using (var dlg = new SaveFileDialog
        {
            Title = "없는 파일 목록 CSV 저장 (폴더 구성은 옆에 _folders.csv로)",
            Filter = "CSV (*.csv)|*.csv",
            FileName = "hoot_" + Path.GetFileName(listDir.TrimEnd('\\')) + "_missing.csv",
            InitialDirectory = hootRoot,
            OverwritePrompt = true,
        })
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        var old = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            var full = zipTask.Result;
            var rel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in full)
            {
                string d = Path.GetRelativePath(hootRoot, Path.GetDirectoryName(kv.Value) ?? hootRoot).Replace('\\', '/');
                rel[kv.Key] = d == "." ? "" : d;
            }
            var r = HootMissing.Check(listDir, hootRoot, games, rel);
            HootMissing.WriteCsv(r, path);
            AppLog.Info("hoot", "없는 파일 " + r.Missing.Count + "개 / zip " + r.Zips + "개 → CSV 저장: " + r.MissingCsv,
                "폴더 구성: " + r.FoldersCsv + "\n게임 " + r.Games + " · zip 있음 " + r.Present + " · archive 없는 항목 " + r.NoArchive + " (찾은 곳: " + hootRoot + " 아래)");
        }
        catch (Exception ex) { AppLog.Error("hoot", "CSV를 저장하지 못했습니다: " + path, ex); }
        finally { Cursor = old; }
    }

    // ---------------------------------------------------------------- 재생

    private string FindZip(string archive)
    {
        if (archive.Length == 0) return null;
        if (zipIndex == null && zipTask != null)
        {
            var old = Cursor;
            Cursor = Cursors.WaitCursor;
            try { zipIndex = zipTask.Result; } finally { Cursor = old; }
        }
        return zipIndex != null && zipIndex.TryGetValue(archive, out var p) ? p : null;
    }

    private void Activate(TreeNode node)
    {
        var s = (SongTag)node.Tag;
        var g = s.Game;
        string zip = FindZip(g.Archive);
        if (zip == null)
        {
            AppLog.Warn("hoot", "아카이브를 찾지 못했습니다: " + g.Archive + ".zip", "찾은 곳: " + (Path.GetDirectoryName(listDir.TrimEnd('\\')) ?? listDir) + " 아래 모든 폴더");
            return;
        }
        if (!Playable(g))
        {
            AppLog.Info("hoot", GameText(g) + ": " + g.Driver + " / " + g.DriverType + "는 hoot 항목으로 아직 재생하지 않습니다 — zip을 폴더 목록에서 엽니다");
            ZipRequested?.Invoke(zip);
            return;
        }
        string hoot;
        var old = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            string dir = ZipFolder.Extract(zip);
            string missing = HootArchive.MissingRom(dir, g.Raw);
            if (missing != null)
            {
                AppLog.Warn("hoot", GameText(g) + ": 곡 파일이 zip에 없습니다 — " + missing, "zip: " + zip + " (같은 이름의 아카이브를 다른 판이 쓰는 경우) · zip을 폴더 목록에서 엽니다");
                ZipRequested?.Invoke(zip);
                return;
            }
            hoot = HootArchive.WriteEntry(dir, g.Raw, g.Archive);
        }
        catch (Exception ex) { AppLog.Error("hoot", "zip을 열 수 없습니다: " + Path.GetFileName(zip), ex); return; }
        finally { Cursor = old; }
        playingHoot = hoot;
        SongActivated?.Invoke(hoot + "::" + s.Index);
    }

    /// <summary>다음 곡 목록 (Jukebox): 지금 재생하는 항목 하나 (곡이 여럿 든 파일이라 Jukebox가 곡마다 펼친다).</summary>
    public List<string> PlaylistPaths { get { return playingHoot != null ? new List<string> { playingHoot } : new List<string>(); } }

    /// <summary>재생 중인 곡 줄을 굵게 (트리에서 연 항목의 곡일 때).</summary>
    public void MarkCurrent(string path)
    {
        if (playingHoot == null || path == null) return;
        string file = AdlibComposer.Audio.ChipSongs.FilePart(path);
        if (!string.Equals(file, playingHoot, StringComparison.OrdinalIgnoreCase)) return;
        AdlibComposer.Audio.NezM3u.SplitTrack(path, out int n);
        var sel = tree.SelectedNode;
        var parent = sel?.Tag is SongTag ? sel.Parent : sel;
        if (parent == null || !(parent.Tag is HootGame)) return;
        tree.BeginUpdate();
        foreach (TreeNode c in parent.Nodes)
            if (c.Tag is SongTag st) c.ForeColor = st.Index == n ? ShellTheme.AccentDark : Color.Empty;   // 굵게는 TreeView가 글자를 자른다
        tree.EndUpdate();
    }
}
