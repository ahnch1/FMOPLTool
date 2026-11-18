using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Shell;

/// <summary>곡 파일 종류 (어느 페이지가 여는지 정할 때).</summary>
internal enum MusicFileKind { None, Adlib, Tracker, Midi, Vgm, Audio }

/// <summary>
/// 왼쪽 공용 폴더 / 곡 목록 (모든 페이지가 같이 쓴다).
/// [폴더 열기]로 고른 폴더(= 뿌리)와 그 아래 폴더만 오간다: 목록 = (위 폴더 ..) · 하위 폴더 · 곡 파일.
/// 폴더 두 번 클릭 = 들어가기, 곡 두 번 클릭 / Enter = FileActivated (지금 페이지가 열거나, 그 형식을 여는 페이지로).
/// 곡이 많으면 세로 스크롤바 + 아래 ◀ ▶ 버튼으로 한 쪽(보이는 줄 수)씩 넘긴다. 연 폴더는 설정(BrowserFolder)에 남겨 다음에 다시 연다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class FileBrowser : UserControl
{
    private static readonly string[] AdlibExt = { ".rol", ".ims", ".sop", ".mus" };
    private static readonly string[] TrackerExt = { ".s3m", ".stm", ".mod", ".nst" };
    private static readonly string[] MidiExt = MidiFiles.Extensions;   // .mid · .midi · .wrk · .xmi · .mdi
    private static readonly string[] VgmExt = AdlibComposer.Audio.ChipSongs.Extensions.ToArray();
    private static readonly string[] AudioExt = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    /// <summary>목록 한 줄: 위 폴더(..) · 폴더 · 곡 파일 · 파일 안 곡 (KSS · NSF · HES, Path = "파일::n").</summary>
    private sealed class Entry
    {
        public string Path;
        public bool IsDir, IsUp;
        /// <summary>곡이 여럿 든 파일 (펼치면 곡마다 줄).</summary>
        public bool Multi;
        /// <summary>파일 안 곡 한 줄 (Path = "파일::n").</summary>
        public bool IsTrack;
        /// <summary>ZIP 파일 (IsDir과 함께: 두 번 클릭하면 폴더처럼 들어간다).</summary>
        public bool IsZip;
    }

    /// <summary>
    /// 들어가 있는 ZIP (바깥 → 안쪽 순서): zip 파일 경로와 그것을 보는 폴더. zip 안의 zip도 들어갈 수 있다.
    /// 풀지 않고 보는 zip은 Cache = zip 경로 자체 (목록 · 재생 = "zip\안 경로"), 푼 zip은 임시 폴더 (경로 표시 · 위로 가기만 zip 기준으로 바꾼다).
    /// </summary>
    private readonly List<(string Zip, string Cache)> zips = new List<(string, string)>();

    /// <summary>펼쳐 둔 파일 (곡이 여럿 든 파일: 곡마다 줄). 폴더를 다시 그려도 그대로.</summary>
    private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Workspace ws;
    private readonly Func<AppSettings> settings;
    private readonly ListView list = new ListView();
    private readonly Label pathLabel = new Label();
    /// <summary>찾기 칸: 파일 이름 · 곡 제목 (Enter = 지금 폴더와 아래 폴더에서 찾기, Esc = 지우고 폴더로).</summary>
    private readonly TextBox searchBox = new TextBox();
    private bool searchMode;
    private int searchGeneration;
    private readonly Label emptyLabel = new Label();
    private readonly Button openButton = new Button(), upButton = new Button(), hootButton = new Button();
    /// <summary>hoot 목록 트리 (hoot 모드에서 목록 대신 보인다).</summary>
    private readonly HootTree hoot = new HootTree { Dock = DockStyle.Fill, Visible = false };
    private bool hootMode;
    private readonly Button prevPage = new Button(), nextPage = new Button();
    private readonly Label pageLabel = new Label();
    private readonly ImageList icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ToolTip tips = new ToolTip();
    private readonly Timer pageTimer = new Timer { Interval = 200 };
    private string root, current, currentPath;
    private int songCount;
    private int lastTop = -1;

    /// <summary>목록의 곡을 두 번 클릭 / Enter.</summary>
    public event Action<string> FileActivated;

    public FileBrowser(Workspace ws, Func<AppSettings> settings)
    {
        this.ws = ws;
        this.settings = settings;
        BackColor = Color.White;

        icons.Images.Add("dir", GlyphImages.Get(Glyphs.Folder, 16, Color.FromArgb(214, 160, 40)));
        icons.Images.Add("up", GlyphImages.Get('', 16, ShellTheme.Muted));   // Up
        icons.Images.Add("song", GlyphImages.Get(Glyphs.Composer, 16, ShellTheme.Accent));
        icons.Images.Add("multi", GlyphImages.Get(Glyphs.Jukebox, 16, ShellTheme.Accent));
        icons.Images.Add("track", GlyphImages.Get(Glyphs.Play, 16, ShellTheme.Muted));
        icons.Images.Add("zip", GlyphImages.Get(Glyphs.Folder, 16, Color.FromArgb(120, 130, 200)));

        // 위: [폴더 열기] [위로]
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 5, 6, 0), BackColor = Color.FromArgb(246, 247, 249), WrapContents = false };
        SetupButton(openButton, " 폴더 열기", Glyphs.Open, 104, "곡 폴더 열기 (폴더를 끌어다 놓아도 된다) — 그 폴더와 아래 폴더만 오간다");
        SetupButton(upButton, " 위로", '', 64, "위 폴더로 (연 폴더 위로는 가지 않는다) · Backspace");
        SetupButton(hootButton, " hoot", Glyphs.Jukebox, 70, "hoot 목록 열기: hoot 게임 목록 폴더(xml · xml_new …, 제작사마다 .xml)를 골라 제작사 → 장비 → 게임 → 곡 트리로 본다\n곡 두 번 클릭 = 재생 (PC-98 DOS 항목) / 그 밖의 기종은 아카이브 zip을 폴더 목록에서 연다\n이미 읽었으면 트리로 돌아가고, Shift+클릭은 폴더를 다시 고른다");
        openButton.Click += delegate { BrowseFolder(); };
        upButton.Click += delegate { GoUp(); };
        hootButton.Click += delegate { OpenHoot((ModifierKeys & Keys.Shift) != 0); };
        bar.Controls.Add(openButton);
        bar.Controls.Add(upButton);
        bar.Controls.Add(hootButton);

        hoot.SongActivated += p => FileActivated?.Invoke(p);
        hoot.ZipRequested += z => { LeaveHoot(); OpenZip(z); };
        hoot.SummaryChanged += delegate { if (hootMode) pathLabel.Text = hoot.Summary; };

        pathLabel.Dock = DockStyle.Top;
        pathLabel.Height = 24;
        pathLabel.Font = ShellTheme.Ui;
        pathLabel.ForeColor = ShellTheme.Muted;
        pathLabel.AutoEllipsis = true;
        pathLabel.Padding = new Padding(8, 4, 4, 0);

        searchBox.Dock = DockStyle.Top;
        searchBox.Font = ShellTheme.Ui;
        searchBox.PlaceholderText = "찾기: 파일 이름 · 제목 (Enter, 아래 폴더까지)";
        searchBox.Visible = false;
        searchBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { StartSearch(searchBox.Text); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { ClearSearch(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                list.Focus();
                if (list.SelectedItems.Count == 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        tips.SetToolTip(searchBox, "파일 이름이나 곡 안의 제목(파일 안 곡 제목 포함)에 낱말이 모두 든 곡을 지금 폴더와 그 아래 폴더에서 찾는다 (zip 안은 들어가 있을 때만)\n"
            + "띄어 쓴 낱말 = 모두 들어간 것 · Enter = 찾기 · Esc = 지우고 폴더 목록으로 · ↓ = 결과 목록으로\n결과 목록이 Jukebox 다음 곡 목록이 된다");

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.Font = ShellTheme.Ui;
        list.ShowItemToolTips = true;
        list.SmallImageList = icons;
        list.Scrollable = true;   // 세로 스크롤바
        list.Columns.Add("이름", 108);
        list.Columns.Add("제목", 75);
        list.Columns.Add("형식", 40);
        list.Columns.Add("가사", 30, HorizontalAlignment.Center);
        list.Columns.Add("뱅크", 30, HorizontalAlignment.Center);
        list.ItemActivate += delegate { ActivateSelected(); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Right && SelectedEntry != null && SelectedEntry.Multi) { SetExpanded(list.SelectedItems[0], true); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && SelectedEntry != null && (SelectedEntry.Multi || SelectedEntry.IsTrack))
            {
                var fileItem = SelectedEntry.IsTrack ? FindItem(AdlibComposer.Audio.ChipSongs.FilePart(SelectedEntry.Path)) : list.SelectedItems[0];
                if (fileItem != null) { SetExpanded(fileItem, false); fileItem.Selected = true; fileItem.Focused = true; fileItem.EnsureVisible(); }
                e.Handled = true;
            }
        };
        list.Resize += delegate { UpdatePager(); };
        tips.SetToolTip(list, "폴더 두 번 클릭: 들어가기 · 곡 두 번 클릭 / Enter: 지금 페이지에서 열기 (못 여는 형식이면 그 형식을 여는 페이지로)\n"
            + "곡이 여럿 든 파일(KSS · NSF · HES, ▸): 두 번 클릭 / → 로 곡 목록 펼치기, ← 로 접기 — 곡 줄을 두 번 클릭하면 그 곡 재생\n"
            + "ZIP: 두 번 클릭하면 폴더처럼 들어가 안의 곡(ROL · IMS · SOP · VGM …)과 가사 · 뱅크를 그대로 쓴다 (ROL · IMS · SOP · MIDI만 든 zip은 풀지 않고 바로 재생)\n"
            + "제목 = 곡 안의 제목 · 가사 = 같은 이름 .ISS · 뱅크 = 같은 이름 .BNK");

        emptyLabel.Dock = DockStyle.Fill;
        emptyLabel.Font = ShellTheme.Ui;
        emptyLabel.ForeColor = ShellTheme.Muted;
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.Text = "[폴더 열기]로 곡 폴더를 고르거나\n폴더를 여기에 끌어다 놓으세요";

        // 아래: ◀ n / m 쪽 ▶
        var pager = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 32, ColumnCount = 3, Padding = new Padding(6, 3, 6, 3), BackColor = Color.FromArgb(246, 247, 249) };
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        SetupButton(prevPage, "", '', 40, "앞 쪽 (한 화면 위로)");
        SetupButton(nextPage, "", '', 40, "다음 쪽 (한 화면 아래로)");
        prevPage.Dock = nextPage.Dock = DockStyle.Fill;
        prevPage.Margin = nextPage.Margin = new Padding(0);
        prevPage.Click += delegate { TurnPage(-1); };
        nextPage.Click += delegate { TurnPage(1); };
        pageLabel.Dock = DockStyle.Fill;
        pageLabel.Font = ShellTheme.Ui;
        pageLabel.ForeColor = ShellTheme.Muted;
        pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        pager.Controls.Add(prevPage, 0, 0);
        pager.Controls.Add(pageLabel, 1, 0);
        pager.Controls.Add(nextPage, 2, 0);

        Controls.Add(list);
        Controls.Add(hoot);
        Controls.Add(emptyLabel);
        Controls.Add(pager);
        Controls.Add(searchBox);
        Controls.Add(pathLabel);
        Controls.Add(bar);

        // 스크롤바를 끌어도 쪽 번호가 맞도록 (ListView에는 Scroll 이벤트가 없다)
        pageTimer.Tick += delegate
        {
            int top = TopIndex;
            if (top != lastTop) UpdatePager();
        };
        pageTimer.Start();
        ShowEmpty();
    }

    private void SetupButton(Button b, string text, char glyph, int width, string tip)
    {
        b.Text = text;
        b.Image = GlyphImages.Get(glyph, 16, Color.FromArgb(50, 54, 62));
        b.ImageAlign = string.IsNullOrEmpty(text) ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        b.TextImageRelation = string.IsNullOrEmpty(text) ? TextImageRelation.Overlay : TextImageRelation.ImageBeforeText;
        b.Font = ShellTheme.Ui;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(214, 218, 224);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 239, 242);
        b.BackColor = Color.White;
        b.Width = width;
        b.Height = 26;
        b.Margin = new Padding(0, 0, 4, 0);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        tips.SetToolTip(b, tip);
    }

    public static MusicFileKind KindOf(string path)
    {
        string ext = (Path.GetExtension(AdlibComposer.Audio.ChipSongs.FilePart(path)) ?? "").ToLowerInvariant();   // "파일::n" = 파일 안 곡
        if (Array.IndexOf(AdlibExt, ext) >= 0) return MusicFileKind.Adlib;
        if (Array.IndexOf(TrackerExt, ext) >= 0) return MusicFileKind.Tracker;
        if (Array.IndexOf(MidiExt, ext) >= 0) return MusicFileKind.Midi;
        if (Array.IndexOf(VgmExt, ext) >= 0) return MusicFileKind.Vgm;
        if (Array.IndexOf(AudioExt, ext) >= 0) return MusicFileKind.Audio;
        return MusicFileKind.None;
    }

    // ---------------------------------------------------------------- 공개

    /// <summary>지금 보이는 폴더의 곡 파일 경로 (보이는 순서, 폴더 · 펼친 곡 줄 빼고 — 곡이 여럿 든 파일은 Jukebox가 곡마다 펼친다).</summary>
    public List<string> Paths
    {
        get
        {
            if (hootMode) return hoot.PlaylistPaths;
            var l = new List<string>();
            foreach (ListViewItem it in list.Items) { var e = (Entry)it.Tag; if (!e.IsDir && !e.IsTrack) l.Add(e.Path); }
            return l;
        }
    }

    private Entry SelectedEntry { get { return list.SelectedItems.Count > 0 ? (Entry)list.SelectedItems[0].Tag : null; } }

    private ListViewItem FindItem(string path)
    {
        foreach (ListViewItem it in list.Items)
            if (string.Equals(((Entry)it.Tag).Path, path, StringComparison.OrdinalIgnoreCase)) return it;
        return null;
    }

    /// <summary>목록에서 고른 곡 (폴더면 null).</summary>
    public string SelectedPath
    {
        get
        {
            if (list.SelectedItems.Count == 0) return null;
            var e = (Entry)list.SelectedItems[0].Tag;
            return e.IsDir ? null : e.Path;
        }
    }

    /// <summary>지금 보이는 폴더 (연 폴더가 없으면 null). 파일 대화상자 시작 폴더 등 — zip 안이면 그 zip이 있는 실제 폴더.</summary>
    public string CurrentFolder { get { return zips.Count > 0 ? Path.GetDirectoryName(zips[0].Zip) : current; } }

    /// <summary>zip을 폴더처럼 연다 (끌어다 놓기 등): 그 zip이 있는 폴더를 뿌리로 열고 안으로 들어간다.</summary>
    public void OpenZip(string zipPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        bool inside = root != null && (string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (!inside) OpenFolder(dir);
        else ShowFolder(dir);
        EnterZip(Path.GetFullPath(zipPath));
    }

    public bool ListFocused { get { return list.Focused; } }
    public void FocusList() { list.Focus(); }

    /// <summary>시작할 때: 지난번에 연 폴더를 다시 연다 (없으면 빈 목록).</summary>
    public void RestoreLastFolder()
    {
        string p = settings().BrowserFolderPath;
        if (p != null && Directory.Exists(p)) OpenFolder(p, false);
    }

    /// <summary>폴더를 연다 (이 폴더가 뿌리: 이 아래만 오간다). 열기 버튼 · 폴더 끌어다 놓기.</summary>
    public void OpenFolder(string dir, bool remember = true)
    {
        try { dir = Path.GetFullPath(dir); } catch (Exception) { }
        if (!Directory.Exists(dir)) return;
        zips.Clear();
        root = dir.TrimEnd('\\', '/');
        if (root.Length == 2 && root[1] == ':') root += "\\";   // 드라이브 뿌리
        ShowFolder(root);
        if (remember)
        {
            AppLog.Info("파일", "폴더 열기: " + root);
            try { var s = settings(); s.BrowserFolder = AppSettings.ToStored(root); s.Save(); }
            catch (Exception ex) { AppLog.Warn("파일", "설정에 폴더를 남기지 못했습니다", ex.Message); }
        }
    }

    /// <summary>
    /// 지금 연(재생 중인) 파일을 굵게 표시하고 고른다. "파일::n"(파일 안 곡)이면 그 파일을 펼쳐 곡 줄을 고르고 파일 줄도 굵게.
    /// </summary>
    public void MarkCurrent(string path)
    {
        hoot.MarkCurrent(path);
        currentPath = path;
        string file = AdlibComposer.Audio.ChipSongs.FilePart(path);
        bool track = path != null && !string.Equals(file, path, StringComparison.OrdinalIgnoreCase);
        if (track)
        {
            var fileItem = FindItem(file);
            if (fileItem != null) SetExpanded(fileItem, true);
        }
        list.BeginUpdate();
        ListViewItem pick = null;
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            bool exact = !e.IsDir && string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase);
            bool parent = track && !e.IsDir && !e.IsTrack && string.Equals(e.Path, file, StringComparison.OrdinalIgnoreCase);
            it.Font = exact || parent ? new Font(list.Font, FontStyle.Bold) : list.Font;
            if (exact || (parent && pick == null)) pick = it;
        }
        list.EndUpdate();
        if (pick != null) { pick.Selected = true; pick.Focused = true; pick.EnsureVisible(); }
    }

    // ---------------------------------------------------------------- 폴더 오가기

    private void BrowseFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "곡 폴더 (이 폴더와 아래 폴더를 왼쪽 목록에서 오간다)", UseDescriptionForTitle = true, ShowNewFolderButton = false })
        {
            if (current != null && Directory.Exists(current)) dlg.SelectedPath = current;
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) OpenFolder(dlg.SelectedPath);
        }
    }

    private bool AtRoot { get { return zips.Count == 0 && (current == null || string.Equals(current.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); } }

    private void GoUp()
    {
        if (hootMode) { LeaveHoot(); return; }
        if (searchMode) { ClearSearch(); return; }
        if (AtRoot) return;
        string from = current;
        string parent;
        if (zips.Count > 0 && string.Equals(current.TrimEnd('\\'), zips[zips.Count - 1].Cache.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            // zip 맨 위에서 위로 = zip 밖 (그 zip이 있던 폴더), 나온 zip을 고른다
            from = zips[zips.Count - 1].Zip;
            parent = Path.GetDirectoryName(from);
            zips.RemoveAt(zips.Count - 1);
        }
        else parent = Path.GetDirectoryName(current.TrimEnd('\\'));
        if (parent == null) return;
        ShowFolder(parent);
        // 나온 폴더를 고른다
        foreach (ListViewItem it in list.Items)
            if (string.Equals(((Entry)it.Tag).Path, from, StringComparison.OrdinalIgnoreCase)) { it.Selected = true; it.Focused = true; it.EnsureVisible(); break; }
    }

    private void ActivateSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var e = (Entry)list.SelectedItems[0].Tag;
        if (e.IsUp) GoUp();
        else if (e.IsZip) EnterZip(e.Path);
        else if (e.IsDir) ShowFolder(e.Path);
        else if (e.Multi) SetExpanded(list.SelectedItems[0], !expanded.Contains(e.Path));
        else if (FileActivated != null) FileActivated(e.Path);
    }

    private void ShowEmpty()
    {
        SetHootMode(false);
        list.Items.Clear();
        list.Visible = false;
        emptyLabel.Visible = true;
        pathLabel.Text = "열린 폴더 없음";
        EndSearch();
        searchBox.Visible = false;
        upButton.Enabled = false;
        songCount = 0;
        UpdatePager();
    }

    /// <summary>폴더 하나를 목록으로: (..) · 하위 폴더 · 곡 파일 (이름순).</summary>
    private void ShowFolder(string dir)
    {
        SetHootMode(false);
        if (searchMode || !string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) { EndSearch(); searchBox.Text = ""; }
        searchBox.Visible = true;
        current = dir;
        list.Visible = true;
        emptyLabel.Visible = false;
        list.BeginUpdate();
        list.Items.Clear();
        songCount = 0;
        if (!AtRoot)
            list.Items.Add(new ListViewItem("..", "up") { Tag = new Entry { Path = Path.GetDirectoryName(dir.TrimEnd('\\')), IsDir = true, IsUp = true }, ToolTipText = "위 폴더로", ForeColor = ShellTheme.Muted });
        try
        {
            bool inZip = ZipPath.IsZipDirectory(dir);   // 풀지 않고 보는 zip 안
            foreach (var d in ZipPath.GetDirectories(dir).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (!inZip)
                {
                    FileAttributes a;
                    try { a = File.GetAttributes(d); } catch (Exception) { continue; }
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                var it = new ListViewItem(Path.GetFileName(d), "dir") { Tag = new Entry { Path = d, IsDir = true }, ToolTipText = d };
                it.SubItems.Add("");
                it.SubItems.Add("폴더");
                list.Items.Add(it);
            }
            var files = ZipPath.GetFiles(dir);
            foreach (var z in files.Where(AdlibComposer.Formats.ZipFolder.IsZip).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                var it = new ListViewItem(Path.GetFileName(z), "zip") { Tag = new Entry { Path = z, IsDir = true, IsZip = true }, ToolTipText = z + "\n두 번 클릭: zip 안으로 (폴더처럼)" };
                it.SubItems.Add("");
                it.SubItems.Add("ZIP");
                list.Items.Add(it);
            }
            var names = new HashSet<string>(files.Select(f => Path.GetFileName(f).ToUpperInvariant()));
            foreach (var f in files.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (KindOf(f) == MusicFileKind.None) continue;
                string stem = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
                bool multi = AdlibComposer.Audio.ChipSongs.HasTracks(f);
                bool open = multi && expanded.Contains(f);
                var it = new ListViewItem(FileText(f, multi, open), multi ? "multi" : "song")
                {
                    Tag = new Entry { Path = f, Multi = multi },
                    ToolTipText = f + (multi ? "\n곡이 여럿 든 파일: 두 번 클릭 / → 로 곡 목록" : ""),
                };
                it.SubItems.Add(CachedTitle(f) ?? "");
                it.SubItems.Add(Path.GetExtension(f).TrimStart('.').ToUpperInvariant());
                it.SubItems.Add(names.Contains(stem + ".ISS") ? "●" : "");
                it.SubItems.Add(names.Contains(stem + ".BNK") ? "●" : "");
                if (string.Equals(f, AdlibComposer.Audio.ChipSongs.FilePart(currentPath), StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(it);
                songCount++;
                if (open) foreach (var t in TrackItems(f)) list.Items.Add(t);
            }
        }
        catch (Exception ex) { AppLog.Warn("파일", "폴더를 읽을 수 없습니다: " + dir, ex.Message); }
        list.EndUpdate();
        upButton.Enabled = !AtRoot;
        searchMode = false;
        // 경로: 연 폴더 이름 › 아래 폴더 …
        string shown = DisplayPath(dir);
        string rel = AtRoot ? "" : Path.GetRelativePath(root, shown);
        string rootName = Path.GetFileName(root.TrimEnd('\\'));
        pathLabel.Text = (rootName.Length > 0 ? rootName : root) + (rel.Length > 0 && rel != "." ? " › " + rel.Replace("\\", " › ") : "");
        tips.SetToolTip(pathLabel, shown + (zips.Count > 0 ? (ZipPath.IsZipDirectory(dir) ? "\n(zip 안 — 풀지 않고 바로 읽음)" : "\n(zip을 푼 임시 폴더: " + dir + ")") : ""));
        if (list.Items.Count > 0) list.Items[0].Focused = true;
        lastTop = -1;
        UpdatePager();
        LoadTitles();
    }

    // ---------------------------------------------------------------- hoot 목록

    /// <summary>[hoot]: 읽어 둔 트리가 있으면 그리로, 없거나 Shift면 hoot 목록 폴더를 고른다.</summary>
    private void OpenHoot(bool choose)
    {
        if (!choose && hoot.ListDir != null) { SetHootMode(true); return; }
        string start = hoot.ListDir ?? settings().HootFolderPath;
        using (var dlg = new FolderBrowserDialog { Description = "hoot 게임 목록 폴더 (xml · xml_new …: 제작사마다 .xml 파일)", UseDescriptionForTitle = true, ShowNewFolderButton = false })
        {
            if (start != null && Directory.Exists(start)) dlg.SelectedPath = start;
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            OpenHootFolder(dlg.SelectedPath);
        }
    }

    /// <summary>hoot 목록 폴더를 읽어 트리로 보인다 (설정에 남김). hoot 폴더 자체를 고르면 그 안의 xml_new / xml.</summary>
    public void OpenHootFolder(string dir)
    {
        try { dir = Path.GetFullPath(dir); } catch (Exception) { }
        if (!Directory.Exists(dir)) return;
        if (!Directory.EnumerateFiles(dir, "*.xml").Any(f => !Path.GetFileName(f).StartsWith("hoot", StringComparison.OrdinalIgnoreCase)))
            foreach (var sub in new[] { "xml_new", "xml" })
                if (Directory.Exists(Path.Combine(dir, sub))) { dir = Path.Combine(dir, sub); break; }
        hoot.Open(dir);
        SetHootMode(true);
        try { var s = settings(); s.HootFolder = AppSettings.ToStored(dir); s.Save(); }
        catch (Exception ex) { AppLog.Warn("파일", "설정에 hoot 폴더를 남기지 못했습니다", ex.Message); }
    }

    private void LeaveHoot()
    {
        if (!hootMode) return;
        if (current != null && ZipPath.DirectoryExists(current)) ShowFolder(current);
        else ShowEmpty();
    }

    private void SetHootMode(bool on)
    {
        hootMode = on;
        hoot.Visible = on;
        if (on) { EndSearch(); searchBox.Visible = false; }   // hoot 트리는 자기 찾기 칸
        hootButton.BackColor = on ? Color.FromArgb(222, 242, 239) : Color.White;
        tips.SetToolTip(upButton, on ? "폴더 목록으로 돌아가기 · Backspace" : "위 폴더로 (연 폴더 위로는 가지 않는다) · Backspace");
        if (!on) return;
        list.Visible = false;
        emptyLabel.Visible = false;
        upButton.Enabled = true;
        pathLabel.Text = hoot.Summary;
        tips.SetToolTip(pathLabel, hoot.ListDir ?? "");
        prevPage.Enabled = nextPage.Enabled = false;
        pageLabel.Text = "";
        hoot.Focus();
    }

    // ---------------------------------------------------------------- ZIP

    /// <summary>
    /// zip 안으로. 곡이 모두 ROL · IMS · SOP · MUS · MIDI(+ 뱅크 · 가사)인 zip은 풀지 않고 zip 안을 바로 보고 읽는다 (경로 = "zip\안 경로", <see cref="ZipPath"/>).
    /// 다른 형식(VGM · 트래커 · hoot 항목 …)이나 zip 안 zip이 있으면 한 번 풀어 둔 임시 폴더를 연다 (처음이면 푸는 동안 잠깐 기다린다).
    /// </summary>
    private void EnterZip(string zipPath)
    {
        string cache;
        bool direct;
        var old = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            direct = !ZipPath.IsInZip(zipPath) && ZipPath.CanBrowse(zipPath, f => KindOf(f) != MusicFileKind.None) && !ZipFolder.IsHootArchive(zipPath);
            cache = direct ? zipPath : ZipFolder.Extract(zipPath);
        }
        catch (Exception ex) { AppLog.Warn("파일", "zip을 열 수 없습니다: " + Path.GetFileName(zipPath), ex.Message); return; }
        finally { Cursor = old; }
        // 지금 보이는 곳 밖의 zip이면 (끌어다 놓기) 쌓인 것을 정리한다
        while (zips.Count > 0 && !IsUnder(zipPath, zips[zips.Count - 1].Cache)) zips.RemoveAt(zips.Count - 1);
        zips.Add((zipPath, cache));
        AppLog.Info("파일", "zip 열기: " + DisplayPath(zipPath), direct ? "풀지 않고 zip 안을 바로 읽음 (ROL · IMS · SOP · MIDI)" : "푼 곳 (임시): " + cache);
        ShowFolder(cache);
    }

    private static bool IsUnder(string path, string dir)
    {
        string d = dir.TrimEnd('\\') + "\\";
        return path.StartsWith(d, StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>임시 폴더 경로 → 보이는 경로 (zip 파일 경로 + zip 안 경로). zip 안의 zip도 바깥까지 거슬러 올라간다.</summary>
    private string DisplayPath(string path)
    {
        for (int i = zips.Count - 1; i >= 0; i--)
        {
            if (!IsUnder(path, zips[i].Cache)) continue;
            string inner = Path.GetRelativePath(zips[i].Cache, path);
            path = inner == "." ? zips[i].Zip : Path.Combine(zips[i].Zip, inner);
        }
        return path;
    }

    // ---------------------------------------------------------------- 곡이 여럿 든 파일 (KSS · NSF · HES)

    private static string FileText(string path, bool multi, bool open)
    {
        return (multi ? (open ? "▾ " : "▸ ") : "") + Path.GetFileName(path);
    }

    /// <summary>파일 안 곡마다 한 줄 ("파일::n", 들여 쓰기). HES는 처음 한 번 곡을 찾느라 1초쯤 걸릴 수 있다.</summary>
    private List<ListViewItem> TrackItems(string file)
    {
        var l = new List<ListViewItem>();
        List<(string path, string title)> tracks;
        try { tracks = AdlibComposer.Audio.ChipSongs.Tracks(file); }
        catch (Exception ex) { AppLog.Warn("파일", "곡 목록을 읽을 수 없습니다: " + Path.GetFileName(file), ex.Message); return l; }
        for (int i = 0; i < tracks.Count; i++)
        {
            var (path, title) = tracks[i];
            var it = new ListViewItem((i + 1).ToString("00"), "track")
            {
                Tag = new Entry { Path = path, IsTrack = true },
                IndentCount = 1,
                ToolTipText = Path.GetFileName(file) + " — " + (i + 1) + "/" + tracks.Count + (title.Length > 0 ? ": " + title : "") + "\n두 번 클릭 / Enter: 이 곡 재생",
            };
            // 제목이 "이름 #n"뿐이면 (m3u 없음) 칸을 비운다: 번호는 이미 이름 칸에
            it.SubItems.Add(System.Text.RegularExpressions.Regex.IsMatch(title, @" #\d+$") ? "" : title);
            it.SubItems.Add("");
            it.SubItems.Add("");
            it.SubItems.Add("");
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
            l.Add(it);
        }
        return l;
    }

    /// <summary>곡이 여럿 든 파일 줄을 펼치거나 접는다 (바로 아래에 곡 줄을 넣거나 뺀다).</summary>
    private void SetExpanded(ListViewItem fileItem, bool open)
    {
        var e = (Entry)fileItem.Tag;
        if (!e.Multi) return;
        bool isOpen = expanded.Contains(e.Path);
        int at = fileItem.Index;
        if (open == isOpen && (!open || (at + 1 < list.Items.Count && ((Entry)list.Items[at + 1].Tag).IsTrack))) return;
        list.BeginUpdate();
        // 있던 곡 줄을 먼저 뺀다
        while (at + 1 < list.Items.Count && ((Entry)list.Items[at + 1].Tag).IsTrack) list.Items.RemoveAt(at + 1);
        if (open)
        {
            var old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                var rows = TrackItems(e.Path);
                for (int i = 0; i < rows.Count; i++) list.Items.Insert(at + 1 + i, rows[i]);
                expanded.Add(e.Path);
                if (rows.Count > 0) list.Items[at + rows.Count].EnsureVisible();
                fileItem.EnsureVisible();
            }
            finally { Cursor = old; }
        }
        else expanded.Remove(e.Path);
        fileItem.Text = FileText(e.Path, true, open);
        list.EndUpdate();
        UpdatePager();
    }

    // ---------------------------------------------------------------- 쪽 넘기기

    /// <summary>
    /// 한 쪽 = 지금 목록에 보이는 줄 수.
    /// 목록을 비우는 도중(Items.Clear)에는 스크롤바가 없어지며 크기가 바뀌어 여기로 오는데, 그때는 화면 목록은 비었고 Items.Count는 아직 남아
    /// GetItemRect가 범위 오류를 낸다: 줄 높이를 못 읽으면 1쪽으로 본다 (비우기가 끝나면 다시 계산).
    /// </summary>
    private int PageSize
    {
        get
        {
            if (list.Items.Count == 0) return 1;
            try
            {
                int rowH = Math.Max(1, list.GetItemRect(0).Height);
                var topItem = list.TopItem;
                int header = topItem != null ? list.GetItemRect(topItem.Index).Top : rowH;
                return Math.Max(1, (list.ClientSize.Height - Math.Max(0, header)) / rowH);
            }
            catch (ArgumentOutOfRangeException) { return 1; }
        }
    }

    /// <summary>목록 맨 위 줄 번호 (없거나 목록을 바꾸는 도중이면 -1).</summary>
    private int TopIndex
    {
        get
        {
            if (list.Items.Count == 0) return -1;
            try { var t = list.TopItem; return t != null ? t.Index : -1; }
            catch (ArgumentOutOfRangeException) { return -1; }
        }
    }

    private void TurnPage(int dir)
    {
        if (list.Items.Count == 0) return;
        int size = PageSize;
        int top = Math.Max(0, TopIndex);
        int target = Math.Max(0, Math.Min(list.Items.Count - 1, top + dir * size));
        // 아래로: 그 쪽의 마지막 줄을 먼저 보이게 한 뒤 맨 위를 맞춘다 (TopItem만으로는 안 움직일 때가 있다)
        if (dir > 0) list.EnsureVisible(Math.Min(list.Items.Count - 1, target + size - 1));
        list.EnsureVisible(target);
        try { list.TopItem = list.Items[target]; } catch (Exception) { }
        UpdatePager();
    }

    private void UpdatePager()
    {
        if (hootMode) return;   // hoot 트리에는 쪽이 없다
        int n = list.Items.Count;
        int size = PageSize;
        int pages = Math.Max(1, (n + size - 1) / size);
        int ti = TopIndex;
        int top = Math.Max(0, ti);
        lastTop = ti;
        int page = Math.Min(pages, top / size + 1);
        if (top + size >= n) page = pages;   // 맨 아래까지 내려왔으면 마지막 쪽
        pageLabel.Text = current == null ? "" : page + " / " + pages + " 쪽 · " + songCount + (searchMode ? "곡 찾음" : "곡");
        prevPage.Enabled = n > 0 && top > 0;
        nextPage.Enabled = n > 0 && top + size < n;
    }

    // ---------------------------------------------------------------- 찾기 (파일 이름 · 제목)

    /// <summary>찾기를 그만둔다 (뒤에서 찾던 것도 멈춤). 목록은 그대로.</summary>
    private void EndSearch()
    {
        searchGeneration++;
        searchMode = false;
    }

    /// <summary>Esc · 위로: 찾기 칸을 지우고 지금 폴더 목록으로.</summary>
    private void ClearSearch()
    {
        searchBox.Text = "";
        if (current != null && ZipPath.DirectoryExists(current)) { EndSearch(); ShowFolder(current); list.Focus(); }
    }

    /// <summary>찾은 곡 하나: 파일 (+ 낱말이 든 파일 안 곡들).</summary>
    private sealed class SearchHit
    {
        public string Path, Title;
        public bool Multi;
        public readonly List<(string path, string title, int index)> Tracks = new List<(string, string, int)>();
    }

    /// <summary>
    /// 지금 폴더와 아래 폴더의 곡 파일 중 이름이나 제목에 낱말(띄어 쓴 것 모두)이 든 것을 다른 스레드에서 찾아 목록에 채운다.
    /// 제목은 목록의 '제목' 칸과 같은 것 (캐시를 같이 쓴다), 곡이 여럿 든 파일은 파일 안 곡 제목도 본다 (HES · GBS는 곡 목록을 이미 알 때만).
    /// </summary>
    private void StartSearch(string text)
    {
        if (current == null || !ZipPath.DirectoryExists(current)) return;
        var words = (text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { ClearSearch(); return; }
        int gen = ++searchGeneration;
        titleGeneration++;   // 폴더 제목 읽기는 그만
        searchMode = true;
        string baseDir = current;
        list.BeginUpdate();
        list.Items.Clear();
        list.EndUpdate();
        list.Visible = true;
        emptyLabel.Visible = false;
        songCount = 0;
        upButton.Enabled = true;
        string label = "찾기 '" + string.Join(" ", words) + "'";
        pathLabel.Text = label + " — 찾는 중…";
        tips.SetToolTip(pathLabel, DisplayPath(baseDir) + " 와 아래 폴더");
        UpdatePager();
        bool Match(string hay) { return words.All(w => hay.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0); }

        System.Threading.Tasks.Task.Run(() =>
        {
            var batch = new List<SearchHit>();
            int scanned = 0, found = 0;
            var lastPost = DateTime.UtcNow;
            void Post(bool done)
            {
                var hits = batch;
                batch = new List<SearchHit>();
                int sc = scanned, fd = found;
                try { BeginInvoke((Action)(() => ApplySearch(gen, hits, label, sc, fd, done))); } catch (Exception) { }
            }
            foreach (var f in SongFilesUnder(baseDir, () => gen == searchGeneration))
            {
                if (gen != searchGeneration) return;
                scanned++;
                string name = Path.GetFileName(f);
                string title = CachedTitle(f);
                if (title == null && KindOf(f) != MusicFileKind.Audio)
                {
                    title = ReadSongTitle(f);
                    try { lock (titleCache) titleCache[f] = (ZipPath.GetLastWriteTimeUtc(f), title); } catch (Exception) { }
                }
                title = title ?? "";
                bool multi = AdlibComposer.Audio.ChipSongs.HasTracks(f);
                var hit = new SearchHit { Path = f, Title = title, Multi = multi };
                bool fileMatch = Match(name + " " + title);
                // 파일 안 곡 줄은 파일 자체는 안 맞고 곡 제목이 맞을 때만 (파일이 맞으면 펼쳐서 보면 된다)
                if (multi && !fileMatch && TrackTitlesCheap(f))
                {
                    try
                    {
                        var tracks = AdlibComposer.Audio.ChipSongs.Tracks(f);
                        for (int i = 0; i < tracks.Count; i++)
                            if (Match(tracks[i].title)) hit.Tracks.Add((tracks[i].path, tracks[i].title, i));
                    }
                    catch (Exception) { }
                }
                if (fileMatch || hit.Tracks.Count > 0) { batch.Add(hit); found++; }
                if (batch.Count >= 50 || (DateTime.UtcNow - lastPost).TotalMilliseconds > 300) { Post(false); lastPost = DateTime.UtcNow; }
            }
            if (gen == searchGeneration) Post(true);
        });
    }

    /// <summary>파일 안 곡 제목을 돌리지 않고 알 수 있는가 (HES · GBS는 곡을 찾아 둔 뒤에만).</summary>
    private static bool TrackTitlesCheap(string f)
    {
        string ext = Path.GetExtension(f);
        if (AdlibComposer.Audio.Hes.Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase))) return AdlibComposer.Audio.Hes.TracksKnown(f);
        if (AdlibComposer.Audio.Gbs.Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase))) return AdlibComposer.Audio.Gbs.TracksKnown(f);
        return true;
    }

    /// <summary>폴더와 그 아래 폴더의 곡 파일 (이름순, 숨김 · 시스템 폴더는 뺌, 읽을 수 없는 폴더는 건너뜀).</summary>
    private static IEnumerable<string> SongFilesUnder(string dir, Func<bool> go)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && go())
        {
            string d = stack.Pop();
            string[] files, subs;
            try
            {
                files = ZipPath.GetFiles(d);
                subs = ZipPath.GetDirectories(d);
            }
            catch (Exception) { continue; }
            foreach (var f in files.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                if (KindOf(f) != MusicFileKind.None) yield return f;
            foreach (var sub in subs.OrderByDescending(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (!ZipPath.IsInZip(sub))
                    try { if ((File.GetAttributes(sub) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; } catch (Exception) { continue; }
                stack.Push(sub);
            }
        }
    }

    private void ApplySearch(int gen, List<SearchHit> hits, string label, int scanned, int found, bool done)
    {
        if (gen != searchGeneration || !searchMode) return;
        if (hits.Count > 0)
        {
            list.BeginUpdate();
            foreach (var h in hits)
            {
                var it = new ListViewItem(FileText(h.Path, h.Multi, false), h.Multi ? "multi" : "song")
                {
                    Tag = new Entry { Path = h.Path, Multi = h.Multi },
                    ToolTipText = DisplayPath(h.Path) + (h.Title.Length > 0 ? "\n" + h.Title : "") + (h.Multi ? "\n곡이 여럿 든 파일: 두 번 클릭 / → 로 곡 목록" : ""),
                };
                it.SubItems.Add(h.Title);
                it.SubItems.Add(Path.GetExtension(h.Path).TrimStart('.').ToUpperInvariant());
                it.SubItems.Add("");
                it.SubItems.Add("");
                if (string.Equals(h.Path, AdlibComposer.Audio.ChipSongs.FilePart(currentPath), StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(it);
                songCount++;
                foreach (var (path, title, index) in h.Tracks)
                {
                    var t = new ListViewItem((index + 1).ToString("00"), "track")
                    {
                        Tag = new Entry { Path = path, IsTrack = true },
                        IndentCount = 1,
                        ToolTipText = Path.GetFileName(h.Path) + " — " + (index + 1) + (title.Length > 0 ? ": " + title : "") + "\n두 번 클릭 / Enter: 이 곡 재생",
                    };
                    t.SubItems.Add(title);
                    t.SubItems.Add(""); t.SubItems.Add(""); t.SubItems.Add("");
                    if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) t.Font = new Font(list.Font, FontStyle.Bold);
                    list.Items.Add(t);
                }
            }
            list.EndUpdate();
        }
        pathLabel.Text = label + " — " + found + "곡" + (done ? " (파일 " + scanned + "개 중)" : " · 찾는 중… (파일 " + scanned + "개)");
        UpdatePager();
    }

    // ---------------------------------------------------------------- 목록의 곡 제목 (뒤에서 읽기)

    private readonly Dictionary<string, (DateTime Time, string Title)> titleCache = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
    private int titleGeneration;

    /// <summary>캐시에 있고 파일이 그대로면 제목, 아니면 null (아직 안 읽음).</summary>
    private string CachedTitle(string path)
    {
        try
        {
            lock (titleCache)
                if (titleCache.TryGetValue(path, out var c) && c.Time == ZipPath.GetLastWriteTimeUtc(path)) return c.Title;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>목록 파일의 곡 안 제목을 다른 스레드에서 읽어 '제목' 칸을 채운다. 폴더가 바뀌면 그만둔다.</summary>
    private void LoadTitles()
    {
        int gen = ++titleGeneration;
        var todo = new List<string>();
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            if (!e.IsDir && !e.IsTrack && KindOf(e.Path) != MusicFileKind.Audio && CachedTitle(e.Path) == null) todo.Add(e.Path);
        }
        if (todo.Count == 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < todo.Count && gen == titleGeneration; i++)
            {
                string t = ReadSongTitle(todo[i]);
                try { lock (titleCache) titleCache[todo[i]] = (ZipPath.GetLastWriteTimeUtc(todo[i]), t); } catch (Exception) { }
                batch[todo[i]] = t;
                if (batch.Count >= 25 || i == todo.Count - 1)
                {
                    var done = batch;
                    batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try { BeginInvoke((Action)(() => ApplyTitles(done, gen))); } catch (Exception) { return; }
                }
            }
        });
    }

    private void ApplyTitles(Dictionary<string, string> titles, int gen)
    {
        if (gen != titleGeneration) return;
        list.BeginUpdate();
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            if (!e.IsDir && !e.IsTrack && titles.TryGetValue(e.Path, out var t)) it.SubItems[1].Text = t;
        }
        list.EndUpdate();
    }

    /// <summary>곡 파일 안의 제목 (ROL은 제목 칸이 없어 빈 값). 파일 이름과 같거나 'untitled'면 빈 값.</summary>
    private string ReadSongTitle(string path)
    {
        string t = "";
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (MidiFiles.IsMidi(path)) t = MidiFiles.Load(path).Title;
            else if (AdlibComposer.Audio.ChipSongs.IsChipSong(path)) t = AdlibComposer.Audio.ChipSongs.Title(path);
            else if (SopFormat.IsSop(path)) t = SopFormat.Load(path).Title;
            else if (TrackerFiles.IsTracker(path)) t = TrackerFiles.Load(path).Title;
            else if (ext == ".ims")
            {
                var fmt = ws.Formats.FindSong(path);
                if (fmt != null && fmt.CanRead) using (var fs = ZipPath.OpenRead(path)) t = fmt.Read(fs).Title;
            }
        }
        catch (Exception) { return ""; }
        t = System.Text.RegularExpressions.Regex.Replace(t ?? "", @"\s+", " ").Trim();
        if (t.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            || t.Equals("untitled", StringComparison.OrdinalIgnoreCase)) return "";
        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tips.Dispose(); pageTimer.Dispose(); icons.Dispose(); }
        base.Dispose(disposing);
    }
}
