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
/// Organizer 페이지 (TOOLS): 폴더의 ROL / IMS / SOP에서 같은 곡을 찾아 정리한다.
/// <code>
/// ┌ 폴더 [..........] [찾아보기] [x] 하위 폴더  [검사]  [x] 같은 곡만 보기   진행 ┐
/// ├ 파일 목록 (같은 곡 그룹끼리 묶음, 체크 = 정리 대상) │ 고른 파일 정보          ┤
/// └ [추천대로 체크] [체크 해제] [x] 시뮬레이션 [x] BNK · ISS도 함께 [삭제] [이동] ┘
/// </code>
/// 판정 · 점수 규칙은 <see cref="MusicDuplicateFinder"/>. 삭제는 휴지통으로, 시뮬레이션이면 할 일만 보여 준다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class OrganizerView : UserControl, IShellPage
{
    private readonly Func<AppSettings> settings;
    private readonly TextBox txtFolder = new TextBox();
    private readonly Button btnBrowse = new Button();
    private readonly CheckBox chkRecursive = new CheckBox();
    private readonly Button btnScan = new Button();
    private readonly CheckBox chkOnlyDup = new CheckBox();
    private readonly Label lblProgress = new Label();
    private readonly SplitContainer split = new SplitContainer();
    private readonly ListView list = new ListView();
    private readonly ListView details = new ListView();
    private readonly Label lblDetails = new Label();
    private readonly Button btnRecommend = new Button();
    private readonly Button btnClear = new Button();
    private readonly CheckBox chkSimulate = new CheckBox();
    private readonly CheckBox chkCompanions = new CheckBox();
    private readonly Button btnDelete = new Button();
    private readonly Button btnMove = new Button();
    private readonly Label lblSummary = new Label();
    private readonly ToolTip tips = new ToolTip();

    private List<MusicFileInfo> files = new List<MusicFileInfo>();
    private List<DuplicateGroup> groups = new List<DuplicateGroup>();
    private bool scanning;
    private bool filling;

    public OrganizerView(Func<AppSettings> settings)
    {
        this.settings = settings;
        BackColor = Color.White;
        Font = ShellTheme.Ui;
        BuildTop();
        BuildList();
        BuildBottom();
        Controls.Add(split);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);
        split.BringToFront();
        txtFolder.Text = settings().SongFolderPath;
        UpdateButtons();
    }

    public void OnShown() { }
    public void OnHidden() { }

    // ------------------------------------------------------------ 구성

    private readonly Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 44 };
    private readonly Panel bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 46 };

    private void BuildTop()
    {
        var lbl = new Label { Text = "폴더", AutoSize = true, Location = new Point(12, 14) };
        txtFolder.SetBounds(50, 10, 420, 24);
        txtFolder.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        btnBrowse.Text = "찾아보기...";
        btnBrowse.SetBounds(476, 9, 84, 26);
        btnBrowse.Click += delegate { Browse(); };
        chkRecursive.Text = "하위 폴더";
        chkRecursive.Checked = true;
        chkRecursive.SetBounds(570, 12, 86, 22);
        btnScan.Text = "검사";
        btnScan.SetBounds(660, 9, 72, 26);
        btnScan.Font = ShellTheme.UiSemibold;
        btnScan.Click += delegate { Scan(); };
        chkOnlyDup.Text = "같은 곡만 보기";
        chkOnlyDup.Checked = true;
        chkOnlyDup.SetBounds(746, 12, 120, 22);
        chkOnlyDup.CheckedChanged += delegate { FillList(); };
        lblProgress.AutoSize = false;
        lblProgress.ForeColor = ShellTheme.Muted;
        lblProgress.SetBounds(872, 14, 400, 18);
        tips.SetToolTip(btnScan, "ROL · IMS · SOP를 읽어 크기 · 내용(바이트) · 음표 · 제목 · 악기 순서로 같은 곡을 찾는다");
        tips.SetToolTip(chkOnlyDup, "끄면 같은 곡이 없는 파일도 '중복 없음' 묶음에 보인다");
        topPanel.Controls.AddRange(new Control[] { lbl, txtFolder, btnBrowse, chkRecursive, btnScan, chkOnlyDup, lblProgress });
    }

    private void BuildList()
    {
        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.SplitterWidth = 5;

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.CheckBoxes = true;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.ShowGroups = true;
        list.BorderStyle = BorderStyle.None;
        list.Columns.Add("파일", 160);
        list.Columns.Add("경로", 260);
        list.Columns.Add("형식", 46);
        list.Columns.Add("크기", 70, HorizontalAlignment.Right);
        list.Columns.Add("제목", 130);
        list.Columns.Add("음", 54, HorizontalAlignment.Right);
        list.Columns.Add("점수", 44, HorizontalAlignment.Right);
        list.Columns.Add("판정", 90);
        list.SelectedIndexChanged += delegate { ShowDetails(); };
        list.ItemChecked += delegate { if (!filling) UpdateSummary(); };
        split.Panel1.Controls.Add(list);

        lblDetails.Text = "파일 정보";
        lblDetails.Font = ShellTheme.UiSemibold;
        lblDetails.Dock = DockStyle.Top;
        lblDetails.Height = 28;
        lblDetails.Padding = new Padding(8, 6, 0, 0);
        details.Dock = DockStyle.Fill;
        details.View = View.Details;
        details.FullRowSelect = true;
        details.HeaderStyle = ColumnHeaderStyle.None;
        details.BorderStyle = BorderStyle.None;
        details.Columns.Add("항목", 110);
        details.Columns.Add("값", 320);
        details.ShowGroups = true;
        split.Panel2.Controls.Add(details);
        split.Panel2.Controls.Add(lblDetails);
        split.Resize += delegate { FitSplit(); };
    }

    private bool splitPlaced;

    private void FitSplit()
    {
        if (split.Width < 400) return;
        if (!splitPlaced) { split.SplitterDistance = Math.Max(300, split.Width * 64 / 100); splitPlaced = true; }
        details.Columns[1].Width = Math.Max(120, details.ClientSize.Width - details.Columns[0].Width - 4);
    }

    private void BuildBottom()
    {
        int x = 12, y = 10;
        foreach (var (b, text, w, tip, act) in new (Button, string, int, string, Action)[]
        {
            (btnRecommend, "추천대로 체크", 104, "각 그룹에서 남길 파일(점수가 가장 높은 것)만 빼고 모두 체크", (Action)CheckRecommended),
            (btnClear, "체크 해제", 76, "모든 체크를 푼다", (Action)ClearChecks),
        })
        {
            b.Text = text; b.SetBounds(x, y, w, 26); b.Click += delegate { act(); };
            tips.SetToolTip(b, tip);
            bottomPanel.Controls.Add(b);
            x += w + 6;
        }
        x += 10;
        chkSimulate.Text = "시뮬레이션 (실제로 바꾸지 않음)";
        chkSimulate.Checked = true;
        chkSimulate.SetBounds(x, y + 3, 210, 22);
        tips.SetToolTip(chkSimulate, "켜 두면 삭제 · 이동할 목록만 보여 주고 파일은 그대로 둔다");
        x += 214;
        chkCompanions.Text = "같은 이름 BNK · ISS도 함께";
        chkCompanions.Checked = true;
        chkCompanions.SetBounds(x, y + 3, 190, 22);
        tips.SetToolTip(chkCompanions, "곡과 같은 이름의 뱅크 · 가사 파일도 같이 지우거나 옮긴다 (같은 폴더에 같은 이름의 다른 곡이 남아 있으면 그대로 둔다)");
        x += 196;
        btnDelete.Text = "체크한 파일 삭제 (휴지통)";
        btnDelete.SetBounds(x, y, 170, 26);
        btnDelete.Click += delegate { Apply(false); };
        x += 176;
        btnMove.Text = "체크한 파일 이동...";
        btnMove.SetBounds(x, y, 130, 26);
        btnMove.Click += delegate { Apply(true); };
        x += 140;
        lblSummary.AutoSize = false;
        lblSummary.ForeColor = ShellTheme.Muted;
        lblSummary.SetBounds(x, y + 5, 360, 18);
        bottomPanel.Controls.AddRange(new Control[] { chkSimulate, chkCompanions, btnDelete, btnMove, lblSummary });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(ShellTheme.HeaderLine))
        {
            e.Graphics.DrawLine(p, 0, topPanel.Bottom - 1, Width, topPanel.Bottom - 1);
            e.Graphics.DrawLine(p, 0, bottomPanel.Top, Width, bottomPanel.Top);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        lblProgress.Width = Math.Max(50, Width - lblProgress.Left - 10);
        lblSummary.Width = Math.Max(50, Width - lblSummary.Left - 10);
        Invalidate();
    }

    // ------------------------------------------------------------ 검사

    private void Browse()
    {
        using (var dlg = new FolderBrowserDialog { Description = "정리할 곡 폴더 (ROL · IMS · SOP)", SelectedPath = Directory.Exists(txtFolder.Text) ? txtFolder.Text : settings().SongFolderPath })
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            txtFolder.Text = dlg.SelectedPath;
            Scan();
        }
    }

    private async void Scan()
    {
        if (scanning) return;
        string dir = txtFolder.Text.Trim();
        if (!Directory.Exists(dir)) { MessageBox.Show(FindForm(), "폴더가 없습니다: " + dir, "Organizer"); return; }
        scanning = true;
        UpdateButtons();
        bool recursive = chkRecursive.Checked;
        lblProgress.Text = "파일 찾는 중...";
        list.Items.Clear(); details.Items.Clear();
        try
        {
            var paths = await Task.Run(() => MusicDuplicateFinder.ListFiles(dir, recursive));
            var found = new List<MusicFileInfo>();
            var progress = new Progress<int>(k => lblProgress.Text = "읽는 중 " + k + " / " + paths.Count);
            IProgress<int> rep = progress;
            await Task.Run(() =>
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    found.Add(MusicDuplicateFinder.Analyze(paths[i]));
                    if (i % 20 == 0) rep.Report(i + 1);
                }
            });
            files = found;
            groups = MusicDuplicateFinder.FindGroups(files);
            int dupFiles = groups.Sum(g => g.Files.Count);
            int errors = files.Count(f => f.Error != null);
            lblProgress.Text = "파일 " + files.Count + "개 · 같은 곡 그룹 " + groups.Count + "개 (파일 " + dupFiles + "개)" + (errors > 0 ? " · 읽기 오류 " + errors + "개" : "");
            AppLog.Info("정리", "검사: " + dir + (recursive ? " (하위 폴더 포함)" : "") + " — " + lblProgress.Text);
            FillList();
        }
        catch (Exception ex)
        {
            lblProgress.Text = "검사 실패: " + ex.Message;
            AppLog.Error("정리", "검사 실패: " + dir, ex);
        }
        finally
        {
            scanning = false;
            UpdateButtons();
        }
    }

    private void FillList()
    {
        filling = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Groups.Clear();
            string root = txtFolder.Text.Trim();
            foreach (var g in groups)
            {
                var lg = new ListViewGroup("그룹 " + g.Id + "  ·  " + g.Files.Count + "개  ·  " + g.ReasonText + "  ·  남김: " + g.Keeper.FileName);
                list.Groups.Add(lg);
                foreach (var f in g.Files) list.Items.Add(MakeItem(f, lg, root));
            }
            if (!chkOnlyDup.Checked)
            {
                var single = files.Where(f => f.Group == null).ToList();
                if (single.Count > 0)
                {
                    var lg = new ListViewGroup("중복 없음  ·  " + single.Count + "개");
                    list.Groups.Add(lg);
                    foreach (var f in single) list.Items.Add(MakeItem(f, lg, root));
                }
            }
        }
        finally
        {
            list.EndUpdate();
            filling = false;
        }
        UpdateSummary();
    }

    private ListViewItem MakeItem(MusicFileInfo f, ListViewGroup lg, string root)
    {
        string folder = f.Folder;
        if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) folder = "." + folder.Substring(root.Length);
        var it = new ListViewItem(f.FileName, lg) { Tag = f };
        it.SubItems.Add(folder);
        it.SubItems.Add(f.Format);
        it.SubItems.Add(SizeText(f.Size));
        it.SubItems.Add(f.Title);
        it.SubItems.Add(f.Error != null ? "오류" : f.NoteCount.ToString());
        it.SubItems.Add(f.Score.ToString());
        it.SubItems.Add(f.Group == null ? "" : f.Keep ? "남김 (추천)" : "정리 후보");
        if (f.Group != null && f.Keep) { it.Font = new Font(list.Font, FontStyle.Bold); it.ForeColor = ShellTheme.AccentDark; }
        if (f.Error != null) it.ForeColor = Color.FromArgb(200, 60, 50);
        return it;
    }

    private static string SizeText(long n)
    {
        if (n < 10 * 1024) return n.ToString("N0") + " B";
        return (n / 1024.0).ToString("N1") + " KB";
    }

    // ------------------------------------------------------------ 오른쪽 정보

    private void ShowDetails()
    {
        details.BeginUpdate();
        details.Items.Clear();
        details.Groups.Clear();
        try
        {
            if (list.SelectedItems.Count == 0) { lblDetails.Text = "파일 정보"; return; }
            var f = (MusicFileInfo)list.SelectedItems[0].Tag;
            lblDetails.Text = f.FileName;
            var gFile = new ListViewGroup("파일");
            var gSong = new ListViewGroup("곡");
            var gJudge = new ListViewGroup("판정");
            details.Groups.AddRange(new[] { gFile, gSong, gJudge });
            Add(gFile, "경로", f.Path);
            Add(gFile, "형식", f.Format);
            Add(gFile, "크기", f.Size.ToString("N0") + " 바이트");
            Add(gFile, "수정한 날", f.Modified == default(DateTime) ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"));
            Add(gFile, "뱅크 (BNK)", f.HasBank ? Path.GetFileName(f.BankPath) : "없음");
            Add(gFile, "가사 (ISS)", f.HasLyrics ? Path.GetFileName(f.LyricsPath) : "없음");
            Add(gFile, "SHA-1", f.ContentHash);
            if (f.Error != null) Add(gFile, "읽기 오류", f.Error);
            Add(gSong, "제목", f.Title.Length > 0 ? f.Title : "(없음)");
            Add(gSong, "템포", f.Tempo.ToString("0.#") + " BPM · 박당 " + f.TickBeat + "틱");
            Add(gSong, "길이", TimeSpan.FromSeconds(Math.Max(0, f.Seconds)).ToString(@"m\:ss"));
            Add(gSong, "보이스 · 음", f.VoicesUsed + "개 · " + f.NoteCount + "개");
            Add(gSong, "악기 " + f.Instruments.Count + "개", string.Join(", ", f.Instruments));
            Add(gSong, "악기 순서", f.InstrumentKey.Replace("|", "  |  "));
            Add(gJudge, "점수", f.Score + "  (" + string.Join(", ", f.ScoreNotes) + ")");
            Add(gJudge, "이름", f.NumericName ? "숫자만 나열된 이름 (남길 순위 낮춤)" : "뜻 있는 이름");
            if (f.Group != null)
            {
                Add(gJudge, "그룹", "그룹 " + f.Group.Id + " · " + f.Group.ReasonText);
                Add(gJudge, "판정", f.Keep ? "남김 (추천)" : "정리 후보 — 남김: " + f.Group.Keeper.FileName);
                foreach (var o in f.Group.Files)
                    if (!ReferenceEquals(o, f)) Add(gJudge, "같은 곡", o.FileName + "  (" + o.Folder + ", 점수 " + o.Score + ")");
            }
            else Add(gJudge, "그룹", "같은 곡 없음");
        }
        finally { details.EndUpdate(); FitSplit(); }
    }

    private void Add(ListViewGroup g, string key, string value)
    {
        var it = new ListViewItem(key, g);
        it.SubItems.Add(value ?? "");
        it.ToolTipText = value;
        details.Items.Add(it);
    }

    // ------------------------------------------------------------ 체크 / 정리

    private void CheckRecommended()
    {
        filling = true;
        foreach (ListViewItem it in list.Items)
        {
            var f = (MusicFileInfo)it.Tag;
            it.Checked = f.Group != null && !f.Keep;
        }
        filling = false;
        UpdateSummary();
    }

    private void ClearChecks()
    {
        filling = true;
        foreach (ListViewItem it in list.Items) it.Checked = false;
        filling = false;
        UpdateSummary();
    }

    private List<MusicFileInfo> Checked()
    {
        var l = new List<MusicFileInfo>();
        foreach (ListViewItem it in list.CheckedItems) l.Add((MusicFileInfo)it.Tag);
        return l;
    }

    private void UpdateSummary()
    {
        var c = Checked();
        int wholeGroups = groups.Count(g => g.Files.All(f => c.Contains(f)));
        lblSummary.Text = "체크 " + c.Count + "개 (" + SizeText(c.Sum(f => f.Size)) + ")" + (wholeGroups > 0 ? " — 주의: 그룹 " + wholeGroups + "개는 모두 체크됨" : "");
        lblSummary.ForeColor = wholeGroups > 0 ? Color.FromArgb(200, 120, 0) : ShellTheme.Muted;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = list.CheckedItems.Count > 0;
        btnScan.Enabled = btnBrowse.Enabled = !scanning;
        btnDelete.Enabled = btnMove.Enabled = any && !scanning;
        btnRecommend.Enabled = btnClear.Enabled = groups.Count > 0 && !scanning;
        btnDelete.Text = chkSimulate.Checked ? "삭제 시뮬레이션" : "체크한 파일 삭제 (휴지통)";
        btnMove.Text = chkSimulate.Checked ? "이동 시뮬레이션..." : "체크한 파일 이동...";
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        chkSimulate.CheckedChanged += delegate { UpdateButtons(); };
    }

    /// <summary>체크한 파일 삭제(휴지통) / 이동. 시뮬레이션이면 할 일 목록만 보여 준다.</summary>
    private void Apply(bool move)
    {
        var targets = Checked();
        if (targets.Count == 0) return;
        bool simulate = chkSimulate.Checked;
        if (!ConfirmWholeGroups(targets, move)) return;
        if (targets.Count == 0) return;
        string dest = null;
        if (move)
        {
            using (var dlg = new FolderBrowserDialog { Description = "옮길 폴더", SelectedPath = txtFolder.Text })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                dest = dlg.SelectedPath;
            }
        }
        // 남는 곡 파일 (짝 BNK · ISS를 그 곡이 쓰는지 판단)
        var targetSet = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var remaining = files.Where(f => !targetSet.Contains(f.Path)).Select(f => f.Path).ToList();
        var ops = new List<KeyValuePair<string, string>>();   // (원본, 대상 또는 null = 삭제)
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in targets)
        {
            var paths = new List<string> { f.Path };
            if (chkCompanions.Checked) paths.AddRange(MusicDuplicateFinder.CompanionsToTake(f, remaining));
            foreach (var p in paths)
            {
                if (!taken.Add(p)) continue;
                ops.Add(new KeyValuePair<string, string>(p, move ? UniqueTarget(dest, Path.GetFileName(p), ops) : null));
            }
        }
        int wholeGroups = groups.Count(g => g.Files.All(f => targetSet.Contains(f.Path)));
        var plan = new List<string>();
        foreach (var op in ops) plan.Add(op.Value == null ? "삭제  " + op.Key : "이동  " + op.Key + "  →  " + op.Value);
        string head = (move ? "이동" : "삭제(휴지통)") + " " + ops.Count + "개 (곡 " + targets.Count + "개 + 짝 파일 " + (ops.Count - targets.Count) + "개)"
            + (wholeGroups > 0 ? "\r\n주의: 그룹 " + wholeGroups + "개는 남는 파일이 없습니다." : "");

        if (simulate)
        {
            AppLog.Info("정리", "시뮬레이션: " + head.Replace("\r\n", " "), string.Join("\n", plan));
            ShowPlan("시뮬레이션 — 실제로 바꾸지 않았습니다", head, plan);
            return;
        }
        if (MessageBox.Show(FindForm(), head + "\r\n\r\n" + string.Join("\r\n", plan.Take(15)) + (plan.Count > 15 ? "\r\n… 외 " + (plan.Count - 15) + "개" : "") + "\r\n\r\n진행할까요?",
                "Organizer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        int ok = 0;
        var failed = new List<string>();
        foreach (var op in ops)
        {
            try
            {
                if (op.Value == null)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(op.Key, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(op.Value));
                    File.Move(op.Key, op.Value);
                }
                ok++;
            }
            catch (Exception ex) { failed.Add(op.Key + ": " + ex.Message); }
        }
        AppLog.Info("정리", (move ? "이동" : "삭제") + " " + ok + "개" + (move ? " → " + dest : " (휴지통)"), string.Join("\n", plan));
        if (failed.Count > 0) AppLog.Warn("정리", "실패 " + failed.Count + "개", string.Join("\n", failed));
        MessageBox.Show(FindForm(), (move ? "옮김 " : "휴지통으로 ") + ok + "개" + (failed.Count > 0 ? "\r\n실패 " + failed.Count + "개:\r\n" + string.Join("\r\n", failed.Take(10)) : ""), "Organizer");
        // 남은 파일로 다시 판정
        files = files.Where(f => File.Exists(f.Path)).ToList();
        foreach (var f in files)
        {
            if (f.BankPath != null && !File.Exists(f.BankPath)) f.BankPath = null;
            if (f.LyricsPath != null && !File.Exists(f.LyricsPath)) f.LyricsPath = null;
        }
        groups = MusicDuplicateFinder.FindGroups(files);
        lblProgress.Text = "파일 " + files.Count + "개 · 같은 곡 그룹 " + groups.Count + "개";
        FillList();
    }

    /// <summary>
    /// 같은 곡 그룹의 파일이 모두 체크돼 있으면(정리하면 그 곡이 하나도 남지 않음) 먼저 묻는다.
    /// 예 = 각 그룹의 추천 파일(남김)을 대상에서 빼고 체크도 푼다 · 아니요 = 그대로 진행 · 취소 = 그만둔다(false).
    /// </summary>
    private bool ConfirmWholeGroups(List<MusicFileInfo> targets, bool move)
    {
        var set = new HashSet<MusicFileInfo>(targets);
        var whole = groups.Where(g => g.Files.Count > 0 && g.Files.All(set.Contains)).ToList();
        if (whole.Count == 0) return true;

        var lines = whole.Take(10).Select(g => "  그룹 " + g.Id + ": " + string.Join(", ", g.Files.Select(f => f.FileName))).ToList();
        if (whole.Count > 10) lines.Add("  … 외 " + (whole.Count - 10) + "개 그룹");
        string msg = "같은 곡 그룹 " + whole.Count + "개는 파일이 모두 체크되어 있어 " + (move ? "옮기면 이 폴더에" : "삭제하면")
            + " 그 곡이 하나도 남지 않습니다.\r\n\r\n" + string.Join("\r\n", lines)
            + "\r\n\r\n예 — 각 그룹에서 추천 파일(남김)은 빼고 진행"
            + "\r\n아니요 — 모두 그대로 " + (move ? "이동" : "삭제")
            + "\r\n취소 — 그만두기";
        var r = MessageBox.Show(FindForm(), msg, "Organizer — 그룹 전체가 " + (move ? "이동" : "삭제") + "됩니다",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.No)
        {
            AppLog.Warn("정리", "그룹 전체 " + (move ? "이동" : "삭제") + " 진행 선택: 그룹 " + string.Join(", ", whole.Select(g => g.Id)));
            return true;
        }
        var keepers = new HashSet<MusicFileInfo>(whole.Select(g => g.Keeper));
        targets.RemoveAll(keepers.Contains);
        filling = true;
        foreach (ListViewItem it in list.Items)
            if (keepers.Contains((MusicFileInfo)it.Tag)) it.Checked = false;
        filling = false;
        UpdateSummary();
        return true;
    }

    /// <summary>옮길 곳에 같은 이름이 있으면 "이름 (2).ROL" 식으로.</summary>
    private static string UniqueTarget(string dir, string name, List<KeyValuePair<string, string>> planned)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int k = 1; k < 1000; k++)
        {
            string cand = Path.Combine(dir, k == 1 ? name : stem + " (" + k + ")" + ext);
            if (!File.Exists(cand) && !planned.Any(p => string.Equals(p.Value, cand, StringComparison.OrdinalIgnoreCase))) return cand;
        }
        return Path.Combine(dir, name);
    }

    private void ShowPlan(string title, string head, List<string> plan)
    {
        using (var f = new Form())
        {
            DialogUtil.Setup(f, title, 760, 460);
            f.FormBorderStyle = FormBorderStyle.Sizable;
            var lbl = new Label { Text = head, Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0) };
            var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = ShellTheme.Console, Text = string.Join("\r\n", plan) };
            var ok = new Button { Text = "닫기", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
            f.Controls.Add(tb);
            f.Controls.Add(lbl);
            f.Controls.Add(ok);
            f.AcceptButton = ok;
            f.ShowDialog(FindForm());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
