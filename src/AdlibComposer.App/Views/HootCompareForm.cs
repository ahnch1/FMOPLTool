using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdlibComposer.App.Shell;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Views;

/// <summary>
/// hoot 게임 목록 비교 창: 묶음 폴더(xml · xml2 · xml3 …, 제작사마다 파일 하나)를 읽어 같은 게임 · 같은 판끼리 묶고 (<see cref="HootLists"/>),
/// 묶음마다 있는지 · 곡 수 · 무엇이 다른지를 한 줄에 보인다. 줄을 고르면 아래에 묶음별 XML과 곡 목록을 나란히.
/// [중복 없이 합치기]로 남긴 항목만 새 폴더(xml_new)에 쓴다 (+ hoot_new.xml).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class HootCompareForm : Form
{
    private readonly TextBox txtSets = new TextBox { ReadOnly = true };
    private readonly Button btnRoot = new Button(), btnAdd = new Button(), btnReload = new Button(), btnMerge = new Button();
    private readonly Label lblStatus = new Label();
    private readonly TextBox txtFind = new TextBox();
    private readonly ComboBox cmbView = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox cmbMaker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListView list = new ListView();
    private readonly TabControl tabs = new TabControl();
    private readonly TableLayoutPanel xmlPanel = new TableLayoutPanel();
    private readonly ListView titleList = new ListView();
    private readonly TextBox summary = new TextBox();
    private readonly ToolTip tips = new ToolTip();
    private readonly Timer findTimer = new Timer { Interval = 250 };

    private readonly List<string> setDirs = new List<string>();
    private string root;
    private List<HootCluster> clusters = new List<HootCluster>();
    private List<HootCluster> view = new List<HootCluster>();
    private List<string> setNames = new List<string>();
    private int sortColumn = -1;
    private bool sortDesc;
    private int loadGen;

    private const int ColGame = 0, ColMaker = 1, ColArchive = 2, ColDriver = 3, ColSets = 4;

    public HootCompareForm(string startFolder)
    {
        Text = "Hoot 게임 목록 비교";
        Size = new Size(1320, 860);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = ShellTheme.Ui;
        BackColor = Color.White;
        BuildTop();
        BuildBody();
        findTimer.Tick += delegate { findTimer.Stop(); ApplyFilter(); };
        Shown += delegate
        {
            if (!string.IsNullOrEmpty(startFolder) && DetectSets(startFolder).Count > 0) UseRoot(startFolder);
            else lblStatus.Text = "[묶음 폴더 고르기]로 hoot 폴더(xml · xml2 · xml3가 든 곳)를 고르세요";
        };
    }

    // ------------------------------------------------------------ 배치

    private static void Setup(Button b, string text, int width, string tip, ToolTip tips)
    {
        b.Text = text; b.Width = width; b.Height = 27; b.FlatStyle = FlatStyle.System; b.Margin = new Padding(0, 0, 6, 0);
        tips.SetToolTip(b, tip);
    }

    private void BuildTop()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 80, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 6, 10, 4), BackColor = Color.FromArgb(246, 247, 249) };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        row1.Controls.Add(new Label { Text = "묶음 (오래된 것 → 새 것)", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        txtSets.Width = 420; txtSets.Margin = new Padding(0, 2, 6, 0);
        row1.Controls.Add(txtSets);
        Setup(btnRoot, "묶음 폴더 고르기...", 130, "hoot 폴더를 고르면 그 안의 xml · xml2 · xml3 … (이름이 xml로 시작하고 .xml이 든 폴더, xml_new 같은 결과 폴더는 빼고)를 이름 순서로 읽는다. 뒤 묶음일수록 새 것(우선)", tips);
        Setup(btnAdd, "묶음 추가...", 90, "폴더 하나를 맨 뒤(가장 새 묶음)에 더한다", tips);
        Setup(btnReload, "다시 읽기", 80, "파일을 다시 읽어 비교", tips);
        btnRoot.Click += delegate { ChooseRoot(); };
        btnAdd.Click += delegate { AddSet(); };
        btnReload.Click += delegate { Reload(); };
        row1.Controls.AddRange(new Control[] { btnRoot, btnAdd, btnReload });
        lblStatus.AutoSize = true; lblStatus.ForeColor = ShellTheme.Muted; lblStatus.Margin = new Padding(8, 6, 0, 0);
        row1.Controls.Add(lblStatus);

        row2.Controls.Add(new Label { Text = "찾기", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        txtFind.Width = 220; txtFind.Margin = new Padding(0, 2, 12, 0);
        txtFind.TextChanged += delegate { findTimer.Stop(); findTimer.Start(); };
        tips.SetToolTip(txtFind, "게임 이름 · 제작사 파일 · 아카이브 · 드라이버에 든 글자 (여러 낱말은 모두 들어야 함)");
        row2.Controls.Add(txtFind);
        row2.Controls.Add(new Label { Text = "보기", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        cmbView.Items.AddRange(new object[] { "전체", "여러 묶음에 있음 (중복)", "한 묶음에만", "묶음마다 다름", "확인 권장 (약한 근거 · 비슷함)", "남김이 가장 새 묶음이 아님" });
        cmbView.SelectedIndex = 0; cmbView.Width = 210; cmbView.Margin = new Padding(0, 2, 12, 0);
        cmbView.SelectedIndexChanged += delegate { ApplyFilter(); };
        row2.Controls.Add(cmbView);
        row2.Controls.Add(new Label { Text = "제작사", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        cmbMaker.Width = 200; cmbMaker.Margin = new Padding(0, 2, 12, 0);
        cmbMaker.SelectedIndexChanged += delegate { ApplyFilter(); };
        row2.Controls.Add(cmbMaker);
        Setup(btnMerge, "중복 없이 합치기...", 140, "묶음마다 같은 항목은 하나만 남겨(남김 열) 새 폴더에 제작사 파일로 쓴다. 원래 글자 · 바이트 그대로, hoot.xsl · hoot.css · _report.txt 함께. hoot.xml이 있으면 목록을 새 폴더로 바꾼 hoot_new.xml도", tips);
        btnMerge.Font = ShellTheme.UiSemibold;
        btnMerge.Click += delegate { Merge(); };
        row2.Controls.Add(btnMerge);

        top.Controls.Add(row1, 0, 0);
        top.Controls.Add(row2, 0, 1);
        Controls.Add(top);
    }

    private void BuildBody()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 420, BackColor = ShellTheme.HeaderLine };
        Controls.Add(split);
        split.BringToFront();

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.VirtualMode = true;
        list.BorderStyle = BorderStyle.None;
        list.RetrieveVirtualItem += (s, e) => e.Item = MakeItem(view[e.ItemIndex]);
        list.SelectedIndexChanged += delegate { ShowDetail(); };
        list.ColumnClick += (s, e) => SortBy(e.Column);
        split.Panel1.Controls.Add(list);

        tabs.Dock = DockStyle.Fill;
        var tXml = new TabPage("XML (묶음별)");
        xmlPanel.Dock = DockStyle.Fill;
        tXml.Controls.Add(xmlPanel);
        var tTitles = new TabPage("곡 목록");
        titleList.Dock = DockStyle.Fill; titleList.View = View.Details; titleList.FullRowSelect = true; titleList.BorderStyle = BorderStyle.None;
        tTitles.Controls.Add(titleList);
        var tSum = new TabPage("요약");
        summary.Dock = DockStyle.Fill; summary.Multiline = true; summary.ReadOnly = true; summary.ScrollBars = ScrollBars.Both; summary.WordWrap = false;
        summary.Font = ShellTheme.Console; summary.BackColor = Color.White; summary.BorderStyle = BorderStyle.None;
        tSum.Controls.Add(summary);
        tabs.TabPages.AddRange(new[] { tXml, tTitles, tSum });
        split.Panel2.Controls.Add(tabs);
        Load += delegate { split.SplitterDistance = Math.Max(200, ClientSize.Height / 2 - 40); };
    }

    // ------------------------------------------------------------ 묶음 폴더

    /// <summary>폴더 안의 묶음: 이름이 xml로 시작하고 .xml 파일이 든 하위 폴더 (결과 폴더 xml_new 등 "_new"로 끝나는 것은 뺌), 이름 순서.</summary>
    private static List<string> DetectSets(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => Path.GetFileName(d).StartsWith("xml", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(d).EndsWith("_new", StringComparison.OrdinalIgnoreCase)
                    && Directory.GetFiles(d, "*.xml").Length > 0)
                .OrderBy(d => Path.GetFileName(d).Length).ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    private void ChooseRoot()
    {
        using (var dlg = new FolderBrowserDialog { Description = "hoot 폴더 (xml · xml2 · xml3 … 묶음 폴더가 든 곳)", UseDescriptionForTitle = true })
        {
            if (root != null) dlg.SelectedPath = root;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sets = DetectSets(dlg.SelectedPath);
            if (sets.Count == 0)
            {
                // 고른 폴더 자체가 묶음이면 그것 하나
                if (Directory.GetFiles(dlg.SelectedPath, "*.xml").Length > 0) { root = Path.GetDirectoryName(dlg.SelectedPath); setDirs.Clear(); setDirs.Add(dlg.SelectedPath); Reload(); return; }
                MessageBox.Show(this, "이 폴더에서 xml로 시작하는 묶음 폴더(.xml이 든)를 찾지 못했습니다.", Text);
                return;
            }
            UseRoot(dlg.SelectedPath);
        }
    }

    private void UseRoot(string dir)
    {
        root = dir;
        setDirs.Clear();
        setDirs.AddRange(DetectSets(dir));
        Reload();
    }

    private void AddSet()
    {
        using (var dlg = new FolderBrowserDialog { Description = "더할 묶음 폴더 (제작사 .xml 파일들이 든 곳) — 가장 새 묶음으로", UseDescriptionForTitle = true })
        {
            if (root != null) dlg.SelectedPath = root;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (setDirs.Any(d => string.Equals(d, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase))) return;
            root = root ?? Path.GetDirectoryName(dlg.SelectedPath);
            setDirs.Add(dlg.SelectedPath);
            Reload();
        }
    }

    private void Reload()
    {
        if (setDirs.Count == 0) return;
        txtSets.Text = string.Join("  →  ", setDirs.Select(d => Path.GetFileName(d.TrimEnd('\\'))));
        tips.SetToolTip(txtSets, string.Join("\n", setDirs));
        int gen = ++loadGen;
        lblStatus.Text = "읽는 중…";
        UseWaitCursor = true;
        var dirs = setDirs.ToList();
        Task.Run(() =>
        {
            var errs = new List<string>();
            var games = new List<HootGame>();
            for (int i = 0; i < dirs.Count; i++) games.AddRange(HootLists.ReadFolder(dirs[i], null, i + 1, errs));
            var cl = HootLists.Cluster(games);
            return (games, cl, errs);
        }).ContinueWith(t =>
        {
            if (IsDisposed || gen != loadGen) return;
            UseWaitCursor = false;
            if (t.IsFaulted) { lblStatus.Text = "읽지 못함: " + t.Exception.GetBaseException().Message; return; }
            var (games, cl, errs) = t.Result;
            clusters = cl;
            setNames = dirs.Select(d => Path.GetFileName(d.TrimEnd('\\'))).ToList();
            BuildColumns();
            cmbMaker.Items.Clear();
            cmbMaker.Items.Add("전체");
            cmbMaker.Items.AddRange(clusters.Select(c => c.OutFile).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
            cmbMaker.SelectedIndex = 0;
            int multi = clusters.Count(c => c.Members.Select(m => m.Set).Distinct().Count() > 1 || c.Members.Count > 1);
            lblStatus.Text = "항목 " + games.Count + "개 → 게임 · 판 " + clusters.Count + "개 (중복 " + (games.Count - clusters.Count) + "개 · 여러 번 나온 것 " + multi + "개 · 확인 권장 "
                + clusters.Count(c => c.Uncertain || c.Similar.Count > 0) + "개)" + (errs.Count > 0 ? " · 읽지 못한 파일 " + errs.Count : "");
            if (errs.Count > 0) AppLog.Warn("Hoot", "읽지 못한 목록 파일 " + errs.Count + "개", string.Join(Environment.NewLine, errs));
            AppLog.Info("Hoot", "목록 비교: " + string.Join(" · ", setNames) + " — " + lblStatus.Text);
            ApplyFilter();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void BuildColumns()
    {
        list.BeginUpdate();
        list.Columns.Clear();
        list.Columns.Add("게임 (남길 이름)", 380);
        list.Columns.Add("제작사 파일", 130);
        list.Columns.Add("아카이브", 110);
        list.Columns.Add("드라이버", 100);
        foreach (var s in setNames) list.Columns.Add(s, 70, HorizontalAlignment.Center);
        list.Columns.Add("남김", 150);
        list.Columns.Add("다른 점", 150);
        list.Columns.Add("비고", 120);
        list.EndUpdate();
    }

    // ------------------------------------------------------------ 목록

    private void ApplyFilter()
    {
        IEnumerable<HootCluster> q = clusters;
        var words = (txtFind.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
            q = q.Where(c => words.All(w => c.Members.Any(m => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                || c.OutFile.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 || c.Best.Archive.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
                || c.Best.Driver.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
        switch (cmbView.SelectedIndex)
        {
            case 1: q = q.Where(c => c.Members.Count > 1); break;
            case 2: q = q.Where(c => c.Members.Count == 1); break;
            case 3: q = q.Where(c => c.Members.Count > 1 && c.Differences.Length > 0); break;
            case 4: q = q.Where(c => c.Uncertain || c.Similar.Count > 0); break;
            case 5: q = q.Where(c => c.Members.Count > 1 && c.Members.Any(m => m.SetRank > c.Best.SetRank)); break;
        }
        if (cmbMaker.SelectedIndex > 0) { string mk = (string)cmbMaker.SelectedItem; q = q.Where(c => c.OutFile == mk); }
        view = q.ToList();
        if (sortColumn >= 0) view = Sorted(view);
        list.VirtualListSize = 0;
        list.VirtualListSize = view.Count;
        list.Invalidate();
        Text = "Hoot 게임 목록 비교 — " + view.Count + " / " + clusters.Count;
        if (view.Count > 0) { list.SelectedIndices.Clear(); list.SelectedIndices.Add(0); list.EnsureVisible(0); }
        else ClearDetail();
    }

    private string Cell(HootCluster c, int col)
    {
        switch (col)
        {
            case ColGame: return c.Best.Name;
            case ColMaker: return c.OutFile;
            case ColArchive: return c.Best.Archive;
            case ColDriver: return c.Best.Driver + (c.Best.DriverType.Length > 0 ? " / " + c.Best.DriverType : "");
        }
        int s = col - ColSets;
        if (s >= 0 && s < setNames.Count)
        {
            var ms = c.Members.Where(m => m.Set == setNames[s]).ToList();
            if (ms.Count == 0) return "";
            return (ms.Count > 1 ? "✓×" + ms.Count : "✓") + " " + ms.Max(m => m.Titles.Count) + "곡";
        }
        int rest = col - ColSets - setNames.Count;
        switch (rest)
        {
            case 0: return c.Best.Set + "/" + c.Best.File;
            case 1: return c.Differences;
            case 2: return c.Uncertain ? "확인 권장 (약한 근거)" : c.Similar.Count > 0 ? "비슷한 항목 " + c.Similar.Count : "";
        }
        return "";
    }

    private ListViewItem MakeItem(HootCluster c)
    {
        var it = new ListViewItem(Cell(c, 0));
        for (int col = 1; col < list.Columns.Count; col++) it.SubItems.Add(Cell(c, col));
        if (c.Uncertain || c.Similar.Count > 0) it.BackColor = Color.FromArgb(255, 246, 220);
        else if (c.Members.Count > 1) it.BackColor = Color.FromArgb(240, 248, 247);
        return it;
    }

    private void SortBy(int col)
    {
        if (sortColumn == col) sortDesc = !sortDesc; else { sortColumn = col; sortDesc = false; }
        view = Sorted(view);
        list.Invalidate();
    }

    private List<HootCluster> Sorted(List<HootCluster> l)
    {
        int col = sortColumn;
        Func<HootCluster, string> key = c => Cell(c, col);
        var o = sortDesc ? l.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : l.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        return o.ThenBy(c => c.Id).ToList();
    }

    // ------------------------------------------------------------ 자세히

    private void ClearDetail()
    {
        xmlPanel.Controls.Clear();
        titleList.Items.Clear();
        summary.Text = "";
    }

    private void ShowDetail()
    {
        if (list.SelectedIndices.Count == 0) { ClearDetail(); return; }
        var c = view[list.SelectedIndices[0]];
        var ms = c.Members.OrderBy(m => m.SetRank).ThenBy(m => m.File, StringComparer.OrdinalIgnoreCase).ToList();

        // XML 나란히
        xmlPanel.SuspendLayout();
        xmlPanel.Controls.Clear();
        xmlPanel.ColumnStyles.Clear();
        xmlPanel.RowStyles.Clear();
        xmlPanel.ColumnCount = ms.Count;
        xmlPanel.RowCount = 2;
        xmlPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        xmlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < ms.Count; i++)
        {
            var m = ms[i];
            xmlPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / ms.Count));
            var head = new Label
            {
                Text = m.Set + "/" + m.File + (m == c.Best ? "   ← 남김" : "") + "   (곡 " + m.Titles.Count + ")",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = m == c.Best ? ShellTheme.UiSemibold : ShellTheme.Ui,
                ForeColor = m == c.Best ? ShellTheme.AccentDark : ShellTheme.Muted, Padding = new Padding(4, 0, 0, 0),
            };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                Font = ShellTheme.Console, BackColor = m == c.Best ? Color.FromArgb(240, 250, 248) : Color.White,
                Text = m.Raw.Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("\t", "  "),
            };
            xmlPanel.Controls.Add(head, i, 0);
            xmlPanel.Controls.Add(box, i, 1);
        }
        xmlPanel.ResumeLayout();

        // 곡 목록: 코드 × 묶음
        titleList.BeginUpdate();
        titleList.Items.Clear();
        titleList.Columns.Clear();
        titleList.Columns.Add("코드", 90);
        foreach (var m in ms) titleList.Columns.Add(m.Set + "/" + m.File + (m == c.Best ? " ← 남김" : ""), Math.Max(200, 900 / Math.Max(1, ms.Count)));
        var codes = new List<string>();
        foreach (var m in ms) foreach (var t in m.Titles) if (!codes.Contains(t.Code)) codes.Add(t.Code);
        foreach (var code in codes)
        {
            var cells = ms.Select(m => m.Titles.Where(t => t.Code == code).Select(t => t.Text).FirstOrDefault()).ToList();
            var it = new ListViewItem(code);
            foreach (var cell in cells) it.SubItems.Add(cell ?? "(없음)");
            if (cells.Any(x => x == null)) it.BackColor = Color.FromArgb(255, 236, 230);
            else if (cells.Distinct().Count() > 1) it.BackColor = Color.FromArgb(255, 248, 220);
            titleList.Items.Add(it);
        }
        titleList.EndUpdate();

        // 요약
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("남김: " + c.Best.Set + "/" + c.Best.File + " → 합친 파일 " + c.OutFile + (c.Uncertain ? "   [근거가 약해 합침: 확인 권장]" : ""));
        sb.AppendLine("다른 점: " + (c.Differences.Length > 0 ? c.Differences : "없음"));
        sb.AppendLine();
        foreach (var m in ms)
        {
            sb.AppendLine((m == c.Best ? "★ " : "  ") + m.Set + "/" + m.File + " #" + (m.Index + 1));
            sb.AppendLine("    이름   : " + m.Name);
            sb.AppendLine("    풀이   : 기계 [" + m.Platform + "] · 제목 \"" + m.Base + "\" · 일본어 \"" + m.Japanese + "\" · 칩 \"" + m.ChipLabel + "\" → " + (m.Chip.Length > 0 ? m.Chip : "(없음)") + (m.Credit.Length > 0 ? " · 만든 이 \"" + m.Credit + "\"" : ""));
            sb.AppendLine("    드라이버: " + m.Driver + " / " + m.DriverType + (m.Alias.Length > 0 ? " · 별칭 " + m.Alias + " (" + m.AliasType + ")" : "") + " · 아카이브 " + m.Archive);
            sb.AppendLine("    옵션   : " + string.Join(", ", m.Options.OrderBy(o => o.Key).Select(o => o.Key + "=" + o.Value)));
            sb.AppendLine("    롬     : " + string.Join(", ", m.Roms.Select(r => r.Text)));
            sb.AppendLine("    곡     : " + m.Titles.Count + "개");
            sb.AppendLine();
        }
        if (c.Similar.Count > 0)
        {
            sb.AppendLine("비슷한 항목 (따로 남김):");
            foreach (var o in c.Similar) sb.AppendLine("    " + o.Best.Set + "/" + o.Best.File + ": " + o.Best.Name + " (곡 " + o.Best.Titles.Count + ")");
        }
        summary.Text = sb.ToString();
    }

    // ------------------------------------------------------------ 합치기

    private void Merge()
    {
        if (setDirs.Count == 0) { MessageBox.Show(this, "먼저 묶음 폴더를 고르세요.", Text); return; }
        string baseDir = root ?? Path.GetDirectoryName(setDirs[0]);
        string outDir;
        using (var dlg = new SaveFileDialog { Title = "합친 목록을 쓸 폴더 (이름)", FileName = "xml_new", InitialDirectory = baseDir, Filter = "폴더|*.", CheckFileExists = false, OverwritePrompt = false })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            outDir = Path.Combine(Path.GetDirectoryName(dlg.FileName), Path.GetFileNameWithoutExtension(dlg.FileName));
        }
        if (setDirs.Any(d => string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(outDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "읽는 묶음 폴더에는 쓸 수 없습니다. 다른 이름을 고르세요.", Text);
            return;
        }
        if (Directory.Exists(outDir) && Directory.GetFiles(outDir, "*.xml").Length > 0
            && MessageBox.Show(this, "'" + Path.GetFileName(outDir) + "' 폴더의 .xml 파일을 지우고 새로 씁니다. 계속할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        string hootXml = Path.Combine(baseDir, "hoot.xml");
        bool writeHoot = File.Exists(hootXml)
            && MessageBox.Show(this, "hoot.xml의 목록을 새 폴더로 바꾼 hoot_new.xml도 만들까요? (hoot.xml은 그대로 둡니다)", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        UseWaitCursor = true;
        lblStatus.Text = "합치는 중…";
        var dirs = setDirs.ToList();
        Task.Run(() => HootLists.Merge(dirs, outDir, writeHoot ? hootXml : null)).ContinueWith(t =>
        {
            if (IsDisposed) return;
            UseWaitCursor = false;
            if (t.IsFaulted) { lblStatus.Text = "합치지 못함"; AppLog.Error("Hoot", "합치지 못했습니다", t.Exception.GetBaseException()); MessageBox.Show(this, t.Exception.GetBaseException().Message, Text); return; }
            var r = t.Result;
            string msg = "항목 " + r.Entries + "개 → " + r.Clusters + "개 (뺀 중복 " + r.Dropped + "개), 제작사 파일 " + r.Files + "개\n"
                + "확인 권장 " + r.Uncertain + "개 · 비슷하지만 따로 둔 쌍 " + r.SimilarPairs + "개\n\n" + outDir + (r.HootXmlPath.Length > 0 ? "\n" + r.HootXmlPath : "") + "\n\n보고서(_report.txt)를 열까요?";
            lblStatus.Text = "합침: " + r.Clusters + "개 → " + outDir;
            AppLog.Info("Hoot", "중복 없이 합침: " + r.Entries + " → " + r.Clusters + "개, 파일 " + r.Files + "개 → " + outDir, r.HootXmlPath.Length > 0 ? "hoot_new.xml: " + r.HootXmlPath : null);
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.ReportPath) { UseShellExecute = true }); } catch (Exception) { }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tips.Dispose(); findTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
